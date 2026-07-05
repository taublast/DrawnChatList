using DrawnUi.Draw;

namespace DrawnChatList;

public class ChatScroll : SkiaScroll
{
    public ChatScroll()
    {
        Rotation = 180;
        ReverseGestures = true;
        TrackIndexPosition = RelativePositionType.Start;
        ResetScrollPositionOnContentSizeChanged = false;
        LoadMoreOffset = 800;
        LoadMoreTopOffset = 800;

        HorizontalOptions = LayoutOptions.Fill;
        VerticalOptions = LayoutOptions.Fill;
    }
}