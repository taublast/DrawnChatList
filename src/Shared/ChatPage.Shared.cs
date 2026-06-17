 using DrawnUi.Controls;
 using DrawnUi.Draw;
 using DrawnUi.Views;
 using System.Diagnostics;
 using System.Windows.Input;
 using AppoMobi.Specials;

 namespace DrawnChatList;

 
public partial class ChatPage  
{
    private const string SvgChevronDown =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M7.41 8.59 12 13.17l4.59-4.58L18 10l-6 6-6-6z'/></svg>";

    private const string SvgReply =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M10 9V5l-7 7 7 7v-4.1c5 0 8.5 1.6 11 5.1-1-5-4-10-11-11z'/></svg>";

    private const string SvgClose =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z'/></svg>";

    //inline image icon for the attach-photo button, no assets needed
    private const string SvgImage =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M21 19V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2zM8.5 13.5l2.5 3 3.5-4.5 4.5 6H5z'/></svg>";

    private const string SvgSend =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M2.01 21 23 12 2.01 3 2 10l15 2-15 2z'/></svg>";

    private static readonly string[] MockFiles =
    {
        "report_2026.pdf (1.2 MB)",
        "invoice_443.xlsx (88 KB)",
        "specs_v2.docx (340 KB)",
        "photos_backup.zip (12 MB)",
    };

    Canvas Canvas;
    public SkiaLayout ChatStack;
    public SkiaScroll MainScroll;
    SkiaEditor Editor;
    SkiaLayer FullscreenOverlay;
    SkiaImage FullscreenImage;
    SkiaShape BtnScrollToEnd;
    SkiaLayout ReplyPanel;
    SkiaLabel ReplyName;
    SkiaLabel ReplyText;
    SkiaLabel StatusLabel;

    public Canvas CreateCanvas()
    {
        
        return new Canvas
        {
            Tag = "ChatCanvas",
            Gestures = GesturesMode.Lock,
            RenderingMode = RenderingModeType.Accelerated,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            BackgroundColor = ChatTheme.Bg,
            Content = new SkiaLayer()
            {
                VerticalOptions = LayoutOptions.Fill,
                Children =
                {
                    new SkiaStack
                    {
                        Spacing = 0,
                        HorizontalOptions = LayoutOptions.Fill,
                        //VerticalOptions = LayoutOptions.Fill,
                        Children =
                        {
                            // NAVBAR: drawn replacement for the MAUI Shell bar (hidden in ctor) —
                            // animated gif avatar + bot name + live "typing…" status
                            new SkiaLayout
                            {
                                ZIndex=10,
                                UseCache = SkiaCacheType.GPU,
                                Type = LayoutType.Grid,
                                ColumnSpacing = 12,
                                Margin = new(0,0,0,0),//todo nav model
                                Padding = new Thickness(12, 8),
                                BackgroundColor = ChatTheme.BarBg,
                                HorizontalOptions = LayoutOptions.Fill,
                                Children =
                                {
                                    //avatar: gif clipped by a circle
                                    new SkiaShape
                                    {
                                        Type = ShapeType.Circle,
                                        WidthRequest = 40,
                                        LockRatio = 1,
                                        BackgroundColor = ChatTheme.InputBg,
                                        VerticalOptions = LayoutOptions.Center,
                                        Children =
                                        {
                                            new SkiaGif
                                            {
                                                Source = "Images/banana.gif",
                                                Repeat = -1,
                                                HorizontalOptions = LayoutOptions.Fill,
                                                VerticalOptions = LayoutOptions.Fill,
                                            },
                                        }
                                    }.WithColumn(0),

                                    new SkiaStack
                                    {
                                        Spacing = 1,
                                        VerticalOptions = LayoutOptions.Center,
                                        Children =
                                        {
                                            new SkiaLabel
                                            {
                                                UseCache = SkiaCacheType.Operations,
                                                Text = "Banana Bot",
                                                FontSize = 15,
                                                TextColor = Colors.White,
                                            },

                                            new SkiaLabel
                                            {
                                                UseCache = SkiaCacheType.Operations,
                                                Text = "online",
                                                FontSize = 11,
                                                TextColor = Color.FromArgb("#88FFFFFF"),
                                            }.Assign(out StatusLabel),
                                        }
                                    }.WithColumn(1),
                                }
                            }.WithColumnDefinitions("40,*"),

                            new SkiaLayer()
                            {
                                VerticalOptions = LayoutOptions.Fill,
                                Children =
                                {
                                    // MESSAGES
                                    new SkiaScroll
                                        {
                                            Orientation = ScrollOrientation.Vertical,
                                            ResetScrollPositionOnContentSizeChanged = false,

                                            // Inverted chat (original app trick): content rotated 180 so the
                                            // list start (= newest message) sits at the visual bottom; cells
                                            // rotate themselves back upright (ChatCell.Rotation = 180).
                                            Rotation = 180,
                                            ReverseGestures = true,
                                            TrackIndexPosition = RelativePositionType.Start,

                                            // bottom trigger = visually scrolling UP = load history
                                            LoadMoreCommand = new Command(LoadOlder),
                                            LoadMoreOffset = 800,

                                            // top trigger = visually scrolling DOWN = reload trimmed newer part
                                            LoadMoreTopCommand = new Command(LoadNewer),
                                            LoadMoreTopOffset = 800,

                                            HorizontalOptions = LayoutOptions.Fill,
                                            VerticalOptions = LayoutOptions.Fill,

                                            Content = new ChatMessagesStack
                                            {
                                                BackgroundMeasurementBatchSize = LoadBatch,
                                                VirtualisationInflatedRatio = 1.0,
                                                ReserveTemplates = LoadBatch,
                                                ItemTemplateType = typeof(ChatCell),
                                                ItemsSource = _items,
                                                RecyclingTemplate = RecyclingTemplate.Enabled,
                                                MeasureItemsStrategy = MeasuringStrategy.MeasureVisible,
                                                Spacing = 4,
                                                Padding = new Thickness(0, 8),
                                            }.Assign(out ChatStack),

                                        }.Assign(out MainScroll)
                                        .Observe(this,
                                            (me, s) =>
                                            {
                                                if (s == nameof(this.KeyboardSize))
                                                {
                                                    me.AdaptToKeyboardFor = Canvas.FocusedChild as SkiaControl;
                                                    me.AdaptToKeyboardSize = KeyboardSize;
                                                }
                                            }),

                                    // ATTACHMENT-REPLY WHILE TYPING: quote panel above the send bar.
                                    // Improvement over the original: tap the panel to JUMP to the quoted
                                    // message, the X cancels (original canceled on any tap).
                                    new SkiaLayout
                                        {
                                            Tag = "ReplyPanel",
                                            IsVisible = false,
                                            Type = LayoutType.Grid,
                                            UseCache = SkiaCacheType.GPU,
                                            ColumnSpacing = 10,
                                            Padding = new Thickness(12, 8),
                                            BackgroundColor = ChatTheme.BarBg,
                                            VerticalOptions = LayoutOptions.End,
                                            HorizontalOptions = LayoutOptions.Fill,
                                            Children =
                                            {
                                                new SkiaSvg
                                                {
                                                    UseCache = SkiaCacheType.Operations,
                                                    SvgString = SvgReply,
                                                    TintColor = ChatTheme.IconMuted,
                                                    HeightRequest = 18,
                                                    LockRatio = 1,
                                                    HorizontalOptions = LayoutOptions.Center,
                                                    VerticalOptions = LayoutOptions.Center,
                                                }.WithColumn(0),

                                                new SkiaStack
                                                {
                                                    Spacing = 1,
                                                    VerticalOptions = LayoutOptions.Center,
                                                    Children =
                                                    {
                                                        new SkiaLabel
                                                        {
                                                            FontSize = 12,
                                                            TextColor = ChatTheme.AccentBright,
                                                            MaxLines = 1,
                                                            LineBreakMode = LineBreakMode.TailTruncation,
                                                        }.Assign(out ReplyName),

                                                        new SkiaLabel
                                                        {
                                                            FontSize = 13,
                                                            TextColor = Color.FromArgb("#AAFFFFFF"),
                                                            MaxLines = 1,
                                                            LineBreakMode = LineBreakMode.TailTruncation,
                                                        }.Assign(out ReplyText),
                                                    }
                                                }.WithColumn(1),

                                                //X cancels the reply
                                                new SkiaLayer
                                                {
                                                    VerticalOptions = LayoutOptions.Fill,
                                                    HorizontalOptions = LayoutOptions.Fill,
                                                    Children =
                                                    {
                                                        new SkiaSvg
                                                        {
                                                            UseCache = SkiaCacheType.Operations,
                                                            SvgString = SvgClose,
                                                            TintColor = Color.FromArgb("#88FFFFFF"),
                                                            HeightRequest = 16,
                                                            LockRatio = 1,
                                                            HorizontalOptions = LayoutOptions.Center,
                                                            VerticalOptions = LayoutOptions.Center,
                                                        },
                                                    }
                                                }.OnTapped(me => CancelReply()).WithColumn(2),
                                            }
                                        }
                                        .WithColumnDefinitions("24,*,40")
                                        .Assign(out ReplyPanel)
                                        .OnTapped(me =>
                                        {
                                            if (_replyTo != null)
                                                ScrollToMessage(_replyTo);
                                        }),
                                }
                            },

                            // SEND BAR: SkiaEditor is a totally drawn control
                            new SkiaLayout
                            {
                                UseCache = SkiaCacheType.Operations,
                                Type = LayoutType.Grid,
                                ColumnSpacing = 8,
                                Padding = new Thickness(8),
                                BackgroundColor = ChatTheme.BarBg,
                                HorizontalOptions = LayoutOptions.Fill,
                                Children =
                                {
                                    // BTN ATTACH IMAGE: mock "photo picker"
                                    new SkiaLayer
                                    {
                                        VerticalOptions = LayoutOptions.Fill,
                                        HorizontalOptions = LayoutOptions.Fill,
                                        UseCache = SkiaCacheType.GPU,
                                        Children =
                                        {
                                            new SkiaSvg
                                            {
                                                UseCache = SkiaCacheType.Operations,
                                                SvgString = SvgImage,
                                                TintColor = ChatTheme.IconMuted,
                                                HeightRequest = 20,
                                                LockRatio = 1,
                                                VerticalOptions = LayoutOptions.Center,
                                                HorizontalOptions = LayoutOptions.Center,
                                            },
                                        }
                                    }.OnTapped(me => SendImage()).WithColumn(0),

                                    // BTN ATTACH FILE: mock "file picker"
                                    new SkiaLayer
                                    {
                                        UseCache = SkiaCacheType.GPU,
                                        VerticalOptions = LayoutOptions.Fill,
                                        HorizontalOptions = LayoutOptions.Fill,
                                        Children =
                                        {
                                            new SkiaSvg
                                            {
                                                UseCache = SkiaCacheType.Operations,
                                                SvgString = ChatCell.SvgAttachment,
                                                TintColor = ChatTheme.IconMuted,
                                                HeightRequest = 20,
                                                LockRatio = 1,
                                                VerticalOptions = LayoutOptions.Center,
                                                HorizontalOptions = LayoutOptions.Center,
                                            },
                                        }
                                    }.OnTapped(me => SendFile()).WithColumn(1),

                                    new SkiaEditor
                                    {
                                        UseCache = SkiaCacheType.Operations,
                                        HorizontalOptions = LayoutOptions.Fill,
                                        VerticalOptions = LayoutOptions.Center,
                                        CornerRadius = 18,
                                        BackgroundColor = ChatTheme.InputBg,
                                        Padding = new Thickness(12, 10),
                                        FontSize = 15,
                                        TextColor = Colors.White,
                                        CursorColor = Colors.Cyan,
                                        PlaceholderText = "Write a message…",
                                        PlaceholderColor = ChatTheme.IconMuted,
                                        MaxLines = 3,
                                        AutoHeight =
                                            true, //will auto-resize when we type more lines, up to MaxLines
                                        ReturnType = ReturnType.Send,
                                        CommandOnSubmit = new Command(SendMessage),
                                    }.Assign(out Editor).WithColumn(2),

                                    // SEND: telegram-style round button with a paper plane
                                    new SkiaShape
                                    {
                                        UseCache = SkiaCacheType.GPU,
                                        Type = ShapeType.Circle,
                                        WidthRequest = 42,
                                        LockRatio = 1,
                                        BackgroundColor = ChatTheme.Accent,
                                        VerticalOptions = LayoutOptions.Center,
                                        Children =
                                        {
                                            new SkiaSvg
                                            {
                                                Left = -1,
                                                UseCache = SkiaCacheType.Operations,
                                                SvgString = SvgSend,
                                                TintColor = Colors.White,
                                                HeightRequest = 19,
                                                LockRatio = 1,
                                                Margin = new Thickness(3, 0, 0, 0), //optical centering
                                                HorizontalOptions = LayoutOptions.Center,
                                                VerticalOptions = LayoutOptions.Center,
                                            },
                                        }
                                    }.OnTapped(me => SendMessage()).WithColumn(3),
                                }
                            }.WithColumnDefinitions("32,32,*,44"),


                            // KEYBOARD SPACER (mobile): pushes the typing bar above the soft keyboard
                            new SkiaControl
                            {
                                UseCache = SkiaCacheType.Operations,
                                HeightRequest = 0,
                                HorizontalOptions = LayoutOptions.Fill,
                            }.Observe(this, (me, prop) =>
                            {
                                if (prop == nameof(KeyboardSize))
                                {
                                    me.HeightRequest = KeyboardSize;
                                }
                            }),
                        }
                    },

                    // DEBUG OVERLAY: shows resident items window for the memory-cap demo
                    new SkiaLabel
                    {
                        UseCache = SkiaCacheType.Operations,
                        Margin = new(16, 0, 16, 100),
                        Padding = 2,
                        BackgroundColor = Color.Parse("#AA000000"),
                        HorizontalOptions = LayoutOptions.Start,
                        InputTransparent = true,
                        TextColor = Colors.LawnGreen,
                        VerticalOptions = LayoutOptions.Center,
                        Rotation = -20,
                        ZIndex = 100,
                    }.ObserveProperty(() => ChatStack, nameof(SkiaLayout.DebugString),
                        me => { me.Text = ChatStack.DebugString; }),

                    // SCROLL TO LAST MESSAGE: appears after scrolling 100+ pts into history
                    new SkiaShape
                    {
                        Type = ShapeType.Circle,
                        UseCache = SkiaCacheType.GPU,
                        IsVisible = false,
                        Opacity = 0,
                        WidthRequest = 46,
                        LockRatio = 1,
                        BackgroundColor = Color.Parse("#F217212B"),
                        HorizontalOptions = LayoutOptions.End,
                        VerticalOptions = LayoutOptions.End,
                        Margin = new Thickness(0, 0, 10, 120),
                        ZIndex = 90,
                        Children =
                        {
                            new SkiaSvg
                            {
                                UseCache = SkiaCacheType.Operations,
                                SvgString = SvgChevronDown,
                                TintColor = Color.FromArgb("#AAFFFFFF"),
                                HeightRequest = 24,
                                LockRatio = 1,
                                HorizontalOptions = LayoutOptions.Center,
                                VerticalOptions = LayoutOptions.Center,
                            },
                        }
                    }.Assign(out BtnScrollToEnd).OnTapped(me => ScrollToNewest(true)),

                    // FULLSCREEN IMAGE VIEWER POPUP: hidden overlay above everything, tap to close
                    // (the original app's GalleryPopup pattern, single image instead of carousel)
                    new SkiaLayer
                    {
                        IsVisible = false,
                        UseCache = SkiaCacheType.Operations,
                        ZIndex = 200,
                        BlockGesturesBelow = true,
                        BackgroundColor = Color.Parse("#EE000000"),
                        HorizontalOptions = LayoutOptions.Fill,
                        VerticalOptions = LayoutOptions.Fill,
                        Children =
                        {
                            new SkiaImage
                            {
                                UseCache = SkiaCacheType.GPU,
                                Aspect = TransformAspect.AspectFitFill,
                                EraseChangedContent = false, //keep small image shown while hi-res loads
                                LoadSourceOnFirstDraw = false,
                                HorizontalOptions = LayoutOptions.Fill,
                                VerticalOptions = LayoutOptions.Center,
                            }.Assign(out FullscreenImage).Adapt(me =>
                            {
                                //once the cached small image displayed, upgrade to hi-res
                                me.Success += (s, e) =>
                                {
                                    var upgrade = _fullscreenUpgradeUrl;
                                    _fullscreenUpgradeUrl = null;
                                    if (upgrade != null)
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => me.Source = upgrade);
                                    }
                                };
                            }),

                            new SkiaLabel
                            {
                                Text = "Tap to close",
                                FontSize = 12,
                                TextColor = Color.FromArgb("#88FFFFFF"),
                                HorizontalOptions = LayoutOptions.Center,
                                VerticalOptions = LayoutOptions.End,
                                Margin = new Thickness(0, 0, 0, 70),
                                InputTransparent = true,
                            },
                        }
                    }.OnTapped(me => HideImageFullscreen()).Assign(out FullscreenOverlay),
                }
            },
        };
    }

    public void AttachCanvas()
    {
        // Cells reach the viewer via Parent.BindingContext (original app pattern).
        ChatStack.BindingContext = this;

        // ViewportOffsetY does not notify, the Scrolled event is the offset signal.
        // No unsubscribe needed: the canvas with this scroll is disposed on every Build.
        MainScroll.Scrolled += OnChatScrolled;
        _scrollDownShown = false;
    }

    private async void HideImageFullscreen()
    {
        if (_hidingOverlay)
            return;

        _hidingOverlay = true;
        _fullscreenUpgradeUrl = null;

        await Task.WhenAll(
            FullscreenOverlay.FadeToAsync(0, 160),
            FullscreenOverlay.ScaleToAsync(0.92, 0.92, 160));

        FullscreenOverlay.IsVisible = false;
        FullscreenImage.Source = null;
        _hidingOverlay = false;
    }

    /// <summary>
    /// Fullscreen image viewer (like the original app's GalleryPopup, single image, tap closes).
    /// The bubble's small image is already in the image manager cache — it displays fullscreen
    /// instantly as the preview, then the Success handler upgrades to the hi-res variant
    /// (EraseChangedContent=false keeps the small one on screen while the big one downloads).
    /// </summary>
    public void ShowImageFullscreen(ChatMessage msg)
    {
        // same picsum seed at higher resolution = same photo, crisper for fullscreen
        _fullscreenUpgradeUrl = msg.ImageUrl.Replace("/400/240", "/800/480");
        FullscreenImage.Source = msg.ImageUrl; //cache hit from the bubble, instant

        _hidingOverlay = false;
        FullscreenOverlay.Opacity = 0;
        FullscreenOverlay.ScaleX = 0.92;
        FullscreenOverlay.ScaleY = 0.92;
        FullscreenOverlay.IsVisible = true;
        _ = FullscreenOverlay.FadeToAsync(1, 200);
        _ = FullscreenOverlay.ScaleToAsync(1, 1, 200);
    }

    private void SendMessage()
    {
        var text = Editor.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        Editor.Text = string.Empty;

        SendOutgoing(CreateTailMessage(text, outgoing: true));
    }

    /// <summary>
    /// Mock "image picker": sends an image message with an instant base64 preview and a remote
    /// url, editor text becomes the caption. A real app would plug a file/photo picker here.
    /// </summary>
    private void SendImage()
    {
        var caption = Editor.Text?.Trim();
        Editor.Text = string.Empty;

        var msg = CreateTailMessage(string.IsNullOrEmpty(caption) ? "Photo" : caption,
            outgoing: true, ChatMessageType.Image);
        msg.ImageUrl = $"https://picsum.photos/seed/sent{_all.Count}/400/240";
        msg.PreviewBase64 = ChatMessage.MockImagePreview;

        SendOutgoing(msg);
    }



    private void OnChatScrolled(object sender, ScaledPoint e)
    {
        var away = Math.Abs(e.Units.Y);

        // While returning to the newest message programmatically (send/FAB), the head-insert
        // viewport pinning and the scroll animation produce transient offsets that would
        // flash the FAB (a tall image bubble shifts by more than the threshold). Ignore them
        // until we arrive; the timeout covers an animation interrupted by the user.
        if (Environment.TickCount64 < _suppressFabUntilMs)
        {
            if (away < 12)
                _suppressFabUntilMs = 0; //arrived
            ShowScrollDownButton(false);
            return;
        }

        // newest message lives at offset 0 (top of the inverted scroll):
        // 100+ pts away into history -> offer the way back
        _wantScrollDown = away > 100;
        ShowScrollDownButton(_wantScrollDown);
    }

    private async void ShowScrollDownButton(bool show)
    {
        // never compete with the reply panel for the same screen area
        if (show && ReplyPanel != null && ReplyPanel.IsVisible)
            show = false;

        if (show == _scrollDownShown || BtnScrollToEnd == null)
            return;

        _scrollDownShown = show;

        if (show)
        {
            BtnScrollToEnd.IsVisible = true;
            await BtnScrollToEnd.FadeToAsync(1, 150);
        }
        else
        {
            await BtnScrollToEnd.FadeToAsync(0, 150);
            if (!_scrollDownShown) //wasn't re-shown while fading out
                BtnScrollToEnd.IsVisible = false;
        }
    }

    /// <summary>
    /// Shared outgoing flow for text and attachments: shows in the list INSTANTLY,
    /// checkmarks catch up as the mock backend confirms stages.
    /// </summary>
    private void SendOutgoing(ChatMessage msg)
    {
        if (_replyTo != null)
        {
            msg.ReplyTo = _replyTo; //quote travels with the message, any type
            CancelReply();
        }

        if (!InsertNewest(msg))
        {
            // Deep in history with the newest end trimmed: rebase the window back to the present.
            _windowEnd = _all.Count;
            _windowStart = Math.Max(0, _windowEnd - LoadBatch);
            _items.ReplaceRangeReset(ReversedRange(_windowStart, _windowEnd - _windowStart));
            // Reset scrolls to content start = newest, nothing more to do.
        }

        // Sending always returns the user to their message (original app behavior).
        ScrollToNewest(true);

        _api.Send(msg); // mock backend advances Sent -> Delivered -> Read, then replies
    }


    /// <summary>
    /// Mock "file picker": sends a file attachment message (paperclip icon + file name).
    /// </summary>
    private void SendFile()
    {
        var msg = CreateTailMessage(MockFiles[_all.Count % MockFiles.Length],
            outgoing: true, ChatMessageType.File);

        SendOutgoing(msg);
    }




    private void ReceiveMessage(string text)
    {
        // Follow the incoming message only when the user is already at the newest messages,
        // don't yank them out of reading history (original app behavior).
        bool atNewest = ChatStack.FirstVisibleIndex <= 1;

        if (InsertNewest(CreateTailMessage(text, outgoing: false)) && atNewest)
        {
            ScrollToNewest(true);
        }
    }

    /// <summary>
    /// _all[from + count - 1] down to _all[from]: a window slice in the inverted (newest-first) order.
    /// </summary>
    private List<ChatMessage> ReversedRange(int from, int count)
    {
        var batch = new List<ChatMessage>(count);
        for (int i = from + count - 1; i >= from; i--)
            batch.Add(_all[i]);
        return batch;
    }

    /// <summary>
    /// History: the scroll's plain bottom LoadMore = visually scrolling UP in the inverted chat.
    /// Older messages get APPENDED at the list end. Memory cap trims the newest end (list head) first.
    /// </summary>
    private void LoadOlder()
    {
        if (_windowStart <= 0)
            return;

        int n = Math.Min(LoadBatch, _windowStart);

        if (LimitMemoryWindow)
        {
            int over = _items.Count + n - MaxItemsInMemory;
            if (over > 0)
            {
                Debug.WriteLine($"[CHAT] TrimNewest {_windowEnd - over}..{_windowEnd - 1}");
                _items.RemoveRange(0, over); // list head = newest
                _windowEnd -= over;
            }
        }

        _windowStart -= n;
        Debug.WriteLine($"[CHAT] LoadOlder {_windowStart}..{_windowStart + n - 1}");
        var loadedData = ReversedRange(_windowStart, n);

        //todo start preloading preview images in background
        _preloadCancellation?.Cancel();
        _preloadCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = PreloadImages(loadedData, _preloadCancellation.Token);

        _items.AddRange(loadedData); // one notification for the whole batch
    }

    private CancellationTokenSource _preloadCancellation;

    private async Task PreloadImages(List<ChatMessage> items, CancellationToken cancellationToken)
    {
        try
        {
            var imageUrls = new List<string>();

            // Add content images
            imageUrls.AddRange(items
                .Where(x => !string.IsNullOrEmpty(x.ImageUrl))
                .Select(x => x.ImageUrl));

            // Add avatar images
            //imageUrls.AddRange(items
            //    .Where(x => !string.IsNullOrEmpty(x.AuthorAvatarUrl))
            //    .Select(x => x.AuthorAvatarUrl));

            // Use DrawnUI's image manager for efficient preloading
            await SkiaImageManager.Instance.PreloadImages(imageUrls, _preloadCancellation);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelled
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error preloading images: {ex.Message}");
        }
    }
    /// <summary>
    /// Back towards the present: the scroll's top LoadMore = visually scrolling DOWN, needed only
    /// after the memory cap trimmed the newest end away while reading history. Newer messages get
    /// head-inserted (framework keeps the viewport pinned). Memory cap trims the oldest end first.
    /// </summary>
    private void LoadNewer()
    {
        if (_windowEnd >= _all.Count)
            return;

        int n = Math.Min(LoadBatch, _all.Count - _windowEnd);

        if (LimitMemoryWindow)
        {
            int over = _items.Count + n - MaxItemsInMemory;
            if (over > 0)
            {
                Debug.WriteLine($"[CHAT] TrimOldest {_windowStart}..{_windowStart + over - 1}");
                _items.RemoveRange(_items.Count - over, over); // list tail = oldest
                _windowStart += over;
            }
        }

        Debug.WriteLine($"[CHAT] LoadNewer {_windowEnd}..{_windowEnd + n - 1}");

        var loadedData = ReversedRange(_windowEnd, n);

        //todo start preloading preview images in background
        // Cancel previous preloading
        _preloadCancellation?.Cancel();
        _preloadCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = PreloadImages(loadedData, _preloadCancellation.Token);

        _items.InsertRange(0, loadedData); // one notification for the whole batch
        _windowEnd += n;
    }

    /// <summary>
    /// Builds a new tail message with correct day/group flags relative to the current last one.
    /// </summary>
    private ChatMessage CreateTailMessage(string text, bool outgoing, ChatMessageType type = ChatMessageType.Text)
    {
        var prev = _all[^1];
        bool isFirstDay = prev.DayDesc != "Today";

        return new ChatMessage
        {
            Index = _all.Count,
            Outgoing = outgoing,
            Type = type,
            Text = text,
            Time = DateTime.Now.ToString("H:mm"),
            DayDesc = "Today",
            IsFirstDay = isFirstDay,
            // first of a consecutive same-sender run; a new day starts a new run
            IsFirstOfGroup = isFirstDay || prev.Outgoing != outgoing,
        };
    }

    /// <summary>
    /// Adds a message at the dataset tail (the logical end of _all) and INSERTS it at index 0
    /// of the inverted list — the new message shows at the visual bottom instantly.
    /// Returns false when the resident window is detached from the present (user deep in history
    /// and the newest end was trimmed) — the message then lives only in _all until they return.
    /// </summary>
    private bool InsertNewest(ChatMessage msg)
    {
        bool windowAtPresent = _windowEnd == _all.Count;
        _all.Add(msg);

        if (!windowAtPresent)
            return false;

        if (LimitMemoryWindow)
        {
            int over = _items.Count + 1 - MaxItemsInMemory;
            if (over > 0)
            {
                _items.RemoveRange(_items.Count - over, over); // trim oldest
                _windowStart += over;
            }
        }

        _windowEnd++;
        _items.Insert(0, msg);
        return true;
    }

    private void ScrollToNewest(bool animate)
    {
        _wantScrollDown = false;
        _suppressFabUntilMs = Environment.TickCount64 + 1200;
        ShowScrollDownButton(false);

        if (_windowEnd != _all.Count)
        {
            // Window detached from the present (after a quote-jump into trimmed history):
            // rebase back; Reset scrolls to content start = newest.
            _windowEnd = _all.Count;
            _windowStart = Math.Max(0, _windowEnd - LoadBatch);
            _items.ReplaceRangeReset(ReversedRange(_windowStart, _windowEnd - _windowStart));
            return; //Reset scrolls to content start = newest; FAB already hidden above
        }

        // top of the inverted scroll = index 0 = newest = visual bottom
        MainScroll.ScrollToIndex(0, animate, RelativePositionType.Start, true);
    }

    /// <summary>
    /// Long-press on a bubble: quote it in the typing bar.
    /// </summary>
    public void ReplyToMessage(ChatMessage msg)
    {
        _replyTo = msg;
        ReplyName.Text = msg.AuthorName;
        ReplyText.Text = msg.Text;

        if (!ReplyPanel.IsVisible)
        {
            ReplyPanel.Opacity = 0;
            ReplyPanel.IsVisible = true;
            _ = ReplyPanel.FadeToAsync(1, 150);
        }

        ShowScrollDownButton(false); //the panel owns that screen area now

        //Editor.IsFocused = true; //original app behavior: start typing the reply right away

        Editor.SetFocus(true);
    }

    private async void CancelReply()
    {
        _replyTo = null;

        if (ReplyPanel.IsVisible)
        {
            await ReplyPanel.FadeToAsync(0, 120);
            if (_replyTo == null) //not re-opened while fading
                ReplyPanel.IsVisible = false;
        }

        // restore the scroll-down button if the user is still away from the newest messages
        ShowScrollDownButton(_wantScrollDown || _windowEnd != _all.Count);
    }

    /// <summary>
    /// Jump to a quoted message. Inside the resident window: just scroll. Outside (trimmed away):
    /// rebase the window so the target is the newest resident item — Reset shows it instantly at
    /// the visual bottom, backward loads then fill older context, LoadNewer fills back to present.
    /// </summary>
    public void ScrollToMessage(ChatMessage msg)
    {
        int local = _windowEnd - 1 - msg.Index;
        if (local >= 0 && local < _items.Count)
        {
            MainScroll.ScrollToIndex(local, true, RelativePositionType.Start, true);
            return;
        }

        _windowEnd = msg.Index + 1;
        _windowStart = Math.Max(0, _windowEnd - LoadBatch);
        Debug.WriteLine($"[CHAT] quote-jump to {msg.Index}, window -> [{_windowStart}..{_windowEnd})");
        _items.ReplaceRangeReset(ReversedRange(_windowStart, _windowEnd - _windowStart));

        // We are now deep in history: offer the way back (offset alone is 0 here, won't trigger it).
        // If the reply panel is open the button stays suppressed and reappears when it closes.
        _wantScrollDown = true;
        ShowScrollDownButton(true);
    }

    private void SetBotTyping(bool typing)
    {
        if (StatusLabel == null)
            return;

        StatusLabel.Text = typing ? "Banana is typing…" : "online";
        StatusLabel.TextColor = typing ? ChatTheme.AccentBright : ChatTheme.IconMuted;
    }

    // Windowed source, INVERTED: _items[i] == _all[_windowEnd - 1 - i].
    // The window covers _all[_windowStart .. _windowEnd), _items shows it newest-first.
    private readonly ObservableRangeCollection<ChatMessage> _items = new();
    private readonly List<ChatMessage> _all = new(); // stands in for a SQLite-paged source
    private int _windowStart;
    private int _windowEnd;

    private const int TotalItems = 322;
    private const int LoadBatch = 50;

    // Memory cap: trim BEFORE loading, opposite end (see DevPage for the full contract).
    private const bool LimitMemoryWindow = true;
    private const int MaxItemsInMemory = 250;

    private bool _scrollDownShown;
    private bool _wantScrollDown; //last computed condition, restored when the reply panel closes
    private long _suppressFabUntilMs; //ignore transient offsets while programmatically returning to newest

    private ChatMessage _replyTo;
    private string _fullscreenUpgradeUrl; //hi-res to load after the cached small one displayed
    private bool _hidingOverlay;

    private readonly MockChatApi _api = new();

}