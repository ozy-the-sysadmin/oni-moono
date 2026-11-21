#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OniMoono.Engine;
using OniMoono.Host.Perf;
using Xunit;

namespace OniMoono.Tests
{
    /// The recorder's file (PerfJson): its schema and which fields are there, the counters'
    /// gauge or count rule, the vertical sync actually used; and PerfDiff's arithmetic, where a
    /// measure missing from a file is missing, not zero (DiffMath).
    public class PerfFileTests
    {
        /// The game's side as the recorder reads it, by reflection (PerfGame's method names).
        public static class FakeGame
        {
            public static string Build() => "U60-764025";
            public static (double[]?, int, string?) View() => (new[] { 1.0, 2.0, 20.0 }, 0, "Terra");
            public static (string[], string[]) Mods() => (new[] { "QoL Pack" }, new[] { "qol" });
            public static string[] GameSpanNames() => Array.Empty<string>();
        }

        static PerfSnapshot Snap(long ts, double gameTime, params (string Key, long Value, bool Gauge)[] counters) => new()
        {
            Timestamp = ts,
            Utc = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc),
            GameTime = gameTime,
            Counters = counters.Select(c => (c.Key, "tweak", c.Value, c.Gauge)).ToList(),
            ColonyKeys = new[] { "duplicants" },
            ColonyValues = new[] { 12.0 },
        };

        static JsonDocument Write(double gameStart, double gameEnd)
        {
            var window = new FrameWindows.Window(frameTimes: true, capacity: 4);
            window.Frames = 4;
            window.WallMs = 40;
            window.PhaseMs[5] = 20;
            window.OutsideMs = 20;
            window.WaitMs[0] = 4;
            foreach (var f in new[] { 9f, 10f, 10f, 11f }) window.FrameTimes!.Add(f);
            var input = new PerfJson.Input
            {
                Window = window,
                Start = Snap(0, gameStart, ("calls", 10, false), ("level", 50, true), ("shrinks", 9, false)),
                End = Snap(System.Diagnostics.Stopwatch.Frequency, gameEnd, ("calls", 30, false), ("level", 40, true), ("shrinks", 4, false)),
                Game = typeof(FakeGame),
                GameUpdate = "U60",
                TweaksOn = new HashSet<string> { "a" },
                AllTweaks = new[] { "a", "b" },
            };
            return JsonDocument.Parse(PerfJson.Write(input));
        }

        [Fact]
        public void TheFileHasItsSchemaRowsAndCountersRule()
        {
            int before = VSync.Override;
            try
            {
                VSync.Override = 0;
                using var doc = Write(100, 103);
                var root = doc.RootElement;
                Assert.Equal("moono-perf/1", root.GetProperty("schema"u8).GetString());
                foreach (var section in new[] { "moono", "game", "run", "display", "recording", "frame", "rows", "gc", "runtime", "counters", "tweaks", "mods", "check" })
                    Assert.True(root.TryGetProperty(section, out _), section);
                Assert.Equal("U60-764025", root.GetProperty("game"u8).GetProperty("build"u8).GetString());
                // The vsync the devices used (Moono Tweaks' Off), the game's own beside it.
                var display = root.GetProperty("display"u8);
                Assert.Equal(0, display.GetProperty("vsync_count"u8).GetInt32());
                Assert.True(display.GetProperty("vsync_override"u8).GetBoolean());
                Assert.True(display.TryGetProperty("vsync_game"u8, out _));
                var rows = root.GetProperty("rows"u8).EnumerateArray().ToDictionary(r => r.GetProperty("key"u8).GetString()!);
                Assert.Contains("phase/update", rows.Keys);
                Assert.Contains("phase/outside-loop", rows.Keys);
                Assert.Contains("wait/device-frame", rows.Keys);
                Assert.Equal(5, rows["phase/update"].GetProperty("ms_pf"u8).GetDouble(), 6);
                // Game time ran (3 s): per game second figures are there.
                Assert.True(rows["phase/update"].TryGetProperty("ms_pgs"u8, out var pgs));
                Assert.Equal(20 / 3.0, pgs.GetDouble(), 5);
                var counters = root.GetProperty("counters"u8).EnumerateArray().ToDictionary(c => c.GetProperty("key"u8).GetString()!);
                Assert.Equal("count", counters["calls"].GetProperty("kind"u8).GetString());
                Assert.Equal(20, counters["calls"].GetProperty("total"u8).GetInt64());
                Assert.Equal(5, counters["calls"].GetProperty("per_frame"u8).GetDouble(), 6);
                // A gauge, and a count that went down, have no rate.
                Assert.Equal("gauge", counters["level"].GetProperty("kind"u8).GetString());
                Assert.False(counters["level"].TryGetProperty("per_frame"u8, out _));
                Assert.Equal("gauge", counters["shrinks"].GetProperty("kind"u8).GetString());
                Assert.Equal(new[] { "a" }, root.GetProperty("tweaks"u8).GetProperty("on"u8).EnumerateArray().Select(e => e.GetString()).ToArray());
                Assert.Equal(new[] { "b" }, root.GetProperty("tweaks"u8).GetProperty("off"u8).EnumerateArray().Select(e => e.GetString()).ToArray());
            }
            finally
            {
                VSync.Override = before;
            }
        }

        [Fact]
        public void APausedRecordingHasNoPerGameSecondFigures()
        {
            int before = VSync.Override;
            VSync.Override = 1;
            using var doc = Write(100, 100);
            VSync.Override = before;
            var root = doc.RootElement;
            Assert.Equal(0, root.GetProperty("recording"u8).GetProperty("game_s"u8).GetDouble());
            foreach (var row in root.GetProperty("rows"u8).EnumerateArray())
                Assert.False(row.TryGetProperty("ms_pgs"u8, out _), row.GetProperty("key"u8).GetString());
            var calls = root.GetProperty("counters"u8).EnumerateArray().First(c => c.GetProperty("key"u8).GetString() == "calls");
            Assert.False(calls.TryGetProperty("per_gs"u8, out _));
        }

        [Fact]
        public void PerfDiffAveragesAMeasureOnlyOverTheFilesThatHaveIt()
        {
            // Three recordings; the third was paused for its whole minute and has no ms_pgs.
            var files = new[]
            {
                new Dictionary<string, double> { ["phase/update ms_pgs"] = 3.0, ["frame.fps"] = 60 },
                new Dictionary<string, double> { ["phase/update ms_pgs"] = 3.3, ["frame.fps"] = 62 },
                new Dictionary<string, double> { ["frame.fps"] = 61 },
            };
            var (mean, se, n) = DiffMath.MeanSe(files.Select(f => DiffMath.Get(f, "phase/update ms_pgs")));
            Assert.Equal(2, n);
            Assert.Equal(3.15, mean, 9);
            Assert.True(se > 0);
            // Not (3.0 + 3.3 + 0) / 3 = 2.1, the fake 33 % gain a zero would make.
            var (fps, _, nf) = DiffMath.MeanSe(files.Select(f => DiffMath.Get(f, "frame.fps")));
            Assert.Equal((3, 61.0), (nf, fps));
            var (none, noneSe, nn) = DiffMath.MeanSe(files.Select(f => DiffMath.Get(f, "missing")));
            Assert.Equal(0, nn);
            Assert.True(double.IsNaN(none) && double.IsNaN(noneSe));
            // CV over the present values only; under two values, none.
            Assert.Equal(0, DiffMath.Cv(new double?[] { 5, null, null }));
            Assert.InRange(DiffMath.Cv(new double?[] { 3.0, 3.3, null }), 6.7, 6.8);
        }
    }
}
