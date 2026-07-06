using DrawnUi.Draw;
using DrawnUi.Views;

namespace DrawnChatList;

/// <summary>
/// Deco-triangle tail glued to the right of an outgoing bubble.
/// Shared-cache trick: created and drawn ONCE, then same cache reused everywhere!
/// Shown for the first message in a day group for every direction.
/// </summary>
public class OutcomingBubbleSign : SkiaShape
{
    public OutcomingBubbleSign()
    {
        InputTransparent = true;
        HorizontalOptions = LayoutOptions.End;
        UseCache = SkiaCacheType.Operations;
        CacheSharing = CacheSharingType.Shared; //same cache used fo ALL instances!
        Type = ShapeType.Polygon;
        Points = new List<SkiaPoint> { new(0, 0), new(1, 0), new(0, 1) };
        BackgroundColor = ChatCell.ColorOutgoing;
        WidthRequest = 8;
        HeightRequest = 10;
        Top = 6;
        Left = -2;
    }
}