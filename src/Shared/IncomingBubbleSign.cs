using DrawnUi.Draw;

namespace DrawnChatList;

/// <summary>
/// Deco-triangle tail glued to the left of an incoming bubble. Dedicated subclass with fixed
/// visuals + CacheSharing=Shared: every instance across all recycled cells reuses ONE physical
/// cache surface for the whole type instead of rendering/storing its own.
/// </summary>
public class IncomingBubbleSign : SkiaShape
{
    /// <summary>
    /// Deco-triangle tail glued to the right of an outgoing bubble.
    /// Shared-cache trick: created and drawn ONCE, then same cache reused everywhere!
    /// Shown for the first message in a day group for every direction.
    /// </summary>
    public IncomingBubbleSign()
    {
        InputTransparent = true;
        UseCache = SkiaCacheType.Operations;
        CacheSharing = CacheSharingType.Shared; //same cache used fo ALL instances!
        Type = ShapeType.Polygon;
        Points = new List<SkiaPoint> { new(1, 0), new(0, 0), new(1, 1) };
        BackgroundColor = ChatCell.ColorIncoming;
        WidthRequest = 8;
        HeightRequest = 10;
        Top = 6;
        Left = 2;
    }
}