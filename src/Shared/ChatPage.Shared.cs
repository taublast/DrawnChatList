using DrawnUi.Controls;
using DrawnUi.Draw;
using DrawnUi.Views;
using System.Diagnostics;
using System.Windows.Input;
using AppoMobi.Specials;
using DrawnUi;

namespace DrawnChatList;

public partial class ChatPage
{
    private const int TotalItems = 322;
    private const int LoadBatch = 25;

    // Memory cap: trim BEFORE loading, opposite end (see DevPage for the full contract).
    private const bool LimitMemoryWindow = true;
    private const int MaxItemsInMemory = 200;

    // Windowed source, INVERTED (Items[i] == All[WindowEnd - 1 - i]). All window/paging/jump logic
    // lives in the reusable WindowedSource<T>; the page only owns UI (cells, FABs, highlight, theme).
    private readonly WindowedSource<ChatMessage> _window = new(LoadBatch, MaxItemsInMemory, LimitMemoryWindow);

    // Simulated remote API (the message history lives here, not in memory locally).
    private const int RemoteLatencyMs = 10;
    private MockChatService _service;

    // Thin passthroughs so the existing read-sites (and the platform debug probes) keep compiling.
    private ObservableRangeCollection<ChatMessage> _items => _window.Items;
    private int _windowStart => _window.WindowStart;
    private int _windowEnd => _window.WindowEnd;

    private bool _scrollDownShown;
    private bool _wantScrollDown; //last computed condition, restored when the reply panel closes
    private long _suppressFabUntilMs; //ignore transient offsets while programmatically returning to newest

    private ChatMessage _replyTo;
    private string _fullscreenUpgradeUrl; //hi-res to load after the cached small one displayed
    private bool _hidingOverlay;

    private readonly MockChatApi _api = new();

    private static readonly string[] MockFiles =
    {
        "report_2026.pdf (1.2 MB)",
        "invoice_443.xlsx (88 KB)",
        "specs_v2.docx (340 KB)",
        "photos_backup.zip (12 MB)",
    };

    Canvas Canvas;
    public AppMessagesStack ChatStack;
    public SkiaScroll MainScroll;
    SkiaEditor Editor;
    SkiaLayer FullscreenOverlay;
    SkiaImage FullscreenImage;
    SkiaLayer DevPickerOverlay;
    SkiaStack DevPickerSheet;

    SkiaShape BtnScrollToEnd;

    // ONE LoadMore spinner (SkiaLottie), repositioned per operation: top = history/LoadOlder,
    // bottom = newer/LoadNewer, center = long-jump window-replace. Driven by WindowedSource.LoadingChanged.
    SkiaShape Spinner;
    SkiaLottie SpinnerLoader;
#if DEBUG
    SkiaShape BtnScrollToOldest; //debug helper: jump to the very first (oldest) message
    private bool _scrollToOldestShown;
#endif
    SkiaLayout ReplyPanel;
    SkiaLabel ReplyName;
    SkiaLabel ReplyText;
    SkiaLabel StatusLabel;

    public SkiaControl CreateCanvasContent()
    {
        return new SkiaLayer()
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
                            ZIndex = 10,
                            UseCache = SkiaCacheType.GPU,
                            Type = LayoutType.Grid,
                            ColumnSpacing = 12,
                            Margin = new(0, 0, 0, 0), //todo nav model
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

                                // DEV TOOLS: opens the dev-options picker (mock pickers / debug actions)
                                new SkiaLayer
                                {
                                    UseCache = SkiaCacheType.GPU,
                                    WidthRequest = 40,
                                    HorizontalOptions = LayoutOptions.Fill,
                                    VerticalOptions = LayoutOptions.Fill,
                                    Children =
                                    {
                                        new SkiaSvg
                                        {
                                            UseCache = SkiaCacheType.Operations,
                                            SvgString = SvgTools,
                                            TintColor = ChatTheme.IconMuted,
                                            HeightRequest = 22,
                                            LockRatio = 1,
                                            HorizontalOptions = LayoutOptions.Center,
                                            VerticalOptions = LayoutOptions.Center,
                                        },
                                    }
                                }.OnTapped(me => ShowDevPicker()).WithColumn(2),
                            }
                        }.WithColumnDefinitions("40,*,40"),

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
                                        LoadMoreCommand = new Command(_window.LoadOlder),
                                        LoadMoreOffset = 800,

                                        // top trigger = visually scrolling DOWN = reload trimmed newer part
                                        LoadMoreTopCommand = new Command(_window.LoadNewer),
                                        LoadMoreTopOffset = 800,

                                        HorizontalOptions = LayoutOptions.Fill,
                                        VerticalOptions = LayoutOptions.Fill,

                                        Content = new AppMessagesStack
                                        {
                                            BackgroundMeasurementBatchSize = LoadBatch,
                                            VirtualisationInflatedRatio = 1.5,
                                            ReserveTemplates = LoadBatch * 2,
                                            ItemTemplatePoolSize = MaxItemsInMemory + LoadBatch + 5, //prefill
                                            ItemTemplateType = typeof(ChatCell),
                                            ItemsSource = _items,
                                            RecyclingTemplate = RecyclingTemplate.Disabled,
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
                                    }
                                    .OnFocusChanged((me, focused) =>
                                    {
                                        ScrollToNewest(false);
                                    })
                                    .Assign(out Editor)
                                    .WithColumn(2),

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
                            //BackgroundColor = Colors.Red,
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

#if DEBUG
                new SkiaLabelFps()
                {
                    Margin = new(0, 0, 4, 54),
                    VerticalOptions = LayoutOptions.End,
                    HorizontalOptions = LayoutOptions.End,
                    Rotation = -45,
                    BackgroundColor = Colors.DarkRed,
                    TextColor = Colors.White,
                    ZIndex = 110,
                },
#endif

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

                // LOADMORE SPINNER (single SkiaLottie, SkiaActivityIndicator-style). One instance, moved by
                // OnWindowLoadingChanged: top = history, bottom = newer, center = long jump.
                new SkiaShape
                {
                    Type = ShapeType.Circle,
                    IsVisible = false,
                    WidthRequest = 40,
                    LockRatio = 1,
                    BackgroundColor = Color.Parse("#F217212B"),
                    Padding = 6,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    ZIndex = 90,
                    Children =
                    {
                        new SkiaLottie
                        {
                            AutoPlay = false,
                            Repeat = -1,
                            Source = "Lottie/iosloader.json",
                            ColorTint = ChatTheme.AccentBright,
                            LockRatio = 1,
                            HorizontalOptions = LayoutOptions.Fill,
                            VerticalOptions = LayoutOptions.Fill,
                        }.Assign(out SpinnerLoader),
                    }
                }.Assign(out Spinner),

#if DEBUG
                // SCROLL TO OLDEST (debug only): same spot as the scroll-to-latest FAB, shown while
                // at the newest message. Arrow points UP (chevron rotated 180).
                new SkiaShape
                {
                    Type = ShapeType.Circle,
                    UseCache = SkiaCacheType.GPU,
                    IsVisible = true,
                    Opacity = 1,
                    WidthRequest = 46,
                    LockRatio = 1,
                    BackgroundColor = Color.Parse("#F217212B"),
                    HorizontalOptions = LayoutOptions.Start,
                    VerticalOptions = LayoutOptions.End,
                    Margin = new Thickness(0, 0, 10, 120),
                    ZIndex = 90,
                    Children =
                    {
                        new SkiaSvg
                        {
                            UseCache = SkiaCacheType.Operations,
                            SvgString = SvgChevronDown,
                            Rotation = 180, //point up
                            TintColor = Color.FromArgb("#AAFFFFFF"),
                            HeightRequest = 24,
                            LockRatio = 1,
                            HorizontalOptions = LayoutOptions.Center,
                            VerticalOptions = LayoutOptions.Center,
                        },
                    }
                }.Assign(out BtnScrollToOldest).OnTapped(me => ScrollToOldest(true)),
#endif

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

                // DEV-OPTIONS PICKER: bottom-sheet style overlay, tap backdrop to close.
                // Rows are built from BuildDevOptions() so new dev actions are one list entry.
                new SkiaLayer
                {
                    IsVisible = false,
                    ZIndex = 210,
                    BlockGesturesBelow = true,
                    BackgroundColor = Color.Parse("#99000000"),
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill,
                    Children =
                    {
                        new SkiaShape
                        {
                            Type = ShapeType.Rectangle,
                            CornerRadius = 16,
                            BackgroundColor = ChatTheme.BarBg,
                            HorizontalOptions = LayoutOptions.Fill,
                            VerticalOptions = LayoutOptions.End,
                            Margin = new Thickness(8, 0, 8, 8),
                            Padding = new Thickness(8, 12),
                            Children =
                            {
                                new SkiaStack
                                {
                                    Spacing = 4,
                                    HorizontalOptions = LayoutOptions.Fill,
                                    Children =
                                    {
                                        new SkiaLabel
                                        {
                                            Text = "Dev Tools",
                                            FontSize = 13,
                                            TextColor = ChatTheme.IconMuted,
                                            Margin = new Thickness(12, 4, 0, 8),
                                        },
                                    }
                                }.Assign(out DevPickerSheet),
                            }
                        }.OnTapped(me => { /* swallow taps on the sheet */ }),
                    }
                }.OnTapped(me => HideDevPicker()).Assign(out DevPickerOverlay),
            }
        };
    }

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
            Content = CreateCanvasContent()
        };
    }

    public void InitializeList()
    {
        // Cells reach the viewer via Parent.BindingContext (original app pattern).
        ChatStack.BindingContext = this;

        // Wire the windowed source to the freshly built scroll/stack (re-attached on each HotReload Build).
        // Built-in lib adapter — uses the layout's base SuppressLoadMore + MeasurementApplied primitives.
        _window.SetHost(new SkiaScrollWindowHost(MainScroll, ChatStack));
        _window.OnSliceLoaded = PreloadSlice;
        _window.LoadingChanged = OnWindowLoadingChanged;

        // Remote data source: the windowed source pages from here (with latency) — no local _all.
        _service = new MockChatService(TotalItems, RemoteLatencyMs);
        _window.SetDataSource(_service);

        // ViewportOffsetY does not notify, the Scrolled event is the offset signal.
        // No unsubscribe needed: the canvas with this scroll is disposed on every Build.
        MainScroll.Scrolled += OnChatScrolled;
        _scrollDownShown = false;

        // Async seed of the present window (shows nothing until the first slice arrives).
        _ = _window.InitializeAsync();
    }

    // One spinner, repositioned per operation: history (LoadOlder) = top, newer (LoadNewer) = bottom,
    // long jump (window-replace) = center. Raised on the UI thread by WindowedSource.
    private void OnWindowLoadingChanged()
    {
        if (Spinner == null)
            return;

        bool on = _window.IsLoadingOlder || _window.IsLoadingNewer || _window.IsLoadingJump;
        if (on)
        {
            if (_window.IsLoadingJump)
            {
                Spinner.VerticalOptions = LayoutOptions.Center;
                Spinner.Margin = new Thickness(0);
            }
            else if (_window.IsLoadingOlder)
            {
                Spinner.VerticalOptions = LayoutOptions.Start;
                Spinner.Margin = new Thickness(0, 64, 0, 0); // clear the navbar
            }
            else // newer
            {
                Spinner.VerticalOptions = LayoutOptions.End;
                Spinner.Margin = new Thickness(0, 0, 0, 64); // clear the send bar
            }
        }

        Spinner.IsVisible = on;
        if (on)
            SpinnerLoader?.Start();
        else
            SpinnerLoader?.Stop();
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

    // Dev-options picker entries. Add a new (label, action) here to expose a new dev action in the
    // tool picker — Send Image / Send File reuse the existing chat-bar handlers.
    private (string Label, Action Action)[] BuildDevOptions() => new (string, Action)[]
    {
        ("Send image", SendImage),
        ("Send file", SendFile),
        ("Mock AI answer", StartMockAiAnswer),
        ("Stop AI mock", StopMockAiAnswer),
    };

    private SkiaControl BuildDevOptionRow(string label, Action action)
    {
        return new SkiaShape
        {
            UseCache = SkiaCacheType.Operations,
            Type = ShapeType.Rectangle,
            CornerRadius = 10,
            BackgroundColor = ChatTheme.InputBg,
            HorizontalOptions = LayoutOptions.Fill,
            Padding = new Thickness(14, 12),
            Children =
            {
                new SkiaLabel
                {
                    Text = label,
                    FontSize = 15,
                    TextColor = Colors.White,
                    VerticalOptions = LayoutOptions.Center,
                },
            }
        }.OnTapped(me =>
        {
            HideDevPicker();
            action();
        });
    }

    private bool pickerInitialized;

    private void ShowDevPicker()
    {
        if (DevPickerOverlay == null || DevPickerSheet == null)
            return;

        // Rebuild rows each open (cheap) so the option list stays the single source of truth.
        // Keep the first child (title), drop the rest.
        if (!pickerInitialized)
        {
            pickerInitialized = true;

            foreach (var (label, action) in BuildDevOptions())
                DevPickerSheet.AddSubView(BuildDevOptionRow(label, action));
        }

        DevPickerOverlay.Opacity = 0;
        DevPickerOverlay.IsVisible = true;
        _ = DevPickerOverlay.FadeToAsync(1, 160);
    }

    private async void HideDevPicker()
    {
        if (DevPickerOverlay == null || !DevPickerOverlay.IsVisible)
            return;

        await DevPickerOverlay.FadeToAsync(0, 140);
        DevPickerOverlay.IsVisible = false;
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
        msg.ImageUrl = $"https://picsum.photos/seed/sent{_window.Count}/400/240";
        msg.PreviewBase64 = ChatMessage.MockImagePreview;

        SendOutgoing(msg);
    }


    private void OnChatScrolled(object sender, ScaledPoint e)
    {
        var away = Math.Abs(e.Units.Y);

        // Jump LoadMore-release is now handled inside WindowedSource: SuppressLoadMore covers only the fetch,
        // then the base ordered-scroll gate blocks LoadMore until the scroll self-completes. No latch here.

        // Jump-to-oldest in flight: release the LoadMore block once the ordered scroll has committed
        // AND the oldest item is actually visible (= we arrived). Parked at the oldest, LoadMore is
        // harmless (LoadOlder no-ops at windowStart 0; LoadNewer needs the far/newest edge), so it's
        // safe to re-enable here. Done before the FAB early-return so it runs during the jump.
        if (ChatStack.SuppressLoadMore
            && !MainScroll.OrderedScrollToIndexIsSet
            && ChatStack.LastVisibleIndex >= _items.Count - 1)
        {
            ChatStack.SuppressLoadMore = false;
        }

        // While returning to the newest message programmatically (send/FAB), the head-insert
        // viewport pinning and the scroll animation produce transient offsets that would
        // flash the FAB (a tall image bubble shifts by more than the threshold). Ignore them
        // until we arrive; the timeout covers an animation interrupted by the user.
        if (Environment.TickCount64 < _suppressFabUntilMs)
        {
            // Arrival can be at EITHER end: newest (offset ~0) or oldest (windowStart 0 + oldest visible).
            // On arrival, clear the suppress and FALL THROUGH to the normal FAB evaluation on this same
            // frame — otherwise, parked at the oldest, no further Scrolled event fires and the correct
            // buttons (scroll-to-newest) would stay hidden until the user scrolls manually.
            bool arrived = away < 12
                           || (_windowStart == 0 && ChatStack.LastVisibleIndex >= _items.Count - 1);
            if (!arrived)
            {
                ShowScrollDownButton(false);
#if DEBUG
                ShowScrollToOldestButton(false);
#endif
                return;
            }

            _suppressFabUntilMs = 0;
        }

        EvaluateFabs(away);
    }

    /// <summary>
    /// Computes both scroll-to-newest / scroll-to-oldest FAB visibility from the current distance to the
    /// newest end (<paramref name="away"/> = |offset|). Called from OnChatScrolled AND directly after a
    /// programmatic jump that snaps without raising a Scrolled event (e.g. the instant return-to-newest):
    /// relying on the next Scrolled event would leave the FABs stale until the user scrolls manually.
    /// </summary>
    private void EvaluateFabs(double away)
    {
        // newest message lives at offset 0 (top of the inverted scroll):
        // 100+ pts away into history -> offer the way back
        _wantScrollDown = away > 100;
        ShowScrollDownButton(_wantScrollDown);

#if DEBUG
        // Same appearing logic as scroll-to-newest, but for the OTHER end: scroll-to-oldest appears
        // when we are 100+ pts away from the oldest message (more history to traverse) and hides once
        // the oldest is reached. Older history still below the window (_windowStart != 0) = away by
        // definition; once the oldest batch is loaded, measure the gap to the far (oldest) end.
        bool atOldest = _windowStart == 0
                        && (Math.Max(0, MainScroll.ContentSize.Units.Height - MainScroll.Viewport.Units.Height) -
                            away) <= 100;
        ShowScrollToOldestButton(!atOldest);
#endif
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

        _service.Append(msg); // the "server" now has it (future range fetches include it)

        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Head-insert when at the present; when detached, ScrollToNewest below rebases to the present
            // window (which now includes this message) and snaps to it.
            _window.InsertNewest(msg);

            // Sending always returns the user to their message (original app behavior).
            ScrollToNewest(true);
        });

        _api.Send(msg); // mock backend advances Sent -> Delivered -> Read, then replies
    }


    /// <summary>
    /// Mock "file picker": sends a file attachment message (paperclip icon + file name).
    /// </summary>
    private void SendFile()
    {
        var msg = CreateTailMessage(MockFiles[_window.Count % MockFiles.Length],
            outgoing: true, ChatMessageType.File);

        SendOutgoing(msg);
    }


    private void ReceiveMessage(string text)
    {
        // Follow the incoming message only when the user is already at the newest messages,
        // don't yank them out of reading history (original app behavior).
        bool atNewest = ChatStack.FirstVisibleIndex <= 1;

        var msg = CreateTailMessage(text, outgoing: false);
        _service.Append(msg);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_window.InsertNewest(msg) && atNewest)
            {
                ScrollToNewest(true);
            }
        });
    }

    // MOCK STREAMING AI: one incoming message whose Text grows word-by-word for ~5s, to stress-test
    // how a cell reacts to a non-stop changing Text (remeasure/reflow). ChatCell.ContextPropertyChanged
    // reacts to nameof(ChatMessage.Text) and re-pushes only the label.
    private CancellationTokenSource _aiMockCts;

    private static readonly string[] AiMockWords =
    {
        "Let", "me", "think", "about", "this", "for", "a", "moment", "—", "analyzing",
        "the", "context", "and", "weighing", "a", "few", "possible", "approaches", "before",
        "committing", "to", "an", "answer", "that", "actually", "makes", "sense", "here.",
        "Considering", "the", "tradeoffs,", "the", "constraints,", "and", "what", "you",
        "really", "asked", "for", "in", "the", "first", "place,", "I", "would", "say",
    };

    private void StartMockAiAnswer()
    {
        StopMockAiAnswer(); // only one stream at a time

        var cts = _aiMockCts = new CancellationTokenSource();

        bool atNewest = ChatStack.FirstVisibleIndex <= 1;
        var msg = CreateTailMessage("…", outgoing: false);
        _service.Append(msg);
        if (_window.InsertNewest(msg) && atNewest)
            ScrollToNewest(true);

        SetBotTyping(true);
        _ = StreamMockAiAnswer(msg, cts.Token);
    }

    private async Task StreamMockAiAnswer(ChatMessage msg, CancellationToken token)
    {
        var sb = new System.Text.StringBuilder();
        var deadline = Environment.TickCount64 + 5000; // grow for ~5 seconds
        int i = 0;

        try
        {
            while (!token.IsCancellationRequested && Environment.TickCount64 < deadline)
            {
                await Task.Delay(180, token);

                var word = AiMockWords[i++ % AiMockWords.Length];
                sb.Append(sb.Length == 0 ? word : " " + word);
                var text = sb.ToString();

                // Text setter raises PropertyChanged -> ChatCell re-pushes the label live.
                MainThread.BeginInvokeOnMainThread(() => msg.Text = text);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped by the user via "Stop AI mock"
        }
        finally
        {
            MainThread.BeginInvokeOnMainThread(() => SetBotTyping(false));
        }
    }

    private void StopMockAiAnswer()
    {
        _aiMockCts?.Cancel();
        _aiMockCts?.Dispose();
        _aiMockCts = null;
    }

    private CancellationTokenSource _preloadCancellation;

    /// <summary>Hooked into WindowedSource.OnSliceLoaded: kick off preview-image preloading for each
    /// freshly paged-in slice (cancels the previous run).</summary>
    private void PreloadSlice(IReadOnlyList<ChatMessage> slice)
    {
        _preloadCancellation?.Cancel();
        _preloadCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = PreloadImages(slice, _preloadCancellation.Token);
    }

    private async Task PreloadImages(IReadOnlyList<ChatMessage> items, CancellationToken cancellationToken)
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
    /// Builds a new tail message with correct day/group flags relative to the current last one.
    /// </summary>
    private ChatMessage CreateTailMessage(string text, bool outgoing, ChatMessageType type = ChatMessageType.Text)
    {
        var prev = _service.At(_window.Count - 1);
        bool isFirstDay = prev.DayDesc != "Today";

        return new ChatMessage
        {
            Index = _window.Count,
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

    private void ScrollToNewest(bool animate)
    {
        //ScrollToIndex(0, RelativePositionType.Start, true);
        //return;

        //we might change observablecollection so use ui thread
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _wantScrollDown = false;
            _suppressFabUntilMs = Environment.TickCount64 + 1200;
            ShowScrollDownButton(false);

            bool wasDetached = !_window.AtPresent;

            // Atomic nav in the windowed source: at-present = plain scroll to newest; detached = fetch the
            // present window (centered jump spinner shows), replace, snap to content start. No blank flash.
            _ = _window.ScrollToNewest(animate);

            if (wasDetached)
            {
                // The window was rebased to the present: surface the FABs now (the jump suppressed them).
                _suppressFabUntilMs = 0;
                EvaluateFabs(0);
            }
        });
    }


    /// <summary>
    /// Universal jump to any GLOBAL message index with an alignment. Resident target = plain scroll;
    /// out-of-window target = the windowed source rebases a slice CENTERED on it (so Center has room
    /// and the user can keep scrolling both ways), then ordered-scrolls there. LoadMore is released
    /// once the scroll lands (see <see cref="_window"/>.OnScrolled in OnChatScrolled).
    /// </summary>
    public void ScrollToIndex(int globalIndex, RelativePositionType align, bool animate)
    {
        // _items may change collection-shape -> on the UI thread.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _ = _window.ScrollToIndex(globalIndex, align, animate);

            // Centered jump may detach from the present: offer the way back (offset alone is ~0 here,
            // so it wouldn't trigger). If the reply panel is open the button stays suppressed.
            if (!_window.AtPresent)
            {
                _wantScrollDown = true;
                ShowScrollDownButton(true);
            }
        });
    }

    /// <summary>
    /// Jump to a quoted message and flash it (Telegram-style). Thin wrapper over the universal
    /// <see cref="ScrollToIndex(int, RelativePositionType, bool)"/>.
    /// </summary>
    public void ScrollToMessage(ChatMessage msg)
    {
        // Flag the REAL backing instance (msg may be a quote clone, but cells bind _all[Index]).
        // Whichever cell holds/binds it flashes — works for both in-window (live notify) and
        // rebase (read fresh at bind time).
        var backing = _service?.At(msg.Index);
        if (backing != null)
            backing.HighlightStamp = Environment.TickCount64;

        ScrollToIndex(msg.Index, RelativePositionType.Center, true);
    }


#if DEBUG
    /// <summary>
    /// Debug helper: jump to the very first (oldest) message. Rebases the window to the start of
    /// history, then scrolls to the visual top (oldest = last resident item in the inverted list).
    /// </summary>
    private void ScrollToOldest(bool animate)
    {
        //we might change observablecollection so use ui thread
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _suppressFabUntilMs = Environment.TickCount64 + 1200;

            // Atomic nav: fetch the head window (centered jump spinner shows), replace, ordered-scroll to the
            // visual top. SuppressLoadMore is set inside and released in OnChatScrolled via _window.OnScrolled.
            _ = _window.ScrollToOldest(animate);
        });
    }

    private async void ShowScrollToOldestButton(bool show)
    {
        if (show && ReplyPanel != null && ReplyPanel.IsVisible)
            show = false;

        if (show == _scrollToOldestShown || BtnScrollToOldest == null)
            return;

        _scrollToOldestShown = show;

        if (show)
        {
            BtnScrollToOldest.IsVisible = true;
            await BtnScrollToOldest.FadeToAsync(1, 150);
        }
        else
        {
            await BtnScrollToOldest.FadeToAsync(0, 150);
            if (!_scrollToOldestShown)
                BtnScrollToOldest.IsVisible = false;
        }
    }

#endif


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
        ShowScrollDownButton(_wantScrollDown || !_window.AtPresent);
    }


    private void SetBotTyping(bool typing)
    {
        if (StatusLabel == null)
            return;

        StatusLabel.Text = typing ? "Banana is typing…" : "online";
        StatusLabel.TextColor = typing ? ChatTheme.AccentBright : ChatTheme.IconMuted;
    }
}