using AppoMobi.Gestures;
using DrawnUi.Draw;

namespace DrawnChatList;

/// <summary>Logs on Tapped to confirm what _row sees when dispatched.</summary>
public sealed class DiagRow : SkiaLayout
{
    public override ISkiaGestureListener ProcessGestures(SkiaGesturesParameters args, GestureEventProcessingInfo apply)
    {
        if (args.Type == TouchActionResult.Tapped)
        {
            SkiaSharp.SKRect hit = CreateHitRect();
            Console.WriteLine(
                $"[DiagRow] Tapped mappedY={apply.MappedLocation.Y:0} DrawRect=[{DrawingRect.Top:0}..{DrawingRect.Bottom:0}] HitRect=[{hit.Top:0}..{hit.Bottom:0}]");
        }

        var result = base.ProcessGestures(args, apply);
        if (args.Type == TouchActionResult.Tapped)
            Console.WriteLine($"[DiagRow] result={(result == null ? "null" : result.GetType().Name)}");
        return result;
    }
}