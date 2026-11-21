#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OniMoono.Host.Perf
{
    /// What the performance recorder records (PerfRecorder): `key=value` pairs separated by `;`,
    /// from `--perf-record SPEC`, ONI_PERF_RECORD=SPEC or the performance window's Record button.
    ///
    ///     delay=40    real seconds of unpaused play after a colony loads before the first recording
    ///                 (paused time does not count; every load starts the wait again)
    ///     seconds=60  each recording's length (real seconds, at least 1)
    ///     count=3     recordings back to back
    ///     gap=2       real seconds of unpaused play before each later recording (and before the
    ///                 first when `speeds` sets one), so a speed change settles
    ///     speeds=3,2  the speed set before each recording, the last one repeating; none: unchanged
    ///     label=A3    in the files' names and in each file
    ///     out=DIR     where the files go (default: the oni-moono data folder's perf/)
    ///     once=1      0: armed again after every load
    ///
    /// Plain .NET (the unit tests compile it in).
    internal sealed class PerfSpec
    {
        public double Delay = 40, Seconds = 60, Gap = 2;
        public int Count = 3;
        public int[]? Speeds;
        public string Label = "rec";
        public string? Out;
        public bool Once = true;
        /// The text it was read from.
        public string Text = "";

        /// The speed for recording `index` (0-based); 0 for unchanged.
        public int SpeedFor(int index) => Speeds == null || Speeds.Length == 0 ? 0 : Speeds[Math.Min(index, Speeds.Length - 1)];

        /// The spec without its `speeds=` part (ONI_PERF_RECORD: the environment alone never
        /// changes the game's speed); `dropped` when it had one.
        public static string WithoutSpeeds(string text, out bool dropped)
        {
            var parts = text.Split(';');
            var kept = parts.Where(p => !p.Trim().StartsWith("speeds=", StringComparison.OrdinalIgnoreCase)).ToArray();
            dropped = kept.Length != parts.Length;
            return string.Join(";", kept);
        }

        /// The spec in `text`; what it could not read is in `problems` (those keys keep their
        /// defaults).
        public static PerfSpec Parse(string? text, out List<string> problems)
        {
            problems = new List<string>();
            var spec = new PerfSpec { Text = text ?? "" };
            foreach (var part in (text ?? "").Split(';'))
            {
                if (part.Trim().Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    problems.Add($"\"{part.Trim()}\" is not key=value");
                    continue;
                }
                string key = part.Substring(0, eq).Trim().ToLowerInvariant(), value = part.Substring(eq + 1).Trim();
                bool Number(out double d) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0 && !double.IsInfinity(d);
                switch (key)
                {
                    case "delay" when Number(out var d): spec.Delay = d; break;
                    case "seconds" when Number(out var d) && d >= 1: spec.Seconds = d; break;
                    case "gap" when Number(out var d): spec.Gap = d; break;
                    case "count" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1: spec.Count = n; break;
                    case "speeds":
                        var speeds = new List<int>();
                        foreach (var s in value.Split(','))
                        {
                            if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int speed) && speed >= 1 && speed <= 3) speeds.Add(speed);
                            else problems.Add($"speeds: \"{s.Trim()}\" is not 1, 2 or 3");
                        }
                        spec.Speeds = speeds.Count > 0 ? speeds.ToArray() : null;
                        break;
                    case "label" when value.Length > 0 && value.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0: spec.Label = value; break;
                    case "out" when value.Length > 0: spec.Out = value; break;
                    case "once" when value is "0" or "1": spec.Once = value == "1"; break;
                    case "delay" or "seconds" or "gap" or "count" or "label" or "out" or "once":
                        problems.Add($"{key}: \"{value}\" is not a valid value");
                        break;
                    default:
                        problems.Add($"unknown key {key}");
                        break;
                }
            }
            return spec;
        }

        /// For the log.
        public string Describe()
        {
            var c = CultureInfo.InvariantCulture;
            return string.Format(c, "delay {0} s, {1} x {2} s, gap {3} s, speeds {4}, label {5}, out {6}, once {7}", Delay, Count, Seconds, Gap,
                Speeds == null ? "unchanged" : string.Join(",", Speeds.Select(s => s.ToString(c))), Label, Out ?? "default", Once ? 1 : 0);
        }
    }
}
