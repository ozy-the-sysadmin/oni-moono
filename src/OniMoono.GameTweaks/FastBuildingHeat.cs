using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;
using STRINGS;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-building-heat`. Five times a game second
    /// StructureTemperatureComponents.Sim200ms walks every building with a temperature (its
    /// header and payload lists, a colony's every building): a dirty entry sends its heat
    /// exchange to the sim; an active building that operates adds its operating (and exhaust)
    /// kilowatts to its own running averages and, when it exhausts heat, sends one ModifyEnergy
    /// message per cell of its footprint, the energy scaled by that cell's mass; a stopped one
    /// clears its status item.
    ///
    /// The tweak splits the walk into index ranges run on worker threads (WorkerLoop), the main
    /// thread taking one too. A worker only reads (the headers, the payloads, Operational's
    /// IsActive, the building's def and kept extents, Grid.Mass) and, for an entry whose whole
    /// run is that, adds to the entry's own running averages as the game does and keeps its
    /// ModifyEnergy messages (cell, kJ, maximum temperature) in its range's buffer instead of
    /// sending them. Every entry that needs more is left to the main thread untouched: dirty,
    /// its status item to set or to clear, a footprint not yet computed (Building.GetExtents
    /// would compute it), or one whose reads threw. Then, on the main thread and in index order,
    /// each left entry runs the game's loop body (transcribed) and each worker entry's messages
    /// go to SimMessages.ModifyEnergy, so the sim receives the same messages with the same
    /// arguments in the same order as from the game's loop, and every heat result is the same.
    ///
    /// Why the order can change for the rest: what a left entry's run can write (its own
    /// payload, header and status item, its heat effect, sim messages) is not read by another
    /// entry's worker part (each entry has its own payload and running averages; status item
    /// listeners are the details screen's; Grid.Mass is the sim's output, which a message changes
    /// only at the next sim step). A worker entry whose averages list did not exist yet gets the
    /// new list written back on the main thread; otherwise its payload is unchanged and is not
    /// written back (the game writes the same value back, which moves only the List's internal
    /// version counter). An exception from an entry's own code (the main thread's run of a left
    /// entry) comes out at that entry as from the game's loop; the worker parts of later entries
    /// have then already run, so the tweak turns off for the session.
    ///
    /// With ONI_TWEAK_VERIFY=1 every 16th call predicts its messages on the workers without
    /// changing anything, lets the game's method run with ModifyEnergy recorded, and checks
    /// that the prediction is the game's messages in order (the left entries' messages
    /// interleaved); the counts are logged every 16 checks.
    ///
    /// Off for the session once another Harmony owner patches a method it replaces, transcribes
    /// or calls from a worker (checked at the first call, then every 64). Runs the game's method
    /// below 1,024 buildings or on one core.
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void StructureTemperatureComponents::Sim200ms(System.Single)", "ad95fc91c5850d4b win-764025")]
    [GameMember(GameUse.Postfix, "System.Void StructureTemperatureComponents::Sim200ms(System.Single)", Conditional = true)]
    [GameMember(GameUse.Prefix, "System.Void SimMessages::ModifyEnergy(System.Int32,System.Single,System.Single,SimMessages/EnergySourceID)", Conditional = true)]
    [GameMember(GameUse.Mirrors, "System.Void StructureTemperatureComponents::ExhaustHeat(Extents,System.Single,System.Single,System.Single)", "efd6b84d0cd9c76f win-764025")]
    [GameMember(GameUse.Mirrors, "Extents StructureTemperaturePayload::GetExtents()", "ec31501e552b2f84 win-764025")]
    [GameMember(GameUse.Mirrors, "Extents Building::GetExtents()", "b1b1c9717f1342d8 win-764025")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<StructureTemperaturePayload/EnergySource> StructureTemperatureComponents::AccumulateProducedEnergyKW(System.Collections.Generic.List`1<StructureTemperaturePayload/EnergySource>,System.Single,System.String)")]
    [GameMember(GameUse.Uses, "System.Void StructureTemperatureComponents::UpdateSimState(StructureTemperaturePayload&)")]
    [GameMember(GameUse.Uses, "System.Single StructureTemperaturePayload::get_OperatingKilowatts()")]
    [GameMember(GameUse.Uses, "System.Single StructureTemperaturePayload::get_ExhaustKilowatts()")]
    public static class FastBuildingHeat
    {
        public const string Name = "fast-building-heat";

        /// Below this many entries, or with no helper thread, the game's method runs.
        const int MinEntries = 1024;
        /// Entries a range takes at least.
        const int RangeEntries = 512;

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static MethodBase target;
        static MethodBase[] guarded;
        static bool off;

        /// Test infrastructure (PerfProbe's ab.txt, ONI_FRAME_STATS_AB): true runs the game's method, for an A/B in one run.
        public static bool Bypass;
        /// Counts since the start: calls, calls done in ranges, entries walked by workers, entries
        /// left to the main thread, messages sent from the buffers; verify mode's checks and
        /// mismatches.
        public static long Calls, Split, Entries, LeftEntries, Messages, Wrong;
        static long verified;

        /// The work counters the performance recorder snapshots at a recording's start and end.
        public static void Counters(Action<string, long> counter)
        {
            counter("calls", Calls);
            counter("split", Split);
            counter("ran-game", Calls - Split);
            counter("entries", Entries);
            counter("left-entries", LeftEntries);
            counter("messages", Messages);
            counter("verified", verified);
            counter("wrong", Wrong);
            counter("stood-aside", off ? 1 : 0);
        }

        struct Message
        {
            public int Entry, Cell;
            public float Kilojoules, MaxTemperature;
        }

        /// A range's results: its messages and the entries left to the main thread, both by
        /// entry; the entries whose averages list was made here, with it.
        sealed class Range
        {
            public int Start, End;
            public readonly List<Message> Messages = new List<Message>();
            public readonly List<int> Left = new List<int>();
            public readonly List<(int Entry, List<StructureTemperaturePayload.EnergySource> Sources)> NewLists = new List<(int, List<StructureTemperaturePayload.EnergySource>)>();

            public void Clear()
            {
                Messages.Clear();
                Left.Clear();
                NewLists.Clear();
            }
        }

        static Range[] ranges = new Range[0];
        static int rangeCount;
        static StructureTemperatureComponents current;
        static float currentDt;
        static bool dry;
        static string operating, exhausting;
        static readonly Action<int> body = RunRange;

        /// Verify mode: the game's messages of the call being checked.
        static List<Message> recorded;

        public static void Install(Harmony harmony)
        {
            guarded = Guarded();
            target = guarded[0];
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(FastBuildingHeat), nameof(Sim200ms)) { priority = Priority.Last },
                postfix: verify ? new HarmonyMethod(typeof(FastBuildingHeat), nameof(VerifyAfter)) : null);
            if (verify)
                harmony.Patch(AccessTools.Method(typeof(SimMessages), nameof(SimMessages.ModifyEnergy), new[] { typeof(int), typeof(float), typeof(float), typeof(SimMessages.EnergySourceID) }),
                    prefix: new HarmonyMethod(typeof(FastBuildingHeat), nameof(Record)));
        }

        /// The methods another owner's patch makes this tweak stand down (ModOverlaps): the one it
        /// replaces, those it transcribes, and those a worker calls.
        internal static MethodBase[] Guarded() =>
        [
            AccessTools.Method(typeof(StructureTemperatureComponents), nameof(StructureTemperatureComponents.Sim200ms), new[] { typeof(float) }),
            AccessTools.Method(typeof(StructureTemperatureComponents), nameof(StructureTemperatureComponents.ExhaustHeat), new[] { typeof(Extents), typeof(float), typeof(float), typeof(float) }),
            AccessTools.Method(typeof(StructureTemperatureComponents), nameof(StructureTemperatureComponents.AccumulateProducedEnergyKW),
                new[] { typeof(List<StructureTemperaturePayload.EnergySource>), typeof(float), typeof(string) }),
            AccessTools.Method(typeof(StructureTemperatureComponents), nameof(StructureTemperatureComponents.UpdateSimState), new[] { typeof(StructureTemperaturePayload).MakeByRefType() }),
            AccessTools.Method(typeof(StructureTemperaturePayload), nameof(StructureTemperaturePayload.GetExtents), Type.EmptyTypes),
            AccessTools.PropertyGetter(typeof(StructureTemperaturePayload), nameof(StructureTemperaturePayload.OperatingKilowatts)),
            AccessTools.PropertyGetter(typeof(StructureTemperaturePayload), nameof(StructureTemperaturePayload.ExhaustKilowatts)),
            AccessTools.Method(typeof(Building), nameof(Building.GetExtents), Type.EmptyTypes),
            AccessTools.Method(typeof(SimMessages), nameof(SimMessages.ModifyEnergy), new[] { typeof(int), typeof(float), typeof(float), typeof(SimMessages.EnergySourceID) }),
        ];

        static bool Foreign()
        {
            if (ForeignPatches.Find(guarded) is not { } why) return false;
            global::Debug.Log($"{Name}: off ({why})");
            return true;
        }

        /// StructureTemperatureComponents.Sim200ms, the walk split into ranges.
        static bool Sim200ms(StructureTemperatureComponents __instance, float dt, bool __runOriginal, out bool __state)
        {
            __state = false;
            recorded = null;
            if (!__runOriginal || off || Bypass) return __runOriginal;
            if ((Calls++ & 0x3F) == 0 && (off = Foreign())) return true;
            __instance.GetDataLists(out var headers, out var payloads);
            int n = headers.Count;
            int count = Math.Min(WorkerLoop.MaxHelpers + 1, n / RangeEntries);
            if (n < MinEntries || count < 2 || payloads.Count != n) return true;
            Fit(count, n);
            operating = BUILDING.STATUSITEMS.OPERATINGENERGY.OPERATING;
            exhausting = BUILDING.STATUSITEMS.OPERATINGENERGY.EXHAUSTING;
            current = __instance;
            currentDt = dt;
            if (verify && (Calls & 0xF) == 1)
            {
                // Predict without changing anything, then let the game's method run, recorded.
                dry = true;
                try
                {
                    WorkerLoop.For(rangeCount, body);
                }
                finally
                {
                    dry = false;
                    current = null;
                }
                recorded = new List<Message>();
                __state = true;
                return true;
            }
            try
            {
                WorkerLoop.For(rangeCount, body);
            }
            catch (Exception e)
            {
                // A range's own bookkeeping failed (an entry's reads are caught per entry):
                // some entries' averages may have moved, so nothing is sent and the tweak is off.
                off = true;
                global::Debug.LogWarning($"[Moono] {Name}: off (a range failed: {e})");
                throw;
            }
            finally
            {
                current = null;
            }
            Split++;
            Commit(__instance, headers, payloads, dt);
            return false;
        }

        static void Fit(int count, int n)
        {
            if (ranges.Length < count)
            {
                var grown = new Range[count];
                Array.Copy(ranges, grown, ranges.Length);
                for (int i = ranges.Length; i < count; i++) grown[i] = new Range();
                ranges = grown;
            }
            rangeCount = count;
            for (int r = 0; r < count; r++)
            {
                var range = ranges[r];
                range.Clear();
                range.Start = (int)((long)n * r / count);
                range.End = (int)((long)n * (r + 1) / count);
            }
        }

        static void RunRange(int r)
        {
            var range = ranges[r];
            var m = current;
            m.GetDataLists(out var headers, out var payloads);
            for (int i = range.Start; i < range.End; i++)
            {
                int mark = range.Messages.Count, made = range.NewLists.Count;
                try
                {
                    Entry(m, headers, payloads, i, range);
                }
                catch (Exception)
                {
                    // The game's run of this entry, in order, throws as the game's loop would;
                    // the messages it buffered are dropped so they are not sent twice. Reads come
                    // first, so only a corrupt averages list (the game throwing every 200 ms) can
                    // throw after a sample was added.
                    range.Messages.RemoveRange(mark, range.Messages.Count - mark);
                    range.NewLists.RemoveRange(made, range.NewLists.Count - made);
                    range.Left.Add(i);
                }
            }
        }

        /// The worker part of entry `i`: its whole run when that only reads and adds to its own
        /// averages, with its messages buffered; otherwise the entry is left to the main thread.
        /// Everything that can throw is read before anything is written.
        [MirrorsGame("System.Void StructureTemperatureComponents::ExhaustHeat(Extents,System.Single,System.Single,System.Single)")]
        [MirrorsGame("Extents StructureTemperaturePayload::GetExtents()")]
        [MirrorsGame("Extents Building::GetExtents()")]
        static void Entry(StructureTemperatureComponents m, List<StructureTemperatureHeader> headers, List<StructureTemperaturePayload> payloads, int i, Range range)
        {
            var header = headers[i];
            if (!Sim.IsValidHandle(header.simHandle)) return;
            if (header.dirty)
            {
                range.Left.Add(i);
                return;
            }
            if (!header.isActiveBuilding) return;
            var payload = payloads[i];
            if (!(payload.operational == null || payload.operational.IsActive))
            {
                // The game writes the payload back unchanged.
                if (payload.isActiveStatusItemSet) range.Left.Add(i);
                return;
            }
            if (!payload.isActiveStatusItemSet)
            {
                range.Left.Add(i);
                return;
            }
            float operatingKw = payload.OperatingKilowatts;
            float exhaustKw = payload.ExhaustKilowatts;
            Extents extents = default;
            if (exhaustKw != 0f)
            {
                // StructureTemperaturePayload.GetExtents, without Building.GetExtents' RefreshCells.
                if (payload.overrideExtents) extents = payload.overriddenExtents;
                else
                {
                    extents = payload.building.extents;
                    if (extents.width == 0 || extents.height == 0)
                    {
                        range.Left.Add(i);
                        return;
                    }
                }
            }
            if (exhaustKw != 0f)
            {
                // ExhaustHeat(extents, exhaustKw, payload.maxTemperature, dt): the same
                // arithmetic, the messages kept.
                float maxTemperature = payload.maxTemperature;
                int num = extents.width * extents.height;
                float num2 = exhaustKw * currentDt / (float)num;
                for (int y = 0; y < extents.height; y++)
                {
                    int num3 = extents.y + y;
                    for (int x = 0; x < extents.width; x++)
                    {
                        int num4 = extents.x + x;
                        int num5 = num3 * Grid.WidthInCells + num4;
                        float num6 = Mathf.Min(Grid.Mass[num5], 1.5f) / 1.5f;
                        float kilojoules = num2 * num6;
                        range.Messages.Add(new Message { Entry = i, Cell = num5, Kilojoules = kilojoules, MaxTemperature = maxTemperature });
                    }
                }
            }
            if (dry) return;
            // Nothing above wrote anything; from here on nothing throws for a live list.
            var before = payload.energySourcesKW;
            var sources = m.AccumulateProducedEnergyKW(before, operatingKw, operating);
            if (exhaustKw != 0f) sources = m.AccumulateProducedEnergyKW(sources, exhaustKw, exhausting);
            if (!ReferenceEquals(sources, before)) range.NewLists.Add((i, sources));
        }

        /// On the main thread, in index order: each left entry's game run, each worker entry's
        /// messages, and the new averages lists written back.
        static void Commit(StructureTemperatureComponents m, List<StructureTemperatureHeader> headers, List<StructureTemperaturePayload> payloads, float dt)
        {
            for (int r = 0; r < rangeCount; r++)
            {
                var range = ranges[r];
                Entries += range.End - range.Start;
                foreach (var (entry, sources) in range.NewLists)
                {
                    var payload = payloads[entry];
                    payload.energySourcesKW = sources;
                    payloads[entry] = payload;
                }
                var messages = range.Messages;
                var left = range.Left;
                int a = 0, b = 0;
                while (a < messages.Count || b < left.Count)
                {
                    int nextMessage = a < messages.Count ? messages[a].Entry : int.MaxValue;
                    int nextLeft = b < left.Count ? left[b] : int.MaxValue;
                    if (nextLeft < nextMessage)
                    {
                        LeftEntries++;
                        try
                        {
                            GameEntry(m, headers, payloads, nextLeft, dt);
                        }
                        catch (Exception)
                        {
                            off = true;
                            global::Debug.LogWarning($"[Moono] {Name}: off (building {nextLeft}'s heat update failed as in the game; the later buildings' worker parts had run)");
                            throw;
                        }
                        b++;
                    }
                    else
                    {
                        var message = messages[a];
                        SimMessages.ModifyEnergy(message.Cell, message.Kilojoules, message.MaxTemperature, SimMessages.EnergySourceID.StructureTemperature);
                        Messages++;
                        a++;
                    }
                }
                range.Clear();
            }
        }

        /// One entry of the game's loop, transcribed (part of Sim200ms' fingerprint).
        static void GameEntry(StructureTemperatureComponents m, List<StructureTemperatureHeader> list, List<StructureTemperaturePayload> list2, int i, float dt)
        {
            StructureTemperaturePayload payload = default;
            StructureTemperatureHeader value = list[i];
            if (!Sim.IsValidHandle(value.simHandle)) return;
            bool flag = value.dirty || value.isActiveBuilding;
            if (flag) payload = list2[i];
            if (value.dirty)
            {
                StructureTemperatureComponents.UpdateSimState(ref payload);
                if (payload.pendingEnergyModifications != 0f)
                {
                    SimMessages.ModifyBuildingEnergy(payload.simHandleCopy, payload.pendingEnergyModifications, 0f, 10000f);
                    payload.pendingEnergyModifications = 0f;
                }
                value.dirty = false;
                list[i] = value;
            }
            if (value.isActiveBuilding)
            {
                if (payload.operational == null || payload.operational.IsActive)
                {
                    if (!payload.isActiveStatusItemSet)
                    {
                        payload.primaryElement.GetComponent<KSelectable>().SetStatusItem(Db.Get().StatusItemCategories.OperatingEnergy, m.operatingEnergyStatusItem, payload.simHandleCopy);
                        payload.isActiveStatusItemSet = true;
                    }
                    payload.energySourcesKW = m.AccumulateProducedEnergyKW(payload.energySourcesKW, payload.OperatingKilowatts, BUILDING.STATUSITEMS.OPERATINGENERGY.OPERATING);
                    if (payload.ExhaustKilowatts != 0f)
                    {
                        StructureTemperatureComponents.ExhaustHeat(payload.GetExtents(), payload.ExhaustKilowatts, payload.maxTemperature, dt);
                        payload.energySourcesKW = m.AccumulateProducedEnergyKW(payload.energySourcesKW, payload.ExhaustKilowatts, BUILDING.STATUSITEMS.OPERATINGENERGY.EXHAUSTING);
                    }
                }
                else if (payload.isActiveStatusItemSet)
                {
                    payload.primaryElement.GetComponent<KSelectable>().SetStatusItem(Db.Get().StatusItemCategories.OperatingEnergy, null);
                    payload.isActiveStatusItemSet = false;
                }
            }
            if (flag) list2[i] = payload;
        }

        /// Verify mode: the game's building heat messages while a checked call runs.
        static void Record(int gameCell, float kilojoules, float max_temperature, SimMessages.EnergySourceID id)
        {
            if (recorded != null && id == SimMessages.EnergySourceID.StructureTemperature)
                recorded.Add(new Message { Cell = gameCell, Kilojoules = kilojoules, MaxTemperature = max_temperature });
        }

        /// Verify mode: the predicted messages are the game's, in order, with the left entries'
        /// messages between them.
        static void VerifyAfter(bool __state)
        {
            if (!__state) return;
            var game = recorded;
            recorded = null;
            if (game == null) return;
            verified++;
            int j = 0, matched = 0, predicted = 0;
            string first = null;
            for (int r = 0; r < rangeCount && first == null; r++)
            {
                foreach (var p in ranges[r].Messages)
                {
                    predicted++;
                    while (j < game.Count && !(game[j].Cell == p.Cell && Bits(game[j].Kilojoules) == Bits(p.Kilojoules) && Bits(game[j].MaxTemperature) == Bits(p.MaxTemperature))) j++;
                    if (j >= game.Count)
                    {
                        first = $"building {p.Entry}'s message to cell {p.Cell} ({p.Kilojoules:R} kJ) not among the game's in order";
                        break;
                    }
                    matched++;
                    j++;
                }
            }
            for (int r = 0; r < rangeCount; r++) ranges[r].Clear();
            if (first != null) global::Debug.Log($"{Name}: a prediction is wrong ({++Wrong}): {first}; the game sent {game.Count}");
            if ((verified & 0xF) == 1) global::Debug.Log($"{Name}: {verified} calls checked, last {matched} of {predicted} messages in the game's order, {Wrong} wrong");
        }

        static int Bits(float v) => BitConverter.SingleToInt32Bits(v);
    }
}
