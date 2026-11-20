using System;
using System.Collections.Generic;

namespace OniMoono.GameTweaks
{
    /// The game's side of the performance recorder (OniMoono.Host.PerfRecorder), not a tweak: what
    /// it reads of the colony, the camera and the mods, and the speed it sets. No patches: plain
    /// reads of the game's state, made when a recording starts or ends (Colony, View, Mods) or
    /// once a frame while one waits or runs (InColony, Paused, GameTime). Each answers safely
    /// outside a colony and while one is torn down.
    public static class PerfGame
    {
        /// A colony is loaded and running (not loading, not leaving).
        public static bool InColony()
        {
            try
            {
                return Game.Instance != null && !KMonoBehaviour.isLoadingScene && !Game.Instance.IsLoading()
                    && SpeedControlScreen.Instance != null && GameClock.Instance != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool Paused()
        {
            try
            {
                var speed = SpeedControlScreen.Instance;
                return speed == null || speed.IsPaused;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// The game's speed setting, 1 to 3 (0 outside a colony).
        public static int Speed()
        {
            try
            {
                var speed = SpeedControlScreen.Instance;
                return speed != null ? speed.GetSpeed() + 1 : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// Sets the speed (1 to 3) as the speed buttons do; a paused game stays paused.
        public static void SetSpeed(int speed)
        {
            var screen = SpeedControlScreen.Instance;
            if (screen != null && speed >= 1 && speed <= 3) screen.SetSpeed(speed - 1);
        }

        /// The game's clock in game seconds (-1 outside a colony).
        public static double GameTime()
        {
            try
            {
                return GameClock.Instance != null ? GameClock.Instance.GetTime() : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// The colony's size: names and values in the same order.
        public static (string[] Keys, double[] Values) Colony()
        {
            var keys = new List<string>();
            var values = new List<double>();
            void Add(string key, Func<double> read)
            {
                try
                {
                    values.Add(read());
                    keys.Add(key);
                }
                catch (Exception)
                {
                    // A count this game version lacks is left out.
                }
            }
            Add("duplicants", () => Components.LiveMinionIdentities.Count);
            Add("robots", () => Components.LiveRobotsIdentities.Count);
            Add("brains", () => Components.Brains.Count);
            Add("buildings", () => Components.BuildingCompletes.Count);
            Add("pickupables", () => Components.Pickupables.Count);
            Add("crops", () => Components.Crops.Count);
            Add("worlds", () => ClusterManager.Instance != null ? ClusterManager.Instance.worldCount : 0);
            Add("cycle", () => GameClock.Instance != null ? GameClock.Instance.GetCycle() + 1 : 0);
            Add("game_time_s", () => GameClock.Instance != null ? GameClock.Instance.GetTime() : 0);
            return (keys.ToArray(), values.ToArray());
        }

        /// The camera (x, y, orthographic size) and the active world (its id and name).
        public static (double[] Camera, int WorldId, string WorldName) View()
        {
            double[] camera = null;
            int id = -1;
            string name = null;
            try
            {
                var controller = CameraController.Instance;
                if (controller != null)
                {
                    var p = controller.transform.position;
                    camera = new double[] { p.x, p.y, controller.baseCamera != null ? controller.baseCamera.orthographicSize : 0 };
                }
                var clusters = ClusterManager.Instance;
                if (clusters != null)
                {
                    id = clusters.activeWorldId;
                    name = clusters.GetWorld(id)?.worldName;
                }
            }
            catch (Exception)
            {
            }
            return (camera, id, name);
        }

        /// The mods enabled for the running game: title and static ID of each.
        public static (string[] Titles, string[] Ids) Mods()
        {
            var titles = new List<string>();
            var ids = new List<string>();
            try
            {
                var mods = Global.Instance?.modManager?.mods;
                if (mods != null)
                    foreach (var mod in mods)
                    {
                        if (mod == null || !mod.IsEnabledForActiveDlc()) continue;
                        titles.Add(mod.title);
                        ids.Add(mod.staticID);
                    }
            }
            catch (Exception)
            {
            }
            return (titles.ToArray(), ids.ToArray());
        }

        /// The game's build ("U60-764025").
        public static string Build()
        {
            try
            {
                return LaunchInitializer.BuildPrefix() + "-" + KleiVersion.ChangeList;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// Frame stats' game rows since start-up (GameFrameStats.Totals; null without frame stats).
        public static long[] GameTotals() => GameFrameStats.Enabled ? GameFrameStats.Totals() : null;

        public static string[] GameSpanNames() => GameFrameStats.SpanNames;

        /// The bucket profile's kinds since start-up (null without ONI_BUCKET_PROFILE=1).
        public static object[] BucketTotals()
        {
            if (!BucketProfile.Enabled) return null;
            var (labels, ticks, calls, entries) = BucketProfile.Totals();
            return new object[] { labels, ticks, calls, entries };
        }
    }
}
