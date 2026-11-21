using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;

namespace OniMoono.GameTweaks
{
    /// The performance window's look inside "Game update" (its Frame tab, while that row is
    /// open): the bucket profile's timed loop (BucketProfile), patched in when the row opens
    /// and taken out when it closes, so it costs nothing otherwise. It times each state machine
    /// bucket's Update in StateMachineUpdater.BucketGroup.AdvanceOneSubTick, the same calls in
    /// the same order, and keeps per kind the time, the calls, the entries and the longest call.
    /// No tweak patches that method, and the patch is ours (OniMoono.*), so none steps aside.
    /// With `--frame-stats` and ONI_BUCKET_PROFILE=1 the frame stats' own profile runs the loop
    /// instead (it comes first) and this one only reads nothing.
    public static class PerfBuckets
    {
        /// The host's Harmony for it (OniMoono.Host.Perf.PerfHud; an OniMoono.* ID, so ours).
        internal static Harmony Harmony;
        static bool on;
        static int mainThread = -1;

        public sealed class Kind
        {
            public string Label;
            public long Ticks, Calls, Entries, MaxTicks;
        }

        static readonly Dictionary<StateMachineUpdater.BaseUpdateBucket, Kind> byBucket = new Dictionary<StateMachineUpdater.BaseUpdateBucket, Kind>();
        static readonly Dictionary<string, Kind> byLabel = new Dictionary<string, Kind>();

        public static bool On => on;

        /// Patches the loop in (true) or takes it out (false). Why not, or null.
        public static string Switch(bool on)
        {
            try
            {
                if (on == PerfBuckets.on) return null;
                if (Harmony == null) return "no Harmony from the host";
                var method = AccessTools.Method(typeof(StateMachineUpdater.BucketGroup), nameof(StateMachineUpdater.BucketGroup.AdvanceOneSubTick));
                if (on)
                {
                    mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    Clear();
                    Harmony.Patch(method, prefix: new HarmonyMethod(typeof(PerfBuckets), nameof(Advance)) { priority = Priority.First - 2 });
                }
                else
                {
                    Harmony.Unpatch(method, HarmonyPatchType.Prefix, Harmony.Id);
                }
                PerfBuckets.on = on;
                return null;
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
        }

        /// "SIM_200ms" as the window says it: "sim 200 ms".
        static string Rate(string rate) => rate.Replace("SIM_", "sim ").Replace("RENDER_", "render ").Replace("EVERY_TICK", "every tick").Replace("ms", " ms");

        public static void Clear()
        {
            foreach (var k in byLabel.Values) k.Ticks = k.Calls = k.Entries = k.MaxTicks = 0;
        }

        /// Every kind with its counts since the last Clear.
        public static IEnumerable<Kind> Kinds => byLabel.Values;

        public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// The prefix: on the main thread, unless an earlier prefix (frame stats' profile) already
        /// ran the loop, the timed loop replaces the game's (BucketProfile.Timed's, with the
        /// longest call kept).
        static bool Advance(StateMachineUpdater.BucketGroup __instance, float dt, bool __runOriginal)
        {
            if (!__runOriginal || System.Threading.Thread.CurrentThread.ManagedThreadId != mainThread) return __runOriginal;
            var group = __instance;
            var list = group.bucketFrames[group.nextUpdateIndex];
            int count = list.Count;
            string rate = null;
            for (int i = 0; i < count; i++)
            {
                var bucket = list[i];
                int entries = bucket.count;
                if (entries == 0) continue;
                long start = Stopwatch.GetTimestamp();
                bucket.Update(dt);
                long ticks = Stopwatch.GetTimestamp() - start;
                if (!byBucket.TryGetValue(bucket, out var kind))
                {
                    rate = rate ?? group.updateRate.ToString();
                    // Most buckets name their rate already ("RequireInputs SIM_200ms").
                    string name = bucket.name ?? "?";
                    string label = name.Contains("SIM_") || name.Contains("RENDER_") ? Rate(name) : name + " · " + Rate(rate);
                    if (!byLabel.TryGetValue(label, out kind)) byLabel[label] = kind = new Kind { Label = label };
                    byBucket[bucket] = kind;
                }
                kind.Ticks += ticks;
                kind.Calls++;
                kind.Entries += entries;
                if (ticks > kind.MaxTicks) kind.MaxTicks = ticks;
            }
            group.nextUpdateIndex = (group.nextUpdateIndex + 1) % group.bucketFrames.Count;
            return false;
        }
    }
}
