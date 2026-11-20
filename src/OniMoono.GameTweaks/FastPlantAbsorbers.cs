using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OniMoono.Tweaks;
using UnityEngine;
using Planner = PlantElementAbsorber.Planner;

namespace OniMoono.GameTweaks
{
    /// Game tweak `fast-plant-absorbers`. Five times a game second every irrigated or fertilized
    /// plant (and every other PlantElementAbsorbers entry) plans what it takes from its storage:
    /// PlantElementAbsorber.PlanConsume builds a Planner, which takes two pooled lists and looks
    /// up the KPrefabID and the PrimaryElement of every stored item, then walks those lists for
    /// each consumed tag until the amount is covered.
    ///
    /// The tweak walks the storage's items directly, looking up an item's two components only
    /// when the walk reaches it, at most once a call, and stops where the game's walk returns: the
    /// same items in the same order (those without both components left out, as the Planner does),
    /// the same tag tests, the same Mathf.Min and subtraction, the same commands in the same list,
    /// the same result. The lookups it leaves out (items after the covering ones) and the pooled
    /// lists change nothing the game reads. The game's preconditions run as its own method. When a
    /// stored item is destroyed (or missing), the game's method runs, so whatever it does with
    /// it (it looks the item's components up) happens as before.
    ///
    /// With ONI_TWEAK_VERIFY=1 the tweak plans into its own list and lets the game's method run,
    /// then compares the result and the commands; the counts are logged every 65,536 calls.
    ///
    /// Off for the session once another Harmony owner patches either PlanConsume or the Planner's
    /// (checked at the first call, then every 4,096).
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Boolean PlantElementAbsorber::PlanConsume(Storage,PlantElementAbsorber/LocalInfo,System.Single,System.Collections.Generic.List`1<PlantElementAbsorber/Planner/ConsumeCommand>)", "d3bab4bed9abbbe8 win-764025")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Boolean PlantElementAbsorber::PlanConsume(Storage,PlantElementAbsorber/ConsumeInfo[],System.Single,System.Collections.Generic.List`1<PlantElementAbsorber/Planner/ConsumeCommand>)", "7be8dc98c851e698 win-764025")]
    [GameMember(GameUse.Postfix, "System.Boolean PlantElementAbsorber::PlanConsume(Storage,PlantElementAbsorber/LocalInfo,System.Single,System.Collections.Generic.List`1<PlantElementAbsorber/Planner/ConsumeCommand>)", Conditional = true)]
    [GameMember(GameUse.Postfix, "System.Boolean PlantElementAbsorber::PlanConsume(Storage,PlantElementAbsorber/ConsumeInfo[],System.Single,System.Collections.Generic.List`1<PlantElementAbsorber/Planner/ConsumeCommand>)", Conditional = true)]
    [GameMember(GameUse.Mirrors, "System.Void PlantElementAbsorber/Planner::.ctor(Storage)", "2d788f1590bdc612 win-764025")]
    [GameMember(GameUse.Mirrors, "System.Boolean PlantElementAbsorber/Planner::PlanConsume(System.Single,Tag,System.Collections.Generic.List`1<PlantElementAbsorber/Planner/ConsumeCommand>)", "9b07ce3aff3e75ca win-764025")]
    public static class FastPlantAbsorbers
    {
        public const string Name = "fast-plant-absorbers";

        static readonly bool verify = Environment.GetEnvironmentVariable("ONI_TWEAK_VERIFY") == "1";
        static MethodBase[] guarded;
        static bool off;

        /// Test infrastructure (PerfProbe's ab.txt, ONI_FRAME_STATS_AB): true runs the game's method.
        public static bool Bypass;
        /// Counts since the start: calls, calls planned here, items looked up, items the game's
        /// Planner would have looked up besides; verify mode's mismatches.
        public static long Calls, Planned, LookedUp, NotLookedUp, Wrong;
        static long verified;

        public static void Counters(Action<string, long> counter)
        {
            counter("calls", Calls);
            counter("planned", Planned);
            counter("ran-game", Calls - Planned);
            counter("looked-up", LookedUp);
            counter("not-looked-up", NotLookedUp);
            counter("verified", verified);
            counter("wrong", Wrong);
            counter("stood-aside", off ? 1 : 0);
        }

        // The current call's items: their components as far as the walk has looked them up.
        static List<GameObject> items;
        static int looked;
        static KPrefabID[] prefabs = new KPrefabID[16];
        static PrimaryElement[] elements = new PrimaryElement[16];
        static bool[] usable = new bool[16];
        static readonly List<Planner.ConsumeCommand> mine = new List<Planner.ConsumeCommand>();

        public struct Prediction
        {
            public bool On, Result;
        }

        public static void Install(Harmony harmony)
        {
            guarded = Guarded();
            harmony.Patch(guarded[0], prefix: new HarmonyMethod(typeof(FastPlantAbsorbers), nameof(PlanLocal)) { priority = Priority.Last },
                postfix: verify ? new HarmonyMethod(typeof(FastPlantAbsorbers), nameof(Verify)) : null);
            harmony.Patch(guarded[1], prefix: new HarmonyMethod(typeof(FastPlantAbsorbers), nameof(PlanMany)) { priority = Priority.Last },
                postfix: verify ? new HarmonyMethod(typeof(FastPlantAbsorbers), nameof(Verify)) : null);
        }

        internal static MethodBase[] Guarded() =>
        [
            AccessTools.Method(typeof(PlantElementAbsorber), nameof(PlantElementAbsorber.PlanConsume),
                new[] { typeof(Storage), typeof(PlantElementAbsorber.LocalInfo), typeof(float), typeof(List<Planner.ConsumeCommand>) }),
            AccessTools.Method(typeof(PlantElementAbsorber), nameof(PlantElementAbsorber.PlanConsume),
                new[] { typeof(Storage), typeof(PlantElementAbsorber.ConsumeInfo[]), typeof(float), typeof(List<Planner.ConsumeCommand>) }),
            AccessTools.Constructor(typeof(Planner), new[] { typeof(Storage) }),
            AccessTools.Method(typeof(Planner), nameof(Planner.PlanConsume), new[] { typeof(float), typeof(Tag), typeof(List<Planner.ConsumeCommand>) }),
        ];

        static bool Foreign()
        {
            if (ForeignPatches.Find(guarded) is not { } why) return false;
            global::Debug.Log($"{Name}: off ({why})");
            return true;
        }

        /// Whether this call is the tweak's: not off, its checks done, every item alive.
        static bool Begin(Storage storage, float dt, out bool failed)
        {
            failed = false;
            if ((Calls++ & 0xFFF) == 0)
            {
                if (off = Foreign()) return false;
                if (verify && Calls > 1) global::Debug.Log($"{Name}: {Calls} calls, {verified} checked, {Wrong} wrong");
            }
            // The preconditions are the game's own method (a null storage, a bad dt).
            // Its answer is the game's (false), without the game's method logging it again.
            if (!PlantElementAbsorber.CheckPreConditions(storage, dt)) return !(failed = true);
            var list = storage.items;
            for (int i = 0; i < list.Count; i++)
                if (list[i] == null) return false;
            if (prefabs.Length < list.Count)
            {
                int size = Math.Max(list.Count, prefabs.Length * 2);
                prefabs = new KPrefabID[size];
                elements = new PrimaryElement[size];
                usable = new bool[size];
            }
            items = list;
            looked = 0;
            return true;
        }

        static void End()
        {
            NotLookedUp += items.Count - looked;
            LookedUp += looked;
            Array.Clear(prefabs, 0, looked);
            Array.Clear(elements, 0, looked);
            items = null;
        }

        /// PlanConsume(Storage, LocalInfo, ...).
        static bool PlanLocal(Storage storage, PlantElementAbsorber.LocalInfo localInfo, float dt, List<Planner.ConsumeCommand> consumeCommands, ref bool __result, bool __runOriginal, out Prediction __state)
        {
            __state = default;
            if (!__runOriginal || off || Bypass) return __runOriginal;
            if (!Begin(storage, dt, out bool failed))
            {
                if (!failed || verify) return true;
                Planned++;
                __result = false;
                return false;
            }
            var commands = verify ? mine : consumeCommands;
            if (verify) mine.Clear();
            bool result = Plan(localInfo.massConsumptionRate * dt, localInfo.tag, commands);
            End();
            if (verify)
            {
                __state = new Prediction { On = true, Result = result };
                return true;
            }
            Planned++;
            __result = result;
            return false;
        }

        /// PlanConsume(Storage, ConsumeInfo[], ...).
        static bool PlanMany(Storage storage, PlantElementAbsorber.ConsumeInfo[] consumedElements, float dt, List<Planner.ConsumeCommand> consumeCommands, ref bool __result, bool __runOriginal, out Prediction __state)
        {
            __state = default;
            if (!__runOriginal || off || Bypass) return __runOriginal;
            if (!Begin(storage, dt, out bool failed))
            {
                if (!failed || verify) return true;
                Planned++;
                __result = false;
                return false;
            }
            var commands = verify ? mine : consumeCommands;
            bool result = false;
            for (int i = 0; i < consumedElements.Length; i++)
            {
                PlantElementAbsorber.ConsumeInfo consumeInfo = consumedElements[i];
                if (verify) mine.Clear();
                else consumeCommands?.Clear();
                if (Plan(consumeInfo.massConsumptionRate * dt, consumeInfo.tag, commands))
                {
                    result = true;
                    break;
                }
            }
            End();
            if (verify)
            {
                __state = new Prediction { On = true, Result = result };
                return true;
            }
            Planned++;
            __result = result;
            return false;
        }

        /// Planner.PlanConsume over the storage's items, each item's components looked up when the
        /// walk first reaches it (the Planner's lookups, in its order: KPrefabID, then
        /// PrimaryElement only when there is one).
        [MirrorsGame("System.Void PlantElementAbsorber/Planner::.ctor(Storage)")]
        [MirrorsGame("System.Boolean PlantElementAbsorber/Planner::PlanConsume(System.Single,Tag,System.Collections.Generic.List`1<PlantElementAbsorber/Planner/ConsumeCommand>)")]
        static bool Plan(float requiredMass, Tag tag, List<Planner.ConsumeCommand> consumeCommands)
        {
            for (int i = 0; i != items.Count; i++)
            {
                if (i == looked)
                {
                    var item = items[i];
                    usable[i] = item.TryGetComponent<KPrefabID>(out var component) && item.TryGetComponent<PrimaryElement>(out var component2) && Set(i, component, component2);
                    looked++;
                }
                if (!usable[i]) continue;
                if (prefabs[i].HasTag(tag))
                {
                    PrimaryElement primaryElement = elements[i];
                    float num = Mathf.Min(requiredMass, primaryElement.Mass);
                    requiredMass -= num;
                    consumeCommands?.Add(new Planner.ConsumeCommand { primaryElement = primaryElement, deltaMass = num });
                    if (requiredMass <= 0f) return true;
                }
            }
            return false;
        }

        static bool Set(int i, KPrefabID prefab, PrimaryElement element)
        {
            prefabs[i] = prefab;
            elements[i] = element;
            return true;
        }

        /// Verify mode: the game's result and commands are the tweak's.
        static void Verify(bool __result, List<Planner.ConsumeCommand> consumeCommands, Prediction __state)
        {
            if (!__state.On) return;
            verified++;
            bool same = __result == __state.Result;
            if (consumeCommands != null)
            {
                same &= consumeCommands.Count == mine.Count;
                for (int i = 0; same && i < mine.Count; i++)
                    same = ReferenceEquals(consumeCommands[i].primaryElement, mine[i].primaryElement)
                        && BitConverter.SingleToInt32Bits(consumeCommands[i].deltaMass) == BitConverter.SingleToInt32Bits(mine[i].deltaMass);
            }
            if (!same) global::Debug.Log($"{Name}: a plan is wrong ({++Wrong}): the game's {__result} with {consumeCommands?.Count} commands, the tweak's {__state.Result} with {mine.Count}");
            mine.Clear();
        }
    }
}
