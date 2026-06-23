using System.Collections.Specialized;
using System.Diagnostics;
using DrawnUi.Draw;
using DrawnUi.Views;
using SkiaSharp;

namespace DrawnChatList;

public class ChatMessagesStack : ChatMessagesStackSimple
{
    public ChatMessagesStack()
    {
        _paintAction = Paint; // pre-alloc: no per-frame delegate/closure allocation
    }

    private static bool UseDoubleBuffering = false;
    private static bool UseCachedScrolling = false;


    private SkiaCacheType PlaneCacheType = SkiaCacheType.Operations;

    private readonly Action<DrawingContext> _paintAction;

    private SKPictureRecorder _recorder; // reused across record frames
    private bool _cacheValid;
    private int _skipStreak; // consecutive frames skipped while the structure rebases (capped)

    private float _recordOffsetY; // context.Destination.Top at the last record (band coverage origin)
    private SKRect _lastDestination, _lastDrawingRect; // for static-frame detection

    // invalidate the picture whenever the windowed ItemsSource changes (LoadMore/trim shifts content) — a
    // trim keeps count≈150 with stable indices, so a count check can't see it; the collection event can.
    private volatile bool _contentChanged = true;

    private int dirtyAfterCache = 0;
    private int _lastDrawn;

    /// <summary>Mutation hook (LoadMore/trim/add/remove on the same collection) — invalidate the picture.</summary>
    protected override void OnItemsSourceCollectionChanged(object sender, NotifyCollectionChangedEventArgs args)
    {
        _contentChanged = true; // invalidate the static blit so collection changes (insert/add/remove) apply

        base.OnItemsSourceCollectionChanged(sender, args);
    }

    public override void OnStructureChanged()
    {
        _contentChanged = true; // head-insert/commit & other structure rebuilds invalidate the cache

        base.OnStructureChanged();
    }

    /// <summary>Set by the page while a programmatic jump (e.g. scroll-to-oldest) is in flight, from
    /// issue until the target lands. Auto-LoadMore must not mutate the window mid-jump — it would shift
    /// _items and make the pending ScrollToIndex target drift. The page clears it once arrived.</summary>
    public bool SuppressLoadMore;

    public override bool ShouldTriggerLoadMore(ScaledRect viewport, LoadMoreDirection direction)
    {
        if (SuppressLoadMore)
            return false;

        if (Parent is SkiaScroll scroll)
        {
            if (scroll.OrderedScrollToIndexIsSet || scroll.OrderedScrollTo.IsValid)
            {
                return false;
            }
        }

        return base.ShouldTriggerLoadMore(viewport, direction);
    }

    protected override void ApplyBackgroundMeasurementChange(StructureChange change)
    {
        base.ApplyBackgroundMeasurementChange(change); // base fires OnAdded

        _contentChanged = true;
    }

    protected override void OnHeadInsertCommitted()
    {
        base.OnHeadInsertCommitted(); // base fires OnAdded

        _contentChanged = true;
    }

    public override void OnTemplatesAvailable()
    {
        base.OnTemplatesAvailable();

        _contentChanged = true;
    }

    public override void UpdateByChild(SkiaControl child)
    {
        base.UpdateByChild(child);

        //image loaded or something else.. existing cell wanted an update..
        TrackChildAsDirty(child);

        Repaint();
    }

    /// <summary>Scroll-invariant fingerprint of the (banded) visible cells: identity + content-space
    /// geometry. Zero allocation. Changes on any cell enter/leave, resize, relayout, or visibility toggle;
    /// does NOT change on plain scroll.</summary>
    private static long Fingerprint(List<ControlInStack> visible)
    {
        unchecked
        {
            long h = 17;
            for (int i = 0; i < visible.Count; i++)
            {
                var c = visible[i];
                if (c == null) continue;
                h = h * 31 + c.ControlIndex;
                h = h * 31 + (long)c.Destination.Left;
                h = h * 31 + (long)c.Destination.Top;
                h = h * 31 + (long)c.Destination.Width;
                h = h * 31 + (long)c.Destination.Height;
            }

            return h;
        }
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


    /// <summary>
    /// Draw cached picture of children with translation
    /// </summary>
    /// <param name="context"></param>
    /// <param name="dest"></param>
    void DrawCache(DrawingContext context, SKRect dest)
    {
        //we draw cache at position "where it was captured"
        //and we apply translation to position it correctly according to current scroll offset
        //translation and RenderTree.Offset will correctly map gestures

        var cache = ForegroundPlane;
        if (cache != null)
        {
            this.RenderTree.Offset = CalculateCacheOffset(cache, dest);
            cache.Draw(context.Context.Canvas, dest, null);
        }
        else
        {
            var nocache = true;
        }
    }

    /// <summary>
    /// Calculate offset between where cache was captured and where it should be drawn
    /// </summary>
    /// <param name="destination"></param>
    /// <returns></returns>
    SKPoint CalculateCacheOffset(CachedObject cache, SKRect destination)
    {
        var moveY = cache.Bounds.Top - cache.RecordingArea.Top;
        var moveX = cache.Bounds.Left - cache.RecordingArea.Left;
        var x = (float)(destination.Left - cache.Bounds.Left + moveX);
        var y = (float)(destination.Top - cache.Bounds.Top + moveY);
        return new SKPoint(x, y);
    }

    // The plane currently being blitted. ONLY the render thread reads/writes it.
    public CachedObject ForegroundPlane { get; set; }

    // A freshly rendered plane published by the offscreen thread, not yet installed. The render thread is
    // the SOLE owner of swapping: it consumes this in UpdatePlanes and promotes it to ForegroundPlane.
    // The offscreen thread only ever publishes here (via Interlocked), never touches ForegroundPlane or
    // picks a slot — removes the "background overwrites the plane being drawn" race + the per-frame swap.
    private CachedObject _preparedPlane;

    void UpdatePlanes()
    {
        if (!UseDoubleBuffering)
        {
            return;
        }

        // Consume a freshly prepared plane. Swaps ONLY when a new background render actually arrived, so the
        // foreground never oscillates between stale/fresh every frame.
        var prepared = Interlocked.Exchange(ref _preparedPlane, null);
        if (prepared != null)
        {
            var old = ForegroundPlane;
            ForegroundPlane = prepared;
            _cacheValid = true;
            DisposeObject(old);
        }
    }

    public override void DrawDirectInternal(DrawingContext context, SKRect drawingRect)
    {
        if (drawingRect.Height == 0 || drawingRect.Width == 0 || IsDisposed || IsDisposing)
            return;

        var destination = context.Destination;

        //needed for updating after some already present cell size changes
        if (HasPendingStructureChanges)
            _contentChanged = true;

        // only dirty children that are inside the current structure trigger a re-record
        bool dirty = !DirtyChildrenTracker.IsEmpty;
        if (dirty)
        {
            dirtyAfterCache++; //for DEBUG
        }

        bool canUseCache;

        UpdatePlanes();

        // nothing moved, nothing changed
        if (_cacheValid && !dirty && !_contentChanged && destination == _lastDestination &&
            drawingRect == _lastDrawingRect)
        {
            canUseCache = true;
        }
        else
        {
            float stableAreaPx = ParentViewport.Pixels.Height;

            //band covers
            canUseCache = _cacheValid && !dirty && !_contentChanged &&
                          Math.Abs(destination.Top - _recordOffsetY) < stableAreaPx;
        }

        if (!canUseCache)
        {
            _cacheValid = true;
            _recordOffsetY = destination.Top;
            _contentChanged = false;

            CreateCache(context, drawingRect);
        }

        // Both modes blit here: double-buffer blits the current plane while a new one bakes; sync blits the
        // plane just recorded this frame. (Skipping this in sync mode left record frames empty/flickering.)
        _lastDestination = destination;
        _lastDrawingRect = drawingRect;
        DrawCache(context, destination);
    }

    CachedObject CreateRenderObject(DrawingContext context, SKRect recordArea)
    {
        dirtyAfterCache = 0;

        if (PlaneCacheType == SkiaCacheType.Operations)
        {
            _recorder ??= new SKPictureRecorder();
            var rc = context.CreateForRecordingOperations(_recorder, recordArea);

            Paint(rc); // => DrawStack, DrawStackVisibleChildren..

            //DrawWithClipAndTransforms(rc, drawingRect, true, true, _paintAction);
            var picture = _recorder.EndRecording();

            return new CachedObject(SkiaCacheType.Operations, picture, context.Destination, recordArea);
        }
        else
        {
            var cacheType = PlaneCacheType == SkiaCacheType.GPU ? SkiaCacheType.GPU : SkiaCacheType.Image;

            //create surface
            var width = (int)recordArea.Width;
            var height = (int)recordArea.Height;

            SKSurface surface;
            bool needCreateSurface = true;

            surface = CreateSurface(width, height, cacheType == SkiaCacheType.GPU);
            if (surface == null)
            {
                return null; //would be totally unexpected
            }

            //record
            var recordingContext = context.CreateForRecordingImage(surface, recordArea.Size);
            recordingContext.Context.IsRecycled = !needCreateSurface;

            // Translate the canvas to start drawing at (0,0)
            recordingContext.Context.Canvas.Translate(-recordArea.Left, -recordArea.Top);

            // Perform the drawing action
            Paint(recordingContext);

            recordingContext.Context.Canvas.Translate(recordArea.Left, recordArea.Top);
            recordingContext.Context.Canvas.Flush();

            return new CachedObject(cacheType, surface, context.Destination, recordArea)
            {
                SurfaceIsRecycled = recordingContext.Context.IsRecycled
            };
        }
    }

    void CreateCache(DrawingContext context, SKRect recordArea)
    {

        if (!UseDoubleBuffering)
        {
            if (PlaneCacheType == SkiaCacheType.Operations)
            {
                _recorder ??= new SKPictureRecorder();
                var rc = context.CreateForRecordingOperations(_recorder, recordArea);

                Paint(rc); // => DrawStack, DrawStackVisibleChildren..

                //DrawWithClipAndTransforms(rc, drawingRect, true, true, _paintAction);
                var picture = _recorder.EndRecording();

                dirtyAfterCache = 0;
                if (ForegroundPlane == null)
                {
                    ForegroundPlane = new CachedObject(SkiaCacheType.Operations, picture, context.Destination, recordArea);
                }
                else
                {
                    ForegroundPlane.Picture?.Dispose();
                    ForegroundPlane.Picture = picture;
                    ForegroundPlane.Bounds = context.Destination;
                    ForegroundPlane.RecordingArea = recordArea;
                }
            }
            else
            {
                var cacheType = PlaneCacheType == SkiaCacheType.GPU ? SkiaCacheType.GPU : SkiaCacheType.Image;

                //create surface
                var width = (int)recordArea.Width;
                var height = (int)recordArea.Height;

                SKSurface surface;
                bool needCreateSurface = !CheckCachedObjectValid(ForegroundPlane, recordArea, context.Context)
                                         && cacheType != SkiaCacheType.GPU;
                if (!needCreateSurface)
                {
                    //reusing existing surface
                    surface = ForegroundPlane.Surface;
                    if (surface == null || surface.Handle == 0)
                    {
                        Super.Log("CreateRenderingObject failed to reuse surface!");
                        return; //would be totally unexpected
                    }

                    ForegroundPlane.PreserveSourceFromDispose = true; //we will dispose that source in this new object

                    if (!IsCacheComposite)
                        surface.Canvas.Clear();
                }
                else
                {
                    surface = CreateSurface(width, height, cacheType == SkiaCacheType.GPU);
                    if (surface == null)
                    {
                        return; //would be totally unexpected
                    }
                }

                //record
                var recordingContext = context.CreateForRecordingImage(surface, recordArea.Size);
                recordingContext.Context.IsRecycled = !needCreateSurface;

                // Translate the canvas to start drawing at (0,0)
                recordingContext.Context.Canvas.Translate(-recordArea.Left, -recordArea.Top);

                // Perform the drawing action
                Paint(recordingContext);

                recordingContext.Context.Canvas.Translate(recordArea.Left, recordArea.Top);
                recordingContext.Context.Canvas.Flush();

                if (ForegroundPlane == null)
                {
                    ForegroundPlane = new CachedObject(cacheType, surface, context.Destination, recordArea)
                    {
                        SurfaceIsRecycled = recordingContext.Context.IsRecycled
                    };
                }
                else
                {
                    if (needCreateSurface)
                    {
                        ForegroundPlane.Surface = surface;
                    }
                    ForegroundPlane.Image?.Dispose();
                    ForegroundPlane.Image = surface.Snapshot();
                    ForegroundPlane.Bounds = context.Destination;
                    ForegroundPlane.RecordingArea = recordArea;
                    ForegroundPlane.SurfaceIsRecycled = recordingContext.Context.IsRecycled;
                }
            }

            return;
        }

        PushToOffscreenRendering(() =>
        {
            //will be executed on background thread in parallel
            if (IsDisposed || IsDisposing)
                return;

            var rendered = CreateRenderObject(context, recordArea);

            // Publish to the prepared slot; the render thread installs it (sole swapper). If a previous
            // prepared plane was never consumed, dispose it here — never the live ForegroundPlane.
            var stale = Interlocked.Exchange(ref _preparedPlane, rendered);

            if (stale.Surface != null && Superview is DrawnView view)
            {
                view.ReturnSurface(stale.Surface);
                stale.Surface = null;
            }

            DisposeObject(stale);
            Repaint();
        });



    }

    public override void OnWillDisposeWithChildren()
    {
        base.OnWillDisposeWithChildren();

        DisposeObject(Interlocked.Exchange(ref _preparedPlane, null));
        DisposeObject(ForegroundPlane);
    }

    public override void OnDisposing()
    {
        DisposeObject(Interlocked.Exchange(ref _preparedPlane, null));
        ForegroundPlane = null;
        _recorder?.Dispose();
        _recorder = null;

        base.OnDisposing();
    }
}