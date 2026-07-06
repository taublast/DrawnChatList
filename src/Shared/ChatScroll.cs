using DrawnUi.Draw;
using DrawnUi.Views;

namespace DrawnChatList;

public class ChatScroll : SkiaScroll
{
    public ChatScroll()
    {
        AutoCache = true;

        Rotation = 180;
        ReverseGestures = true;
        TrackIndexPosition = RelativePositionType.Start;
        ResetScrollPositionOnContentSizeChanged = false;
        LoadMoreOffset = 800;
        LoadMoreTopOffset = 800;

        HorizontalOptions = LayoutOptions.Fill;
        VerticalOptions = LayoutOptions.Fill;

        ScrollBar = new SkiaScrollBar()
        {
            Dock = ScrollBarDock.Start //since we are rotated
        };
    }
}