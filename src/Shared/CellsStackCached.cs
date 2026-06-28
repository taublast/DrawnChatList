using System.Collections.Specialized;
using System.Diagnostics;
using DrawnUi.Draw;
using DrawnUi.Views;
using SkiaSharp;

namespace DrawnChatList;

public class CellsStackCached : CellsStack
{
    public CellsStackCached()
    {
        _paintAction = Paint; // pre-alloc: no per-frame delegate/closure allocation

        // Overscan when double-buffering: record ± one viewport so the plane can be REUSED (blitted, not
        // re-recorded) while scrolling within the margin = smooth. Without it the plane covers exactly the
        // viewport and every scroll frame re-records (jerky). The snapshot-fill + view-readiness gates keep
        // the wider async bakes hole-free.
        if (UseDoubleBuffering)
            VirtualisationInflatedRatio = 1.0;
        else
            VirtualisationInflatedRatio = 1.5;
    }

    // Content-space Y range (viewport at bake time) that the corresponding plane actually covers — the async
    // plane records only the cells visible when it was baked, so once the viewport scrolls beyond this range
    // the plane can't fill it. Used to fall back to a sync record instead of blitting an uncovered plane.
    private float _foregroundCoveredTop, _foregroundCoveredBot;
    private volatile float _preparedCoveredTop, _preparedCoveredBot;
    private volatile bool _bakeInFlight; // one off-thread bake at a time; keep blitting current plane meanwhile

    private static bool UseDoubleBuffering = true;

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
    private bool _logLastLive; // [PLANE] probe: track live-fallback <-> blit transitions

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

    // ---- structure double-buffering (UseDoubleBuffering only) ----------------------------------------
    // The off-thread plane bake runs Paint -> DrawStack on a BACKGROUND thread, which reads
    // GetStackStructure() WHILE the render thread mutates that same structure (head-remove translate,
    // ApplyPendingStructureChanges, cell offsets). LayoutStructure.Clone() is shallow (shares the live
    // ControlInStack), so it doesn't isolate geometry. We hand the bake thread a DEEP-frozen snapshot via
    // a thread-local override of GetStackStructure(): only the baking thread sees it; the render thread
    // keeps mutating the live structure. Kills the torn read => no empty/gray cells, no blank bands.
    [ThreadStatic] private static LayoutStructure _bakeStructure;

    // POOLED freeze target. The deep copy used to allocate a fresh LayoutStructure + ~200 ControlInStack on
    // EVERY async bake (every scroll-dispatch). That per-frame churn was the dominant managed-heap allocation
    // and drove the periodic GC pauses (Gen0/1/2) that showed up as the "0.2s smooth, lag spike, smooth"
    // hitch — 100% of slow frames coincided with a collection. Reuse one structure + one ControlInStack pool
    // instead: zero per-bake allocation once warmed, so no GC churn, while still handing the bake thread a
    // frozen geometry snapshot (no torn read -> no holes). Safe to reuse a single buffer because _bakeInFlight
    // serializes bakes: the next freeze only runs after the prior bake's lambda finished reading the snapshot
    // (CreateRenderObject completes and clears _bakeStructure before the finally sets _bakeInFlight=false).
    private LayoutStructure _frozenReusable;
    private readonly List<ControlInStack> _bakePool = new();

    public override LayoutStructure GetStackStructure()
        => (UseDoubleBuffering  && _bakeStructure != null) ? _bakeStructure : base.GetStackStructure();

    /// <summary>Deep copy into the pooled buffer: reused ControlInStack instances (frozen geometry/flags) over
    /// the SAME live Views. Zero allocation once the pool has grown to the structure size.</summary>
    private LayoutStructure FreezeStructure(LayoutStructure src)
    {
        var clone = _frozenReusable ??= new LayoutStructure();
        clone.Clear();
        if (src == null) return clone;
        int i = 0;
        foreach (var c in src.GetChildren())
        {
            if (c == null) continue;
            ControlInStack dst;
            if (i < _bakePool.Count)
                dst = _bakePool[i];
            else
                _bakePool.Add(dst = new ControlInStack());
            i++;

            dst.ControlIndex = c.ControlIndex;
            dst.Measured = c.Measured;
            dst.Layout = c.Layout;
            dst.Area = c.Area;
            dst.Destination = c.Destination;
            dst.OffsetOthers = c.OffsetOthers;
            dst.View = c.View;
            dst.Offset = c.Offset;
            dst.WasMeasured = c.WasMeasured;
            dst.IsVisible = c.IsVisible;
            dst.ZIndex = c.ZIndex;
            dst.Column = c.Column;
            dst.Row = c.Row;
            dst.IsCollapsed = c.IsCollapsed;
            dst.DebugMeasureBatch = c.DebugMeasureBatch;

            clone.Add(dst, c.Column, c.Row);
        }
        return clone;
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

    /// <summary>
    /// A head trim reclaimed dead space: the base translated every cell -deltaPixels and compensated the
    /// parent scroll +deltaPixels, so next frame destination.Top moves +deltaPixels. Our plane was baked in
    /// the PRE-shift coordinate frame; the rigid blit (CalculateCacheOffset reduces to y = destination.Top -
    /// RecordingArea.Top) would then land it deltaPixels off — a one-frame hole until a fresh bake lands.
    /// Re-anchor the plane's RecordingArea/Bounds by the SAME delta so the blit stays pixel-stable across the
    /// commit. Double-buffer only (the sync path re-records every miss, so it never blits a stale plane).
    /// Runs on the render thread before the parent scroll computes its frame offset.
    /// </summary>
    public override void OnContentTranslatedVertically(float deltaPixels)
    {
        base.OnContentTranslatedVertically(deltaPixels);

        if (!UseDoubleBuffering || deltaPixels == 0)
            return;

        ReanchorPlane(ForegroundPlane, deltaPixels);
        ReanchorPlane(_preparedPlane, deltaPixels); // a bake from the pre-shift frame may be mid-flight
        _recordOffsetY += deltaPixels; // keep band-drift origin in the post-shift frame

        // cells moved -deltaPixels in content space, so the plane's covered content range moves with them
        _foregroundCoveredTop -= deltaPixels;
        _foregroundCoveredBot -= deltaPixels;
        _preparedCoveredTop -= deltaPixels;
        _preparedCoveredBot -= deltaPixels;
    }

    static void ReanchorPlane(CachedObject plane, float deltaPixels)
    {
        if (plane == null)
            return;
        plane.RecordingArea = new SKRect(plane.RecordingArea.Left, plane.RecordingArea.Top + deltaPixels,
            plane.RecordingArea.Right, plane.RecordingArea.Bottom + deltaPixels);
        plane.Bounds = new SKRect(plane.Bounds.Left, plane.Bounds.Top + deltaPixels,
            plane.Bounds.Right, plane.Bounds.Bottom + deltaPixels);
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
            _foregroundCoveredTop = _preparedCoveredTop; // carry the bake's coverage with the promoted plane
            _foregroundCoveredBot = _preparedCoveredBot;
            _cacheValid = true;
            DisposeObject(old);
            Debug.WriteLine($"[PLANE] SWAP consumed cover=[{_foregroundCoveredTop:0}..{_foregroundCoveredBot:0}]");
        }
    }


    public bool IsCaching
    {
        get;
        protected set
        {
            if (value == field) return;
            field = value;
            OnPropertyChanged();
            Debug.WriteLine($"IsCaching {value}");
        }
    }

    public override void DrawDirectInternal(DrawingContext context, SKRect drawingRect)
    {
        if (drawingRect.Height == 0 || drawingRect.Width == 0 || IsDisposed || IsDisposing)
            return;
 

        // INITIAL-MEASURE GATE: while a MeasureVisible pass hasn't measured every item yet, the content
        // extent is estimated and a scroll can enter the un-measured region. The plane only covers measured
        // cells, so blitting it there paints a BLANK band. Treat incomplete-measure exactly like a fast
        // fling — draw live cells directly (no plane) until measurement catches up, then resume caching.
        // (Re-arms during a LoadMore grow and resumes the plane once measure reaches the new tail.)
        if (MeasureItemsStrategy == MeasuringStrategy.MeasureVisible
            && (ItemsSource != null && LastMeasuredIndex < ItemsSource.Count - 1) 
            || ChildrenFactory.TotalCellsCount < ItemTemplatePoolSize
            )
        {
            // Keep the plane invalidated while gated: this branch skips all plane upkeep (UpdatePlanes /
            // CreateCache / _recordOffsetY), so ForegroundPlane would otherwise stay frozen at a pre-gate
            // scroll position. When the gate releases mid-scroll the stale plane would blit at the wrong
            // offset (cells-over-cells). Force a fresh record on resume.
            _contentChanged = true;
            _cacheValid = false;
            IsCaching = false;
            base.DrawDirectInternal(context, drawingRect);
            return;
        }


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

        // While an ordered ScrollToIndex is in flight the viewport is stepping toward a target that may be
        // OUTSIDE the cached band. Blitting the band skips Paint()/DrawStackVisibleChildren, so cells toward
        // the target never get tracked/positioned and the scroll stalls before reaching it (the "jump to a
        // replied message -> not shown" bug). Force live paint until the ordered scroll completes.
        bool orderedScroll = Parent is SkiaScroll os &&
                             (os.OrderedScrollToIndexIsSet || os.OrderedScrollTo.IsValid);

        // nothing moved, nothing changed
        if (!orderedScroll && _cacheValid && !dirty && !_contentChanged && destination == _lastDestination &&
            drawingRect == _lastDrawingRect)
        {
            canUseCache = true;
        }
        else
        {
            float stableAreaPx = ParentViewport.Pixels.Height;

            //band covers
            canUseCache = !orderedScroll && _cacheValid && !dirty && !_contentChanged &&
                          Math.Abs(destination.Top - _recordOffsetY) < stableAreaPx;
        }

        // In double-buffer the plane can be SEVERAL frames stale (async lag); the drift threshold alone would
        // reuse a plane the viewport has already outrun and blit an uncovered strip (the residual band). Also
        // require the viewport to still sit within the plane's recorded coverage. Sync re-records every miss,
        // so its plane is never stale enough — leave it on the cheaper drift check.
        if (canUseCache && UseDoubleBuffering && ForegroundPlane != null)
        {
            float vpTop = -destination.Top;
            float vpBot = vpTop + (float)ParentViewport.Pixels.Height;
            if (!(vpTop >= _foregroundCoveredTop - 1f && vpBot <= _foregroundCoveredBot + 1f))
                canUseCache = false;
        }

        if (!canUseCache)
        {
            _cacheValid = true;
            _recordOffsetY = destination.Top;
            _contentChanged = false;

            CreateCache(context, drawingRect);
        }

        _lastDestination = destination;
        _lastDrawingRect = drawingRect;

        // STALE-PLANE BAND GUARD: in double-buffer the plane can lag several frames behind a faster-than-bake
        // flick. CreateCache just kicked an async bake and kept the OLD plane; if that plane doesn't cover the
        // current viewport, blitting it paints an empty band. Draw the LIVE cells this ONE frame instead — the
        // correct "cache not ready yet" fallback (what the sync path does on every miss), incurred only on the
        // rare uncovered frame so smoothness is preserved. The next ready bake resumes blitting.
        if (UseDoubleBuffering && ForegroundPlane != null)
        {
            float vpTop = -destination.Top;
            float vpBot = vpTop + (float)ParentViewport.Pixels.Height;
            bool planeCovers = vpTop >= _foregroundCoveredTop - 1f && vpBot <= _foregroundCoveredBot + 1f;
            if (!planeCovers) //FAST FLING, JUMP
            {
                if (!_logLastLive)
                {
                    Debug.WriteLine($"[PLANE] LIVE-fallback START vp=[{vpTop:0}..{vpBot:0}] cover=[{_foregroundCoveredTop:0}..{_foregroundCoveredBot:0}] (plane uncovered)");
                    _logLastLive = true;
                }
                IsCaching = false;
                base.DrawDirectInternal(context, drawingRect); // live cells, no stale-plane band
                return;
            }
        }

        IsCaching = true;

        if (_logLastLive)
        {
            Debug.WriteLine($"[PLANE] BLIT resume");
            _logLastLive = false;
        }

        // Both modes blit here: double-buffer blits the current plane while a new one bakes; sync blits the
        // plane just recorded this frame. (Skipping this in sync mode left record frames empty/flickering.)
        DrawCache(context, destination);
    }

    CachedObject CreateRenderObject(DrawingContext context, SKRect recordArea)
    {
        dirtyAfterCache = 0;

        if (PlaneCacheType == SkiaCacheType.Operations)
        {
            // DEDICATED recorder per async bake (bg thread): the shared _recorder is owned by the render-thread
            // sync-fallback path, and a sync record can now run WHILE this bake is in flight — sharing one
            // SKPictureRecorder across threads is a native crash (EndRecording AV).
            using var recorder = new SKPictureRecorder();
            var rc = context.CreateForRecordingOperations(recorder, recordArea);

            Paint(rc); // => DrawStack, DrawStackVisibleChildren..

            var picture = recorder.EndRecording();

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

    /// <summary>
    /// Does the snapshot's measured cells TILE the content band [top..bot] with no gap and reach its bottom?
    /// A transient structure (mid grow/trim reposition) leaves a hole the async bake would freeze into the
    /// plane; detecting it here lets the caller record live (sync) instead. Cells are in index order.
    /// <para><paramref name="tol"/> is the gap tolerance in PIXELS — must be >= the layout inter-cell spacing
    /// (Spacing * RenderingScale), else the legitimate spacing between every cell reads as a hole and the gate
    /// rejects every valid bake (the Android blank-plane bug: 12px spacing vs a hardcoded 8px tol).</para>
    /// </summary>
    static bool SnapshotFillsViewport(LayoutStructure s, float top, float bot, float tol)
    {
        if (s == null) return false;
        float cursor = top;
        bool started = false;
        foreach (var c in s.GetChildren())
        {
            if (c == null || !c.WasMeasured) continue;
            float ct = c.Destination.Top, cb = c.Destination.Bottom;
            if (cb <= top) continue;            // entirely above the band
            if (ct >= bot) break;               // entirely below (index-ordered) -> done scanning
            if (!started)
            {
                // Empty space ABOVE the first cell in the band (viewport overscrolled past content top, e.g.
                // the inverted start-anchor putting bareTop negative) is NOT a hole — start covering at the
                // first cell instead of demanding tiling from the (out-of-content) band top.
                cursor = ct;
                started = true;
            }
            else if (ct > cursor + tol)
            {
                return false;                   // real gap between cells (> spacing)
            }
            if (cb > cursor) cursor = cb;
        }
        return cursor >= bot - tol;             // covered all the way to the band bottom
    }

    /// <summary>
    /// Are realized views in place for every snapshot cell overlapping the content band [top..bot]? The
    /// off-thread bake bails its whole draw on the first null GetViewForIndex; if any viewport cell isn't
    /// realized yet (pool mid-rekey after a grow), record live this frame instead so the bake never bails.
    /// </summary>
    bool ViewportViewsRealized(LayoutStructure s, float top, float bot)
    {
        if (s == null) return false;
        foreach (var c in s.GetChildren())
        {
            if (c == null || !c.WasMeasured) continue;
            if (c.Destination.Bottom <= top) continue; // above band
            if (c.Destination.Top >= bot) break;        // below band (index-ordered) -> done
            if (!ChildrenFactory.IsViewRealizedForIndex(c.ControlIndex))
                return false;
        }
        return true;
    }

    void CreateCache(DrawingContext context, SKRect recordArea)
    {
        // An ordered ScrollToIndex steps the viewport toward a target that may be outside the cached band; it
        // relies on LIVE paint each frame to track/position cells toward the target (the "jump to a replied
        // message" fix). Keep ordered-scroll frames on the SYNC path.
        bool orderedScroll = Parent is SkiaScroll os &&
                             (os.OrderedScrollToIndexIsSet || os.OrderedScrollTo.IsValid);

        // OVERSCAN: a record covers the INFLATED band [coveredTop..coveredBot] (viewport ± VirtualisationInflated
        // [Ratio]) so the plane can be REUSED (blitted, no record) while the bare viewport stays inside it.
        float vpH = (float)ParentViewport.Pixels.Height;
        float inflateY = (float)(VirtualisationInflated * RenderingScale);
        if (VirtualisationInflatedRatio >= 0)
            inflateY += (float)(VirtualisationInflatedRatio * vpH);
        float bareTop = -context.Destination.Top;
        float bareBot = bareTop + vpH;
        float coveredTop = bareTop - inflateY;
        float coveredBot = bareBot + inflateY;

        // SYNC only when async genuinely can't serve: not double-buffering, the FIRST plane (nothing to blit
        // yet), or an ordered-scroll jump. LoadMore / trim / normal scroll NEVER sync — a render-thread record
        // on those frames IS the spike double-buffering exists to kill. They bake ASYNC and keep blitting the
        // current (whole) plane; an incomplete bake is DISCARDED (below), never swapped in as a hole.
        bool useSync = !UseDoubleBuffering ||
                       (ForegroundPlane == null || orderedScroll);

        if (useSync)
        {
            Debug.WriteLine($"[PLANE] SYNC record firstPlane={ForegroundPlane == null} ordered={orderedScroll} cover=[{coveredTop:0}..{coveredBot:0}]");
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

            _foregroundCoveredTop = coveredTop; // a sync record covers the inflated band around the viewport
            _foregroundCoveredBot = coveredBot;
            return;
        }

        // ===== ASYNC bake: NO render-thread record => NO LoadMore/scroll spike =====
        // Keep blitting the current (whole) plane while a new one bakes off-thread. One bake at a time.
        if (_bakeInFlight)
        {
            Debug.WriteLine($"[PLANE] async-skip (bake in flight)");
            return;
        }

        Debug.WriteLine($"[PLANE] async-kick band=[{bareTop:0}..{bareBot:0}] cover=[{coveredTop:0}..{coveredBot:0}]");

        // Freeze the structure ONLY during an actual structure MUTATION (LoadMore grow / head-insert / trim) —
        // the only window the live structure is unsafe for the off-thread bake (cells mid-translate/rekey).
        // During PURE SCROLL the viewport cells are stable, so the bake reads LIVE with NO per-frame O(N) deep
        // copy: that copy on every scroll-dispatch was the periodic render-thread spike ("0.2s smooth, lag,
        // smooth"). Background measure only touches far off-viewport cells, so it needs no freeze.
        LayoutStructure bakeSnapshot = null;
        lock (LockMeasure)
        {
            bakeSnapshot = FreezeStructure(base.GetStackStructure());
        }
        _bakeInFlight = true;

        PushToOffscreenRendering(() =>
        {
            try
            {
                if (IsDisposed || IsDisposing)
                    return;

                _bakeStructure = bakeSnapshot; // null for raw -> bake reads live
                CachedObject rendered;
                try { rendered = CreateRenderObject(context, recordArea); }
                finally { _bakeStructure = null; }

                // Completeness gate: DrawStackVisibleChildren bails the whole draw on the first null view (pool
                // under-realized after a grow), and a transient structure can leave a position gap — either bakes
                // a HOLE. DISCARD an incomplete bake instead of swapping the hole in: keep the current whole
                // plane, retry next frame (the next bake renders further as the pool/measure catch up). Reliable
                // post-bake: ReleaseViewInUse keeps drawn cells mapped, bailed cells were never realized. ALL
                // off-thread => still zero render-thread work, zero spike. Guards-off skips the check (raw).
                // Pure-scroll bakes (no freeze) read the stable live viewport -> trust them. Only validate a
                // bake taken DURING a mutation (has a frozen snapshot), where a hole is possible.
                // gap tolerance must cover the real inter-cell spacing in pixels (Spacing * scale), plus a small
                // epsilon — a hardcoded 8px rejected the 12px-spaced chat cells and discarded every bake.
                float fillTol = Math.Max(8f, (float)(Spacing * RenderingScale) + 2f);
                bool fills = bakeSnapshot == null || SnapshotFillsViewport(bakeSnapshot, bareTop, bareBot, fillTol);
                bool realized = bakeSnapshot == null || ViewportViewsRealized(bakeSnapshot, bareTop, bareBot);
                bool complete = bakeSnapshot == null || (fills && realized);
                if (!complete)
                {
                    // dump snapshot measured-cell span to see if cells are in a different frame than the band
                    float sMin = float.MaxValue, sMax = float.MinValue; int sCnt = 0; float firstGapAt = float.NaN;
                    if (bakeSnapshot != null)
                    {
                        float cursor = bareTop;
                        foreach (var c in bakeSnapshot.GetChildren())
                        {
                            if (c == null || !c.WasMeasured) continue;
                            sCnt++;
                            if (c.Destination.Top < sMin) sMin = c.Destination.Top;
                            if (c.Destination.Bottom > sMax) sMax = c.Destination.Bottom;
                            if (c.Destination.Bottom <= bareTop) continue;
                            if (c.Destination.Top >= bareBot) continue;
                            if (float.IsNaN(firstGapAt) && c.Destination.Top > cursor + fillTol) firstGapAt = cursor;
                            if (c.Destination.Bottom > cursor) cursor = c.Destination.Bottom;
                        }
                    }
                    // list cells overlapping the band in ITERATION order (detect inverted/descending order or real gap)
                    var sb = new System.Text.StringBuilder();
                    if (bakeSnapshot != null)
                    {
                        int n = 0;
                        foreach (var c in bakeSnapshot.GetChildren())
                        {
                            if (c == null || !c.WasMeasured) continue;
                            if (c.Destination.Bottom <= bareTop || c.Destination.Top >= bareBot) continue;
                            sb.Append($" #{c.ControlIndex}:{c.Destination.Top:0}-{c.Destination.Bottom:0}");
                            if (++n >= 14) { sb.Append(" ..."); break; }
                        }
                    }
                    Debug.WriteLine($"[PLANE] DISCARD fills={fills} band=[{bareTop:0}..{bareBot:0}] snapCells={sCnt} snapSpan=[{sMin:0}..{sMax:0}] firstGapAt={firstGapAt:0} cells:{sb}");
                    DisposeObject(rendered);
                    _contentChanged = true; // force a retry next frame
                    return;
                }
                Debug.WriteLine($"[PLANE] PUBLISH bake cover=[{coveredTop:0}..{coveredBot:0}] (pureScroll={bakeSnapshot == null})");

                // Coverage set BEFORE publish so the render thread consumes a consistent plane+range pair.
                _preparedCoveredTop = coveredTop;
                _preparedCoveredBot = coveredBot;
                var stale = Interlocked.Exchange(ref _preparedPlane, rendered);
                if (stale != null)
                {
                    if (stale.Surface != null && Superview is DrawnView view)
                    {
                        view.ReturnSurface(stale.Surface);
                        stale.Surface = null;
                    }
                    DisposeObject(stale);
                }
            }
            finally
            {
                _bakeInFlight = false;
                Repaint();
            }
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