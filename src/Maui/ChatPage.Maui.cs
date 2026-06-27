using AppoMobi.Specials;
using DrawnUi.Controls;
using DrawnUi.Draw;
using DrawnUi.Views;
using System.Diagnostics;

namespace DrawnChatList;

/// <summary>
/// Chat sample on top of the same windowed-ItemsSource machinery as DevPage, with the classic
/// INVERTED chat scroll (like the original app): scroll Rotation=180 + ReverseGestures, cells
/// Rotation=180. _items goes NEWEST-FIRST: index 0 = latest message = content start = visual
/// BOTTOM of the rotated scroll. New messages are inserted at 0, history loads APPEND at the
/// list end (the scroll's plain LoadMoreCommand = visually scrolling up). 1000 mock messages,
/// sliding window keeps max MaxItemsInMemory resident; the memory cap trims the opposite end.
/// Typing entry is a SkiaEditor — a totally drawn control.
/// </summary>
public partial class ChatPage : BasePageReloadable, IChatCellActions
{
    public ChatPage()
    {
        BackgroundColor = ChatTheme.Bg;

        // our own drawn navbar replaces the MAUI Shell one
        Shell.SetNavBarIsVisible(this, false);

        _api.ReplyReceived += (s, text) => ReceiveMessage(text); //already on UI thread
        _api.Typing += (s, typing) => SetBotTyping(typing); //already on UI thread
    }


    /// <summary>
    /// This will be called by HotReload, MAUI will call this on UI thread
    /// </summary>
    public override void Build()
    {
        Canvas?.Dispose(); //cleanup if HotReload

        // Data source + async seed happen in InitializeList (after the scroll/host are built); the inverted
        // scroll starts at content start = newest message at the visual bottom once the first slice arrives.
        Canvas = CreateCanvas();

        InitializeList();

        Content = new Grid() //respect safeinsets, MAUI needs its wrapper for that
        {
            Children =
            {
                Canvas
            }
        };
    }

    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing)
        {
            this.Content = null;
            Canvas?.Dispose();
        }

        base.Dispose(isDisposing);
    }
}