#nullable enable
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using OniMoono.Engine;
using OniMoono.Tweaks;
using UnityEngine;

namespace OniMoono.Host.GameTweaks
{
    /// fast-serialization's warm-up at the end of a load. The first save of a launch was about
    /// 0.65 s longer than the next ones on McBench (session AB: 1,374 against 720 ms): its frame
    /// compiled the writers of some 880 templates and the JIT compiled 2,770 methods in it
    /// (457 ms, against 71 ms in the second save). The save's buffer was not the cause: the load
    /// already sizes it (SaveLoader.lastUncompressedSize is the loaded body's length).
    ///
    /// Here, on the colony's second frame while the game is still paused (the load's own pause,
    /// before post-load-gc's collections), each component type the colony saves gets its
    /// template and its writers compiled, and the writers run once over one live component of
    /// that type into a writer that keeps nothing (Stream.Null), so that the JIT compiles them and
    /// the game's writing code they call. Only fields are read: in the warm-up the templates'
    /// callbacks ([OnSerializing], [OnSerialized]), properties and custom serializers are not
    /// called, nested values go through the same field-only path, and a field that throws is not
    /// reported. Nothing in the game changes: the templates made are those the next save's
    /// would be (KSerialization.Manager makes them from the type alone, and the save starts with
    /// Manager.Clear), and the writers are kept for the next save as before. Until that first
    /// save, KSerialization.Manager holds the templates and type entries made here (under the
    /// load's type-info mask, which the save's Manager.Clear resets); nothing in the game
    /// serializes through Manager between a load and its first save.
    ///
    /// Skipped when the game is not paused on that frame (unpaused before the load settled), and
    /// with ONI_SAVE_WARM=0 (for measuring the first save without it).
    [GameMember(GameUse.Postfix, "System.Void Game::OnSpawn()")]
    [GameMember(GameUse.Uses, "SpeedControlScreen SpeedControlScreen::get_Instance()")]
    [GameMember(GameUse.Uses, "System.Boolean SpeedControlScreen::get_IsPaused()")]
    [GameMember(GameUse.Uses, "SaveLoader SaveLoader::get_Instance()")]
    [GameMember(GameUse.Uses, "SaveManager SaveLoader::saveManager")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.Dictionary`2<Tag,System.Collections.Generic.List`1<SaveLoadRoot>> SaveManager::GetLists()")]
    [GameMember(GameUse.Uses, "KSerialization.SerializationTemplate KSerialization.Manager::GetSerializationTemplate(System.Type)")]
    [GameMember(GameUse.Uses, "KMonoBehaviour")]
    [GameMember(GameUse.Uses, "SkipSaveFileSerialization")]
    [GameMember(GameUse.Uses, "SaveLoadRoot")]
    [GameMember(GameUse.Uses, "SaveManager")]
    [GameMember(GameUse.Uses, "SaveLoader")]
    [GameMember(GameUse.Uses, "KSerialization.Helper")]
    [GameMember(GameUse.Uses, "KSerialization.Manager")]
    [GameMember(GameUse.Uses, "KSerialization.SerializationTemplate")]
    [GameMember(GameUse.Uses, "StateMachineController")]
    [GameMember(GameUse.Uses, "Storage")]
    internal static partial class FastSerialization
    {
        /// The warm-up of the last load: component types warmed, and its milliseconds.
        public static long WarmedTypes, WarmSkipped;
        public static double LastWarmMs;

        [ThreadStatic] static bool warming;
        static bool warmInstalled;
        static PropertyInfo? speedInstance, isPaused, saveLoaderInstance;
        static FieldInfo? saveManagerField;
        static MethodInfo? getLists, getTemplate;
        static Type? kMonoBehaviour, skipAttribute;

        static void InstallWarm(Harmony harmony)
        {
            if (Environment.GetEnvironmentVariable("ONI_SAVE_WARM") == "0")
            {
                EngineLog.Engine($"{Name}: no warm-up at load (ONI_SAVE_WARM=0)");
                return;
            }
            var speed = AccessTools.TypeByName("SpeedControlScreen");
            var saveLoader = AccessTools.TypeByName("SaveLoader");
            var saveManager = AccessTools.TypeByName("SaveManager");
            var manager = AccessTools.TypeByName("KSerialization.Manager");
            speedInstance = AccessTools.Property(speed, "Instance");
            isPaused = AccessTools.Property(speed, "IsPaused");
            saveLoaderInstance = AccessTools.Property(saveLoader, "Instance");
            saveManagerField = AccessTools.Field(saveLoader, "saveManager");
            getLists = AccessTools.Method(saveManager, "GetLists");
            getTemplate = AccessTools.Method(manager, "GetSerializationTemplate", [typeof(Type)]);
            kMonoBehaviour = AccessTools.TypeByName("KMonoBehaviour");
            skipAttribute = AccessTools.TypeByName("SkipSaveFileSerialization");
            if (speedInstance == null || isPaused == null || saveLoaderInstance == null || saveManagerField == null || getLists == null || getTemplate == null || kMonoBehaviour == null)
            {
                EngineLog.Engine($"{Name}: no warm-up at load (a member it reads is missing)");
                return;
            }
            harmony.Patch(AccessTools.Method("Game:OnSpawn"), postfix: new HarmonyMethod(typeof(FastSerialization), nameof(GameSpawned)));
            warmInstalled = true;
        }

        static void GameSpawned(MonoBehaviour __instance) => __instance.StartCoroutine(WarmAfterLoad());

        static IEnumerator WarmAfterLoad()
        {
            yield return null;
            yield return null;
            if (!warmInstalled || !Enabled) yield break;
            var screen = speedInstance!.GetValue(null);
            if (screen == null || !(bool)isPaused!.GetValue(screen)!)
            {
                WarmSkipped++;
                EngineLog.Engine($"{Name}: no warm-up, the game was not paused on the colony's second frame");
                yield break;
            }
            var clock = Stopwatch.StartNew();
            int types = 0;
            try
            {
                types = WarmComponents();
            }
            catch (Exception e)
            {
                var cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                EngineLog.Engine($"{Name}: warm-up stopped: {cause.GetType().Name}: {cause.Message}");
            }
            LastWarmMs = clock.Elapsed.TotalMilliseconds;
            WarmedTypes = types;
            EngineLog.Engine($"{Name}: writers of {types} component types warmed for the first save ({PreparedMethods} save methods compiled), {LastWarmMs:F0} ms, still paused");
        }

        /// One live component of each type the save roots carry, through its template's writers.
        static int WarmComponents()
        {
            var saveManager = saveManagerField!.GetValue(saveLoaderInstance!.GetValue(null));
            if (saveManager == null || getLists!.Invoke(saveManager, null) is not IDictionary lists) return 0;
            var seen = new System.Collections.Generic.HashSet<Type>();
            using var discard = new BinaryWriter(Stream.Null);
            foreach (var list in lists.Values)
            {
                if (list is not IList roots || roots.Count == 0 || roots[0] is not Component root || root == null) continue;
                foreach (var component in root.GetComponents(kMonoBehaviour!))
                {
                    if (component == null) continue;
                    var type = component.GetType();
                    if (!seen.Add(type) || (skipAttribute != null && type.IsDefined(skipAttribute, false))) continue;
                    WarmOne(getTemplate!.Invoke(null, [type])!, component, discard);
                    PrepareSaveMethods(type);
                }
            }
            // The save's own walk, which the warm-up does not call (it would run the callbacks).
            PrepareAll(AccessTools.TypeByName("SaveLoadRoot"));
            PrepareAll(AccessTools.TypeByName("SaveManager"));
            PrepareAll(AccessTools.TypeByName("SaveLoader"));
            PrepareAll(AccessTools.TypeByName("KSerialization.Helper"));
            PrepareAll(AccessTools.TypeByName("KSerialization.Manager"));
            PrepareAll(AccessTools.TypeByName("KSerialization.SerializationTemplate"));
            PrepareAll(AccessTools.TypeByName("StateMachineController"));
            PrepareAll(AccessTools.TypeByName("Storage"));
            return seen.Count;
        }

        /// The JIT compiles a component type's save callbacks and custom serializer now, not in
        /// the save's frame (RuntimeHelpers.PrepareMethod compiles, it runs nothing; the type's
        /// static constructor has run, an instance of it exists).
        static void PrepareSaveMethods(Type type)
        {
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (var method in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    bool callback = false;
                    foreach (var a in method.CustomAttributes)
                        if (a.AttributeType.Name is "OnSerializingAttribute" or "OnSerializedAttribute") callback = true;
                    if (callback || method.Name == "Serialize") Prepare(method);
                }
        }

        static void PrepareAll(Type? type)
        {
            if (type == null) return;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                Prepare(method);
        }

        static void Prepare(MethodInfo method)
        {
            if (method.IsAbstract || method.ContainsGenericParameters || method.DeclaringType!.ContainsGenericParameters) return;
            try
            {
                System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
                PreparedMethods++;
            }
            catch (Exception)
            {
                // Not compilable ahead (an icall, a P/Invoke): the save compiles what it needs.
            }
        }

        public static long PreparedMethods;

        static void WarmOne(object template, object obj, BinaryWriter discard)
        {
            warming = true;
            try
            {
                SerializeData(template, obj, discard);
            }
            catch (Exception)
            {
                // A field that can't be written now is written (or reported) by the save itself.
            }
            finally
            {
                warming = false;
            }
        }

        /// SerializeData while warming: the fields only (no callbacks, properties or custom
        /// serializer), into the discarding writer.
        static bool WarmFields(Plan plan, object obj, BinaryWriter writer)
        {
            plan.Fields(obj, writer);
            return false;
        }
    }
}
