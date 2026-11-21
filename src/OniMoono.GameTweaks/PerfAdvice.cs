using System;
using System.Collections.Generic;
using System.Globalization;

namespace OniMoono.GameTweaks
{
    /// What the Colony tab's advice is decided on: the colony and the frame as measured now.
    public sealed class PerfAdviceState
    {
        /// A colony has run unpaused long enough to judge (the last minute had 20 colony seconds).
        public bool Judged;
        public double Fps, KeepUp, Speed;
        /// Per minute over the last minute: stutters (20 ms or more), and those from waiting for
        /// the simulation step.
        public int StuttersPerMinute, SimWaitsPerMinute;
        /// Tweaks on this run (their names).
        public ISet<string> TweaksOn = new HashSet<string>(StringComparer.Ordinal);
        /// The colony's counts (-1: not read).
        public int Duplicants = -1, Critters = -1, LooseItems = -1, Buildings = -1, Utilities = -1;
    }

    /// The Colony tab's advice: rules, each a condition on the measured state and a card whose
    /// gain was measured. Only rules with a measured gain are listed. To add one: a Rule with its
    /// condition, its gain in the units it was measured in (Rank orders the cards, biggest gain
    /// first), its card's three lines and the line it shows when it does not apply, plus its
    /// strings in TweakStrings.Ui (and zh.po). Plain .NET; `Ui` is the window's string lookup.
    public static class PerfAdvice
    {
        public sealed class Rule
        {
            public string Id;
            /// Whether the card shows.
            public Func<PerfAdviceState, bool> Applies;
            /// Its expected gain in fps-equivalent points, for ordering (biggest first).
            public Func<PerfAdviceState, double> Rank;
            /// The card: the action, the gain sentence, the reason with counts.
            public Func<PerfAdviceState, Func<string, string>, (string Title, string Gain, string Reason)> Card;
            /// The line under "Nothing to gain here" when it does not apply (null: none).
            public Func<PerfAdviceState, Func<string, string>, string> Nothing;
        }

        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        /// sim-step-pace (Moono Tweaks, Changes gameplay, off by default). Measured on McBench at
        /// speed 3 (sessions AB and AC, reports/sessionAB-moono-stalls.md §3 and
        /// reports/sessionAC-moono-consolidate.md §4): frames of 20 ms or more 122-126 a minute
        /// with it off, 4-11 with it on; 100 % of those frames were the zero-time wait for the
        /// simulation step it removes. Shown while that wait causes 30 or more stutters a minute.
        public const string SimStepPace = "sim-step-pace";
        public const int SimStepPaceMinPerMinute = 30;
        public const int SimStepPaceMeasuredOff = 126, SimStepPaceMeasuredOn = 8;

        public static readonly Rule[] Rules =
        {
            new Rule
            {
                Id = SimStepPace,
                Applies = s => s.Judged && !s.TweaksOn.Contains(SimStepPace) && s.SimWaitsPerMinute >= SimStepPaceMinPerMinute,
                // Stutters removed, counted as a tenth of a frame rate point each (only one rule yet).
                Rank = s => s.SimWaitsPerMinute / 10.0,
                Card = (s, Ui) => (Ui("PERF_ADVICE_SIM_STEP_PACE"),
                    string.Format(C, Ui("PERF_ADVICE_SIM_STEP_PACE_GAIN"), s.SimWaitsPerMinute, SimStepPaceMeasuredOff, SimStepPaceMeasuredOn),
                    Ui("PERF_ADVICE_SIM_STEP_PACE_WHY")),
                Nothing = (s, Ui) => !s.Judged ? null
                    : s.TweaksOn.Contains(SimStepPace) ? Ui("PERF_NOTHING_SIM_STEP_PACE_ON")
                    : string.Format(C, Ui("PERF_NOTHING_SIM_WAITS_FEW"), s.SimWaitsPerMinute),
            },
        };

        /// The cards that apply, biggest gain first, and the lines of the rules that do not.
        public static (List<(string Title, string Gain, string Reason)> Cards, List<string> Nothing) Evaluate(PerfAdviceState state, Func<string, string> ui)
        {
            var applying = new List<(double Rank, (string, string, string) Card)>();
            var nothing = new List<string>();
            foreach (var rule in Rules)
            {
                if (rule.Applies(state)) applying.Add((rule.Rank(state), rule.Card(state, ui)));
                else if (rule.Nothing?.Invoke(state, ui) is string line) nothing.Add(line);
            }
            applying.Sort((a, b) => b.Rank.CompareTo(a.Rank));
            return (applying.ConvertAll(a => a.Card), nothing);
        }
    }
}
