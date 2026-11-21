using System;
using System.Collections.Generic;
using System.Linq;

/// PerfDiff's arithmetic (plain .NET; the unit tests compile it in). A measure missing from a
/// recording is missing, not zero: recordings leave rows out (a row with no calls, the per
/// game second figures of a recording made while paused, a gauge's per-frame rate), so each
/// measure is averaged over the recordings that have it, and how many that were is kept.
static class DiffMath
{
    /// The mean and standard error of the values present (null: missing), and how many there were.
    public static (double Mean, double Se, int N) MeanSe(IEnumerable<double?> values)
    {
        var v = values.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        if (v.Count == 0) return (double.NaN, double.NaN, 0);
        double mean = v.Average();
        if (v.Count < 2) return (mean, 0, v.Count);
        double sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1));
        return (mean, sd / Math.Sqrt(v.Count), v.Count);
    }

    /// The coefficient of variation in % of the values present (0 under two values).
    public static double Cv(IEnumerable<double?> values)
    {
        var v = values.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        if (v.Count < 2) return 0;
        double mean = v.Average();
        if (mean == 0) return 0;
        return 100 * Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1)) / Math.Abs(mean);
    }

    /// A recording's value for `key`, or null when it has none.
    public static double? Get(IReadOnlyDictionary<string, double> values, string key) => values.TryGetValue(key, out var v) ? v : null;
}
