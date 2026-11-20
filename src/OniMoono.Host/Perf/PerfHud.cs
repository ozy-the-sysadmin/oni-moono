#nullable enable
using System;
using System.Reflection;
using OniMoono.Engine;

namespace OniMoono.Host.Perf
{
    /// The HUD's frame rate counter and frame time panel (OniMoono.GameTweaks.PerfOverlay; the
    /// Frame rate counter row, `--fps-counter`): made once the game's fonts are loaded, by a
    /// frame-start call that stops once it has. The panel's numbers come from a frame window
    /// (FrameWindows) open only while the panel shows; its Record button arms the recorder for
    /// 60 seconds from now.
    internal static class PerfHud
    {
        public const string PanelRecording = "delay=0;seconds=60;count=1;gap=0;label=panel";

        static Func<bool>? create;
        static FrameWindows.Window? panel;

        /// Program, after the tweaks are in. `position`: EffectiveOptions.FpsCounterChoices.
        public static void Install(string position)
        {
            if (position == "off") return;
            try
            {
                var overlay = GameTweaks.GameTweaks.TypedAssembly.GetType("OniMoono.GameTweaks.PerfOverlay", throwOnError: true)!;
                overlay.GetMethod("Install")!.Invoke(null, [position, (Action<bool>)Timing, (Func<double[]>)Take,
                    (Func<string>)(() => PerfRecorder.Arm(PanelRecording)), (Func<bool>)(() => PerfRecorder.Busy)]);
                create = overlay.GetMethod("TryCreate")!.CreateDelegate<Func<bool>>();
                PlayerLoop.FrameStart += Create;
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

        static void Timing(bool on)
        {
            if (on && panel == null) panel = FrameWindows.Open();
            else if (!on && panel != null)
            {
                FrameWindows.Drop(panel);
                panel = null;
            }
        }

        /// The panel's frames since the last call (the layout PerfOverlay.Panel reads), and a new
        /// count from here.
        static double[] Take()
        {
            var w = panel;
            var n = new double[21];
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
            w.Clear();
            return n;
        }
    }
}
