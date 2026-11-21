#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The frame rate counter's memory (PerfTrack): the light's rules and their hysteresis, the
    /// causes, the summaries; and the Colony tab's advice rules (PerfAdvice).
    public class PerfTrackTests
    {
        /// Feeds `seconds` of frames of `ms` each (every `slowEvery`-th frame `slowMs`, waiting for
        /// the simulation), the game advancing `rate` game seconds per real second at speed `scale`.
        static double Feed(PerfTrack t, double now, double seconds, double ms, double scale = 3, double rate = 3,
            int slowEvery = 0, double slowMs = 0, bool paused = false, bool colony = true)
        {
            int i = 0;
            double end = now + seconds;
            while (now < end)
            {
                bool slow = slowEvery > 0 && ++i % slowEvery == 0;
                double m = slow ? slowMs : ms;
                now += m / 1000;
                t.Add(new PerfFrame
                {
                    Now = now, Ms = m, InColony = colony, InMenu = !colony, Paused = paused, Scale = scale,
                    GameDt = paused ? 0 : m / 1000 * rate, ZeroSubTick = slow,
                });
            }
            return now;
        }

        [Fact]
        public void SmoothPlayIsGreenWithTheRate()
        {
            var t = new PerfTrack();
            Feed(t, 0, 12, 6);
            Assert.Equal(0, t.Light);
            Assert.InRange(t.Fps, 160, 175);
            Assert.InRange(t.KeepUp, 0.99, 1.01);
            Assert.InRange(t.MedianMs, 5.5, 6.5);
        }

        [Fact]
        public void FrequentStuttersTurnItYellowAndItClearsAfterTwoSeconds()
        {
            var t = new PerfTrack();
            // 126 a minute of 21 ms frames among 5.6 ms ones, as on McBench (session AB).
            double now = Feed(t, 0, 12, 5.6, slowEvery: 80, slowMs: 21);
            Assert.Equal(1, t.Light);
            Assert.True(t.Stutters10s >= 6);
            var last = t.LastMinute(10);
            Assert.True(last.SimWaits > 0 && last.SimWaits == last.Stutters);
            Assert.InRange(last.PerMinute(last.Stutters), 115, 140);
            Assert.Equal(PerfCause.SimWait, (PerfCause)Array.IndexOf(t.CauseTimes, t.CauseTimes.Max()));
            // Smooth again: still yellow while the 10 s window holds the stutters, then green.
            now = Feed(t, now, 4, 5.6);
            Assert.Equal(1, t.Light);
            Feed(t, now, 10, 5.6);
            Assert.Equal(0, t.Light);
        }

        [Fact]
        public void ARareStutterDoesNotFlicker()
        {
            var t = new PerfTrack();
            // One 25 ms frame every 3 s: never yellow.
            var lights = new HashSet<int>();
            double now = 0;
            for (int k = 0; k < 10; k++)
            {
                now = Feed(t, now, 3, 6, slowEvery: 499, slowMs: 25);
                lights.Add(t.Light);
            }
            Assert.Equal(new[] { 0 }, lights.ToArray());
        }

        [Fact]
        public void FallingBehindTheSpeedTurnsItRedAndPauseGreen()
        {
            var t = new PerfTrack();
            // 100 ms frames at speed 3 with the game capped at 0.2 game s a frame: 2 game s per s.
            double now = Feed(t, 0, 8, 100, scale: 3, rate: 2);
            Assert.Equal(2, t.Light);
            Assert.InRange(t.KeepUp, 0.6, 0.7);
            now = Feed(t, now, 2, 100, paused: true);
            Assert.Equal(0, t.Light);
            // Keeping up again.
            now = Feed(t, now, 8, 6);
            Assert.Equal(0, t.Light);
        }

        [Fact]
        public void OutsideAColonyItIsGreenAndCountsNoStutters()
        {
            var t = new PerfTrack();
            Feed(t, 0, 12, 5.6, slowEvery: 20, slowMs: 30, colony: false);
            Assert.Equal(0, t.Light);
            Assert.Equal(0, t.CauseTimes.Sum());
        }

        [Fact]
        public void SavesTheFirstFrameAndLoadsAreNamed()
        {
            var t = new PerfTrack { StartedAt = 0 };
            double now = 1;
            // Menu, then a load (between scenes), then the colony, paused.
            t.Add(new PerfFrame { Now = now += 0.01, Ms = 10, InMenu = true });
            for (int i = 0; i < 50; i++) t.Add(new PerfFrame { Now = now += 0.2, Ms = 200, Loading = true });
            t.Add(new PerfFrame { Now = now += 0.01, Ms = 10, InColony = true, Paused = true, Scale = 0 });
            Assert.InRange(t.StartToMenuMs, 1000, 1100);
            Assert.InRange(t.LoadMs, 9900, 10300);
            Assert.Equal(50, t.CauseTimes[(int)PerfCause.Load]);
            // The first unpaused frame.
            t.Add(new PerfFrame { Now = now += 0.3, Ms = 300, InColony = true, Scale = 3, GameDt = 0.2 });
            Assert.Equal(300, t.FirstFrameMs);
            // An autosave, its preview frames, a timelapse and a GC pause.
            t.Add(new PerfFrame { Now = now += 0.8, Ms = 800, GcMs = 90, JitMs = 40, InColony = true, Scale = 3, GameDt = 0.2, Saved = true, AutoSave = true });
            t.Add(new PerfFrame { Now = now += 0.05, Ms = 50, InColony = true, Scale = 3, GameDt = 0.15, Preview = true });
            Assert.Equal(850, t.LastSaveMs);
            Assert.Equal(850, t.FirstSaveMs);
            var save = t.Freezes.Last();
            Assert.Equal(PerfCause.Autosave, save.Cause);
            Assert.Equal((90.0, 40.0, 50.0), (save.GcMs, save.JitMs, save.PreviewMs));
            now = Feed(t, now, 3, 6);
            t.Add(new PerfFrame { Now = now += 0.2, Ms = 200, InColony = true, Scale = 3, GameDt = 0.2, Timelapse = true });
            t.Add(new PerfFrame { Now = now += 0.15, Ms = 150, GcMs = 120, InColony = true, Scale = 3, GameDt = 0.2 });
            t.Add(new PerfFrame { Now = now += 0.12, Ms = 120, InColony = true, Scale = 3, GameDt = 0.2 });
            Assert.Equal(new[] { PerfCause.Timelapse, PerfCause.Gc, PerfCause.Unknown }, t.Freezes.Skip(t.Freezes.Count - 3).Select(f => f.Cause).ToArray());
        }

        [Fact]
        public void SummaryPercentilesComeFromTheFrames()
        {
            var t = new PerfTrack();
            double now = Feed(t, 0, 10, 5, slowEvery: 50, slowMs: 25);
            var s = t.Summarize(10, now);
            Assert.InRange(s.MedianMs, 4.9, 5.1);
            Assert.InRange(s.P99Ms, 24.9, 25.1);
            Assert.Equal(25, s.MaxMs);
            Assert.True(s.Stutters > 0);
            var cols = new float[100];
            var kinds = new byte[100];
            t.Columns(10, now, cols, kinds);
            Assert.Contains((byte)1, kinds);
            Assert.True(cols.Max() >= 25);
        }

        [Fact]
        public void AMinuteOfPlayIsTheOneSourceAndPausedFramesAreNotStutters()
        {
            var t = new PerfTrack();
            // 30 s paused with slow frames (the player in menus), then 30 s of play at 126 a minute.
            double now = Feed(t, 0, 30, 5.6, slowEvery: 40, slowMs: 30, paused: true);
            now = Feed(t, now, 30, 5.6, slowEvery: 80, slowMs: 21);
            var last = t.LastMinute();
            Assert.InRange(last.PlaySeconds, 28, 31);
            // Only play's stutters, per second of play: the same number the verdict, Right now,
            // the advice and the Hitches tab show.
            Assert.InRange(last.PerMinute(last.Stutters), 115, 140);
            Assert.Equal(last.Stutters, last.SimWaits);
            Assert.Empty(t.Freezes);
            // The light counts the same frames over its 10 s.
            Assert.InRange(t.Stutters10s, 18, 24);
        }

        [Fact]
        public void ARunOfSlowFramesIsALowRateNotStutters()
        {
            var t = new PerfTrack();
            double now = Feed(t, 0, 5, 6);
            // Ten 25 ms frames in a row: one stutter (the first), not ten.
            for (int i = 0; i < 10; i++) t.Add(new PerfFrame { Now = now += 0.025, Ms = 25, InColony = true, Scale = 3, GameDt = 0.075 });
            now = Feed(t, now, 2, 6);
            Assert.Equal(1, t.Summarize(10, now).Stutters);
        }

        [Fact]
        public void TheFramesAfterALoadUntilItSettlesAreLoadingNotPlay()
        {
            var t = new PerfTrack { StartedAt = 0 };
            double now = 1;
            t.Add(new PerfFrame { Now = now += 0.01, Ms = 10, InMenu = true });
            // A 7 s frame in the menu (start-up work) is not play either.
            t.Add(new PerfFrame { Now = now += 7, Ms = 7000, InMenu = true });
            for (int i = 0; i < 10; i++) t.Add(new PerfFrame { Now = now += 1.7, Ms = 1700, Loading = true });
            // Arrival, then the game finishing the load while paused: 3.3 s, 166 ms, 242 ms, 38 ms.
            t.Add(new PerfFrame { Now = now += 0.2, Ms = 200, InColony = true, Paused = true });
            foreach (double m in new[] { 3340.0, 166, 242, 38 })
                t.Add(new PerfFrame { Now = now += m / 1000, Ms = m, InColony = true, Paused = true, JitMs = m > 1000 ? 2300 : 0 });
            // 17 s between scenes, the arriving frame and the three frames before the first under 50 ms.
            Assert.InRange(t.LoadMs, 20_800, 21_100);
            Assert.Empty(t.Freezes);
            Assert.Equal(14, t.CauseTimes[(int)PerfCause.Load]);
            Assert.Equal(0, t.CauseTimes[(int)PerfCause.Unknown] + t.CauseTimes[(int)PerfCause.Jit]);
            // Then the first frame with game time (the Loading panel's), then play.
            t.Add(new PerfFrame { Now = now += 0.064, Ms = 64, InColony = true, Scale = 3, GameDt = 0.2 });
            Assert.Equal(64, t.FirstFrameMs);
            // The first 200 ms simulation step after the load (a 299 ms frame on McBench) is theirs too.
            t.Add(new PerfFrame { Now = now += 0.299, Ms = 299, InColony = true, Scale = 3, GameDt = 0.2, SubTicks = 12, Step200 = true });
            Assert.Equal(363, t.FirstFrameMs);
            Assert.Equal(new[] { PerfCause.FirstFrame }, t.Freezes.Select(f => f.Cause).ToArray());
            Assert.Equal(4, t.LoadFrames.Count);
            now = Feed(t, now, 20, 5.6);
            var play = t.Summarize(120, now, play: true);
            Assert.True(play.MaxMs < 10, "play's worst frame leaves loading and the first frame out");
            Assert.InRange(1000 / play.MeanMs, 170, 185);
            Assert.Equal(0, t.PlayFreezeMs);
        }

        [Fact]
        public void CompilingAndSwitchingAsteroidAreNamedAndPausedFreezesListedButNotPlay()
        {
            var t = new PerfTrack();
            double now = Feed(t, 0, 5, 6);
            t.Add(new PerfFrame { Now = now += 0.3, Ms = 300, JitMs = 220, InColony = true, Paused = true });
            t.Add(new PerfFrame { Now = now += 0.13, Ms = 130, InColony = true, Paused = true, WorldSwitch = true });
            now = Feed(t, now, 2, 6);
            t.Add(new PerfFrame { Now = now += 0.2, Ms = 200, InColony = true, Scale = 3, GameDt = 0.6 });
            Assert.Equal(new[] { PerfCause.Jit, PerfCause.World, PerfCause.Unknown }, t.Freezes.Select(f => f.Cause).ToArray());
            Assert.Equal(new[] { true, true, false }, t.Freezes.Select(f => f.Paused).ToArray());
            Assert.Equal(200, t.PlayFreezeMs);
            Assert.Contains(t.SlowFrames(), f => f.Cause == PerfCause.Jit && f.JitMs == 220);
        }

        static string Ui(string name) => "<" + name + ">";

        [Fact]
        public void SimStepPaceAdviceShowsOnlyWhenItsWaitsAreFrequentAndItIsOff()
        {
            var state = new PerfAdviceState { Judged = true, SimWaitsPerMinute = 120, StuttersPerMinute = 126 };
            var (cards, nothing) = PerfAdvice.Evaluate(state, Ui);
            Assert.Single(cards);
            Assert.Equal("<PERF_ADVICE_SIM_STEP_PACE>", cards[0].Title);
            Assert.Empty(nothing);
            state.TweaksOn.Add("sim-step-pace");
            (cards, nothing) = PerfAdvice.Evaluate(state, Ui);
            Assert.Empty(cards);
            Assert.Equal(new[] { "<PERF_NOTHING_SIM_STEP_PACE_ON>" }, nothing.ToArray());
            state = new PerfAdviceState { Judged = true, SimWaitsPerMinute = 5 };
            (cards, nothing) = PerfAdvice.Evaluate(state, Ui);
            Assert.Empty(cards);
            Assert.Single(nothing);
            (cards, nothing) = PerfAdvice.Evaluate(new PerfAdviceState { SimWaitsPerMinute = 200 }, Ui);
            Assert.Empty(cards);
            Assert.Empty(nothing);
        }

        /// Frames with vsync at 60 Hz: `pattern` repeated (ms each), the game keeping up.
        static double FeedPattern(PerfTrack t, double now, double seconds, double[] pattern)
        {
            double end = now + seconds;
            int i = 0;
            while (now < end)
            {
                double m = pattern[i++ % pattern.Length];
                now += m / 1000;
                t.Add(new PerfFrame { Now = now, Ms = m, InColony = true, Scale = 1, GameDt = m / 1000 });
            }
            return now;
        }

        [Fact]
        public void VsyncHoldingTheRefreshRateIsNoStutter()
        {
            const double refresh = 1000 / 59.95;
            var t = new PerfTrack { RefreshMs = refresh };
            // At the refresh rate, the wall clock's ±0.5 ms and a pipelined catch-up (25 ms, then 8 ms).
            double now = FeedPattern(t, 0, 2, new[] { refresh });
            FeedPattern(t, now, 15, new[] { refresh, refresh + 0.5, refresh - 0.5, refresh, 25, 8.4, refresh, refresh });
            Assert.Equal(0, t.Light);
            Assert.InRange(t.KeepUp, 0.99, 1.01);
            Assert.Equal(0, t.LastMinute().Stutters);
            Assert.InRange(t.StutterThreshold(), 29, 29.3);
        }

        [Fact]
        public void WithVsyncAMissedRefreshAlwaysCounts()
        {
            const double refresh = 1000 / 59.95;
            // A frame of two refreshes sits on twice the median's line: with the period known it
            // counts every time (a picture shown twice), whichever side of the bin it falls.
            var pattern = new List<double>();
            for (int k = 0; k < 59; k++) pattern.Add(k % 2 == 0 ? refresh - 0.05 : refresh + 0.05);
            pattern.Add(2 * refresh);
            var t = new PerfTrack { RefreshMs = refresh };
            FeedPattern(t, 0, 30, pattern.ToArray());
            var minute = t.LastMinute();
            Assert.InRange(minute.PerMinute(minute.Stutters), 55, 65);
            // Without vsync's period the line is twice the median, as before.
            t.RefreshMs = 0;
            Assert.Equal(Math.Max(PerfTrack.StutterMs, 2 * t.MedianMs), t.StutterThreshold());
        }

        [Fact]
        public void ScanAgainMeasuresTheMinuteFromThen()
        {
            var t = new PerfTrack();
            double now = Feed(t, 0, 30, 5.6, slowEvery: 80, slowMs: 21);
            Assert.True(t.LastMinute().Stutters > 30);
            t.RestartMinute();
            Assert.Equal(t.Now, t.RestartedAt);
            Assert.Equal(0, t.LastMinute().Stutters);
            Assert.Equal(0, t.LastMinute().PlaySeconds);
            now = Feed(t, now, 10.5, 5.6);
            var after = t.LastMinute();
            Assert.Equal(0, after.Stutters);
            Assert.InRange(after.PlaySeconds, 8.9, 10.1);
            // The session's totals are kept.
            Assert.True(t.CauseTimes[(int)PerfCause.SimWait] > 30);
        }
    }
}