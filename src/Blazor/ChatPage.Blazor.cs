using System.Windows.Input;
using AppoMobi.Specials;
using DrawnUi.Draw;
using DrawnUi.Views;


namespace DrawnChatList;

/// <summary>
/// GPU reproduction using the REAL ChatCell + ChatMessage + windowing copied from the Android repro
/// ChatPage, so the OpenTK sample exercises exactly what breaks on device (no simplified cell). Inverted
/// scroll + windowed 150-cap ItemsSource + bidirectional LoadMore + trim, UsePlaneCache on.
/// </summary>
public sealed partial class ChatPage : BindableObject, IChatCellActions
{

    // tap diagnostics
    public int LastTapMsgIndex = -1;
    public string LastTapAction = "";
    public int LastChildIndex = -1;   // ANY cell tapped (cell-level hit)
    public int LastImageIndex = -1;   // inner image tapped (inner hit-rect)

    public int LoadOlderCalls;
    public int LoadNewerCalls;
    public int TrimEvents;

    public float KeyboardSize
    {
        get;
        set
        {
            if (value.Equals(field)) return;
            field = value;
            OnPropertyChanged();
        }
    }

    public int ResidentCount => _items.Count;
    public int WindowStart => _windowStart;
    public int WindowEnd => _windowEnd;

    /// <summary>Local index of an image cell roughly in the middle of the resident window (for tap tests).</summary>
    public int MiddleImageLocal()
    {
        int mid = _items.Count / 2;
        for (int off = 0; off < _items.Count; off++)
        {
            int i = mid + off; if (i < _items.Count && _items[i].Type == ChatMessageType.Image) return i;
            i = mid - off; if (i >= 0 && _items[i].Type == ChatMessageType.Image) return i;
        }
        return -1;
    }

    public ChatPage()
    {
        // Data source + async seed happen in InitializeList (after the scroll/host are built).
        _api.ReplyReceived += (s, text) => ReceiveMessage(text);
        _api.Typing += (s, typing) => SetBotTyping(typing);  
    }
     

    public Canvas BuildCanvas()
    {
        var canvas = CreateCanvas();

        InitializeList();

        // AI testing: fires for ANY child tap (text or image) -> tells us the tap reached a cell + which index
        ChatStack.ChildTapped += (s, e) =>
        {
            var c = e.Control as SkiaControl;
            LastChildIndex = c?.ContextIndex ?? -2;
        };

        return canvas;
    }

 
}
