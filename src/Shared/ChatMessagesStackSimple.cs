using DrawnUi.Draw;

namespace DrawnChatList;

 
public class ChatMessagesStackSimple : SkiaStack
{
    public ChatMessagesStackSimple()
    {
        UseCache = SkiaCacheType.None; // we own DrawDirectInternal + our own Operations cache
        FastMeasurement = true;
    }

    public Action OnAdded;

   
    protected override void ApplyBackgroundMeasurementChange(StructureChange change)
    {
        base.ApplyBackgroundMeasurementChange(change);

        OnAdded?.Invoke();
    }

    // Backward LoadMore (head insert) commits through this path, NOT ApplyBackgroundMeasurementChange,
    // so without this the deferred opposite-end trim (LimitSourceForNewer) never fired and the window
    // grew unbounded on the way back to newest.
    protected override void OnHeadInsertCommitted()
    {
        base.OnHeadInsertCommitted();

        OnAdded?.Invoke();
    }

 
}