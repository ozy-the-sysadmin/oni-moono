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
            var last = t.LastSeconds(10);
            Assert.True(last.SimWaits > 0 && last.SimWaits == last.Stutters);
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
    }
}
