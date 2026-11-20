using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Klei.AI;
using OniMoono.Tweaks;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-suit-checks`, two parts in the Gas Liquid Exposure Monitor's update (every
    /// Duplicant, 30 times a game second).
    ///
    /// Suits. SuitEquipper.IsWearingAirtightSuit (the monitor asks it for every Duplicant with a
    /// suit tank; oxygen breathing, peeing, vomiting and others ask too) looks up the
    /// Duplicant's MinionIdentity, its assignables proxy and that proxy's Equipment, walks the
    /// equipment slots and, for each equippable, looks up its KPrefabID for the AirtightSuit tag.
    /// The tweak keeps, per Duplicant, those components and a copy of the slots' contents taken
    /// after a run of the game's method. Each later call reads again what can change: the proxy
    /// (with the game's own Ref.Get call), the slot list, its length, each slot and each slot's
    /// assignable (by reference); if all are the kept ones it runs the game's per-slot test (alive,
    /// the tag on the kept KPrefabID, isEquipped) in the game's order and returns its answer;
    /// otherwise the game's method runs and a new copy is kept. A component is the same object
    /// while its object lives (kept ones are tested alive), so the answer is the game's.
    ///
    /// Irritation. GasLiquidExposureMonitor.ApplyEffects, below the minor irritation threshold
    /// (exposure under 8), removes both irritation effects and sets isIrritated to false. When
    /// neither effect is in either of the Effects' lists and isIrritated is already false,
    /// Effects.Remove finds nothing (no write, event or scheduler call) and the parameter's Set
    /// compares first and does nothing: the call changes nothing and is skipped. Otherwise,
    /// and for any exposure from 8 up or NaN, the game's method runs. Nothing is remembered.
    ///
    /// With ONI_TWEAK_VERIFY=1 every answer of either part is checked: the game's method runs
    /// and its result (suits) or the effects and the parameter (irritation) are compared; the
    /// counts are logged every 65,536 calls of each part.
    ///
    /// Off for the session once another Harmony owner patches a method a part replaces or
    /// predicts (checked at the first call, then every 4,096); the parts stand down together.
    [GameMember(GameUse.Prefix | GameUse.Replaces, "Equippable SuitEquipper::IsWearingAirtightSuit()", "2c0a4038fd164eb9 win-764025")]
    [GameMember(GameUse.Postfix, "Equippable SuitEquipper::IsWearingAirtightSuit()")]
    [GameMember(GameUse.Mirrors, "Equipment MinionIdentity::GetEquipment()", "c7de14ea313227df win-764025")]
    [GameMember(GameUse.Mirrors, "System.Collections.Generic.List`1<AssignableSlotInstance> Assignables::get_Slots()", "d827cc8455bf088b win-764025")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void GasLiquidExposureMonitor::ApplyEffects(GasLiquidExposureMonitor/Instance)", "f0b6e6d93a6364ad win-764025")]
    [GameMember(GameUse.Postfix, "System.Void GasLiquidExposureMonitor::ApplyEffects(GasLiquidExposureMonitor/Instance)", Conditional = true)]
    [GameMember(GameUse.Mirrors, "System.Void Klei.AI.Effects::Remove(Klei.AI.Effect)", "2fc77141c06ad857 win-764025")]
    [GameMember(GameUse.Mirrors, "System.Void Klei.AI.Effects::Remove(HashedString)", "f32b6b3cef8a5247 win-764025")]
    public static class FastSuitChecks
    {
        public const string Name = "fast-suit-checks";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static MethodBase[] guarded;
        static bool off;

        /// Test infrastructure (PerfProbe's ab.txt, ONI_FRAME_STATS_AB): true runs the game's
        /// methods of both parts; BypassSuits and BypassIrritation of one part only.
        public static bool Bypass, BypassSuits, BypassIrritation;
        /// Counts since the start, per part: calls, answered here, copies taken; skips; verify
        /// mode's mismatches of both.
        public static long SuitCalls, SuitAnswers, SuitCopies, IrritationCalls, IrritationSkips, Wrong;
        static long verifiedSuits, verifiedIrritation;

        public static void Counters(Action<string, long> counter)
        {
            counter("suit-calls", SuitCalls);
            counter("suit-answered", SuitAnswers);
            counter("suit-ran-game", SuitCalls - SuitAnswers);
            counter("suit-copies", SuitCopies);
            counter("irritation-calls", IrritationCalls);
            counter("irritation-skipped", IrritationSkips);
            counter("irritation-ran-game", IrritationCalls - IrritationSkips);
            counter("verified", verifiedSuits + verifiedIrritation);
            counter("wrong", Wrong);
            counter("stood-aside", off ? 1 : 0);
        }

        /// A Duplicant's kept components and slots, as the last game run read them.
        sealed class Copy
        {
            public MinionIdentity Identity;
            public MinionAssignablesProxy Proxy;
            public Equipment Equipment;
            public List<AssignableSlotInstance> Slots;
            public int Count;
            public AssignableSlotInstance[] Slot;
            public Assignable[] Assignable;
            public Equippable[] Equippable;
            public KPrefabID[] Prefab;
        }

        static readonly ConditionalWeakTable<SuitEquipper, Copy> copies = new ConditionalWeakTable<SuitEquipper, Copy>();
        static long suitTurn, irritationTurn;

        public static void Install(Harmony harmony)
        {
            guarded = Guarded();
            harmony.Patch(guarded[0], prefix: new HarmonyMethod(typeof(FastSuitChecks), nameof(IsWearing)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(FastSuitChecks), nameof(AfterIsWearing)) { priority = Priority.First });
            harmony.Patch(guarded[1], prefix: new HarmonyMethod(typeof(FastSuitChecks), nameof(ApplyEffects)) { priority = Priority.Last },
                postfix: verify ? new HarmonyMethod(typeof(FastSuitChecks), nameof(AfterApplyEffects)) : null);
        }

        internal static MethodBase[] Guarded() =>
        [
            AccessTools.Method(typeof(SuitEquipper), nameof(SuitEquipper.IsWearingAirtightSuit), Type.EmptyTypes),
            AccessTools.Method(typeof(GasLiquidExposureMonitor), nameof(GasLiquidExposureMonitor.ApplyEffects), new[] { typeof(GasLiquidExposureMonitor.Instance) }),
            AccessTools.Method(typeof(MinionIdentity), nameof(MinionIdentity.GetEquipment), Type.EmptyTypes),
            AccessTools.PropertyGetter(typeof(Assignables), nameof(Assignables.Slots)),
            AccessTools.Method(typeof(Effects), nameof(Effects.Remove), new[] { typeof(Effect) }),
            AccessTools.Method(typeof(Effects), nameof(Effects.Remove), new[] { typeof(HashedString) }),
        ];

        static bool Foreign()
        {
            if (ForeignPatches.Find(guarded) is not { } why) return false;
            global::Debug.Log($"{Name}: off ({why})");
            return true;
        }

        // ---- Suits

        /// 0: the game's method runs; 1: it runs and a copy is taken after; 2: it runs to check `Answer`.
        public struct SuitCall
        {
            public int Mode;
            public Equippable Answer;
        }

        /// SuitEquipper.IsWearingAirtightSuit, answered from the kept copy when it still holds.
        static bool IsWearing(SuitEquipper __instance, ref Equippable __result, bool __runOriginal, out SuitCall __state)
        {
            __state = default;
            if (!__runOriginal || off || Bypass || BypassSuits) return __runOriginal;
            if ((suitTurn++ & 0xFFF) == 0)
            {
                if (off = Foreign()) return true;
                if (verify && suitTurn > 1) global::Debug.Log($"{Name}: suits {SuitCalls} calls, {verifiedSuits} checked, {Wrong} wrong");
            }
            SuitCalls++;
            // A destroyed Duplicant: the game's method decides (its GetComponent).
            if (__instance == null) return true;
            if (copies.TryGetValue(__instance, out var copy) && Answer(copy, out var answer))
            {
                if (verify)
                {
                    __state = new SuitCall { Mode = 2, Answer = answer };
                    return true;
                }
                SuitAnswers++;
                __result = answer;
                return false;
            }
            __state = new SuitCall { Mode = 1 };
            return true;
        }

        /// The kept copy's answer when everything it was taken from is still there: the game's
        /// proxy read, the slots, and each slot's assignable; then the game's per-slot test.
        [MirrorsGame("Equipment MinionIdentity::GetEquipment()")]
        [MirrorsGame("System.Collections.Generic.List`1<AssignableSlotInstance> Assignables::get_Slots()")]
        static bool Answer(Copy copy, out Equippable answer)
        {
            answer = null;
            if (copy.Identity == null || copy.Equipment == null) return false;
            // GetEquipment's own read, with what it does when the proxy is gone.
            var proxy = copy.Identity.assignableProxy.Get();
            if (!ReferenceEquals(proxy, copy.Proxy) || proxy == null) return false;
            var slots = copy.Equipment.slots;
            if (!ReferenceEquals(slots, copy.Slots) || slots.Count != copy.Count) return false;
            for (int i = 0; i < copy.Count; i++)
            {
                var slot = slots[i];
                if (!ReferenceEquals(slot, copy.Slot[i]) || !ReferenceEquals(slot.assignable, copy.Assignable[i])) return false;
            }
            for (int i = 0; i < copy.Count; i++)
            {
                var equippable = copy.Equippable[i];
                if ((bool)equippable && copy.Prefab[i].HasTag(GameTags.AirtightSuit) && equippable.isEquipped)
                {
                    answer = equippable;
                    break;
                }
            }
            return true;
        }

        /// After the game's run: a new copy (mode 1), or the check of the copy's answer (mode 2).
        static void AfterIsWearing(SuitEquipper __instance, Equippable __result, SuitCall __state)
        {
            if (__state.Mode == 2)
            {
                verifiedSuits++;
                if (!ReferenceEquals(__result, __state.Answer))
                    global::Debug.Log($"{Name}: a suit answer is wrong ({++Wrong}): kept {(__state.Answer == null ? "none" : __state.Answer.name)}, the game's {(__result == null ? "none" : __result.name)}");
                return;
            }
            if (__state.Mode == 1) Take(__instance);
        }

        /// A copy of what the game's method read, unless something in it would make the game's
        /// method throw or answer otherwise than the copy's test (then none is kept).
        static void Take(SuitEquipper equipper)
        {
            copies.Remove(equipper);
            var identity = equipper.GetComponent<MinionIdentity>();
            if (identity == null) return;
            var proxy = identity.assignableProxy.Get();
            if (proxy == null) return;
            var equipment = proxy.GetComponent<Equipment>();
            if (equipment == null) return;
            var slots = equipment.slots;
            int n = slots.Count;
            var copy = new Copy
            {
                Identity = identity, Proxy = proxy, Equipment = equipment, Slots = slots, Count = n,
                Slot = new AssignableSlotInstance[n], Assignable = new Assignable[n], Equippable = new Equippable[n], Prefab = new KPrefabID[n],
            };
            for (int i = 0; i < n; i++)
            {
                var slot = slots[i];
                // The game's loop casts each slot to EquipmentSlotInstance.
                if (slot is not EquipmentSlotInstance) return;
                copy.Slot[i] = slot;
                var assignable = slot.assignable;
                copy.Assignable[i] = assignable;
                var equippable = assignable as Equippable;
                copy.Equippable[i] = equippable;
                if ((object)equippable != null && (bool)equippable)
                {
                    copy.Prefab[i] = equippable.GetComponent<KPrefabID>();
                    // The game's test would throw on it: never kept.
                    if (copy.Prefab[i] == null) return;
                }
            }
            copies.Add(equipper, copy);
            SuitCopies++;
        }

        // ---- Irritation

        public struct IrritationCall
        {
            public bool On;
            public int Effects, Expiring;
        }

        /// GasLiquidExposureMonitor.ApplyEffects, skipped when it would change nothing.
        [MirrorsGame("System.Void Klei.AI.Effects::Remove(Klei.AI.Effect)")]
        [MirrorsGame("System.Void Klei.AI.Effects::Remove(HashedString)")]
        static bool ApplyEffects(GasLiquidExposureMonitor __instance, GasLiquidExposureMonitor.Instance smi, bool __runOriginal, out IrritationCall __state)
        {
            __state = default;
            if (!__runOriginal || off || Bypass || BypassIrritation) return __runOriginal;
            if ((irritationTurn++ & 0xFFF) == 0)
            {
                if (off = Foreign()) return true;
                if (verify && irritationTurn > 1) global::Debug.Log($"{Name}: irritation {IrritationCalls} calls, {verifiedIrritation} checked, {Wrong} wrong");
            }
            IrritationCalls++;
            // The game's else branch only (neither irritation; NaN goes to the game).
            if (!(smi.exposure < 8f)) return true;
            var minor = GasLiquidExposureMonitor.minorIrritationEffect;
            var major = GasLiquidExposureMonitor.majorIrritationEffect;
            var effects = smi.effects;
            if (minor == null || major == null || effects == null) return true;
            HashedString a = minor.IdHash, b = major.IdHash;
            if (HasEither(effects.effectsThatExpire, a, b) || HasEither(effects.effects, a, b)) return true;
            if (__instance.isIrritated.Get(smi)) return true;
            if (verify)
            {
                __state = new IrritationCall { On = true, Effects = effects.effects.Count, Expiring = effects.effectsThatExpire.Count };
                return true;
            }
            IrritationSkips++;
            return false;
        }

        static bool HasEither(List<EffectInstance> list, HashedString a, HashedString b)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var id = list[i].effect.IdHash;
                if (id == a || id == b) return true;
            }
            return false;
        }

        /// Verify mode: the game's run of a call the tweak would skip changed nothing.
        static void AfterApplyEffects(GasLiquidExposureMonitor __instance, GasLiquidExposureMonitor.Instance smi, IrritationCall __state)
        {
            if (!__state.On) return;
            verifiedIrritation++;
            var effects = smi.effects;
            if (effects.effects.Count != __state.Effects || effects.effectsThatExpire.Count != __state.Expiring || __instance.isIrritated.Get(smi))
                global::Debug.Log($"{Name}: an irritation skip is wrong ({++Wrong}): exposure {smi.exposure}, effects {__state.Effects} → {effects.effects.Count}, expiring {__state.Expiring} → {effects.effectsThatExpire.Count}");
        }
    }
}
