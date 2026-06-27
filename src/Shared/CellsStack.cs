using DrawnUi.Draw;

namespace DrawnChatList;

 
public class CellsStack : SkiaStack
{
    public CellsStack()
    {
        UseCache = SkiaCacheType.None; // we own DrawDirectInternal + our own Operations cache
        FastMeasurement = true;
    }

    // SuppressLoadMore, ordered-scroll LoadMore gating, and the structure-applied hook (was OnAdded /
    // ShouldTriggerLoadMore / ApplyBackgroundMeasurementChange / OnHeadInsertCommitted overrides) now live
    // in base SkiaLayout: SuppressLoadMore property + MeasurementApplied event, wired by SkiaScrollWindowHost.

    protected ScaledPoint ScrollOffset;
    public override void OnViewportWasChanged(ScaledRect viewport, ScaledPoint offset)
    {
        base.OnViewportWasChanged(viewport, offset);

        ScrollOffset = offset;
    }

 

}