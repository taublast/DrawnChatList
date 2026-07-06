
using System.Collections.Specialized;
using System.Diagnostics;
using DrawnUi.Draw;

namespace DrawnChatList;

public class ChatMessagesStack : SkiaCachedStack
{
    public ChatMessagesStack()
    {
        //cells will still be reused for newly arriving data inside limited window
        RecyclingTemplate = RecyclingTemplate.Disabled;
    }

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
