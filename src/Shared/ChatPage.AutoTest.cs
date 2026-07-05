using System.Diagnostics;
using System.Linq;
using DrawnUi;
using DrawnUi.Draw;

namespace DrawnChatList;

/// <summary>
/// Self-contained automated test driver for the chat page. DISABLED by default — the app runs
/// completely normally. Flip <see cref="AutoTestEnabled"/> to true (single switch) to have the app
/// drive itself after startup and print [AUTOTEST] PASS/FAIL lines to the console (adb logcat
/// mono-stdout on Android). Used to validate ScrollToOldest jumps and scroll smoothness on-device
/// without manual input. Safe to leave in the project: when the flag is false this file does nothing.
/// </summary>
public partial class ChatPage
{
    /// <summary>
    /// THE SWITCH: false = normal app for manual use (default). true = auto-test drives the chat
    /// after startup and logs [AUTOTEST] results.
    /// </summary>
    public static bool AutoTestEnabled = false;

    /// <summary>
    /// Second switch: passive motion tracer for MANUAL scrolling. Logs only anomalies — frames where
    /// scroll motion abruptly HOLDS (offset stops mid-fling) or JUMPS, with window/loading state.
    /// Near-zero overhead per frame; find lines via logcat tag DOTNET, marker [MOTION].
    /// </summary>
    public static bool MotionTraceEnabled = true;

    private bool _autoTestStarted;

    partial void MaybeStartAutoTest()
    {

        if (_autoTestStarted)
            return;

        if (MotionTraceEnabled)
        {
            _autoTestStarted = true;
            StartMotionTrace();
            // both switches on = trace the suite too (HOLD/SLOWFRAME lines during automated runs)
            if (AutoTestEnabled)
                _ = Task.Run(RunAutoTestsAsync);
            return;
        }

        if (!AutoTestEnabled)
            return;
        _autoTestStarted = true;
        _ = Task.Run(RunAutoTestsAsync);
    }

    private void StartMotionTrace()
    {
        float lastOff = float.NaN;
        float lastDelta = 0;
        long holdStart = 0;
        float holdVelocity = 0;
        long lastTs = 0;
        int violations = 0;

        // frame-pacing histogram: deltas between drawn frames WHILE the scroll offset is moving.
        // Dumped once motion stops. Answers "uniform-slow (draw cost ceiling) vs bimodal (spikes)".
        var histo = new int[7]; // <17 <25 <33 <50 <80 <120 >=120 ms
        int histoCount = 0;
        long histoIdleSince = 0;

        void OnFrame(object s, DrawnUi.Draw.SkiaDrawingContext? e)
        {
            // per-frame STRUCTURE integrity (duplicates/sequence/overlap) — silent until a violation
            if (violations < 6)
            {
                var report = TreeIntegrityReport();
                if (report != null)
                {
                    violations++;
                    Log($"[t6-frame] {report}");
                }
            }

            long now = Stopwatch.GetTimestamp();
            var off = MainScroll.ViewportOffsetY;
            if (!float.IsNaN(lastOff))
            {
                var delta = off - lastOff;
                double frameMs = lastTs != 0 ? (now - lastTs) * 1000.0 / Stopwatch.Frequency : 0;

                bool wasMoving = Math.Abs(lastDelta) > 4f;
                bool isStill = Math.Abs(delta) < 0.1f;

                if (wasMoving && isStill && holdStart == 0)
                {
                    holdStart = now;
                    holdVelocity = lastDelta;
                }
                else if (holdStart != 0 && !isStill)
                {
                    double holdMs = (now - holdStart) * 1000.0 / Stopwatch.Frequency;
                    if (holdMs > 32)
                        Log($"[MOTION] HOLD {holdMs:0}ms at offY={off:0} (was moving {holdVelocity:0}/frame) " +
                            $"win=[{_limitedSource.WindowStart}..{_limitedSource.WindowEnd}) " +
                            $"older={_limitedSource.IsLoadingOlder} newer={_limitedSource.IsLoadingNewer} jump={_limitedSource.IsLoadingJump}");
                    holdStart = 0;
                }
                else if (holdStart == 0 && wasMoving && Math.Sign(delta) != 0
                         && Math.Sign(delta) == -Math.Sign(lastDelta) && Math.Abs(delta) > 40)
                {
                    Log($"[MOTION] REVERSAL {lastDelta:0} -> {delta:0} at offY={off:0} " +
                        $"win=[{_limitedSource.WindowStart}..{_limitedSource.WindowEnd})");
                }
                else if (frameMs > 32 && (wasMoving || Math.Abs(delta) > 4f))
                {
                    Log($"[MOTION] SLOWFRAME {frameMs:0}ms delta={delta:0} at offY={off:0} " +
                        $"win=[{_limitedSource.WindowStart}..{_limitedSource.WindowEnd}) " +
                        $"older={_limitedSource.IsLoadingOlder}");
                }

                if (!isStill)
                    lastDelta = delta;

                // histogram sampling: any frame that belongs to active motion
                if (!isStill || wasMoving)
                {
                    if (frameMs > 0)
                    {
                        int bucket = frameMs < 17 ? 0 : frameMs < 25 ? 1 : frameMs < 33 ? 2 :
                            frameMs < 50 ? 3 : frameMs < 80 ? 4 : frameMs < 120 ? 5 : 6;
                        histo[bucket]++;
                        histoCount++;
                    }

                    histoIdleSince = 0;
                }
                else if (histoCount > 30)
                {
                    if (histoIdleSince == 0)
                    {
                        histoIdleSince = now;
                    }
                    else if ((now - histoIdleSince) * 1000.0 / Stopwatch.Frequency > 700)
                    {
                        Log($"[PACING] frames={histoCount} <17ms={histo[0]} 17-25={histo[1]} 25-33={histo[2]} " +
                            $"33-50={histo[3]} 50-80={histo[4]} 80-120={histo[5]} >120={histo[6]}");
                        Array.Clear(histo);
                        histoCount = 0;
                        histoIdleSince = 0;
                    }
                }
            }

            lastOff = off;
            lastTs = now;
        }

        var view = MainScroll.Superview;
        if (view != null)
        {
            view.WasDrawn += OnFrame;
            Log("[MOTION] trace armed");
        }
        else
        {
            // canvas not attached yet — retry shortly
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 50 && MainScroll.Superview == null; i++)
                    await Task.Delay(100);
                var v = MainScroll.Superview;
                if (v != null) { v.WasDrawn += OnFrame; Log("[MOTION] trace armed (late)"); }
            });
        }
    }

    private async Task RunAutoTestsAsync()
    {
        try
        {
            Log("waiting for first window...");
            for (int i = 0; i < 200 && ChatStack.LastVisibleIndex < 0; i++)
                await Task.Delay(100);
            await Task.Delay(2000); // let startup measurement/anims settle

            int pass = 0, fail = 0;
            void Check(bool ok, string name)
            {
                if (ok) pass++; else fail++;
                Log($"{(ok ? "PASS" : "FAIL")} {name} win=[{_limitedSource.WindowStart}..{_limitedSource.WindowEnd}) " +
                    $"vis=[{ChatStack.FirstVisibleIndex}..{ChatStack.LastVisibleIndex}] offY={MainScroll.ViewportOffsetY:0}");
            }

            // TEST 1: cold jump to oldest
            MainThread.BeginInvokeOnMainThread(() => ScrollToOldestForTest());
            await SettleAsync("jump1");
            Check(AtOldestTop(), "jump1-cold");

            // TEST 2: back to newest, then the CONSECUTIVE jump (the reported bug)
            MainThread.BeginInvokeOnMainThread(() => ScrollToNewestForTest());
            await SettleAsync("back1");
            MainThread.BeginInvokeOnMainThread(() => ScrollToOldestForTest());
            await SettleAsync("jump2");
            Check(AtOldestTop(), "jump2-consecutive");

            // TEST 3: back to newest, fling into history, jump mid-fling (the original repro)
            MainThread.BeginInvokeOnMainThread(() => ScrollToNewestForTest());
            await SettleAsync("back2");
            MainThread.BeginInvokeOnMainThread(() => MainScroll.ScrollTo(0, MainScroll.ViewportOffsetY - 2500, 1.5f, true));
            await Task.Delay(350); // fling in flight
            MainThread.BeginInvokeOnMainThread(() => ScrollToOldestForTest());
            await SettleAsync("jump3");
            Check(AtOldestTop(), "jump3-mid-fling");

            // TEST 4: smoothness — programmatic flings through history with natural LoadMore,
            // sampling Scrolled-event gaps as a frame-cadence proxy.
            MainThread.BeginInvokeOnMainThread(() => ScrollToNewestForTest());
            await SettleAsync("back3");

            // Per-FRAME probe: DrawnView.WasDrawn fires for every drawn frame. Count a gap only when
            // the scroll offset moved since the previous frame (i.e. the frame belongs to an active
            // scroll animation) so idle pauses between flings don't register as fake spikes.
            var gaps = new List<double>();
            long last = 0;
            int rawFrames = 0;
            float lastOffProbe = float.NaN;
            void OnFrameProbe(object s, object e)
            {
                rawFrames++;
                long now = Stopwatch.GetTimestamp();
                var off = MainScroll.ViewportOffsetY;
                if (off != lastOffProbe)
                {
                    if (last != 0)
                        gaps.Add((now - last) * 1000.0 / Stopwatch.Frequency);
                    last = now;
                }
                else
                {
                    last = 0; // idle frame: restart the gap chain
                }

                lastOffProbe = off;
            }

            var view = MainScroll.Superview;
            EventHandler<DrawnUi.Draw.SkiaDrawingContext?> frameHandler = (s, e) => OnFrameProbe(s, e);
            if (view != null)
                view.WasDrawn += frameHandler;

            long allocBefore = GC.GetTotalAllocatedBytes(false);
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            var smoothSw = Stopwatch.StartNew();

            for (int k = 0; k < 8; k++)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                    MainScroll.ScrollTo(0, MainScroll.ViewportOffsetY - 1600, 0.8f, true));
                await Task.Delay(1000);
            }

            smoothSw.Stop();
            if (view != null)
                view.WasDrawn -= frameHandler;

            long allocDelta = GC.GetTotalAllocatedBytes(false) - allocBefore;
            Log($"SMOOTH-RAW frames={rawFrames} in {smoothSw.ElapsedMilliseconds}ms " +
                $"(~{rawFrames * 1000.0 / smoothSw.ElapsedMilliseconds:0.0} fps) " +
                $"alloc={allocDelta / 1024.0 / 1024.0:0.0}MB ({allocDelta / Math.Max(1, rawFrames) / 1024.0:0.0}KB/frame) " +
                $"GC[g0+={GC.CollectionCount(0) - gc0} g1+={GC.CollectionCount(1) - gc1} g2+={GC.CollectionCount(2) - gc2}]");

            if (gaps.Count > 10)
            {
                gaps.Sort();
                double avg = gaps.Average();
                double p95 = gaps[(int)(gaps.Count * 0.95)];
                double max = gaps[^1];
                int spikes32 = gaps.Count(g => g > 32);
                int spikes50 = gaps.Count(g => g > 50);
                Log($"SMOOTH frames={gaps.Count} avg={avg:0.0}ms p95={p95:0.0}ms max={max:0.0}ms " +
                    $"spikes>32ms={spikes32} spikes>50ms={spikes50}");
                Check(spikes50 <= 2, "smoothness(spikes>50ms<=2)");
            }
            else
            {
                Log("SMOOTH: not enough samples");
                fail++;
            }

            // TEST 5: delete-message integrity (the reported "..270, 288, GAP, 273.." corruption):
            // scroll into history, delete a first-of-day message there, scroll away and back through
            // the deletion site, then verify the visible sequence is consecutive.
            MainThread.BeginInvokeOnMainThread(() => ScrollToNewestForTest());
            await SettleAsync("del-home");

            async Task PanAsync(float deltaUnits) // negative = into history (offY decreases)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                    MainScroll.ScrollTo(0, MainScroll.ViewportOffsetY + deltaUnits, 0.6f, true));
                await Task.Delay(750);
            }

            for (int k = 0; k < 3; k++) await PanAsync(-1200);
            await SettleAsync("del-scrollin");

            ChatMessage victim = null;
            var tree = ChatStack.RenderTree;
            if (tree != null)
                foreach (var t in tree)
                    if (t.FreezeBindingContext is ChatMessage m && (victim == null || m.IsFirstDay))
                    {
                        victim = m.IsFirstDay ? m : victim ?? m;
                        if (m.IsFirstDay) break;
                    }

            if (victim != null)
            {
                Log($"TEST5 deleting Index={victim.Index} firstOfDay={victim.IsFirstDay}");
                var deleted = victim;
                MainThread.BeginInvokeOnMainThread(() => DeleteMessage(deleted));
                await Task.Delay(800);

                for (int k = 0; k < 4; k++) await PanAsync(-1200); // deeper into history
                for (int k = 0; k < 4; k++) await PanAsync(1200);  // back through the site
                await SettleAsync("del-return");

                var seq = new List<(float top, int idx)>();
                tree = ChatStack.RenderTree;
                if (tree != null)
                    foreach (var t in tree)
                        if (t.FreezeBindingContext is ChatMessage m)
                            seq.Add((t.HitRect.Top, m.Index));
                seq.Sort((a, b) => a.top.CompareTo(b.top));

                bool ordered = seq.Count >= 5;
                int skips = 0;
                for (int i = 1; i < seq.Count; i++)
                {
                    var diff = seq[i].idx - seq[i - 1].idx;
                    if (diff == -1) continue;
                    if (diff == -2) { skips++; continue; }
                    ordered = false;
                    Log($"TEST5 BAD SEQUENCE: {seq[i - 1].idx} -> {seq[i].idx} (diff {diff})");
                }

                Check(ordered && skips <= 1, $"delete-integrity(cells={seq.Count} skips={skips})");
            }
            else
            {
                Log("TEST5 no victim found");
                fail++;
            }

            // TEST 6: user-reported duplicate-cell corruption flow — delete a visible message at
            // startup-ish state, several oldest<->newest jump cycles, then from oldest scroll DOWN
            // toward newest (LoadNewer + tail trims). Integrity is checked EVERY FRAME via WasDrawn
            // (the corruption is transient/racy — post-settle sampling missed what the user saw live).
            int frameViolations = 0;
            string firstViolation = null;
            void OnFrameIntegrity(object s, DrawnUi.Draw.SkiaDrawingContext? e)
            {
                if (frameViolations > 4) return; // enough evidence, stop spamming
                var report = TreeIntegrityReport();
                if (report != null)
                {
                    frameViolations++;
                    firstViolation ??= report;
                    Log($"[t6-frame] {report}");
                }
            }

            var view6 = MainScroll.Superview;
            if (view6 != null)
                view6.WasDrawn += OnFrameIntegrity;

            MainThread.BeginInvokeOnMainThread(() => ScrollToNewestForTest());
            await SettleAsync("t6-home");

            // PREFER a first-of-day victim: both observed corruptions involved day-chip cells — the
            // successor inherits IsFirstDay live (chip + remeasure), which is part of the recipe
            ChatMessage victim6 = null;
            var tree6 = ChatStack.RenderTree;
            if (tree6 != null)
                foreach (var t in tree6)
                    if (t.FreezeBindingContext is ChatMessage m)
                    {
                        if (m.IsFirstDay)
                        {
                            victim6 = m;
                            break;
                        }

                        victim6 ??= m;
                    }

            if (victim6 != null)
            {
                var deleted6 = victim6;
                Log($"TEST6 deleting Index={deleted6.Index}");
                MainThread.BeginInvokeOnMainThread(() => DeleteMessage(deleted6));
                await Task.Delay(800);

                for (int c = 0; c < 3; c++)
                {
                    MainThread.BeginInvokeOnMainThread(() => ScrollToOldestForTest());
                    await SettleAsync($"t6-old{c}");
                    MainThread.BeginInvokeOnMainThread(() => ScrollToNewestForTest());
                    await SettleAsync($"t6-new{c}");
                }

                MainThread.BeginInvokeOnMainThread(() => ScrollToOldestForTest());
                await SettleAsync("t6-oldest");

                // CHAOTIC pans toward newest: no settling, variable speeds, occasional reverse —
                // mimic a human thumb; per-frame integrity hook does the detection
                var rnd = new Random(12345);
                for (int k = 0; k < 22 && frameViolations == 0; k++)
                {
                    var dist = 900 + rnd.Next(1200);
                    var back = rnd.Next(4) == 0; // sometimes flick back
                    MainThread.BeginInvokeOnMainThread(() =>
                        MainScroll.ScrollTo(0, MainScroll.ViewportOffsetY + (back ? -dist : dist), 0.35f, true));
                    await Task.Delay(180 + rnd.Next(260)); // often interrupt the previous fling
                }

                await Task.Delay(1500);
                var final6 = TreeIntegrityReport();
                if (final6 != null) Log($"[t6-final-rest] {final6}");
                Check(frameViolations == 0 && final6 == null,
                    $"delete-jump-cycle-integrity(frames={frameViolations})");
            }
            else
            {
                Log("TEST6 no victim");
                fail++;
            }

            // keep the per-frame integrity watcher ARMED after the suite: manual/adb REAL-touch
            // swipes go through the gesture pipeline (a thread the programmatic ScrollTo pans never
            // exercise) — any later corruption still gets logged as [t6-frame]
            if (firstViolation != null)
                Log($"TEST6 FIRST VIOLATION: {firstViolation}");

            Log($"SUMMARY pass={pass} fail={fail}");
            frameViolations = 0; // fresh budget for the post-suite manual/adb phase
            Log("integrity watcher stays ARMED for manual/adb gestures");
        }
        catch (Exception ex)
        {
            Log($"CRASHED {ex}");
        }
    }

    private bool AtOldestTop()
    {
        // Landed at the very top of history: window rebased to 0 and the OLDEST message
        // (local index Items.Count-1 in the inverted list) is actually visible.
        return _limitedSource.WindowStart == 0
               && ChatStack.LastVisibleIndex == _limitedSource.Items.Count - 1;
    }

    private async Task SettleAsync(string label)
    {
        float lastOff = float.NaN;
        int stableMs = 0;
        for (int t = 0; t < 15000; t += 100)
        {
            await Task.Delay(100);
            var off = MainScroll.ViewportOffsetY;
            bool busy = MainScroll.OrderedScrollToIndexIsSet
                        || _limitedSource.IsLoadingJump || _limitedSource.IsLoadingOlder || _limitedSource.IsLoadingNewer;
            if (!busy && Math.Abs(off - lastOff) < 0.5f)
            {
                stableMs += 100;
                if (stableMs >= 700)
                    return;
            }
            else
            {
                stableMs = 0;
            }

            lastOff = off;
        }

        Log($"WARNING {label} did not settle in 15s (ordered={MainScroll.OrderedScrollToIndexIsSet})");
    }

    // Wrappers over the private jump helpers so the driver reuses the exact app code paths.
    private void ScrollToNewestForTest() => ScrollToNewest(true);

    private void ScrollToOldestForTest() => ScrollToOldest(true);

    /// <summary>Null = tree healthy; otherwise a one-line report of the first violation with a
    /// dump of the visible cells ("idx@top+h ..") for post-mortem.</summary>
    private string TreeIntegrityReport()
    {
        var seq = new List<(float top, float bottom, int idx)>();
        var tree = ChatStack.RenderTree;
        if (tree == null) return null;
        foreach (var t in tree)
            if (t.FreezeBindingContext is ChatMessage m)
                seq.Add((t.HitRect.Top, t.HitRect.Bottom, m.Index));
        if (seq.Count < 3) return null;

        string Dump()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in seq)
                sb.Append(c.idx).Append('@').Append(c.top.ToString("0")).Append('+')
                    .Append((c.bottom - c.top).ToString("0")).Append(' ');
            return sb.ToString();
        }

        var seen = new HashSet<int>();
        foreach (var c in seq)
            if (!seen.Add(c.idx))
                return $"DUPLICATE msg {c.idx} | {Dump()}";

        seq.Sort((a, b) => a.top.CompareTo(b.top));
        int skips = 0;
        for (int i = 1; i < seq.Count; i++)
        {
            var diff = seq[i].idx - seq[i - 1].idx;
            if (diff == -1) continue;
            if (diff == -2 && skips++ == 0) continue;
            return $"BAD SEQ {seq[i - 1].idx}->{seq[i].idx} (diff {diff}) | {Dump()}";
        }

        float prevBottom = float.NaN;
        foreach (var c in seq)
        {
            if (!float.IsNaN(prevBottom) && prevBottom - c.top > 2f)
                return $"OVERLAP {prevBottom - c.top:0}px at msg {c.idx} | {Dump()}";
            prevBottom = Math.Max(float.IsNaN(prevBottom) ? c.bottom : prevBottom, c.bottom);
        }

        return null;
    }

    /// <summary>Duplicate indices, broken sequence or overlapping cells in the render tree.</summary>
    private bool CheckTreeIntegrity(string label)
    {
        var seq = new List<(float top, float bottom, int idx)>();
        var tree = ChatStack.RenderTree;
        if (tree == null) return true;
        foreach (var t in tree)
            if (t.FreezeBindingContext is ChatMessage m)
                seq.Add((t.HitRect.Top, t.HitRect.Bottom, m.Index));
        if (seq.Count < 3) return true;

        var seen = new HashSet<int>();
        foreach (var c in seq)
            if (!seen.Add(c.idx))
            {
                Log($"[{label}] DUPLICATE cell for msg {c.idx}!");
                return false;
            }

        seq.Sort((a, b) => a.top.CompareTo(b.top));
        int skips = 0;
        for (int i = 1; i < seq.Count; i++)
        {
            var diff = seq[i].idx - seq[i - 1].idx;
            if (diff == -1) continue;
            if (diff == -2 && skips++ == 0) continue;
            Log($"[{label}] BAD SEQUENCE {seq[i - 1].idx} -> {seq[i].idx} (diff {diff})");
            return false;
        }

        float prevBottom = float.NaN;
        foreach (var c in seq)
        {
            if (!float.IsNaN(prevBottom) && prevBottom - c.top > 2f)
            {
                Log($"[{label}] OVERLAP {prevBottom - c.top:0}px at msg {c.idx}");
                return false;
            }
            prevBottom = Math.Max(float.IsNaN(prevBottom) ? c.bottom : prevBottom, c.bottom);
        }

        return true;
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[AUTOTEST] {message}");
    }
}
