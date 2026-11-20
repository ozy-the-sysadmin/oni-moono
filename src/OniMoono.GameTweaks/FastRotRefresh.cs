using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Klei.AI;
using OniMoono.Tweaks;
using UnityEngine;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-rot-refresh`. Every food item calls Rottable.Instance.RefreshModifiers
    /// once a game second (Rot, from its SIM_1000ms bucket). The method clears the rot delta's
    /// modifiers and adds them again (the refrigeration one and the atmosphere one, then the
    /// spice's), adds and removes the DeepFrozen and Refrigerated tags, and sets the two
    /// preservation status items, computing the refrigeration level and the atmosphere twice
    /// (once for the modifiers, once in SetStatusItems). For almost every item nothing it writes
    /// changes from one second to the next.
    ///
    /// The tweak computes the level and the atmosphere once, with the game's reads in the game's
    /// order, and checks that every output is already what the game's run would leave: the
    /// modifier list holds exactly the expected modifiers in order (by reference), the tags the
    /// game adds are there and those it removes are absent (from the tags and the serialized
    /// tags, as RemoveTag clears both), the first PreservationTemperature entry is the expected
    /// status item, and the first PreservationAtmosphere entry is the expected one (none for a
    /// normal atmosphere), and the delta has no OnDirty listener. Then the game's run would be
    /// ClearModifiers and the same Adds (no listener to call), AddTag and RemoveTag calls that
    /// change nothing and send no event, and SetStatusItem calls that return the existing
    /// entries: the same state, so the method is skipped. Otherwise, and for a null master, an
    /// invalid cell (items in space) or a missing component, the game's method runs. The outputs
    /// are checked, not remembered, so no writer of them needs a hook: whatever else changed a
    /// modifier, a tag or a status entry makes the check fail and the game's run repairs it as it
    /// would. The storage event Rot sends after the call is not touched.
    ///
    /// With ONI_TWEAK_VERIFY=1 every call the tweak would skip runs the game's method instead,
    /// and its outputs are compared with what they were before it (and the game's level and
    /// atmosphere with the tweak's); the counts are logged every 65,536 calls.
    ///
    /// Off for the session once another Harmony owner patches a method it replaces or mirrors
    /// (checked at the first call, then every 4,096).
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void Rottable/Instance::RefreshModifiers(System.Single)", "8e84b5e020cbb96e win-764025")]
    [GameMember(GameUse.Postfix, "System.Void Rottable/Instance::RefreshModifiers(System.Single)", Conditional = true)]
    [GameMember(GameUse.Mirrors, "Rottable/RotRefrigerationLevel Rottable::RefrigerationLevel(IRottable)", "e12bba3f6653bfb1 win-764025")]
    [GameMember(GameUse.Mirrors, "Rottable/RotAtmosphereQuality Rottable::AtmosphereQuality(IRottable)", "dc436bba8472fe1e win-764025")]
    [GameMember(GameUse.Mirrors, "System.Boolean Rottable::IsInActiveFridge(IRottable)", "d4e9f0036cda4dca win-764025")]
    [GameMember(GameUse.Mirrors, "System.Void Rottable::SetStatusItems(IRottable)", "467ecb292daa0d9f win-764025")]
    [GameMember(GameUse.Mirrors, "System.Guid StatusItemGroup::SetStatusItem(StatusItemCategory,StatusItem,System.Object)", "373ea7905badbc88 win-764025")]
    [GameMember(GameUse.Mirrors, "System.Void Klei.AI.AttributeInstance::ClearModifiers()", "f27b5a99b2e96396 win-764025")]
    [GameMember(GameUse.Mirrors, "System.Void Klei.AI.AttributeInstance::Add(Klei.AI.AttributeModifier)", "bed61a4161d05b78 win-764025")]
    [GameMember(GameUse.Mirrors, "System.Void KPrefabID::AddTag(Tag,System.Boolean)", "476207bb0a09fda3 win-764025")]
    [GameMember(GameUse.Mirrors, "System.Void KPrefabID::RemoveTag(Tag)", "9b5a3c9907c237a6 win-764025")]
    public static class FastRotRefresh
    {
        public const string Name = "fast-rot-refresh";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static MethodBase target;
        static MethodBase[] guarded;
        static bool off;

        /// Test infrastructure (PerfProbe's ab.txt): true runs the game's method, for an A/B in one run.
        public static bool Bypass;
        /// Counts since the start: calls, calls skipped, and (verify mode) calls the tweak would
        /// have skipped whose game run changed something.
        public static long Calls, Skipped, Wrong;
        static long verified;

        /// The components an item's check reads, looked up once (each is the same object while it lives).
        sealed class Parts
        {
            public KPrefabID Prefab;
            public KSelectable Selectable;
            public Storage Storage;
            public Refrigerator Fridge;
        }

        static readonly ConditionalWeakTable<Rottable.Instance, Parts> parts = new ConditionalWeakTable<Rottable.Instance, Parts>();

        /// The database's objects the check compares with, for the Db they were read from.
        static Db db;
        static Tag spice;
        static AttributeModifier spiceModifier;
        static StatusItemCategory temperatureCategory, atmosphereCategory;
        static StatusItem frozen, refrigerated, unrefrigerated, contaminated, sterilizing;

        public static void Install(Harmony harmony)
        {
            target = Guarded()[0];
            guarded = Guarded();
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(FastRotRefresh), nameof(Refresh)) { priority = Priority.Last },
                postfix: verify ? new HarmonyMethod(typeof(FastRotRefresh), nameof(Verify)) : null);
        }

        /// The methods another owner's patch makes this tweak stand down (ModOverlaps): the one it
        /// skips, and those whose results it computes or predicts itself.
        internal static MethodBase[] Guarded() =>
        [
            AccessTools.Method(typeof(Rottable.Instance), nameof(Rottable.Instance.RefreshModifiers), new[] { typeof(float) }),
            AccessTools.Method(typeof(Rottable), nameof(Rottable.RefrigerationLevel), new[] { typeof(IRottable) }),
            AccessTools.Method(typeof(Rottable), nameof(Rottable.AtmosphereQuality), new[] { typeof(IRottable) }),
            AccessTools.Method(typeof(Rottable), nameof(Rottable.IsInActiveFridge), new[] { typeof(IRottable) }),
            AccessTools.Method(typeof(Rottable), nameof(Rottable.SetStatusItems), new[] { typeof(IRottable) }),
            AccessTools.Method(typeof(StatusItemGroup), nameof(StatusItemGroup.SetStatusItem), new[] { typeof(StatusItemCategory), typeof(StatusItem), typeof(object) }),
            AccessTools.Method(typeof(AttributeInstance), nameof(AttributeInstance.ClearModifiers), Type.EmptyTypes),
            AccessTools.Method(typeof(AttributeInstance), nameof(AttributeInstance.Add), new[] { typeof(AttributeModifier) }),
            AccessTools.Method(typeof(KPrefabID), nameof(KPrefabID.AddTag), new[] { typeof(Tag), typeof(bool) }),
            AccessTools.Method(typeof(KPrefabID), nameof(KPrefabID.RemoveTag), new[] { typeof(Tag) }),
        ];

        static bool Foreign()
        {
            if (ForeignPatches.Find(guarded) is not { } why) return false;
            global::Debug.Log($"{Name}: off ({why})");
            return true;
        }

        /// What the verify mode compares: the state before the game's run, and the tweak's level
        /// and atmosphere.
        public struct Before
        {
            public bool On;
            public string Outputs;
            public Rottable.RotRefrigerationLevel Level;
            public Rottable.RotAtmosphereQuality Atmosphere;
        }

        /// Rottable.Instance.RefreshModifiers, skipped when its run would change nothing.
        static bool Refresh(Rottable.Instance __instance, bool __runOriginal, out Before __state)
        {
            __state = default;
            if (!__runOriginal || off || Bypass) return __runOriginal;
            if ((Calls++ & 0xFFF) == 0)
            {
                if (off = Foreign()) return true;
                if (verify && Calls > 1 && (Calls & 0xFFFF) == 1) global::Debug.Log($"{Name}: {Calls} calls, {verified} checked, {Wrong} wrong");
            }
            // The game's early return, and items in space (an invalid cell): the game's method.
            if (__instance.GetMaster().isNull) return true;
            int cell = Grid.PosToCell(__instance.gameObject);
            if (!Grid.IsValidCell(cell)) return true;
            var p = PartsOf(__instance);
            if (p.Prefab == null || p.Selectable == null) return true;
            Database();
            var level = Level(__instance, p, cell);
            var atmosphere = Atmosphere(cell);
            if (!Unchanged(__instance, p, level, atmosphere)) return true;
            if (verify)
            {
                __state = new Before { On = true, Outputs = Outputs(__instance, p), Level = level, Atmosphere = atmosphere };
                return true;
            }
            Skipped++;
            return false;
        }

        /// Verify mode: the game's run of a call the tweak would skip left every output as it was.
        static void Verify(Rottable.Instance __instance, Before __state)
        {
            if (!__state.On) return;
            verified++;
            var p = PartsOf(__instance);
            string after = Outputs(__instance, p);
            var level = Rottable.RefrigerationLevel(__instance);
            var atmosphere = Rottable.AtmosphereQuality(__instance);
            if (after != __state.Outputs || level != __state.Level || atmosphere != __state.Atmosphere)
                global::Debug.Log($"{Name}: a skip would differ ({++Wrong}): before [{__state.Outputs}] after [{after}], level {__state.Level}/{level}, atmosphere {__state.Atmosphere}/{atmosphere}");
        }

        static Parts PartsOf(Rottable.Instance smi)
        {
            if (parts.TryGetValue(smi, out var p) && p.Prefab != null && p.Selectable != null) return p;
            parts.Remove(smi);
            var go = smi.gameObject;
            p = new Parts { Prefab = go.GetComponent<KPrefabID>(), Selectable = go.GetComponent<KSelectable>() };
            parts.Add(smi, p);
            return p;
        }

        static void Database()
        {
            var now = Db.Get();
            if (ReferenceEquals(now, db)) return;
            spice = now.Spices.PreservingSpice.Id;
            spiceModifier = now.Spices.PreservingSpice.FoodModifier;
            temperatureCategory = now.StatusItemCategories.PreservationTemperature;
            atmosphereCategory = now.StatusItemCategories.PreservationAtmosphere;
            frozen = now.CreatureStatusItems.RefrigeratedFrozen;
            refrigerated = now.CreatureStatusItems.Refrigerated;
            unrefrigerated = now.CreatureStatusItems.Unrefrigerated;
            contaminated = now.CreatureStatusItems.ContaminatedAtmosphere;
            sterilizing = now.CreatureStatusItems.SterilizingAtmosphere;
            db = now;
        }

        /// Rottable.RefrigerationLevel for an item on a valid cell: the same reads and comparisons.
        [MirrorsGame("Rottable/RotRefrigerationLevel Rottable::RefrigerationLevel(IRottable)")]
        static Rottable.RotRefrigerationLevel Level(Rottable.Instance smi, Parts p, int cell)
        {
            var element = smi.primaryElement;
            float temperature = element.Temperature;
            if (Grid.Element[cell].id != SimHashes.Vacuum) temperature = Mathf.Min(Grid.Temperature[cell], element.Temperature);
            if (temperature < smi.PreserveTemperature) return Rottable.RotRefrigerationLevel.Frozen;
            if (temperature < smi.RotTemperature || InActiveFridge(smi, p)) return Rottable.RotRefrigerationLevel.Refrigerated;
            return Rottable.RotRefrigerationLevel.Normal;
        }

        /// Rottable.IsInActiveFridge, with the storage's Refrigerator looked up once per storage.
        [MirrorsGame("System.Boolean Rottable::IsInActiveFridge(IRottable)")]
        static bool InActiveFridge(Rottable.Instance smi, Parts p)
        {
            var pickupable = smi.pickupable;
            if (pickupable == null) return false;
            var storage = pickupable.storage;
            if (storage == null) return false;
            if (!ReferenceEquals(storage, p.Storage) || (p.Fridge is { } kept && kept == null))
            {
                p.Storage = storage;
                p.Fridge = storage.GetComponent<Refrigerator>();
            }
            return p.Fridge != null && p.Fridge.IsActive();
        }

        /// Rottable.AtmosphereQuality for a valid cell.
        [MirrorsGame("Rottable/RotAtmosphereQuality Rottable::AtmosphereQuality(IRottable)")]
        static Rottable.RotAtmosphereQuality Atmosphere(int cell)
        {
            int above = Grid.CellAbove(cell);
            Rottable.AtmosphereModifier.TryGetValue((int)Grid.Element[cell].id, out var here);
            var overhead = here;
            if (Grid.IsValidCell(above) && !Rottable.AtmosphereModifier.TryGetValue((int)Grid.Element[above].id, out overhead)) overhead = here;
            if (here == overhead) return here;
            if (here == Rottable.RotAtmosphereQuality.Contaminating || overhead == Rottable.RotAtmosphereQuality.Contaminating) return Rottable.RotAtmosphereQuality.Contaminating;
            if (here == Rottable.RotAtmosphereQuality.Normal || overhead == Rottable.RotAtmosphereQuality.Normal) return Rottable.RotAtmosphereQuality.Normal;
            return Rottable.RotAtmosphereQuality.Sterilizing;
        }

        /// Whether the game's RefreshModifiers (its modifiers, its tags, SetStatusItems) would
        /// leave every output as it is now.
        [MirrorsGame("System.Void Rottable::SetStatusItems(IRottable)")]
        [MirrorsGame("System.Guid StatusItemGroup::SetStatusItem(StatusItemCategory,StatusItem,System.Object)")]
        [MirrorsGame("System.Void Klei.AI.AttributeInstance::ClearModifiers()")]
        [MirrorsGame("System.Void Klei.AI.AttributeInstance::Add(Klei.AI.AttributeModifier)")]
        [MirrorsGame("System.Void KPrefabID::AddTag(Tag,System.Boolean)")]
        [MirrorsGame("System.Void KPrefabID::RemoveTag(Tag)")]
        static bool Unchanged(Rottable.Instance smi, Parts p, Rottable.RotRefrigerationLevel level, Rottable.RotAtmosphereQuality atmosphere)
        {
            var delta = smi.rotAmountInstance.deltaAttribute;
            // ClearModifiers and Add call the listener: a run would be seen.
            if (delta.OnDirty != null) return false;
            var prefab = p.Prefab;
            var mods = delta.Modifiers;
            int n = 0;
            if (!prefab.HasAnyTags(Rottable.PRESERVED_TAGS))
            {
                AttributeModifier levelModifier, atmosphereModifier;
                switch (level)
                {
                    case Rottable.RotRefrigerationLevel.Frozen:
                        levelModifier = Rottable.Instance.frozenModifier;
                        if (!prefab.tags.Contains(GameTags.RotModifierTags.DeepFrozen) || !Absent(prefab, GameTags.RotModifierTags.Refrigerated)) return false;
                        break;
                    case Rottable.RotRefrigerationLevel.Refrigerated:
                        levelModifier = Rottable.Instance.refrigeratedModifier;
                        if (!prefab.tags.Contains(GameTags.RotModifierTags.Refrigerated) || !Absent(prefab, GameTags.RotModifierTags.DeepFrozen)) return false;
                        break;
                    default:
                        levelModifier = Rottable.Instance.unrefrigeratedModifier;
                        if (!Absent(prefab, GameTags.RotModifierTags.Refrigerated) || !Absent(prefab, GameTags.RotModifierTags.DeepFrozen)) return false;
                        break;
                }
                atmosphereModifier = atmosphere switch
                {
                    Rottable.RotAtmosphereQuality.Contaminating => Rottable.Instance.contaminatedAtmosphereModifier,
                    Rottable.RotAtmosphereQuality.Sterilizing => Rottable.Instance.sterileAtmosphereModifier,
                    _ => Rottable.Instance.normalAtmosphereModifier,
                };
                if (mods.Count < 2 || !ReferenceEquals(mods[0], levelModifier) || !ReferenceEquals(mods[1], atmosphereModifier)) return false;
                n = 2;
            }
            if (prefab.HasTag(spice))
            {
                if (mods.Count < n + 1 || !ReferenceEquals(mods[n], spiceModifier)) return false;
                n++;
            }
            if (mods.Count != n) return false;
            var group = p.Selectable.statusItemGroup;
            // KSelectable.SetStatusItem does nothing without a group.
            if (group == null) return true;
            var temperatureItem = level switch
            {
                Rottable.RotRefrigerationLevel.Frozen => frozen,
                Rottable.RotRefrigerationLevel.Refrigerated => refrigerated,
                _ => unrefrigerated,
            };
            var atmosphereItem = atmosphere switch
            {
                Rottable.RotAtmosphereQuality.Contaminating => contaminated,
                Rottable.RotAtmosphereQuality.Sterilizing => sterilizing,
                _ => null,
            };
            // SetStatusItem throws for these: the game's run throws as it would.
            if (temperatureItem == null || temperatureItem.allowMultiples || (atmosphereItem != null && atmosphereItem.allowMultiples)) return false;
            return FirstIs(group.items, temperatureCategory, temperatureItem) && FirstIs(group.items, atmosphereCategory, atmosphereItem);
        }

        /// SetStatusItem returns the existing entry, changing nothing, when the first entry of
        /// the category is the item; with no item, when the category has no entry.
        static bool FirstIs(List<StatusItemGroup.Entry> items, StatusItemCategory category, StatusItem item)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].category == category) return item != null && items[i].item == item;
            return item == null;
        }

        /// RemoveTag(tag) changes nothing: the tag is in neither set.
        static bool Absent(KPrefabID prefab, Tag tag) => !prefab.tags.Contains(tag) && !prefab.serializedTags.Contains(tag);

        /// Verify mode: a text image of everything RefreshModifiers writes.
        static string Outputs(Rottable.Instance smi, Parts p)
        {
            var text = new System.Text.StringBuilder();
            var mods = smi.rotAmountInstance.deltaAttribute.Modifiers;
            for (int i = 0; i < mods.Count; i++) text.Append(RuntimeHelpers.GetHashCode(mods[i])).Append(',');
            var prefab = p.Prefab;
            text.Append(" deep-frozen ").Append(prefab.tags.Contains(GameTags.RotModifierTags.DeepFrozen)).Append('/').Append(prefab.serializedTags.Contains(GameTags.RotModifierTags.DeepFrozen));
            text.Append(" refrigerated ").Append(prefab.tags.Contains(GameTags.RotModifierTags.Refrigerated)).Append('/').Append(prefab.serializedTags.Contains(GameTags.RotModifierTags.Refrigerated));
            var group = p.Selectable.statusItemGroup;
            if (group != null)
                foreach (var e in group.items)
                    if (e.category == temperatureCategory || e.category == atmosphereCategory) text.Append(' ').Append(e.item.Id).Append(':').Append(e.id);
            return text.ToString();
        }
    }
}
