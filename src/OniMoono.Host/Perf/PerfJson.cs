#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OniMoono.Engine;

namespace OniMoono.Host.Perf
{
    /// A recording's file, schema `moono-perf/1` (docs/PERFORMANCE.md, "Recordings"): one JSON
    /// object. Rows (`rows[]`) carry `key`, `kind`, `ms`, `ms_pf` (per frame) and, where the
    /// game clock ran, `ms_pgs` (per game second); rows that count add `calls`/`objects` with the
    /// same suffixes. The `phase` rows and `phase/outside-loop` sum to the frame (`check`);
    /// `wait`, `game` and `bucket` rows lie inside them.
    internal static class PerfJson
    {
        public const string Schema = "moono-perf/1";

        // Filled by PerfRecorder; the unit tests compile this file without it.
#pragma warning disable CS0649
        internal sealed class Input
        {
            public PerfSpec Spec = new();
            public int Index;
            public FrameWindows.Window Window = null!;
            public PerfSnapshot Start = null!, End = null!;
            public long PausedFrames;
            public int SpeedSetting;
            public IReadOnlySet<string> TweaksOn = new HashSet<string>();
            public IReadOnlyCollection<string> AllTweaks = [];
            public IReadOnlyDictionary<string, string> Unavailable = new Dictionary<string, string>();
            public bool FrameStats;
            public Type Game = null!;
            public string? GameUpdate;
        }
#pragma warning restore CS0649

        public static byte[] Write(Input r)
        {
            var w = r.Window;
            long frames = Math.Max(1, w.Frames);
            double wallS = (r.End.Timestamp - r.Start.Timestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
            double gameS = r.End.GameTime >= 0 && r.Start.GameTime >= 0 ? r.End.GameTime - r.Start.GameTime : 0;
            var summary = w.FrameTimes != null ? FrameSummary.Of(w.FrameTimes) : new FrameSummary();

            using var stream = new MemoryStream();
            using (var j = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                void Num(string name, double value)
                {
                    if (double.IsFinite(value)) j.WriteNumber(name, Math.Round(value, 6));
                    else j.WriteNull(name);
                }
                void Row(string key, string kind, double ms, long? calls = null, long? objects = null, double? maxFrameMs = null, Action? extra = null)
                {
                    j.WriteStartObject();
                    j.WriteString("key"u8, key);
                    j.WriteString("kind"u8, kind);
                    Num("ms", ms);
                    Num("ms_pf", ms / frames);
                    if (gameS > 0) Num("ms_pgs", ms / gameS);
                    if (calls is long c)
                    {
                        j.WriteNumber("calls"u8, c);
                        Num("calls_pf", c / (double)frames);
                        if (gameS > 0) Num("calls_pgs", c / gameS);
                    }
                    if (objects is long o)
                    {
                        j.WriteNumber("objects"u8, o);
                        Num("objects_pf", o / (double)frames);
                        if (gameS > 0) Num("objects_pgs", o / gameS);
                        if (calls is long n && n > 0) Num("objects_per_call", o / (double)n);
                    }
                    if (maxFrameMs is double max) Num("max_frame_ms", max);
                    extra?.Invoke();
                    j.WriteEndObject();
                }

                j.WriteStartObject();
                j.WriteString("schema"u8, Schema);
                j.WriteString("written_at"u8, r.End.Utc.ToString("o"));

                j.WriteStartObject("moono"u8);
                j.WriteString("version"u8, UpdateCheck.CurrentTag());
                var informational = typeof(PerfJson).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
                j.WriteString("informational_version"u8, informational);
                int plus = informational?.IndexOf('+') ?? -1;
                j.WriteString("commit"u8, plus >= 0 ? informational!.Substring(plus + 1) : null);
                j.WriteString("runtime"u8, Environment.Version.ToString());
                j.WriteBoolean("server_gc"u8, System.Runtime.GCSettings.IsServerGC);
                j.WriteEndObject();

                j.WriteStartObject("game"u8);
                j.WriteString("build"u8, Call<string>(r.Game, "Build"));
                j.WriteString("update"u8, r.GameUpdate);
                j.WriteEndObject();

                j.WriteStartObject("run"u8);
                j.WriteString("label"u8, r.Spec.Label);
                j.WriteNumber("index"u8, r.Index + 1);
                j.WriteNumber("count"u8, r.Spec.Count);
                j.WriteString("spec"u8, r.Spec.Text);
                j.WriteBoolean("frame_stats"u8, r.FrameStats);
                j.WriteBoolean("bucket_profile"u8, r.Start.BucketLabels != null);
                j.WriteEndObject();

                j.WriteStartObject("display"u8);
                j.WriteNumber("width"u8, UnityEngine.Screen.width);
                j.WriteNumber("height"u8, UnityEngine.Screen.height);
                j.WriteString("fullscreen_mode"u8, UnityEngine.Screen.fullScreenMode.ToString());
                // The swap interval the devices presented with (the Moono Tweaks choice or
                // --vsync-count when set), and the game's own setting beside it.
                j.WriteNumber("vsync_count"u8, VSync.Interval);
                j.WriteNumber("vsync_game"u8, GameVSync());
                j.WriteBoolean("vsync_override"u8, VSync.Override >= 0);
                j.WriteNumber("target_frame_rate"u8, UnityEngine.Application.targetFrameRate);
                j.WriteBoolean("focused"u8, PlayerLoop.HasFocus);
                j.WriteEndObject();

                var (camera, worldId, worldName) = Call<(double[]?, int, string?)>(r.Game, "View");
                j.WriteStartObject("camera"u8);
                if (camera != null)
                {
                    Num("x", camera[0]);
                    Num("y", camera[1]);
                    Num("ortho", camera[2]);
                }
                j.WriteEndObject();
                j.WriteStartObject("world"u8);
                j.WriteNumber("id"u8, worldId);
                j.WriteString("name"u8, worldName);
                j.WriteEndObject();

                j.WriteStartObject("recording"u8);
                Num("seconds_target", r.Spec.Seconds);
                j.WriteNumber("frames"u8, w.Frames);
                Num("wall_s", wallS);
                Num("frames_wall_s", w.WallMs / 1000);
                Num("game_s", gameS);
                j.WriteNumber("speed_setting"u8, r.SpeedSetting);
                Num("speed_reached", wallS > 0 ? gameS / wallS : 0);
                j.WriteNumber("paused_frames"u8, r.PausedFrames);
                if (r.Start.Game != null && r.End.Game != null)
                {
                    int at = GameSpans * 3;
                    long gameFrames = r.End.Game[at + 3] - r.Start.Game[at + 3];
                    long steps = r.End.Game[at + 6] - r.Start.Game[at + 6];
                    j.WriteNumber("sim_frames"u8, gameFrames);
                    j.WriteNumber("sim_paused_frames"u8, r.End.Game[at + 4] - r.Start.Game[at + 4]);
                    j.WriteNumber("sim_capped_frames"u8, r.End.Game[at + 5] - r.Start.Game[at + 5]);
                    j.WriteNumber("sim_steps"u8, steps);
                    Num("sim_steps_pf", steps / (double)frames);
                }
                j.WriteEndObject();

                j.WriteStartObject("frame"u8);
                Num("ms_pf", w.WallMs / frames);
                Num("fps", w.WallMs > 0 ? 1000.0 * w.Frames / w.WallMs : 0);
                Num("p50_ms", summary.P50Ms);
                Num("p90_ms", summary.P90Ms);
                Num("p99_ms", summary.P99Ms);
                Num("p999_ms", summary.P999Ms);
                Num("low1_fps", summary.Low1Fps);
                Num("low01_fps", summary.Low01Fps);
                Num("max_ms", w.MaxFrameMs);
                j.WriteNumber("frames_over_33ms"u8, summary.Over33);
                j.WriteNumber("frames_over_50ms"u8, summary.Over50);
                j.WriteNumber("frames_over_100ms"u8, summary.Over100);
                j.WriteNumber("frames_over_250ms"u8, summary.Over250);
                j.WriteEndObject();

                j.WriteStartArray("rows"u8);
                for (int i = 0; i < FrameWindows.Phases; i++)
                    Row("phase/" + FrameWindows.PhaseNames[i], "phase", w.PhaseMs[i], maxFrameMs: w.PhaseMaxMs[i]);
                Row("phase/outside-loop", "phase", w.OutsideMs, maxFrameMs: w.OutsideMaxMs);
                for (int i = 0; i < FrameWindows.Waits; i++)
                    Row("wait/" + FrameWindows.WaitNames[i], "wait", w.WaitMs[i], calls: i == 1 ? w.DeviceCalls : null,
                        extra: i == 4 ? () => j.WriteString("thread"u8, "device"u8) : null);
                Row("wait/gc-pause", "wait", w.GcPauseMs, calls: w.FramesWithGc, maxFrameMs: w.GcMostInFrameMs);
                if (r.Start.Game != null && r.End.Game != null) GameRows(r, Row);
                if (r.Start.BucketLabels != null && r.End.BucketLabels != null) BucketRows(r, Row);
                j.WriteEndArray();

                var d = r.End.Runtime - r.Start.Runtime;
                j.WriteStartObject("gc"u8);
                j.WriteNumber("gen0"u8, d.Gen0);
                j.WriteNumber("gen1"u8, d.Gen1);
                j.WriteNumber("gen2"u8, d.Gen2);
                Num("pause_ms", d.PauseMs);
                Num("pause_ms_pf", d.PauseMs / frames);
                j.WriteNumber("frames_with_pause"u8, w.FramesWithGc);
                Num("most_in_a_frame_ms", w.GcMostInFrameMs);
                Num("allocated_mb", d.Allocated / 1048576.0);
                Num("allocated_kb_pf", d.Allocated / 1024.0 / frames);
                Num("allocated_mb_ps", wallS > 0 ? d.Allocated / 1048576.0 / wallS : 0);
                if (gameS > 0) Num("allocated_mb_pgs", d.Allocated / 1048576.0 / gameS);
                Num("heap_mb_start", r.Start.HeapBytes / 1048576.0);
                Num("heap_mb_end", r.End.HeapBytes / 1048576.0);
                Num("committed_mb_end", r.End.CommittedBytes / 1048576.0);
                j.WriteEndObject();

                j.WriteStartObject("runtime"u8);
                Num("jit_ms", d.JitMs);
                j.WriteNumber("jit_methods"u8, d.JitMethods);
                j.WriteNumber("lock_contentions"u8, d.Contentions);
                j.WriteNumber("pool_threads"u8, d.PoolThreads);
                j.WriteNumber("pool_items"u8, d.PoolCompleted);
                j.WriteEndObject();

                void Colony(string name, PerfSnapshot s)
                {
                    j.WriteStartObject(name);
                    for (int i = 0; i < s.ColonyKeys.Length; i++) Num(s.ColonyKeys[i], s.ColonyValues[i]);
                    j.WriteEndObject();
                }
                Colony("colony_start", r.Start);
                Colony("colony_end", r.End);

                j.WriteStartArray("counters"u8);
                var before = r.Start.Counters.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First().Value);
                foreach (var (key, tweak, value, gauge) in r.End.Counters)
                {
                    long total = value - before.GetValueOrDefault(key);
                    j.WriteStartObject();
                    j.WriteString("key"u8, key);
                    j.WriteString("tweak"u8, tweak);
                    // A gauge (a level, not a count), or a count that went down, has no rate.
                    bool level = gauge || total < 0;
                    j.WriteString("kind"u8, level ? "gauge" : "count");
                    j.WriteNumber("start"u8, before.GetValueOrDefault(key));
                    j.WriteNumber("end"u8, value);
                    if (level)
                    {
                        j.WriteEndObject();
                        continue;
                    }
                    j.WriteNumber("total"u8, total);
                    Num("per_frame", total / (double)frames);
                    if (gameS > 0) Num("per_gs", total / gameS);
                    j.WriteEndObject();
                }
                j.WriteEndArray();

                j.WriteStartObject("tweaks"u8);
                j.WriteStartArray("on"u8);
                foreach (var t in r.AllTweaks.Where(r.TweaksOn.Contains)) j.WriteStringValue(t);
                j.WriteEndArray();
                j.WriteStartArray("off"u8);
                foreach (var t in r.AllTweaks.Where(t => !r.TweaksOn.Contains(t))) j.WriteStringValue(t);
                j.WriteEndArray();
                j.WriteStartObject("unavailable"u8);
                foreach (var (t, why) in r.Unavailable.OrderBy(p => p.Key, StringComparer.Ordinal)) j.WriteString(t, why);
                j.WriteEndObject();
                j.WriteEndObject();

                var (titles, ids) = Call<(string[], string[])>(r.Game, "Mods");
                j.WriteStartArray("mods"u8);
                for (int i = 0; i < (titles?.Length ?? 0); i++)
                {
                    j.WriteStartObject();
                    j.WriteString("title"u8, titles![i]);
                    j.WriteString("id"u8, ids[i]);
                    j.WriteEndObject();
                }
                j.WriteEndArray();

                j.WriteStartObject("check"u8);
                Num("frame_ms_pf", w.WallMs / frames);
                Num("phases_sum_ms_pf", w.PartitionMs / frames);
                Num("unattributed_ms_pf", (w.WallMs - w.PartitionMs) / frames);
                j.WriteEndObject();

                j.WriteEndObject();
            }
            return stream.ToArray();
        }

        const int GameSpans = 17;

        /// Frame stats' game rows: the spans (each nested in a phase), SimDLL on the main thread
        /// and the wait for the job workers.
        static void GameRows(Input r, Action<string, string, double, long?, long?, double?, Action?> row)
        {
            long[] a = r.Start.Game!, b = r.End.Game!;
            int at = GameSpans * 3;
            double msPerTick = 1000.0 / b[at + 7];
            var names = Call<string[]>(r.Game, "GameSpanNames") ?? [];
            for (int i = 0; i < GameSpans && i < names.Length; i++)
            {
                long calls = b[GameSpans + i] - a[GameSpans + i];
                if (calls == 0) continue;
                row("game/" + names[i], "game", (b[i] - a[i]) * msPerTick, calls, null, null, null);
            }
            row("game/simdll-main-thread", "game", (b[at] - a[at]) * msPerTick, b[at + 1] - a[at + 1], null, null, null);
            row("game/job-wait", "game", (b[at + 2] - a[at + 2]) * msPerTick, null, null, null, null);
        }

        /// The bucket profile's kinds: ms, calls and entries (objects).
        static void BucketRows(Input r, Action<string, string, double, long?, long?, double?, Action?> row)
        {
            double msPerTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            var before = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < r.Start.BucketLabels!.Length; i++) before[r.Start.BucketLabels[i]] = i;
            var e = r.End;
            for (int i = 0; i < e.BucketLabels!.Length; i++)
            {
                long ticks = e.BucketTicks![i], calls = e.BucketCalls![i], entries = e.BucketEntries![i];
                if (before.TryGetValue(e.BucketLabels[i], out int k))
                {
                    ticks -= r.Start.BucketTicks![k];
                    calls -= r.Start.BucketCalls![k];
                    entries -= r.Start.BucketEntries![k];
                }
                if (calls == 0) continue;
                row("bucket/" + e.BucketLabels[i], "bucket", ticks * msPerTick, calls, entries, null, null);
            }
        }

        /// The game's own vsync setting (-1 when it cannot be read: no player, the unit tests).
        static int GameVSync()
        {
            try
            {
                return UnityEngine.QualitySettings.vSyncCount;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        static T Call<T>(Type type, string method)
        {
            try
            {
                return (T)type.GetMethod(method)!.Invoke(null, null)!;
            }
            catch (Exception)
            {
                return default!;
            }
        }
    }
}
