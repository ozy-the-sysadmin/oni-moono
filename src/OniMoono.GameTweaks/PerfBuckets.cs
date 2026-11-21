using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;

namespace OniMoono.GameTweaks
{
    /// The performance window's look inside "Game update" (its Frame tab, while that row is
    /// open): the game's state machine updates timed by their rate. A prefix and a postfix on
    /// StateMachineUpdater.BucketGroup.AdvanceOneSubTick, patched in when the row opens and taken
    /// out when it closes, read the clock around the game's own method, which always runs as it
    /// is (and with any mod's patches on it): nothing of the game or of a mod is replaced. The
    /// prefix also counts the objects the sub-tick will update (the buckets' counts, read only).
    /// Per rate group (sim every tick, 33 ms, 200 ms, 1 s, 4 s; render every frame, 200 ms, 1 s)
    /// it keeps the time, the sub-ticks run, the objects and the longest sub-tick. Coarser than
    /// timing each bucket, which would need the game's loop replaced.
    ///
    /// The groups are kept by their rate (an enum), so nothing of a colony is referenced after
    /// it is left; Clear also runs at every scene change (PerfOverlay).
    public static class PerfBuckets
    {
        /// The host's Harmony for it (OniMoono.Host.Perf.PerfHud; an OniMoono.* ID, so ours).
        internal static Harmony Harmony;
        static bool on;
        static int mainThread = -1;

        public sealed class Kind
        {
            public UpdateRate Rate;
            public long Ticks, Calls, Entries, MaxTicks;
        }

        static readonly Dictionary<UpdateRate, Kind> byRate = new Dictionary<UpdateRate, Kind>();

        public static bool On => on;

        /// Frames seen while on (PerfOverlay.Frame counts them): the per-frame numbers' divisor.
        public static long Frames;

        /// Patches the timing in (true) or takes it out (false). Why not, or null.
        public static string Switch(bool on)
        {
            try
            {
                if (on == PerfBuckets.on) return null;
                if (Harmony == null) return "no Harmony from the host";
                var method = AccessTools.Method(typeof(StateMachineUpdater.BucketGroup), nameof(StateMachineUpdater.BucketGroup.AdvanceOneSubTick));
                if (method == null) return "StateMachineUpdater.BucketGroup.AdvanceOneSubTick is not in this game version";
                if (on)
                {
                    mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    Clear();
                    Harmony.Patch(method, prefix: new HarmonyMethod(typeof(PerfBuckets), nameof(Before)) { priority = Priority.Last },
                        postfix: new HarmonyMethod(typeof(PerfBuckets), nameof(After)) { priority = Priority.First });
                }
                else
                {
                    Harmony.Unpatch(method, HarmonyPatchType.Prefix, Harmony.Id);
                    Harmony.Unpatch(method, HarmonyPatchType.Postfix, Harmony.Id);
                }
                PerfBuckets.on = on;
                return null;
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
        }

        public static void Clear()
        {
            byRate.Clear();
            Frames = 0;
        }

        /// Every rate group with its counts since the last Clear.
        public static IEnumerable<Kind> Kinds => byRate.Values;

        public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// The prefix (last of the prefixes, nearest the game's code): the clock and the objects
        /// the sub-tick will update. Its state carries both to the postfix.
        static void Before(StateMachineUpdater.BucketGroup __instance, out long __state)
        {
            __state = 0;
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != mainThread) return;
            long entries = 0;
            try
            {
                var list = __instance.bucketFrames[__instance.nextUpdateIndex];
                for (int i = 0; i < list.Count; i++) entries += list[i].count;
            }
            catch (Exception)
            {
                // A changed game: the objects are not counted, the time still is.
            }
            pendingEntries = entries;
            __state = Stopwatch.GetTimestamp();
        }

        static long pendingEntries;

        /// The postfix (first of the postfixes): the sub-tick's time, added to its rate group.
        static void After(StateMachineUpdater.BucketGroup __instance, long __state)
        {
            if (__state == 0) return;
            long ticks = Stopwatch.GetTimestamp() - __state;
            var rate = __instance.updateRate;
            if (!byRate.TryGetValue(rate, out var kind)) byRate[rate] = kind = new Kind { Rate = rate };
            kind.Ticks += ticks;
            kind.Calls++;
            kind.Entries += pendingEntries;
            if (ticks > kind.MaxTicks) kind.MaxTicks = ticks;
        }
    }
}
