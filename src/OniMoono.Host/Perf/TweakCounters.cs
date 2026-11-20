#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace OniMoono.Host.Perf
{
    /// The tweaks' work counters, read whole when a recording starts and ends (never per frame).
    ///
    /// A tweak (a class with `public const string Name`, in the host's or OniMoono.GameTweaks'
    /// assembly) gives its counters in one of two ways:
    ///
    /// - a method `public static void Counters(Action<string, long> counter)` that names each
    ///   one: `counter("calls", Calls); counter("skipped", Skipped);` (the uniform way, for
    ///   counters whose meaning the tweak states: what it did and what it left to the game);
    /// - without one, its public static `long` and `int` fields (not constants) and properties,
    ///   as they are named: most tweaks already count their work that way for verify mode and the
    ///   tests (a few such fields are not counts, a frame number or a flag: the method says which
    ///   are).
    ///
    /// Counters are plain fields the main thread adds to; this reads them without locks, so a
    /// worker's count may be a frame behind. Keys are `tweak/NAME.COUNTER`.
    internal static class TweakCounters
    {
        sealed class Source
        {
            public string Tweak = "";
            public MethodInfo? Method;
            public FieldInfo[] Fields = [];
            public PropertyInfo[] Properties = [];
        }

        static Source[]? sources;

        /// Every counter of the tweaks named in `tweaks`, by key, with the tweak it belongs to.
        public static List<(string Key, string Tweak, long Value)> Read(IEnumerable<Assembly> assemblies, IReadOnlySet<string> tweaks)
        {
            sources ??= Find(assemblies);
            var values = new List<(string, string, long)>();
            foreach (var source in sources)
            {
                if (!tweaks.Contains(source.Tweak)) continue;
                try
                {
                    if (source.Method != null)
                    {
                        source.Method.Invoke(null, [(Action<string, long>)((name, value) => values.Add(($"tweak/{source.Tweak}.{name}", source.Tweak, value)))]);
                        continue;
                    }
                    foreach (var field in source.Fields)
                        values.Add(($"tweak/{source.Tweak}.{field.Name}", source.Tweak, Convert.ToInt64(field.GetValue(null))));
                    foreach (var property in source.Properties)
                        values.Add(($"tweak/{source.Tweak}.{property.Name}", source.Tweak, Convert.ToInt64(property.GetValue(null))));
                }
                catch (Exception e)
                {
                    var cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                    OniMoono.Engine.EngineLog.Engine($"perf record: {source.Tweak}'s counters not read: {cause.GetType().Name}: {cause.Message}");
                }
            }
            return values;
        }

        static Source[] Find(IEnumerable<Assembly> assemblies)
        {
            var found = new List<Source>();
            foreach (var assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray()!;
                }
                foreach (var type in types)
                {
                    if (type.GetField("Name", BindingFlags.Public | BindingFlags.Static) is not { IsLiteral: true } name || name.FieldType != typeof(string)) continue;
                    var source = new Source { Tweak = (string)name.GetRawConstantValue()! };
                    source.Method = type.GetMethod("Counters", BindingFlags.Public | BindingFlags.Static, [typeof(Action<string, long>)]);
                    if (source.Method == null)
                    {
                        source.Fields = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                            .Where(f => !f.IsLiteral && (f.FieldType == typeof(long) || f.FieldType == typeof(int))).ToArray();
                        source.Properties = type.GetProperties(BindingFlags.Public | BindingFlags.Static)
                            .Where(p => p.GetMethod != null && p.GetIndexParameters().Length == 0 && (p.PropertyType == typeof(long) || p.PropertyType == typeof(int))).ToArray();
                    }
                    if (source.Method != null || source.Fields.Length > 0 || source.Properties.Length > 0) found.Add(source);
                }
            }
            return found.OrderBy(s => s.Tweak, StringComparer.Ordinal).ToArray();
        }
    }
}
