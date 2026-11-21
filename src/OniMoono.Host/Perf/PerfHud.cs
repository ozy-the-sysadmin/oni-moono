#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using OniMoono.Engine;

namespace OniMoono.Host.Perf
{
    /// The HUD's frame rate counter and its performance window (OniMoono.GameTweaks.PerfOverlay;
    /// the Frame rate counter row, `--fps-counter on|off`): made once the game's fonts are loaded,
    /// by a frame-start call that stops once it has. Off: nothing is installed and nothing runs.
    /// On: one frame-start call a frame hands the frame that ended (its wall time from
    /// PlayerLoop's frame starts, the GC's pause and the main thread's JIT time in it) to the
    /// game side. The window's deeper numbers come from a frame window (FrameWindows) open only
    /// while its Colony or Frame tab shows; its Record button arms the recorder for 60 seconds.
    internal static class PerfHud
    {
        public const string PanelRecording = "delay=0;seconds=60;count=1;gap=0;label=panel";
        /// The badge's place, beside the settings file.
        public const string PositionFileName = "perf-counter-position.txt";

        static Func<bool>? create;
        static Action<double, double, double, double>? frame;
        static FrameWindows.Window? panel;
        static long lastStart;
        static double lastPause = -1, lastJit = -1;
        static readonly double msPerTick = 1000.0 / Stopwatch.Frequency;

        /// Program, after the tweaks are in. `fpsCounter`: "on" or "off" (EffectiveOptions).
        public static void Install(string fpsCounter, string settingsPath, string[] tweaksOn, int tweaksAll, int switchedOff)
        {
            if (fpsCounter == "off") return;
            try
            {
                var overlay = GameTweaks.GameTweaks.TypedAssembly.GetType("OniMoono.GameTweaks.PerfOverlay", throwOnError: true)!;
                double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                double sinceStart = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds;
                var ui = GameTweaks.TweakStrings.Ui;
                overlay.GetMethod("Install")!.Invoke(null, [Path.Combine(Path.GetDirectoryName(settingsPath)!, PositionFileName),
                    (Action<bool>)Timing, (Func<double[]>)Take, (Func<string>)(() => PerfRecorder.Arm(PanelRecording)), (Func<bool>)(() => PerfRecorder.Busy),
                    (Func<double[]>)EngineNumbers, (Func<string[]?>)LastRecording, tweaksOn, tweaksAll, switchedOff,
                    ui.Select(u => u.Name).ToArray(), ui.Select(u => u.English).ToArray(), now - sinceStart,
                    new HarmonyLib.Harmony("OniMoono.Host.PerfMonitor")]);
                create = overlay.GetMethod("TryCreate")!.CreateDelegate<Func<bool>>();
                frame = overlay.GetMethod("Frame")!.CreateDelegate<Action<double, double, double, double>>();
                PlayerLoop.FrameStart += Create;
                PlayerLoop.FrameStart += Tick;
            }
            catch (Exception e)
            {
                var cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                EngineLog.Engine($"fps counter: not installed: {cause.GetType().Name}: {cause.Message}");
            }
        }

        static void Create()
        {
            bool made;
            try
            {
                made = create!();
            }
            catch (Exception e)
            {
                EngineLog.Engine($"fps counter: not made: {e.GetType().Name}: {e.Message}");
                made = true;
            }
            if (made) PlayerLoop.FrameStart -= Create;
        }

        /// Each frame's start: the frame that just ended, to the game side.
        static void Tick()
        {
            long start = PlayerLoop.FrameStartedAt;
            double pause = GC.GetTotalPauseDuration().TotalMilliseconds;
            double jit = System.Runtime.JitInfo.GetCompilationTime(currentThread: true).TotalMilliseconds;
            if (lastStart != 0)
            {
                try
                {
                    frame!(start / (double)Stopwatch.Frequency, (start - lastStart) * msPerTick, Math.Max(0, pause - lastPause), Math.Max(0, jit - lastJit));
                }
                catch (Exception e)
                {
                    EngineLog.Engine($"fps counter: stopped: {e.GetType().Name}: {e.Message}");
                    PlayerLoop.FrameStart -= Tick;
                }
            }
            lastStart = start;
            lastPause = pause;
            lastJit = jit;
        }

        static void Timing(bool on)
        {
            if (on && panel == null) panel = FrameWindows.Open();
            else if (!on && panel != null)
            {
                FrameWindows.Drop(panel);
                panel = null;
            }
        }

        /// The window's frames since the last call, and a new count from here: frames, wall ms,
        /// the 9 phases' ms (FrameWindows' order), outside the loop, the 5 waits' ms, device calls,
        /// GC pause ms, frames with a GC pause, the longest frame, the 9 phases' longest, outside's
        /// longest.
        static double[] Take()
        {
            var w = panel;
            var n = new double[31];
            if (w == null) return n;
            n[0] = w.Frames;
            n[1] = w.WallMs;
            for (int i = 0; i < FrameWindows.Phases; i++) n[2 + i] = w.PhaseMs[i];
            n[11] = w.OutsideMs;
            for (int i = 0; i < FrameWindows.Waits; i++) n[12 + i] = w.WaitMs[i];
            n[17] = w.DeviceCalls;
            n[18] = w.GcPauseMs;
            n[19] = w.FramesWithGc;
            n[20] = w.MaxFrameMs;
            for (int i = 0; i < FrameWindows.Phases; i++) n[21 + i] = w.PhaseMaxMs[i];
            n[30] = w.OutsideMaxMs;
            w.Clear();
            return n;
        }

        /// The window's memory numbers (asked once a second while it shows): the process's working
        /// set, the memory installed, bytes allocated so far, gen0, gen1 and gen2 collections, the
        /// last background save write (ms; 0 for none).
        static double[] EngineNumbers()
        {
            var info = GC.GetGCMemoryInfo();
            return [Environment.WorkingSet, info.TotalAvailableMemoryBytes, GC.GetTotalAllocatedBytes(false),
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GameTweaks.BackgroundSave.LastWriteMs];
        }

        /// The newest recording in the perf folder: its name, its age in seconds, then
        /// "phase=ms a frame" for each `phase/` row; null when there is none.
        static string[]? LastRecording()
        {
            try
            {
                var dir = Path.Combine(EngineConfig.PersistentDataPath, "oni-moono", "perf");
                if (!Directory.Exists(dir)) return null;
                var file = new DirectoryInfo(dir).GetFiles("perf-*.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (file == null) return null;
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
                var result = new System.Collections.Generic.List<string> { file.Name, (DateTime.UtcNow - file.LastWriteTimeUtc).TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) };
                foreach (var row in doc.RootElement.GetProperty("rows"u8).EnumerateArray())
                {
                    var key = row.GetProperty("key"u8).GetString();
                    if (key == null || !key.StartsWith("phase/", StringComparison.Ordinal)) continue;
                    result.Add(key.Substring(6) + "=" + row.GetProperty("ms_pf"u8).GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                }
                return result.ToArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
            {
                return null;
            }
        }
    }
}
