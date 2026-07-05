using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AppoMobi.Gestures;
using DrawnUi;
using DrawnUi.Controls;
using DrawnUi.Draw;
using DrawnUi.Views;

namespace DrawnChatList;

/// <summary>
/// Recycled chat bubble cell: incoming/outgoing alignment + colors, optional image banner
/// (Image type), tappable link (Link type), date separator on day change and a bubble tail
/// on the first message of a consecutive same-sender run, runs resetting on a new day
/// (ghost tail on follow-ups keeps alignment).
/// All visual states are reset in SetContent because the same instance is recycled
/// across message types.
/// </summary>
public class ChatCell : SkiaDynamicDrawnCell
{
    // Non-recycled chat: a rebind means the SAME message (or its shifted neighbor) — stale front
    // pixels for 1-2 frames beat a blank blink. NOTE: switching this to true crashed on-device
    // (native SIGSEGV on GLThread, null deref in Skia): the destroy disposes the cache surface on
    // the mutating thread while the render thread can still be blitting it. If skeleton-on-rebind
    // is ever wanted, the destroy must be deferred through the render thread, not immediate.
    protected override bool DestroyCacheOnContextChange => false;

    private const float MaxBubbleWidth = 280f;

    public static readonly Color ColorIncoming = ChatTheme.Incoming;
    public static readonly Color ColorOutgoing = ChatTheme.Outgoing;
    public static readonly Color ColorCheck = ChatTheme.Check;
    public static readonly Color ColorCheckRead = ChatTheme.AccentBright;

    private readonly SkiaLayout _row;
    private readonly SkiaShape _bubble;
    private readonly IncomingBubbleSign _tailIn;
    private readonly OutcomingBubbleSign _tailOut;
    private readonly SkiaImage _banner;
    private readonly SkiaSvg _fileIcon;
    private readonly SkiaLayout _quoteBox;
    private readonly SkiaLabel _quoteName;
    private readonly SkiaLabel _quoteText;
    private readonly SkiaRichLabel _label;
    private readonly SkiaLabel _time;
    private readonly SkiaSvg _checkSent;
    private readonly SkiaSvg _checkDelivered;
    private readonly SkiaShape _dayChip;
    private readonly SkiaLabel _day;

    //inline so the sample needs no assets, same look as the original app's SvgCheck
    private const string SvgCheck =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M9 16.2 4.8 12l-1.4 1.4L9 19 21 7l-1.4-1.4z'/></svg>";

    //paperclip for file attachments, like the original app's SvgAttachment
    public const string SvgAttachment =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M16.5 6v11.5a4 4 0 0 1-8 0V5a2.5 2.5 0 0 1 5 0v10.5a1 1 0 0 1-2 0V6H10v9.5a2.5 2.5 0 0 0 5 0V5a4 4 0 0 0-8 0v12.5a5.5 5.5 0 0 0 11 0V6z'/></svg>";


    #region HIGHLIGHT


    // Jump highlight: full-row band behind the bubble, instant tint on draw,
    // hold, then fade out so it's gone ~5s after the cell was drawn.
    private const double HighlightPeakOpacity = 0.4;
    private const int HighlightHoldStillMs = 2500;
    private const float HighlightFadeMs = 1500;
    private const long HighlightFreshMs = 4000; // older requests are consumed without playing (recycle)

    private readonly SkiaShape _highlight;
    private long _playedHighlightStamp;
    private CancellationTokenSource _highlightCts;
    private bool _flashActive; // a jump-flash animation currently owns _highlight

    #endregion

    public override ISkiaGestureListener ProcessGestures(SkiaGesturesParameters args, GestureEventProcessingInfo apply)
    {
        if (args.Type == TouchActionResult.Tapped)
        {
            if (BindingContext is ChatMessage msg)
            {
                Debug.WriteLine($"Tapped CELL data {msg.Index} index {ContextIndex}");
            }
            else
            {
                Debug.WriteLine($"Tapped CELL index {ContextIndex}");
            }
        }

        return base.ProcessGestures(args, apply);
    }


    public ChatCell()
    {
        HorizontalOptions = LayoutOptions.Fill;
        Rotation = 180; //the scroll is rotated 180 (inverted chat), rotate the cell back upright

        FastMeasurement = false; //can't use fast measurement default for cells cuz we use some height FILL children while having cell AUTO height
        
        //we will be suing sell in context of MeasureVisible (background measure) so
        //we about ImageDoubleBuffered and GPU for background thread safe processing
        UseCache = SkiaCacheType.ImageDoubleBuffered;

        Children = new List<SkiaControl>
        {
            //jump-highlight band: covers the whole cell behind the content, hidden when idle
            new SkiaShape
            {
                ZIndex = -1,
                IsVisible = false,
                InputTransparent = true,
                Type = ShapeType.Rectangle,
                BackgroundColor = ChatTheme.AccentBright,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
            }.Assign(out _highlight),

            new DiagStack
            {
                Spacing = 0,
                Children =
                {
                    //date separator on day change: telegram-style pill chip
                    new SkiaShape
                    {
                        InputTransparent = true,
                        IsVisible = false,
                        UseCache = SkiaCacheType.Image,
                        Type = ShapeType.Rectangle,
                        CornerRadius = 11,
                        BackgroundColor = Color.FromArgb("#40000000"),
                        Padding = new Thickness(10, 3),
                        HorizontalOptions = LayoutOptions.Center,
                        Margin = new Thickness(10, 6, 10, 8),
                        Children =
                        {
                            new SkiaLabel
                            {
                                FontSize = 11,
                                TextColor = Color.FromArgb("#BBC7D3"),
                            }.Assign(out _day),
                        }
                    }.Assign(out _dayChip),

                    //container to glue the bubble with its tail
                    new SkiaLayout
                        {
                            Type = LayoutType.Row,
                            Spacing = 0,
                            Margin = new Thickness(8, 0),
                            Children =
                            {
                                new IncomingBubbleSign().Assign(out _tailIn),

                                new SkiaShape
                                {
                                    Type = ShapeType.Rectangle,
                                    CornerRadius = 14,
                                    Padding = 0,
                                    MaximumWidthRequest = MaxBubbleWidth,
                                    Children =
                                    {
                                        //row glues the optional file icon to the message column (original app structure)
                                        new SkiaLayout
                                        {
                                            Type = LayoutType.Row,
                                            Spacing = 0,
                                            Children =
                                            {
                                                //icon for file attachments
                                                new SkiaSvg
                                                {
                                                    IsVisible = false,
                                                    UseCache = SkiaCacheType.Operations,
                                                    SvgString = SvgAttachment,
                                                    TintColor = Color.FromArgb("#88FFFFFF"),
                                                    HeightRequest = 20,
                                                    LockRatio = 1,
                                                    Margin = new Thickness(10, 0, 0, 0),
                                                    VerticalOptions = LayoutOptions.Center,
                                                }.Assign(out _fileIcon),

                                                new SkiaStack
                                                {
                                                    Spacing = 0,
                                                    Children =
                                                    {
                                                        //image/link attachment banner
                                                        new SkiaImage
                                                        {
                                                            //LoadSourceOnFirstDraw = false, //soft preload
                                                            IsVisible = false,
                                                            RescalingQuality = FilterQuality.None,
                                                            //UseCache = SkiaCacheType.ImageDoubleBuffered, //avoid spikes when updating
                                                            Aspect = TransformAspect.AspectCover,
                                                            BackgroundColor = Colors.DimGray,
                                                            HeightRequest = 140,
                                                            HorizontalOptions = LayoutOptions.Fill,
                                                            EraseChangedContent = true,
                                                        }.Assign(out _banner),

                                                        //QUOTED MESSAGE this one replies to (original app's
                                                        //AttachedMessageStack): accent bar + author + text,
                                                        //tap jumps to the original message
                                                        new SkiaLayout
                                                        {
                                                            IsVisible = false,
                                                            UseCache = SkiaCacheType.Operations,
                                                            Margin = new Thickness(10, 8, 10, 0),
                                                            HorizontalOptions = LayoutOptions.Fill,
                                                            Children =
                                                            {
                                                                new SkiaShape
                                                                {
                                                                    UseCache = SkiaCacheType.Operations,
                                                                    Type = ShapeType.Rectangle,
                                                                    CornerRadius = 2,
                                                                    WidthRequest = 3,
                                                                    BackgroundColor = ChatTheme.AccentBright,
                                                                    VerticalOptions = LayoutOptions.Fill,
                                                                },

                                                                new SkiaStack
                                                                {
                                                                    HorizontalOptions = LayoutOptions.Fill,
                                                                    Margin = new(11, 0, 0, 0),
                                                                    Spacing = 1,
                                                                    Children =
                                                                    {
                                                                        new SkiaLabel
                                                                        {
                                                                            FontSize = 11,
                                                                            TextColor = ChatTheme.AccentBright,
                                                                            MaxLines = 1,
                                                                            LineBreakMode =
                                                                                LineBreakMode.TailTruncation,
                                                                        }.Assign(out _quoteName),

                                                                        new SkiaLabel
                                                                        {
                                                                            FontSize = 12,
                                                                            TextColor = Color.FromArgb("#AAFFFFFF"),
                                                                            MaxLines = 1,
                                                                            LineBreakMode =
                                                                                LineBreakMode.TailTruncation,
                                                                        }.Assign(out _quoteText),
                                                                    }
                                                                },
                                                            }
                                                        }.Assign(out _quoteBox).OnTapped(me =>
                                                        {
                                                            if (BindingContext is ChatMessage msg
                                                                && msg.ReplyTo != null
                                                                && Parent?.BindingContext is IChatCellActions actions)
                                                            {
                                                                actions.ScrollToMessage(msg.ReplyTo);
                                                            }
                                                        }),

                                                        //message text (markdown links supported)
                                                        new SkiaRichLabel
                                                        {
                                                            UseCache = SkiaCacheType.Operations,
                                                            InputTransparent = true,
                                                            FontSize = 14,
                                                            TextColor = Colors.White,
                                                            Margin = new Thickness(12, 8, 12, 0),
                                                        }.Assign(out _label),

                                                        //time sent + delivery status checkmarks
                                                        new SkiaRow
                                                        {
                                                            UseCache = SkiaCacheType.Operations,
                                                            InputTransparent = true,
                                                            Spacing = 3,
                                                            HorizontalOptions = LayoutOptions.End,
                                                            Margin = new Thickness(12, 2, 12, 6),
                                                            Children =
                                                            {
                                                                new SkiaLabel
                                                                {
                                                                    FontSize = 9,
                                                                    TextColor = Color.FromArgb("#88FFFFFF"),
                                                                }.Assign(out _time),

                                                                // STATUS SENT
                                                                new SkiaSvg
                                                                {
                                                                    IsVisible = false,
                                                                    UseCache = SkiaCacheType.Operations,
                                                                    SvgString = SvgCheck,
                                                                    TintColor = ColorCheck,
                                                                    HeightRequest = 11,
                                                                    WidthRequest = 11,
                                                                    VerticalOptions = LayoutOptions.Center,
                                                                }.Assign(out _checkSent),

                                                                // STATUS DELIVERED: overlaps the first check into a double-check
                                                                new SkiaSvg
                                                                {
                                                                    IsVisible = false,
                                                                    UseCache = SkiaCacheType.Operations,
                                                                    SvgString = SvgCheck,
                                                                    TintColor = ColorCheck,
                                                                    HeightRequest = 11,
                                                                    WidthRequest = 11,
                                                                    VerticalOptions = LayoutOptions.Center,
                                                                    TranslationX = -8,
                                                                }.Assign(out _checkDelivered),
                                                            }
                                                        },
                                                    }
                                                },
                                            }
                                        }
                                    }
                                }.Assign(out _bubble),

                                new OutcomingBubbleSign().Assign(out _tailOut),
                            }
                        }.Assign(out _row)
                        .OnLongPressing(me =>
                        {
                            //long-press a bubble: quote it (original app showed an options
                            //menu here; the sample goes straight to reply)
                            if (me.BindingContext is ChatMessage msg
                                && Parent?.BindingContext is IChatCellActions actions)
                            {
                                actions.ShowMessageOptions(msg);
                            }
                        })
                        .OnTapped(me =>
                        {
                            if (me.BindingContext is ChatMessage msg)
                            {
                                Debug.WriteLine($"[CHAT] tapped message {msg.Index} ({msg.Type})");

                                if (msg.IsFirstDay)
                                {
                                    msg.IsFirstDay = false;
                                    return;
                                }

                                //open tapped image fullscreen, page reached like in the
                                //original app: cell -> Parent (items stack) -> BindingContext
                                if (msg.Type == ChatMessageType.Image
                                    && Parent?.BindingContext is IChatCellActions actions)
                                {
                                    actions.ShowImageFullscreen(msg);
                                }
                            }
                        }),
                }
            }
        };

        _label.LinkTapped += (s, url) => Debug.WriteLine($"[CHAT] link tapped: {url}");
    }

    #region SKELETON PLACEHOLDER

    // Cold ImageDoubleBuffered cell: the FIRST cache is baked in background, and the base
    // DrawPlaceholder paints NOTHING — at fling speed that's a visible hole in the chat.
    // Draw a cheap telegram-style skeleton bubble instead (one rounded rect, ~0.1ms): the slot
    // reads as "message loading", never as a gap. Replaced by real pixels the frame the bake lands.
    // Lightened versions of the bubble colors: the raw bubble tints are near-invisible against the
    // chat background (verified on-device), a skeleton must READ as a loading bubble.
    private static readonly SkiaSharp.SKPaint PaintSkeletonIn = new()
    {
        Color = new SkiaSharp.SKColor(0x2A, 0x3B, 0x4D), IsAntialias = true,
    };

    private static readonly SkiaSharp.SKPaint PaintSkeletonOut = new()
    {
        Color = new SkiaSharp.SKColor(0x35, 0x60, 0x8C), IsAntialias = true,
    };

    private static readonly SkiaSharp.SKPaint PaintSkeletonNeutral = new()
    {
        Color = new SkiaSharp.SKColor(0x2A, 0x38, 0x46), IsAntialias = true,
    };

    public override void DrawPlaceholder(DrawingContext ctx)
    {
        var dest = ctx.Destination;
        if (dest.Width <= 1 || dest.Height <= 1)
            return;

        // The placeholder can draw BEFORE the cell is bound (pool warm-up) — direction is unknowable
        // then, and guessing painted every skeleton as "incoming". Bound => real side+color; unbound
        // => neutral centered pill.
        var msg = BindingContext as ChatMessage;
        float scale = ctx.Scale;

        float padV = 3f * scale;
        float padH = 12f * scale;

        // By placeholder time the cell has already been MEASURED (measure precedes draw) — use the
        // real bubble size instead of guessing a fixed fraction, so the skeleton matches the bubble
        // that will replace it. Fallback heuristic only for a not-yet-measured bubble.
        float w, h;
        var bubblePx = _bubble?.MeasuredSize.Pixels ?? default;
        if (bubblePx.Width > 1 && bubblePx.Height > 1)
        {
            w = Math.Min(bubblePx.Width, dest.Width - padH * 2f);
            h = Math.Min(bubblePx.Height, dest.Height - padV * 2f);
        }
        else
        {
            w = Math.Min(MaxBubbleWidth * scale * 0.72f, dest.Width * 0.62f);
            h = Math.Max(dest.Height - padV * 2f, 10f * scale);
        }

        float left;
        SkiaSharp.SKPaint paint;
        if (msg == null)
        {
            left = dest.Left + (dest.Width - w) / 2f;
            paint = PaintSkeletonNeutral;
        }
        else if (msg.Outgoing)
        {
            // MIRRORED on purpose: the placeholder paints in the cache-record pass, where the cell's
            // own 180° rotation is NOT applied (only the scroll's flip is) — verified on-device:
            // aligning "End" here landed outgoing skeletons on the LEFT. So outgoing = Left in this
            // space => appears right on screen, like the real bubble.
            left = dest.Left + padH;
            paint = PaintSkeletonOut;
        }
        else
        {
            left = dest.Right - padH - w;
            paint = PaintSkeletonIn;
        }

        var rect = new SkiaSharp.SKRect(left, dest.Top + padV, left + w, dest.Top + padV + h);
        float r = 14f * scale;
        ctx.Context.Canvas.DrawRoundRect(rect, r, r, paint);
    }

    #endregion

    protected virtual void UpdateContent(ChatMessage msg)
    {
        _dayChip.IsVisible = msg.IsFirstDay;
        _day.Text = msg.DayDesc;

        _row.HorizontalOptions = msg.Outgoing ? LayoutOptions.End : LayoutOptions.Start;
        _bubble.BackgroundColor = msg.Outgoing ? ColorOutgoing : ColorIncoming;

        //tail only on the first message of a consecutive same-sender run (run resets on
        //a new day); follow-ups keep an invisible ghost tail so all bubbles stay aligned
        _tailIn.IsVisible = !msg.Outgoing;
        _tailIn.IsGhost = !msg.IsFirstOfGroup;
        _tailOut.IsVisible = msg.Outgoing;
        _tailOut.IsGhost = !msg.IsFirstOfGroup;

        _fileIcon.IsVisible = msg.Type == ChatMessageType.File; //paperclip glued left of the text

        //quoted message block (reset on recycle!)
        if (msg.ReplyTo != null)
        {
            _quoteBox.IsVisible = true;
            _quoteName.Text = msg.ReplyTo.AuthorName;
            _quoteText.Text = msg.ReplyTo.Text;
        }
        else
        {
            _quoteBox.IsVisible = false;
        }

        if (msg.Type == ChatMessageType.Image)
        {
            _banner.PreviewBase64 = msg.PreviewBase64; //instant inline preview while the url loads
            _banner.Source = msg.ImageUrl;
            _bubble.WidthRequest = MaxBubbleWidth; //banner needs a defined width
            _banner.IsVisible = true;
        }
        else
        {
            _banner.IsVisible = false;
            _banner.PreviewBase64 = null;
            _banner.Source = null;
            _bubble.WidthRequest = -1; //auto-width from text
        }

        _label.Text = msg.Type == ChatMessageType.Link
            ? $"{msg.Text}\n<{msg.LinkUrl}>"
            : msg.Text;

        _time.Text = msg.Time;
        _time.TextColor = msg.Outgoing ? ChatTheme.TimeOutgoing : ChatTheme.TimeIncoming;

        UpdateStatus(msg);
    }

    protected override void SetContent(object ctx)
    {
        if (ctx is not ChatMessage msg)
            return;

        UpdateContent(msg);
        MaybeHighlight(msg);
        ApplyUnreadHighlight(msg);
    }

    /// <summary>
    /// Plays (or skips) the jump-highlight based on the message's HighlightStamp. Called on bind
    /// (covers a freshly-created target cell after a rebase jump) and on live stamp change (covers
    /// a target already resident when the quote is tapped). A per-cell consumed-stamp + freshness
    /// guard stop a recycled cell from replaying a stale highlight.
    /// </summary>
    private void MaybeHighlight(ChatMessage msg)
    {
        var stamp = msg.HighlightStamp;
        if (stamp == _playedHighlightStamp)
            return; // already handled this request (or none) for the bound message

        _playedHighlightStamp = stamp;

        if (stamp == 0 || Environment.TickCount64 - stamp > HighlightFreshMs)
        {
            ResetHighlight(); // stale/none: recycle clean-up, never flash
            return;
        }

        PlayHighlight();
    }

    private void ResetHighlight()
    {
        _highlightCts?.Cancel();
        _highlight.IsVisible = false;
    }

    private async void PlayHighlight()
    {
        _highlightCts?.Cancel();
        _highlightCts?.Dispose();
        var cts = _highlightCts = new CancellationTokenSource();
        _flashActive = true;

        try
        {
            _highlight.Opacity = HighlightPeakOpacity; // instant tint on draw, Telegram-style
            _highlight.IsVisible = true;

            await Task.Delay(HighlightHoldStillMs, cts.Token);
            await _highlight.FadeToAsync(0, HighlightFadeMs, Easing.Linear, cts);

            if (!cts.IsCancellationRequested)
                _highlight.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
            // recycled / re-triggered mid-flash: the new request owns the overlay
        }
        finally
        {
            _flashActive = false;
            if (Context is ChatMessage msg)
                ApplyUnreadHighlight(msg); // restore/clear the steady unread tint the flash covered
        }
    }

    /// <summary>
    /// Steady (non-animated) unread tint, reusing the jump-flash band. No-op while a jump-flash
    /// animation is currently playing on it (that owns the overlay until it finishes).
    /// </summary>
    private void ApplyUnreadHighlight(ChatMessage msg)
    {
        if (_flashActive)
            return;

        _highlight.Opacity = HighlightPeakOpacity;
        _highlight.IsVisible = msg.IsUnread;
    }


    /// <summary>
    /// Delivery checkmarks for outgoing messages, like the original app:
    /// nothing while sending, one check when Sent, two when Delivered, blue when Read.
    /// Called from SetContent on (re)bind and live from ContextPropertyChanged
    /// while the mock api advances the stages.
    /// </summary>
    private void UpdateStatus(ChatMessage msg)
    {
        if (!msg.Outgoing)
        {
            _checkSent.IsVisible = false;
            _checkDelivered.IsVisible = false;
            return;
        }

        var tint = msg.Read ? ColorCheckRead : ColorCheck;
        _checkSent.TintColor = tint;
        _checkDelivered.TintColor = tint;

        _checkSent.IsVisible = msg.Sent; //nothing while still sending
        _checkDelivered.IsVisible = msg.Delivered; //second check completes the pair
    }

    protected override void ContextPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatMessage.Sent)
            or nameof(ChatMessage.Delivered)
            or nameof(ChatMessage.Read))
        {
            if (Context is ChatMessage msg)
            {
                UpdateStatus(msg);
            }
        }
        else if (e.PropertyName is nameof(ChatMessage.Text))
        {
            // Live text mutation (e.g. streaming "AI is thinking" mock): re-push only the label text
            // so the cell remeasures/reflows on the fly without a full rebind.
            if (Context is ChatMessage msg)
            {
                _label.Text = msg.Type == ChatMessageType.Link
                    ? $"{msg.Text}\n<{msg.LinkUrl}>"
                    : msg.Text;
            }
        }
        else if (e.PropertyName is nameof(ChatMessage.IsFirstDay))
        {
            SetContent(BindingContext);
            //if (Parent is SkiaLayout skia) skia.Invalidate(); // TEST: rely on framework OffsetOthers self-heal
        }
        else if (e.PropertyName is nameof(ChatMessage.HighlightStamp))
        {
            if (Context is ChatMessage msg)
                MaybeHighlight(msg);
        }
        else if (e.PropertyName is nameof(ChatMessage.IsUnread))
        {
            if (Context is ChatMessage msg)
                ApplyUnreadHighlight(msg);
        }

        base.ContextPropertyChanged(sender, e);
    }

//    public override bool NeedMeasure
//    {
//        get { return base.NeedMeasure; }
//        set
//        {
//#if DEBUG
//        Debug.WriteLine($"ChatCellNeedMeasure {value}");
//#endif
//            base.NeedMeasure = value;
//        }
//    }

}





#if DEBUG

#endif