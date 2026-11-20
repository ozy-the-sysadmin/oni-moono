using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-side-screens`. The Timer Sensor's and the automation ribbon bit
    /// selector's side screens update themselves every frame (IRenderEveryTick, from the game's
    /// state machine updater in Game.LateUpdate). Once either screen has been opened, it stays
    /// registered for the rest of the session and keeps updating while hidden: the timer screen
    /// formats two time strings into a sentence every frame, the bit selector sets every row's
    /// colour and text.
    ///
    /// The tweak skips the update while the screen is not active in the hierarchy (the details
    /// screen hides side screens by deactivating them or a parent), and runs one update when it
    /// becomes active again, before anything of it is drawn. Why what is drawn is the game's:
    /// the update writes only the screen's own texts, colours and the timer marker's rotation,
    /// from its target's state; nothing else reads them, and nothing of an inactive object is
    /// drawn. A screen made active during the frame's update pass (a click selecting a building,
    /// a tab switch) gets the frame's regular update in Game.LateUpdate before the frame is drawn,
    /// as in the game, so the catch-up's values are overwritten by the same values the game's
    /// screen shows. One activated after Game.LateUpdate (no game code does this) shows the
    /// target's state at activation: the game's state is the same then (the simulation runs in
    /// the update pass), unless the target was changed in between, where the game's screen would
    /// show the old target's numbers for that frame. A hidden screen's update that would throw is
    /// not run, so it is not logged every frame. The screen being merely disabled (its object
    /// still active, so still drawn) is not skipped.
    ///
    /// The idea is Fast Track's (Peter Han's mod, MIT; its SideScreenOpts skips these updates);
    /// this is our own implementation, with no Fast Track code.
    ///
    /// With ONI_TWEAK_VERIFY=1 hidden updates run as in the game and every activation runs the
    /// catch-up: when the frame's regular update has already run (an activation after it), the
    /// catch-up must leave the screen as the game's hidden update left it; each activation's
    /// counts are logged.
    ///
    /// Off for the session once another Harmony owner patches either update (checked at the first
    /// call, then every 4,096).
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void TimerSideScreen::RenderEveryTick(System.Single)", "7d078a1796e36318 win-764025")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void LogicBitSelectorSideScreen::RenderEveryTick(System.Single)", "08e96b077fd9d026 win-764025")]
    public static class FastSideScreens
    {
        public const string Name = "fast-side-screens";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static MethodBase[] guarded;
        static bool off;
        static bool catchingUp;

        /// Test infrastructure (PerfProbe's ab.txt, ONI_FRAME_STATS_AB): true runs the game's updates.
        public static bool Bypass;
        /// Counts since the start: updates, hidden updates skipped, catch-ups at activation; verify
        /// mode's activations after the frame's update and mismatches.
        public static long Calls, Skipped, CatchUps, LateActivations, Wrong;

        public static void Counters(Action<string, long> counter)
        {
            counter("calls", Calls);
            counter("skipped", Skipped);
            counter("ran-game", Calls - Skipped);
            counter("catch-ups", CatchUps);
            counter("late-activations", LateActivations);
            counter("wrong", Wrong);
            counter("stood-aside", off ? 1 : 0);
        }

        /// On a screen whose update was skipped (in verify mode: on every screen that was
        /// hidden): runs the catch-up when the screen becomes active.
        public sealed class Wake : MonoBehaviour
        {
            public SideScreenContent Screen;
            public bool Stale;
            /// Time.frameCount of the screen's last update that ran (verify mode).
            public int LastUpdate = -1;

            void OnEnable()
            {
                if (!Stale || Screen == null) return;
                Stale = false;
                CatchUp(this);
            }
        }

        static readonly List<Wake> wakes = new List<Wake>();

        public static void Install(Harmony harmony)
        {
            guarded = Guarded();
            harmony.Patch(guarded[0], prefix: new HarmonyMethod(typeof(FastSideScreens), nameof(Update)) { priority = Priority.Last });
            harmony.Patch(guarded[1], prefix: new HarmonyMethod(typeof(FastSideScreens), nameof(Update)) { priority = Priority.Last });
        }

        internal static MethodBase[] Guarded() =>
        [
            AccessTools.Method(typeof(TimerSideScreen), nameof(TimerSideScreen.RenderEveryTick), new[] { typeof(float) }),
            AccessTools.Method(typeof(LogicBitSelectorSideScreen), nameof(LogicBitSelectorSideScreen.RenderEveryTick), new[] { typeof(float) }),
        ];

        static bool Foreign()
        {
            if (ForeignPatches.Find(guarded) is not { } why) return false;
            global::Debug.Log($"{Name}: off ({why})");
            return true;
        }

        static Wake WakeOf(SideScreenContent screen)
        {
            for (int i = wakes.Count - 1; i >= 0; i--)
            {
                var w = wakes[i];
                if (w == null) wakes.RemoveAt(i);
                else if (ReferenceEquals(w.Screen, screen)) return w;
            }
            var made = screen.gameObject.AddComponent<Wake>();
            made.Screen = screen;
            wakes.Add(made);
            return made;
        }

        /// Either screen's RenderEveryTick: skipped while the screen is not active in the hierarchy.
        static bool Update(SideScreenContent __instance, bool __runOriginal)
        {
            if (!__runOriginal || off || Bypass || catchingUp) return __runOriginal;
            if ((Calls++ & 0xFFF) == 0 && (off = Foreign())) return true;
            if (__instance == null) return true;
            bool hidden = !__instance.gameObject.activeInHierarchy;
            if (verify)
            {
                // Hidden updates run as in the game; activations are checked.
                if (hidden || wakes.Count > 0)
                {
                    var wake = WakeOf(__instance);
                    wake.LastUpdate = Time.frameCount;
                    if (hidden) wake.Stale = true;
                }
                return true;
            }
            if (!hidden) return true;
            WakeOf(__instance).Stale = true;
            Skipped++;
            return false;
        }

        /// The update the screen missed, now that it is active again.
        static void CatchUp(Wake wake)
        {
            var screen = wake.Screen;
            CatchUps++;
            bool late = verify && wake.LastUpdate == Time.frameCount;
            string before = late ? Image(screen) : null;
            catchingUp = true;
            try
            {
                if (screen is TimerSideScreen timer) timer.RenderEveryTick(0f);
                else if (screen is LogicBitSelectorSideScreen bits) bits.RenderEveryTick(0f);
            }
            catch (Exception e)
            {
                // What the game's next update would throw too, logged as it would be.
                global::Debug.LogException(e);
            }
            finally
            {
                catchingUp = false;
            }
            if (!verify) return;
            if (late)
            {
                LateActivations++;
                string after = Image(screen);
                if (after != before) global::Debug.Log($"{Name}: a catch-up is wrong ({++Wrong}): the game's screen [{before}], the catch-up's [{after}]");
            }
            global::Debug.Log($"{Name}: {CatchUps} activations, {LateActivations} after the frame's update, {Wrong} wrong");
        }

        /// Verify mode: what either screen's update writes.
        static string Image(SideScreenContent screen)
        {
            var text = new System.Text.StringBuilder();
            if (screen is TimerSideScreen timer)
            {
                text.Append(timer.timeLeft.text).Append(" | ").Append(timer.currentTimeMarker.rotation.eulerAngles.z.ToString("R"));
            }
            else if (screen is LogicBitSelectorSideScreen bits)
            {
                foreach (var kv in bits.toggles_by_int)
                {
                    var refs = kv.Value.gameObject.GetComponent<HierarchyReferences>();
                    text.Append(kv.Key).Append(':').Append(refs.GetReference<KImage>("stateIcon").color).Append(' ').Append(refs.GetReference<LocText>("stateText").text).Append(';');
                }
                text.Append(" in ").Append(bits.inputDisplayIcon.color).Append(" out ").Append(bits.outputDisplayIcon.color);
            }
            return text.ToString();
        }
    }
}
