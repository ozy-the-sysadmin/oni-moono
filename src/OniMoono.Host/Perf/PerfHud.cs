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
    /// by a frame-start call that stops once it has. Off: nothing is installed and nothing runs;
    /// the Moono Tweaks row switches it on or off while the game runs (SetOn). On: one
    /// frame-start call a frame hands the frame that ended (its wall time from PlayerLoop's
    /// frame starts, the GC's pause and the main thread's JIT time in it) to the game side. The
    /// window's deeper numbers come from a frame window (FrameWindows) open while its Colony or
    /// Frame tab shows, and in the menus and loads (the copy report's account of a load); its
    /// Record button arms the recorder for 60 seconds.
    internal static class PerfHud
    {
        public const string PanelRecording = "delay=0;seconds=60;count=1;gap=0;label=panel";
        /// The badge's place, beside the settings file.
        public const string PositionFileName = "perf-counter-position.txt";

        static Func<bool>? create;
        static Action<double, double, double, double>? frame;
        static Action<bool>? show;
        static FrameWindows.Window? panel;
        static long lastStart;
        static double lastPause = -1, lastJit = -1;
        static readonly double msPerTick = 1000.0 / Stopwatch.Frequency;
        static string settings = "";
        static string[] on = [], off = [];
        static int all;
        static bool installed, ticking;

        /// Program, after the tweaks are in. `fpsCounter`: "on" or "off" (EffectiveOptions);
        /// `switchedOff`: the tweaks on by default that the player turned off. Off: nothing is
        /// installed until the Moono Tweaks row turns it on (SetOn).
        public static void Install(string fpsCounter, string settingsPath, string[] tweaksOn, int tweaksAll, string[] switchedOff)
        {
            settings = settingsPath;
            on = tweaksOn;
            all = tweaksAll;
            off = switchedOff;
            if (fpsCounter == "off") return;
            InstallNow();
        }

        static void InstallNow()
        {
            if (installed) return;
            try
            {
                var overlay = GameTweaks.GameTweaks.TypedAssembly.GetType("OniMoono.GameTweaks.PerfOverlay", throwOnError: true)!;
                double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                double sinceStart = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds;
                var ui = GameTweaks.TweakStrings.Ui;
                overlay.GetMethod("Install")!.Invoke(null, [Path.Combine(Path.GetDirectoryName(settings)!, PositionFileName),
                    (Action<bool>)Timing, (Func<double[]>)Take, (Func<string>)(() => PerfRecorder.Arm(PanelRecording)), (Func<double>)(() => PerfRecorder.Progress),
                    (Func<double[]>)EngineNumbers, (Func<string[]?>)LastRecording, on, all, off,
                    ui.Select(u => u.Name).ToArray(), ui.Select(u => u.English).ToArray(), now - sinceStart,
                    new HarmonyLib.Harmony("OniMoono.Host.PerfMonitor"),
                    (Func<int>)(() => VSync.Interval), (Func<double>)(() => FramePacing.PacedPeriodMs)]);
                create = overlay.GetMethod("TryCreate")!.CreateDelegate<Func<bool>>();
                frame = overlay.GetMethod("Frame")!.CreateDelegate<Action<double, double, double, double>>();
                show = overlay.GetMethod("SetShown")!.CreateDelegate<Action<bool>>();
                installed = true;
                PlayerLoop.FrameStart += Create;
                StartTicking();
            }
            catch (Exception e)
            {
                var cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                EngineLog.Engine($"fps counter: not installed: {cause.GetType().Name}: {cause.Message}");
            }
        }

        static void StartTicking()
        {
            if (ticking) return;
            ticking = true;
            lastStart = 0;
            PlayerLoop.FrameStart += Tick;
        }

        /// The Moono Tweaks row, while the game runs (main thread): the counter shown or hidden
        /// now. Hidden, its frame call stops and its window closes; nothing is measured.
        public static void SetOn(bool value)
        {
            if (value && !installed)
            {
                InstallNow();
                return;
            }
            if (!installed) return;
            try
            {
                show!(value);
            }
            catch (Exception e)
            {
                EngineLog.Engine($"fps counter: not {(value ? "shown" : "hidden")}: {e.GetType().Name}: {e.Message}");
            }
            if (value) StartTicking();
            else if (ticking)
            {
                ticking = false;
                PlayerLoop.FrameStart -= Tick;
            }
            EngineLog.Engine($"fps counter: {(value ? "on" : "off")} (Moono Tweaks)");
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
                    ticking = false;
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
        /// longest. One buffer, filled again at each call (no allocation a frame during loads):
        /// a caller that keeps the numbers copies them.
        static readonly double[] taken = new double[31];

        static double[] Take()
        {
            var w = panel;
            var n = taken;
            Array.Clear(n);
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
        /// set, the memory installed, bytes allocated so far, gen0, gen1 and gen2 collections (a
        /// collection of a generation counts in the younger ones too), the last background save
        /// write (ms; 0 for none), its save's number and the saves handed to the writer.
        static double[] EngineNumbers()
        {
            var info = GC.GetGCMemoryInfo();
            return [Environment.WorkingSet, info.TotalAvailableMemoryBytes, GC.GetTotalAllocatedBytes(false),
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GameTweaks.BackgroundSave.LastWriteMs,
                GameTweaks.BackgroundSave.LastWriteSeq, GameTweaks.BackgroundSave.Started];
        }

        /// The newest recording in the perf folder: its name, its age in seconds when read, then
        /// "phase=ms a frame" for each `phase/` row and "wait/NAME=ms a frame" for the device's
        /// next frame and the queued pictures; null when there is none.
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
                    // The phases, and the waits the window takes out of Drawing (the device's next
                    // frame, queued pictures), so that the comparison splits the frame as the rows do.
                    string? name = key == null ? null : key.StartsWith("phase/", StringComparison.Ordinal) ? key.Substring(6)
                        : key is "wait/device-frame" or "wait/queued-frames" ? key : null;
                    if (name == null) continue;
                    result.Add(name + "=" + row.GetProperty("ms_pf"u8).GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
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
