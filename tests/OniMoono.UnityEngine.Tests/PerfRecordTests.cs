using System.Collections.Generic;
using OniMoono.Engine;
using OniMoono.Host.Perf;
using Xunit;

namespace OniMoono.Tests
{
    /// The performance recorder's parts that need no game: its spec (PerfSpec), the frame
    /// times' summary (FrameSummary) and the engine's frame windows (FrameWindows).
    public class PerfRecordTests
    {
        [Fact]
        public void SpecReadsEveryKeyAndKeepsDefaultsForBadValues()
        {
            var spec = PerfSpec.Parse("delay=10; seconds=30;count=2;gap=1.5;speeds=3,2;label=A3;out=C:\\perf;once=0", out var problems);
            Assert.Empty(problems);
            Assert.Equal(10, spec.Delay);
            Assert.Equal(30, spec.Seconds);
            Assert.Equal(2, spec.Count);
            Assert.Equal(1.5, spec.Gap);
            Assert.Equal(new[] { 3, 2 }, spec.Speeds);
            Assert.Equal(3, spec.SpeedFor(0));
            Assert.Equal(2, spec.SpeedFor(1));
            Assert.Equal(2, spec.SpeedFor(5));
            Assert.Equal("A3", spec.Label);
            Assert.Equal("C:\\perf", spec.Out);
            Assert.False(spec.Once);

            var bad = PerfSpec.Parse("seconds=0;count=x;speeds=4;colour=red;label=a/b;oops", out problems);
            Assert.Equal(60, bad.Seconds);
            Assert.Equal(3, bad.Count);
            Assert.Null(bad.Speeds);
            Assert.Equal(0, bad.SpeedFor(0));
            Assert.Equal("rec", bad.Label);
            Assert.Equal(6, problems.Count);
        }

        [Fact]
        public void SummaryGivesPercentilesAndLows()
        {
            var frames = new List<float>();
            for (int i = 0; i < 990; i++) frames.Add(5);
            for (int i = 0; i < 10; i++) frames.Add(50);
            var s = FrameSummary.Of(frames);
            Assert.Equal(1000, s.Frames);
            Assert.Equal(5.45, s.MeanMs, 6);
            Assert.Equal(5, s.P50Ms);
            Assert.Equal(5, s.P99Ms);
            Assert.Equal(50, s.P999Ms);
            Assert.Equal(50, s.MaxMs);
            Assert.Equal(20, s.Low1Fps, 6);
            Assert.Equal(10, s.Over33);
            Assert.Equal(0, s.Over50);
            Assert.Equal(0, FrameSummary.Of(new List<float>()).Fps);
        }

        [Fact]
        public void WindowCountsTheFramesBetweenItsBoundsAndSumsThePhases()
        {
            bool timing = PlayerLoop.Timing, stats = PlayerLoop.FrameStats;
            try
            {
                PlayerLoop.FrameStats = false;
                int began = 0, ended = 0;
                var phases = new double[FrameWindows.Phases];
                phases[5] = 2e-6; // ms: far below the time between two calls, so the wall time holds them
                phases[0] = 1e-6;
                var window = FrameWindows.Open(frameTimes: true, capacity: 8, began: () => began++, ended: () => ended++);
                Assert.True(PlayerLoop.Timing);
                FrameWindows.Frame(phases); // baselines only
                Assert.Equal(0, began);
                FrameWindows.Frame(phases); // the window begins at this frame's end
                Assert.Equal(1, began);
                Assert.Equal(0, window.Frames);
                FrameWindows.Frame(phases);
                FrameWindows.Frame(phases);
                FrameWindows.End(window);
                FrameWindows.Frame(phases); // counted, then the window ends
                Assert.Equal(1, ended);
                Assert.True(window.Closed);
                Assert.Equal(3, window.Frames);
                Assert.Equal(3, window.FrameTimes.Count);
                Assert.Equal(6e-6, window.PhaseMs[5], 12);
                Assert.Equal(3e-6, window.PhaseMs[0], 12);
                Assert.False(FrameWindows.Any);
                Assert.False(PlayerLoop.Timing);
                FrameWindows.Frame(phases);
                Assert.Equal(3, window.Frames);
            }
            finally
            {
                PlayerLoop.Timing = timing;
                PlayerLoop.FrameStats = stats;
            }
        }

        /// Frame stats start their wait counters again from 0 every 600 frames (Epoch): a window's
        /// waits are the counters' changes frame by frame, never a negative or a doubled jump.
        [Fact]
        public void WaitsCountTheirChangesAndStartAgainAtFrameStatsEpoch()
        {
            bool timing = PlayerLoop.Timing, stats = PlayerLoop.FrameStats;
            long queued = FrameStatsWindow.QueuedFrameWait;
            try
            {
                PlayerLoop.FrameStats = false;
                var phases = new double[FrameWindows.Phases];
                long tick = System.Diagnostics.Stopwatch.Frequency / 1000; // 1 ms of ticks
                FrameStatsWindow.QueuedFrameWait = 0;
                var window = FrameWindows.Open();
                FrameWindows.Frame(phases); // baselines
                FrameWindows.Frame(phases); // begins
                FrameStatsWindow.QueuedFrameWait += 3 * tick;
                FrameWindows.Frame(phases); // +3 ms
                Assert.Equal(3, window.WaitMs[3], 6);
                // Frame stats' window ends: its counters go back to 0, the epoch moves on.
                FrameStatsWindow.QueuedFrameWait = 2 * tick;
                FrameStatsWindow.Epoch++;
                FrameWindows.Frame(phases); // +2 ms from 0, not -1
                Assert.Equal(5, window.WaitMs[3], 6);
                FrameStatsWindow.QueuedFrameWait += tick;
                FrameWindows.Frame(phases);
                Assert.Equal(6, window.WaitMs[3], 6);
                FrameWindows.Drop(window);
            }
            finally
            {
                FrameStatsWindow.QueuedFrameWait = queued;
                PlayerLoop.Timing = timing;
                PlayerLoop.FrameStats = stats;
            }
        }

        /// Two windows count side by side (the window's tabs and a recording), and a window
        /// dropped from inside another's callback leaves the other going.
        [Fact]
        public void TwoWindowsAndADropInsideACallback()
        {
            bool timing = PlayerLoop.Timing, stats = PlayerLoop.FrameStats;
            try
            {
                PlayerLoop.FrameStats = false;
                var phases = new double[FrameWindows.Phases];
                FrameWindows.Window other = null;
                var first = FrameWindows.Open();
                other = FrameWindows.Open(began: () => FrameWindows.Drop(first));
                FrameWindows.Frame(phases); // baselines
                FrameWindows.Frame(phases); // both begin; the second's Began drops the first
                Assert.True(first.Closed);
                Assert.False(other.Closed);
                Assert.True(FrameWindows.Any);
                FrameWindows.Frame(phases);
                FrameWindows.Frame(phases);
                Assert.Equal(2, other.Frames);
                Assert.Equal(0, first.Frames);
                FrameWindows.End(other);
                FrameWindows.Frame(phases);
                Assert.Equal(3, other.Frames);
                Assert.False(FrameWindows.Any);
                Assert.False(PlayerLoop.Timing);
            }
            finally
            {
                PlayerLoop.Timing = timing;
                PlayerLoop.FrameStats = stats;
            }
        }

        [Fact]
        public void TheEnvironmentsSpecLosesItsSpeeds()
        {
            Assert.Equal("seconds=30;label=x", PerfSpec.WithoutSpeeds("seconds=30;speeds=3,2;label=x", out bool dropped));
            Assert.True(dropped);
            Assert.Equal("seconds=30", PerfSpec.WithoutSpeeds("seconds=30", out dropped));
            Assert.False(dropped);
            Assert.Equal("delay=1", PerfSpec.WithoutSpeeds("delay=1; Speeds=1", out dropped));
            Assert.True(dropped);
        }
    }
}
