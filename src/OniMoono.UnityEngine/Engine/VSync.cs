namespace OniMoono.Engine
{
    /// The swap interval the devices present with: QualitySettings.vSyncCount (the game's quality
    /// level, or a mod that sets it), unless the player chose otherwise. `Override` is set by the
    /// host from `--vsync-count N` (wins) or the Moono Tweaks "Vertical sync" row ("Off": 0), and
    /// can change while the game runs: the devices read Interval at every present, and the
    /// presenters remake their swapchain (Vulkan) or pass the interval to DXGI's Present when it
    /// changes, so a change applies from the next frame.
    internal static class VSync
    {
        static volatile int overrideInterval = -1;

        /// The interval chosen over the game's (0 to 4), or -1 to follow QualitySettings.vSyncCount.
        public static int Override
        {
            get => overrideInterval;
            set => overrideInterval = value < 0 ? -1 : System.Math.Min(value, 4);
        }

        /// The swap interval for the next present: 0 without vsync, else refreshes per picture.
        public static int Interval
        {
            get
            {
                int chosen = overrideInterval;
                return chosen >= 0 ? chosen : UnityEngine.QualitySettings.vSyncCount;
            }
        }
    }
}
