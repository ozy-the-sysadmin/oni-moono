using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace OniMoono.Engine
{
    /// The frame's time, frame by frame, for those who ask for it now: the performance recorder
    /// (OniMoono.Host.PerfRecorder, a JSON file per recording) and the frame rate counter's performance window.
    /// Each opens a window (Open), which then sums every frame it sees whole: the main thread's
    /// time by PlayerLoop's phases, the time outside the loop (the host's input poll and frame
    /// cap), what the main thread waited for that the engine owns (the device, texel copies) and
    /// the GC's pauses. While a window is open PlayerLoop times its phases as it does for
    /// `--frame-stats` (PlayerLoop.Timing), without logging anything; with none open nothing here
    /// runs. The counters it reads (FrameStatsWindow's) are its own between two frame ends, so
    /// frame stats' logging and its 600-frame resets go on as before.
    internal static class FrameWindows
    {
        /// PlayerLoop's phases in its own order (statTotals): render, gui, end of frame, present,
        /// early+fixed, update (scripts' Update, Starts, coroutines, directors), late (animation and
        /// LateUpdate), post-late (canvases), particles.
        public const int Phases = 9;
        public static readonly string[] PhaseNames = { "render", "gui", "end-of-frame", "present", "early-fixed", "update", "late", "post-late", "particles" };

        /// The main thread's waits read per frame (FrameStatsWindow), in this order: the device
        /// taking the next frame, device calls' results, last frame's texel copies, queued frames
        /// (vsync), and the device thread waiting for the presenter (not the main thread's).
        public const int Waits = 5;
        public static readonly string[] WaitNames = { "device-frame", "device-calls", "texel-copies", "queued-frames", "presenter-queue" };

        /// A window: what the frames it saw whole add up to. Read on the main thread; a window
        /// ends at a frame's end (End), after which it no longer changes.
        public sealed class Window
        {
            /// Frames seen whole, and their wall time (from the previous frame's end to theirs),
            /// in milliseconds.
            public long Frames;
            public double WallMs;
            /// The main thread's time by phase (PhaseNames) and outside the loop, milliseconds.
            public readonly double[] PhaseMs = new double[Phases];
            public double OutsideMs;
            /// Each phase's longest frame, and the longest outside part, milliseconds.
            public readonly double[] PhaseMaxMs = new double[Phases];
            public double OutsideMaxMs;
            /// Waits (WaitNames), milliseconds, and the device calls waited for.
            public readonly double[] WaitMs = new double[Waits];
            public long DeviceCalls;
            /// GC pauses: in all, frames with one, the most in a frame (milliseconds).
            public double GcPauseMs, GcMostInFrameMs;
            public long FramesWithGc;
            /// Each frame's wall time, when asked for (percentiles); null otherwise.
            public readonly List<float> FrameTimes;
            /// The longest frame, milliseconds.
            public double MaxFrameMs;
            /// Called on the main thread at the frame end where the window begins counting (its
            /// first frame is the next one) and where it stops (after its last frame): the
            /// snapshots a recording takes stay outside its frames.
            internal Action Began, Ended;
            internal bool counting, ending, closed;

            internal Window(bool frameTimes, int capacity) => FrameTimes = frameTimes ? new List<float>(capacity) : null;

            /// Whether the window has stopped (End was called and a frame ended since).
            public bool Closed => closed;

            /// Main-thread milliseconds summed over the phases and outside the loop: the wall
            /// time, up to the clock reads' own rounding.
            public double PartitionMs
            {
                get
                {
                    double sum = OutsideMs;
                    foreach (var ms in PhaseMs) sum += ms;
                    return sum;
                }
            }

            /// Starts the window over (the performance window's rolling view), keeping it open.
            public void Clear()
            {
                Frames = DeviceCalls = FramesWithGc = 0;
                WallMs = OutsideMs = OutsideMaxMs = GcPauseMs = GcMostInFrameMs = MaxFrameMs = 0;
                Array.Clear(PhaseMs);
                Array.Clear(PhaseMaxMs);
                Array.Clear(WaitMs);
                FrameTimes?.Clear();
            }
        }

        static readonly List<Window> open = new(), looping = new();
        static long lastEnd;
        static double lastPause = -1;
        static readonly long[] lastWaits = new long[Waits];
        static long lastCalls;
        static int lastEpoch = -1;
        static readonly double msPerTick = 1000.0 / Stopwatch.Frequency;

        /// Whether a window is open (PlayerLoop calls Frame only then).
        public static bool Any => open.Count > 0;

        /// Opens a window that counts from the frame after the current one ends. `frameTimes`
        /// keeps every frame's time (with room for `capacity`); `began` and `ended` run at the
        /// frame ends that bound it (see Window).
        public static Window Open(bool frameTimes = false, int capacity = 0, Action began = null, Action ended = null)
        {
            var window = new Window(frameTimes, capacity) { Began = began, Ended = ended };
            if (open.Count == 0)
            {
                // Timing starts: the next frame end only sets the baselines.
                lastEnd = 0;
                PlayerLoop.Timing = true;
            }
            open.Add(window);
            return window;
        }

        /// Ends a window at the current frame's end (its Ended runs there); a window that has not
        /// begun counting closes without calling Began.
        public static void End(Window window)
        {
            if (window == null || window.closed) return;
            window.ending = true;
        }

        /// Ends a window now, without its Ended (a scene change, a failure).
        public static void Drop(Window window)
        {
            if (window == null || window.closed) return;
            window.closed = true;
            open.Remove(window);
            if (open.Count == 0) PlayerLoop.Timing = PlayerLoop.FrameStats;
        }

        /// PlayerLoop, at each frame's end while a window is open: the frame's phases (ms).
        internal static void Frame(double[] phases)
        {
            long now = Stopwatch.GetTimestamp();
            bool whole = lastEnd != 0;
            double wall = whole ? (now - lastEnd) * msPerTick : 0;
            double inLoop = 0;
            for (int i = 0; i < Phases; i++) inLoop += phases[i];
            double outside = Math.Max(0, wall - inLoop);

            double pause = GC.GetTotalPauseDuration().TotalMilliseconds;
            double gc = lastPause >= 0 ? Math.Max(0, pause - lastPause) : 0;
            lastPause = pause;

            // FrameStatsWindow's counters, as this frame changed them: they start again from 0
            // when frame stats take their 600-frame window (Epoch).
            if (FrameStatsWindow.Epoch != lastEpoch)
            {
                lastEpoch = FrameStatsWindow.Epoch;
                Array.Clear(lastWaits);
                lastCalls = 0;
            }
            Span<long> waits = stackalloc long[Waits];
            waits[0] = FrameStatsWindow.DeviceFrameWait;
            waits[1] = FrameStatsWindow.DeviceCallWait;
            waits[2] = FrameStatsWindow.TexelCopyWait;
            waits[3] = FrameStatsWindow.QueuedFrameWait;
            waits[4] = Interlocked.Read(ref FrameStatsWindow.PresenterQueueWait);
            long calls = FrameStatsWindow.DeviceCalls;

            // Over a copy: a callback may open or drop windows (a drop inside Began used to make
            // the loop see a window twice and count its first frame early).
            looping.Clear();
            looping.AddRange(open);
            for (int w = looping.Count - 1; w >= 0; w--)
            {
                var window = looping[w];
                if (window.closed) continue;
                if (window.counting && whole)
                {
                    window.Frames++;
                    window.WallMs += wall;
                    if (wall > window.MaxFrameMs) window.MaxFrameMs = wall;
                    window.FrameTimes?.Add((float)wall);
                    for (int i = 0; i < Phases; i++)
                    {
                        window.PhaseMs[i] += phases[i];
                        if (phases[i] > window.PhaseMaxMs[i]) window.PhaseMaxMs[i] = phases[i];
                    }
                    window.OutsideMs += outside;
                    if (outside > window.OutsideMaxMs) window.OutsideMaxMs = outside;
                    for (int i = 0; i < Waits; i++) window.WaitMs[i] += Math.Max(0, waits[i] - lastWaits[i]) * msPerTick;
                    window.DeviceCalls += Math.Max(0, calls - lastCalls);
                    if (gc > 0)
                    {
                        window.FramesWithGc++;
                        window.GcPauseMs += gc;
                        if (gc > window.GcMostInFrameMs) window.GcMostInFrameMs = gc;
                    }
                }
                if (window.ending)
                {
                    window.closed = true;
                    open.Remove(window);
                    if (window.counting) Call(window.Ended);
                }
                else if (!window.counting && whole)
                {
                    window.counting = true;
                    Call(window.Began);
                }
            }
            for (int i = 0; i < Waits; i++) lastWaits[i] = waits[i];
            lastCalls = calls;
            if (open.Count == 0) PlayerLoop.Timing = PlayerLoop.FrameStats;
            // The callbacks' time falls between this frame and the next, in no window's frames.
            lastEnd = Stopwatch.GetTimestamp();
        }

        static void Call(Action action)
        {
            if (action == null) return;
            try
            {
                action();
            }
            catch (Exception e)
            {
                EngineLog.Engine($"frame windows: a callback failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
