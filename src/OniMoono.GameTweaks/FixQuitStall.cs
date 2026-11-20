using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fix-quit-stall` (Bug fixes). A bug in the game: leaving a colony while it runs
    /// unpaused can stall the game for good. App.LoadScene (quitting to the main menu, loading a
    /// save) only sets KMonoBehaviour.isLoadingScene; the scene changes in App.LateUpdate. A
    /// saved object destroyed in that frame's update pass (a Destroy the simulation asked for in
    /// Game.Update, carried out when the update pass ends, as in Unity's player and Moono's
    /// PlayerLoop) then gets OnLoadLevel instead of OnCleanUp, so its SaveLoadRoot never leaves
    /// SaveManager's lists. App.LateUpdate's OnPreLoadScene calls Game.StopBE, which reads each
    /// listed root's gameObject and throws on the destroyed one (Moono's Component.gameObject
    /// throws on a destroyed component, as Unity's does). App.LateUpdate never reaches
    /// `isLoading = false`, so every frame repeats the unload, a full collection and StopBE's
    /// exception: about one frame every few seconds, for good.
    ///
    /// The fix puts a handler first in App.OnPreLoadScene (Game appends StopBE at each colony
    /// start; the tweak's handler, added when the tweaks install, stays ahead of it). It removes
    /// from SaveManager's lists the roots already destroyed (Unity null), without unregister
    /// events, and logs how many. StopBE then destroys the live roots as usual and the scene
    /// change completes. It changes only the teardown: OnPreLoadScene runs only while a scene
    /// load is pending, only destroyed roots are removed (the paused route removes them too, in
    /// OnCleanUp), no save can be written after this point, and nothing in the next colony reads
    /// these lists. On the main menu or the first load (no SaveLoader) it does nothing.
    ///
    /// With ONI_TWEAK_VERIFY=1 every scene change with a colony's save lists is logged with the
    /// entries dropped so far (a test that leaves a running colony expects a count above 0 there).
    ///
    /// Ported from McCore's QuitStallFix (our own code). No Harmony patch. Does nothing for the
    /// session once another Harmony owner patches Game.StopBE or SaveManager.Unregister (a mod
    /// that may rely on seeing those entries).
    [GameMember(GameUse.Uses, "System.Void Game::StopBE()")]
    [GameMember(GameUse.Uses, "System.Void SaveManager::Unregister(SaveLoadRoot)")]
    public static class FixQuitStall
    {
        public const string Name = "fix-quit-stall";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static MethodBase[] guarded;
        static bool off;

        /// Test infrastructure: true makes the handler do nothing.
        public static bool Bypass;
        /// Counts since the start: scene changes seen with a colony's save lists, stale entries dropped.
        public static long Runs, Removed;

        public static void Counters(Action<string, long> counter)
        {
            counter("runs", Runs);
            counter("removed", Removed);
            counter("stood-aside", off ? 1 : 0);
        }

        public static void Install(Harmony harmony)
        {
            guarded = Guarded();
            App.OnPreLoadScene = (System.Action)Delegate.Combine(new System.Action(OnPreLoadScene), App.OnPreLoadScene);
        }

        internal static MethodBase[] Guarded() =>
        [
            AccessTools.Method(typeof(Game), nameof(Game.StopBE), Type.EmptyTypes),
            AccessTools.Method(typeof(SaveManager), nameof(SaveManager.Unregister), new[] { typeof(SaveLoadRoot) }),
        ];

        static void OnPreLoadScene()
        {
            if (off || Bypass) return;
            if (ForeignPatches.Find(guarded) is { } why)
            {
                off = true;
                global::Debug.Log($"{Name}: off ({why})");
                return;
            }
            var loader = SaveLoader.Instance;
            if (loader == null) return;
            var manager = loader.saveManager;
            if (manager == null) return;
            Runs++;
            int removed = 0;
            foreach (List<SaveLoadRoot> list in manager.GetLists().Values)
            {
                if (list == null) continue;
                // Unity's ==: true for a destroyed root as for a real null.
                for (int i = list.Count - 1; i >= 0; i--)
                    if (list[i] == null)
                    {
                        list.RemoveAt(i);
                        removed++;
                    }
            }
            if (verify) global::Debug.Log($"{Name}: {Runs} scene changes with a colony's save lists, {Removed + removed} stale entries dropped");
            if (removed == 0) return;
            Removed += removed;
            global::Debug.LogWarning($"[Moono] {Name}: dropped {removed} save entr{(removed == 1 ? "y" : "ies")} of objects destroyed while the colony was being left (the game's race when quitting or loading unpaused); the scene change can complete");
        }
    }
}
