using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using OniMoono.Tweaks;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-storage-panel` (changes what the details panel shows a little: off by
    /// default, left out of `exact`). The details panel's Status tab lists what the selected
    /// object stores (SimpleInfoScreen.RefreshStoragePanel: every stored item's name, mass and
    /// temperature, grouped). Besides its refresh every frame while the panel shows, every item
    /// stored in or taken from the selected object fires an event that refreshes the whole list
    /// again (TriggerRefreshStorage): a bin receiving 700 items in a frame refreshes 700 times,
    /// and the events keep refreshing the list while the panel is closed.
    ///
    /// The tweak answers those events with one refresh a frame, made just before the canvases are
    /// laid out and drawn (Canvas.preWillRenderCanvases), and none while the panel is hidden: one
    /// is made when the panel shows again, before it is drawn. The refresh is the game's own
    /// method, a function of the colony's state when it runs (labels by id, the unused hidden by
    /// Commit, groups redrawn through their expand callbacks), so many refreshes leave what one
    /// leaves. What changes: the list shows the items as they are when the frame is drawn, not as
    /// they were at the frame's last storage event (a few milliseconds apart within one frame;
    /// masses and temperatures can differ in their last digits), and a panel shown again shows
    /// the current contents at once, where the game shows the contents of the last event while
    /// hidden for one frame. Nothing else reads the list. No game state changes.
    ///
    /// Ported from McCore's PanelOpts (our own code): its event parts only. Its snapshot of the
    /// list (values up to seconds old) and its windowed rows are not part of this tweak.
    ///
    /// With ONI_TWEAK_VERIFY=1 every refresh this tweak makes is made twice and the panel's texts
    /// and shown rows compared, which must be the same (the premise that one refresh leaves what
    /// many leave); the counts are logged every 256 refreshes.
    ///
    /// Off for the session once another Harmony owner patches TriggerRefreshStorage or
    /// RefreshStoragePanel (checked at the first event, then every 4,096).
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void SimpleInfoScreen::TriggerRefreshStorage(System.Object)", "a6b697ca2a228537 win-764025")]
    [GameMember(GameUse.Uses, "System.Void SimpleInfoScreen::RefreshStoragePanel(CollapsibleDetailContentPanel,UnityEngine.GameObject)")]
    public static class FastStoragePanel
    {
        public const string Name = "fast-storage-panel";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static MethodBase[] guarded;
        static bool off;

        /// Test infrastructure (PerfProbe's ab.txt, ONI_FRAME_STATS_AB): true runs the game's method.
        public static bool Bypass;
        /// Counts since the start: events, events while hidden, refreshes made; verify mode's
        /// refreshes checked and those whose second run differed.
        public static long Events, HiddenEvents, Refreshes, Wrong;
        static long verified;

        public static void Counters(Action<string, long> counter)
        {
            counter("events", Events);
            counter("hidden-events", HiddenEvents);
            counter("refreshes", Refreshes);
            counter("verified", verified);
            counter("wrong", Wrong);
            counter("stood-aside", off ? 1 : 0);
        }

        static SimpleInfoScreen pending;
        static bool hooked, running;
        static readonly Canvas.WillRenderCanvases runPending = RunPending;

        /// On the details screen: a refresh owed while it was hidden runs when it shows again.
        public sealed class Wake : MonoBehaviour
        {
            public SimpleInfoScreen Screen;
            public bool Owed;

            void OnEnable()
            {
                if (!Owed || Screen == null) return;
                Owed = false;
                // Not during the activation itself: before the canvases are laid out.
                Schedule(Screen);
            }
        }

        public static void Install(Harmony harmony)
        {
            guarded = Guarded();
            harmony.Patch(guarded[0], prefix: new HarmonyMethod(typeof(FastStoragePanel), nameof(TriggerRefreshStorage)) { priority = Priority.Last });
        }

        internal static MethodBase[] Guarded() =>
        [
            AccessTools.Method(typeof(SimpleInfoScreen), nameof(SimpleInfoScreen.TriggerRefreshStorage), new[] { typeof(object) }),
            AccessTools.Method(typeof(SimpleInfoScreen), nameof(SimpleInfoScreen.RefreshStoragePanel), new[] { typeof(CollapsibleDetailContentPanel), typeof(GameObject) }),
        ];

        static bool Foreign()
        {
            if (ForeignPatches.Find(guarded) is not { } why) return false;
            global::Debug.Log($"{Name}: off ({why})");
            return true;
        }

        /// SimpleInfoScreen.TriggerRefreshStorage: noted, refreshed once before the frame is drawn
        /// (or when the panel shows again).
        static bool TriggerRefreshStorage(SimpleInfoScreen __instance, bool __runOriginal)
        {
            if (!__runOriginal || off || Bypass || running) return __runOriginal;
            if ((Events++ & 0xFFF) == 0 && (off = Foreign()))
            {
                Flush();
                return true;
            }
            if (__instance == null) return true;
            if (!__instance.gameObject.activeInHierarchy)
            {
                HiddenEvents++;
                var wake = __instance.GetComponent<Wake>() ?? __instance.gameObject.AddComponent<Wake>();
                wake.Screen = __instance;
                wake.Owed = true;
                return false;
            }
            Schedule(__instance);
            return false;
        }

        static void Schedule(SimpleInfoScreen screen)
        {
            // Another screen than the one owed (not expected): its refresh now.
            if (pending != null && !ReferenceEquals(pending, screen)) Run();
            pending = screen;
            if (hooked) return;
            hooked = true;
            Canvas.preWillRenderCanvases += runPending;
        }

        static void RunPending()
        {
            Canvas.preWillRenderCanvases -= runPending;
            hooked = false;
            Run();
        }

        /// Turned off with a refresh owed: it runs now, as the game's would have.
        static void Flush()
        {
            if (hooked)
            {
                Canvas.preWillRenderCanvases -= runPending;
                hooked = false;
            }
            Run();
        }

        static void Run()
        {
            var screen = pending;
            pending = null;
            if (screen == null) return;
            Refreshes++;
            running = true;
            try
            {
                screen.TriggerRefreshStorage(null);
                if (verify)
                {
                    string first = Image(screen.StoragePanel);
                    screen.TriggerRefreshStorage(null);
                    string second = Image(screen.StoragePanel);
                    verified++;
                    if (first != second) global::Debug.Log($"{Name}: a second refresh is wrong ({++Wrong}): [{first}] then [{second}]");
                    if ((verified & 0xFF) == 1) global::Debug.Log($"{Name}: {verified} refreshes checked, {Wrong} wrong");
                }
            }
            catch (Exception e)
            {
                // The game's refresh threw, as it would have from the event.
                global::Debug.LogException(e);
            }
            finally
            {
                running = false;
            }
        }

        /// Verify mode: the panel's texts and which of its rows show.
        static string Image(CollapsibleDetailContentPanel panel)
        {
            if (panel == null) return "";
            var text = new StringBuilder();
            text.Append(panel.gameObject.activeSelf ? "shown" : "hidden");
            foreach (var label in panel.GetComponentsInChildren<LocText>(true))
                text.Append('|').Append(label.gameObject.activeInHierarchy ? '+' : '-').Append(label.text);
            return text.ToString();
        }
    }
}
