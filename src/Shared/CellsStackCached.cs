using DrawnUi.Draw;
using System.Collections.Specialized;
using System.Diagnostics;

namespace DrawnChatList;

/// <summary>
/// Chat cells stack — the band-plane machinery (record gates, coverage clamp, re-anchor, off-thread
/// compositor) now lives in the lib control <see cref="SkiaCachedStack"/>; this subclass keeps only the
/// chat-specific motion-trace logging and tap diagnostics.
/// </summary>
public class CellsStackCached : SkiaCachedStack
{
    protected override void OnItemsSourceCollectionChanged(object sender, NotifyCollectionChangedEventArgs args)
    {
        if (ChatPage.MotionTraceEnabled)
            Console.WriteLine($"[MOTION] EVT collection {args.Action} new={args.NewItems?.Count ?? 0}@{args.NewStartingIndex} old={args.OldItems?.Count ?? 0}@{args.OldStartingIndex}");

        base.OnItemsSourceCollectionChanged(sender, args);
    }

    protected override void OnHeadInsertCommitted()
    {
        if (ChatPage.MotionTraceEnabled)
            Console.WriteLine("[MOTION] EVT head-insert COMMIT");

        base.OnHeadInsertCommitted();
    }

    public override void OnChildTapped(SkiaControl child, SkiaGesturesParameters args, GestureEventProcessingInfo apply)
    {
        if (child.BindingContext is ChatMessage msg)
        {
            Debug.WriteLine($"Tapped child data {msg.Index} index {child.ContextIndex}");
        }
        else
        {
            Debug.WriteLine($"Tapped child index {child.ContextIndex}");
        }

        base.OnChildTapped(child, args, apply);
    }
}
