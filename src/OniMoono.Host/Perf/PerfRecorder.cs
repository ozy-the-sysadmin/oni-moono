#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using OniMoono.Engine;

namespace OniMoono.Host.Perf
{
    /// The performance recorder: N seconds of play into one JSON file (schema `moono-perf/1`,
    /// PerfJson), for comparing builds, tweaks and colonies (tools/PerfDiff). Armed by
    /// `--perf-record SPEC` or ONI_PERF_RECORD=SPEC (PerfSpec), or by the HUD's frame time panel.
    ///
    /// What it records is the engine's own timing (FrameWindows: PlayerLoop's phases, the time
    /// outside the loop, the device's and the GC's waits, every frame's time) with snapshots
    /// taken at the frame ends that bound the recording: the game clock, the tweaks' counters
    /// (TweakCounters), the colony, the runtime's GC and JIT counts. It patches nothing in the
    /// game, so no tweak steps aside for it. With `--frame-stats` its game rows are added, and
    /// with ONI_BUCKET_PROFILE=1 the buckets' (those are frame stats' own patches).
    ///
    /// Cost: nothing while not armed. Armed and waiting, one call a frame (three reads of the
    /// game's state). Recording, PlayerLoop's ten clock reads a frame and the engine's waits'
    /// clock reads (the frame-stats timing, without its logging), a GC counter read and a few
    /// dozen additions a frame; the snapshots and the file's text are made between frames, the
    /// file written on the thread pool.
    internal static class PerfRecorder
    {
        public const string EnvVar = "ONI_PERF_RECORD";

        enum Stage { Off, Waiting, WarmUp, Recording, Ending }

        static Func<bool>? inColony, paused;
        static Func<double>? gameTime;
        static Func<int>? speed;
        static Action<int>? setSpeed;
        static Type? game;
        static IReadOnlySet<string> tweaksOn = new HashSet<string>();
        static IReadOnlyCollection<string> allTweaks = [];
        static bool frameStats;

        static PerfSpec spec = new();
        static Stage stage;
        static int index, written;
        static double unpausedS, warmS;
        static long lastTick;
        static FrameWindows.Window? window;
        static Snapshot? start;
        static long pausedFrames;
        static int speedSetting;
        static bool subscribed;
        /// Re-armed after the recordings (once=0): the wait starts at the next load.
        static bool needLoad;

        /// What the recorder says about itself (for the panel).
        public static string State => stage switch
        {
            Stage.Off => "off",
            Stage.Waiting => $"waiting ({unpausedS:F0}/{spec.Delay:F0} s)",
            Stage.WarmUp => $"warming up ({warmS:F0}/{spec.Gap:F0} s)",
            _ => $"recording {index + 1}/{spec.Count}",
        };

        public static bool Busy => stage != Stage.Off;

        /// Program, after the tweaks are in: binds the game's side (OniMoono.GameTweaks.PerfGame)
        /// and arms a recording when the command line or the environment asks for one.
        public static void Install(string? commandLine, IEnumerable<string> enabledTweaks, IReadOnlyCollection<string> all, bool withFrameStats)
        {
            tweaksOn = enabledTweaks.ToHashSet(StringComparer.Ordinal);
            allTweaks = all;
            frameStats = withFrameStats;
            string? text = commandLine ?? Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrWhiteSpace(text)) return;
            EngineLog.Engine("perf record: " + Arm(text));
        }

        static bool Bind()
        {
            if (game != null) return true;
            try
            {
                var type = GameTweaks.GameTweaks.TypedAssembly.GetType("OniMoono.GameTweaks.PerfGame", throwOnError: true)!;
                T Of<T>(string name) where T : Delegate => type.GetMethod(name)!.CreateDelegate<T>();
                inColony = Of<Func<bool>>("InColony");
                paused = Of<Func<bool>>("Paused");
                gameTime = Of<Func<double>>("GameTime");
                speed = Of<Func<int>>("Speed");
                setSpeed = Of<Action<int>>("SetSpeed");
                game = type;
                return true;
            }
            catch (Exception e)
            {
                var cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                EngineLog.Engine($"perf record: the game's side is missing: {cause.GetType().Name}: {cause.Message}");
                game = null;
                return false;
            }
        }

        /// Arms (or re-arms) the recorder; returns what it understood, for the log.
        public static string Arm(string text)
        {
            if (!Bind()) return "not armed (the game's side is missing)";
            var parsed = PerfSpec.Parse(text, out var problems);
            if (window != null) FrameWindows.Drop(window);
            window = null;
            spec = parsed;
            index = written = 0;
            unpausedS = warmS = 0;
            needLoad = false;
            stage = Stage.Waiting;
            lastTick = 0;
            if (!subscribed)
            {
                PlayerLoop.FrameStart += Tick;
                subscribed = true;
            }
            return "armed: " + spec.Describe() + (problems.Count > 0 ? "; ignored: " + string.Join("; ", problems) : "");
        }

        /// Stops what is armed or running; a recording in progress is not written.
        public static void Disarm()
        {
            if (window != null) FrameWindows.Drop(window);
            window = null;
            Finish();
        }

        static void Finish()
        {
            stage = Stage.Off;
            if (subscribed)
            {
                PlayerLoop.FrameStart -= Tick;
                subscribed = false;
            }
        }

        /// Each frame's start while armed.
        static void Tick()
        {
            long now = Stopwatch.GetTimestamp();
            double dt = lastTick != 0 ? (now - lastTick) / (double)Stopwatch.Frequency : 0;
            lastTick = now;
            bool colony = inColony!();
            switch (stage)
            {
                case Stage.Waiting:
                    if (!colony)
                    {
                        unpausedS = 0;
                        needLoad = false;
                        return;
                    }
                    if (needLoad || paused!()) return;
                    unpausedS += dt;
                    if (unpausedS < spec.Delay) return;
                    BeginWarmUp();
                    return;
                case Stage.WarmUp:
                    if (!colony)
                    {
                        Restart();
                        return;
                    }
                    if (!paused!()) warmS += dt;
                    if (warmS < WarmUpFor(index)) return;
                    Begin();
                    return;
                case Stage.Recording:
                    if (!colony)
                    {
                        EngineLog.Engine($"perf record: recording {index + 1} ended early (the colony was left); not written");
                        FrameWindows.Drop(window);
                        window = null;
                        Restart();
                        return;
                    }
                    if (window == null) return;
                    if (window.Closed)
                    {
                        // Ended at a frame end by the engine (Drop elsewhere): go on.
                        window = null;
                        Next();
                        return;
                    }
                    if (paused!()) pausedFrames++;
                    if (window.Frames > 0 && window.WallMs >= spec.Seconds * 1000)
                    {
                        FrameWindows.End(window);
                        stage = Stage.Ending;
                    }
                    return;
                case Stage.Ending:
                    if (window != null && !window.Closed) return;
                    window = null;
                    Next();
                    return;
            }
        }

        static double WarmUpFor(int i) => i == 0 && spec.SpeedFor(0) == 0 ? 0 : spec.Gap;

        static void BeginWarmUp()
        {
            int s = spec.SpeedFor(index);
            if (s > 0) setSpeed!(s);
            warmS = 0;
            stage = Stage.WarmUp;
        }

        static void Begin()
        {
            pausedFrames = 0;
            speedSetting = speed!();
            int capacity = (int)Math.Min(1 << 20, spec.Seconds * 300);
            window = FrameWindows.Open(frameTimes: true, capacity, began: () => start = Take(), ended: Write);
            stage = Stage.Recording;
        }

        static void Next()
        {
            index++;
            if (index < spec.Count)
            {
                BeginWarmUp();
                return;
            }
            EngineLog.Engine($"perf record: done, {written} file(s) written");
            if (spec.Once)
            {
                Finish();
                return;
            }
            // Armed again: the next load's wait.
            index = 0;
            unpausedS = 0;
            needLoad = true;
            stage = Stage.Waiting;
        }

        /// The colony was left: wait for the next load (once or not, the recordings asked for
        /// are still owed).
        static void Restart()
        {
            unpausedS = warmS = 0;
            stage = Stage.Waiting;
        }

        /// What is read at a recording's bounds.
        internal sealed class Snapshot
        {
            public long Timestamp;
            public DateTime Utc;
            public double GameTime;
            public List<(string Key, string Tweak, long Value)> Counters = [];
            public string[] ColonyKeys = [];
            public double[] ColonyValues = [];
            public long[]? Game;
            public string[]? BucketLabels;
            public long[]? BucketTicks, BucketCalls, BucketEntries;
            public FrameStatsWindow.RuntimeSample Runtime;
            public long HeapBytes, CommittedBytes;
        }

        static Snapshot Take()
        {
            var s = new Snapshot { Timestamp = Stopwatch.GetTimestamp(), Utc = DateTime.UtcNow, GameTime = gameTime!(), Runtime = FrameStatsWindow.RuntimeSample.Now() };
            var assemblies = new[] { typeof(PerfRecorder).Assembly, GameTweaks.GameTweaks.TypedAssembly };
            s.Counters = TweakCounters.Read(assemblies, tweaksOn);
            try
            {
                var colony = ((string[], double[]))game!.GetMethod("Colony")!.Invoke(null, null)!;
                (s.ColonyKeys, s.ColonyValues) = colony;
                s.Game = (long[]?)game.GetMethod("GameTotals")!.Invoke(null, null);
                if (game.GetMethod("BucketTotals")!.Invoke(null, null) is object[] buckets)
                {
                    s.BucketLabels = (string[])buckets[0];
                    s.BucketTicks = (long[])buckets[1];
                    s.BucketCalls = (long[])buckets[2];
                    s.BucketEntries = (long[])buckets[3];
                }
            }
            catch (Exception e)
            {
                var cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                EngineLog.Engine($"perf record: the game's state not read: {cause.GetType().Name}: {cause.Message}");
            }
            var gc = GC.GetGCMemoryInfo();
            s.HeapBytes = gc.HeapSizeBytes;
            s.CommittedBytes = gc.TotalCommittedBytes;
            return s;
        }

        /// The window's end (a frame end): the end snapshot, the file's text, the write.
        static void Write()
        {
            var end = Take();
            var w = window;
            if (w == null || start == null) return;
            try
            {
                var record = new PerfJson.Input
                {
                    Spec = spec, Index = index, Window = w, Start = start, End = end, PausedFrames = pausedFrames, SpeedSetting = speedSetting,
                    TweaksOn = tweaksOn, AllTweaks = allTweaks, Unavailable = GameTweaks.GameTweaks.Unavailable, FrameStats = frameStats,
                    Game = game!,
                };
                byte[] json = PerfJson.Write(record);
                string dir = spec.Out ?? Path.Combine(EngineConfig.PersistentDataPath, "oni-moono", "perf");
                string path = Path.Combine(dir, $"perf-{spec.Label}-{index + 1:D2}-{start.Utc.ToLocalTime():yyyyMMdd-HHmmss}.json");
                written++;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllBytes(path, json);
                        EngineLog.Engine($"perf record: recording {Path.GetFileName(path)} written to {dir}");
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        EngineLog.Engine($"perf record: {path} could not be written: {e.Message}");
                    }
                });
            }
            catch (Exception e)
            {
                EngineLog.Engine($"perf record: recording {index + 1} not written: {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
