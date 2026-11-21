using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// The performance monitor's reads of the game (PerfOverlay, PerfTrack): plain reads of its
    /// state, no patches, so no tweak steps aside for them. Frame: a few fields a frame while
    /// the counter is on. Colony: counts, every few seconds while the Colony tab shows.
    public static class PerfReads
    {
        static int lastSub = -1, lastWorld = -1;
        static float lastSimDt;
        static double lastGameTime = -1;
        static bool lastPaused = true, lastTimelapse;
        static double lastScale;
        static bool saved, autoSaved;
        static string lastSaveFile;
        static int lastSaveSize = -1;
        static bool listening;
        /// sim-step-pace is on: the game makes no zero-time calls, so none is counted.
        public static bool SimStepPaceOn;

        /// The game's "Saved to [path]" line (SaveLoader.Save, after the file is written): a save
        /// ended in this frame. An autosave's path is in its auto_save folder.
        static void OnLog(string condition, string stackTrace, LogType type)
        {
            // The game's own logger puts its time and level first ("[00:06:20.614] [2] [INFO] Saved to [path]").
            if (condition == null || condition.IndexOf("Saved to [", StringComparison.Ordinal) < 0) return;
            saved = true;
            autoSaved = condition.IndexOf("auto_save", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void Listen(bool on)
        {
            if (on == listening) return;
            listening = on;
            if (on) Application.logMessageReceived += OnLog;
            else Application.logMessageReceived -= OnLog;
        }

        /// The frame that just ended, read at the next frame's start: `ms` its wall time, `gcMs`
        /// and `jitMs` the engine's GC pause and the main thread's JIT time in it, `now` the
        /// real time in seconds.
        public static PerfFrame Frame(double now, double ms, double gcMs, double jitMs)
        {
            var f = new PerfFrame { Now = now, Ms = ms, GcMs = gcMs, JitMs = jitMs, Saved = saved, AutoSave = autoSaved };
            saved = autoSaved = false;
            try
            {
                f.InMenu = MainMenu.Instance != null;
                var game = Game.Instance;
                bool colony = PerfGame.InColony();
                f.InColony = colony;
                f.Loading = KMonoBehaviour.isLoadingScene || (!colony && !f.InMenu) || (game != null && game.IsLoading());
                // A save in a running colony: SaveLoader.Save sets both of these after writing (and a
                // load before the colony runs). Its log line is the other sign (OnLog).
                string file = KCrashReporter.MOST_RECENT_SAVEFILE;
                int size = SaveLoader.Instance != null ? SaveLoader.Instance.lastUncompressedSize : -1;
                if (colony && lastSaveSize >= 0 && (!ReferenceEquals(file, lastSaveFile) || size != lastSaveSize))
                {
                    f.Saved = true;
                    f.AutoSave = file != null && file.IndexOf("auto_save", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                lastSaveFile = file;
                lastSaveSize = colony ? size : -1;
                if (!colony)
                {
                    lastSub = -1;
                    lastWorld = -1;
                    lastGameTime = -1;
                    lastPaused = true;
                    return f;
                }
                // What governed the frame: the pause and speed at its start.
                f.Paused = lastPaused;
                f.Scale = lastScale;
                lastPaused = SpeedControlScreen.Instance.IsPaused;
                lastScale = Time.timeScale;
                double time = GameClock.Instance.GetTime();
                f.GameDt = lastGameTime >= 0 && time >= lastGameTime && time - lastGameTime < 60 ? time - lastGameTime : 0;
                lastGameTime = time;
                // No sub-tick ran, but time was added (SimEveryTick's else branch): the
                // zero-time call that waits for the step under way. 12 sub-ticks in one frame
                // would leave the counter where it was too, but they take time out of simDt.
                int sub = game.simSubTick;
                float simDt = game.simDt;
                f.ZeroSubTick = !SimStepPaceOn && lastSub >= 0 && sub == lastSub && simDt > lastSimDt + 1e-6f;
                if (lastSub >= 0)
                {
                    // Sub-ticks advanced; 12 when the counter came round: simDt did not grow
                    // although the game ran (a frame with no sub-tick adds its time to simDt; one
                    // capped at 0.2 s runs 12 and can leave simDt where it was). The 200 ms step
                    // runs when the counter passes 0.
                    int advanced = (sub - lastSub + 12) % 12;
                    if (advanced == 0 && simDt <= lastSimDt + 1e-6f && f.Scale > 0 && !f.Paused) advanced = 12;
                    f.SubTicks = advanced;
                    f.Step200 = advanced > 0 && (sub < lastSub || advanced == 12 || sub == 0);
                }
                lastSub = sub;
                lastSimDt = simDt;
                int world = ClusterManager.Instance != null ? ClusterManager.Instance.activeWorldId : -1;
                f.WorldSwitch = lastWorld >= 0 && world >= 0 && world != lastWorld;
                lastWorld = world;
                var timelapser = game.timelapser;
                bool pending = timelapser != null && (timelapser.screenshotPending || timelapser.screenshotActive);
                f.Timelapse = (pending || lastTimelapse) && !(timelapser != null && timelapser.previewScreenshot);
                f.Preview = (pending || lastTimelapse) && timelapser != null && timelapser.previewScreenshot;
                lastTimelapse = pending;
            }
            catch (Exception)
            {
                // Half torn down (leaving a colony): what was read stays.
            }
            return f;
        }

        /// The game's speed setting 1 to 3 (0 outside a colony).
        public static int Speed() => PerfGame.Speed();

        public struct ColonyCounts
        {
            public int Cycle, Duplicants, Critters, LooseItems, Buildings, Utilities, Asteroids;
        }

        /// The colony's counts (each -1 when it could not be read): duplicants, critters (brains
        /// that are not duplicants or robots), loose items (pickupables in no storage that are
        /// not creatures), buildings and utilities (pipes, wires, rails: buildings whose def is a
        /// utility), asteroids discovered (those of the planetoid list, rocket interiors left out).
        public static ColonyCounts Colony()
        {
            var c = new ColonyCounts { Cycle = -1, Duplicants = -1, Critters = -1, LooseItems = -1, Buildings = -1, Utilities = -1, Asteroids = -1 };
            if (!PerfGame.InColony()) return c;
            try { c.Cycle = GameClock.Instance.GetCycle() + 1; } catch (Exception) { }
            try { c.Duplicants = Components.LiveMinionIdentities.Count; } catch (Exception) { }
            try
            {
                int critters = 0;
                foreach (var brain in Components.Brains.Items)
                    if (brain is CreatureBrain) critters++;
                c.Critters = critters;
            }
            catch (Exception) { }
            try
            {
                int loose = 0;
                foreach (var item in Components.Pickupables.Items)
                    if (item != null && item.storage == null && !item.KPrefabID.HasTag(GameTags.Creature) && !item.KPrefabID.HasTag(GameTags.BaseMinion)) loose++;
                c.LooseItems = loose;
            }
            catch (Exception) { }
            try
            {
                int buildings = 0, utilities = 0;
                foreach (var b in Components.BuildingCompletes.Items)
                {
                    if (b == null) continue;
                    if (b.Def != null && b.Def.isUtility) utilities++;
                    else buildings++;
                }
                c.Buildings = buildings;
                c.Utilities = utilities;
            }
            catch (Exception) { }
            try
            {
                // The planetoid list's asteroids (WorldSelector shows the discovered worlds, rocket
                // interiors under them): the simulation steps every discovered world.
                int discovered = 0;
                foreach (var world in ClusterManager.Instance.WorldContainers)
                    if (world != null && !world.IsModuleInterior && world.IsDiscovered) discovered++;
                c.Asteroids = discovered;
            }
            catch (Exception) { }
            return c;
        }

        /// Every world with its flags (test runs: the Asteroids count against the planetoid list).
        public static string Worlds()
        {
            try
            {
                var parts = new List<string>();
                foreach (var world in ClusterManager.Instance.WorldContainers)
                    if (world != null)
                        parts.Add(world.GetComponent<ClusterGridEntity>()?.Name + (world.IsModuleInterior ? " interior" : "") + (world.IsDiscovered ? " discovered" : "") + (world.IsDupeVisited ? " visited" : ""));
                return string.Join("; ", parts);
            }
            catch (Exception e)
            {
                return e.GetType().Name;
            }
        }

        /// Tweaks that step aside for a mod's patch this run (ModOverlaps), distinct.
        public static int SteppedAside()
        {
            try
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var o in ModOverlaps.Found)
                    if (o.Grade == OverlapGrade.StepsAside) names.Add(o.Tweak);
                return names.Count;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// The cases of SteppedAside (ModOverlaps' records, as the log's overlap table lists them):
        /// one per tweak, method and mod.
        public static List<Overlap> Adjusted()
        {
            var list = new List<Overlap>();
            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var o in ModOverlaps.Found)
                    if (o.Grade == OverlapGrade.StepsAside && seen.Add(o.Tweak + "|" + o.Method + "|" + o.Mod)) list.Add(o);
            }
            catch (Exception)
            {
            }
            return list;
        }

        /// The game's worker threads (its job manager's, the main thread not counted).
        public static int Workers()
        {
            try { return GlobalJobManager.ThreadCount - 1; }
            catch (Exception) { return -1; }
        }
    }
}
