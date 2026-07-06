using System.Diagnostics;
using AppoMobi.Gestures;
using DrawnUi.Draw;

namespace DrawnChatList;

/// <summary>Logs RenderTree hit-test details on every Tapped to expose dead-zone coordinates.</summary>
public sealed class DiagStack : SkiaStack
{
    public override ISkiaGestureListener ProcessGestures(SkiaGesturesParameters args, GestureEventProcessingInfo apply)
    {
        if (args.Type == TouchActionResult.Tapped && RenderTree != null)
        {
            var adj = RenderTree.AdjustOffset(apply.ChildOffset);
            var thisOff = TranslateInputCoords(adj, true);
            float ptY = apply.MappedLocation.Y + thisOff.Y;
            Console.WriteLine(
                $"[DiagStack] Tapped ptY={ptY:0} mappedY={apply.MappedLocation.Y:0} offY={thisOff.Y:0} DrawRect=[{DrawingRect.Top:0}..{DrawingRect.Bottom:0}]");
            foreach (var ch in RenderTree)
            {
                var loc = new SkiaSharp.SKPoint(apply.MappedLocation.X + thisOff.X, ptY);
                bool hit = IsGestureForChild(ch, loc);
                Debug.WriteLine(
                    $"  child={ch.Control.GetType().Name} IT={ch.Control.InputTransparent} HitRect=[{ch.HitRect.Top:0}..{ch.HitRect.Bottom:0}] DrawRect=[{ch.Control.DrawingRect.Top:0}..{ch.Control.DrawingRect.Bottom:0}] hit={hit}");
            }
        }

        var result = base.ProcessGestures(args, apply);
        if (args.Type == TouchActionResult.Tapped)
            Console.WriteLine($"[DiagStack] result={(result == null ? "null" : result.GetType().Name)}");
        return result;
    }
}
