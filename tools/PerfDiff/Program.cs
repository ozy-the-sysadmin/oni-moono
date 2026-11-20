using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

// TEST TOOLING: PerfDiff, the performance recorder's files (perf-*.json, schema moono-perf/1)
// compared. Each argument after -a, -b or -set is a file or a folder (its perf-*.json).
//
//   PerfDiff -a A... -b B... [-out PREFIX]
//     two setups: per measure, the mean of A and of B, the difference and the difference in %
//     (with each side's standard error over its files), the rows' ms per frame first, sorted by
//     the size of the difference, then the counts (calls, objects and tweak counters per game
//     second, per frame) sorted by their difference in %.
//   PerfDiff -set FILES... [-group-by label|none] [-out PREFIX]
//     N recordings of one setup: per measure, the mean and the coefficient of variation; grouped
//     (by the run's label: one label a launch), also the CV within groups (the mean of each
//     group's CV) and between them (the CV of the groups' means).
//
// Prints Markdown; -out also writes PREFIX.md and PREFIX.csv. Measures: frame.* (ms_pf, fps,
// percentiles, lows, longest), recording.speed_reached, gc.*, each row's ms_pf, ms_pgs,
// calls_pgs, objects_pgs, each counter's per_gs and per_frame.
var c = CultureInfo.InvariantCulture;
List<string> a = new(), b = new(), set = new();
List<string>? into = null;
string? outPrefix = null, groupBy = "label";
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-a": into = a; break;
        case "-b": into = b; break;
        case "-set": into = set; break;
        case "-out": outPrefix = args[++i]; into = null; break;
        case "-group-by": groupBy = args[++i]; into = null; break;
        default:
            if (into == null) return Usage($"unexpected {args[i]}");
            into.Add(args[i]);
            break;
    }
}
if (set.Count > 0 && (a.Count > 0 || b.Count > 0)) return Usage("-set or -a/-b, not both");
if (set.Count == 0 && (a.Count == 0 || b.Count == 0)) return Usage("give -a and -b, or -set");

var md = new StringBuilder();
var csv = new StringBuilder();
try
{
    if (set.Count > 0) Set(Load(set));
    else Pair(Load(a), Load(b));
}
catch (InvalidOperationException e)
{
    return Usage(e.Message);
}
Console.Write(md.ToString());
if (outPrefix != null)
{
    File.WriteAllText(outPrefix + ".md", md.ToString(), new UTF8Encoding(false));
    File.WriteAllText(outPrefix + ".csv", csv.ToString(), new UTF8Encoding(false));
}
return 0;

static int Usage(string problem)
{
    Console.Error.WriteLine("perfdiff: " + problem);
    Console.Error.WriteLine("usage: PerfDiff -a A... -b B... [-out PREFIX] | PerfDiff -set FILES... [-group-by label|none] [-out PREFIX]");
    return 2;
}

List<Recording> Load(List<string> paths)
{
    var files = new List<string>();
    foreach (var p in paths)
    {
        if (Directory.Exists(p)) files.AddRange(Directory.GetFiles(p, "perf-*.json").OrderBy(f => f, StringComparer.Ordinal));
        else if (File.Exists(p)) files.Add(p);
        else Console.Error.WriteLine($"perfdiff: {p} not found");
    }
    var recordings = new List<Recording>();
    foreach (var f in files)
    {
        try
        {
            recordings.Add(Recording.Read(f));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            Console.Error.WriteLine($"perfdiff: {f}: {e.Message}");
        }
    }
    if (recordings.Count == 0) throw new InvalidOperationException("no recordings read");
    return recordings;
}

void Pair(List<Recording> left, List<Recording> right)
{
    md.Append($"# perfdiff: A ({left.Count} files) against B ({right.Count} files)\n\n");
    md.Append($"A: {Describe(left)}\n\nB: {Describe(right)}\n\n");
    csv.Append("measure,a_mean,a_se,b_mean,b_se,diff,diff_pct\n");
    var keys = left.SelectMany(r => r.Values.Keys).Concat(right.SelectMany(r => r.Values.Keys)).Distinct().ToList();
    var rows = new List<(string Key, double A, double SeA, double B, double SeB)>();
    foreach (var key in keys)
    {
        var (ma, sa) = MeanSe(left.Select(r => r.Values.GetValueOrDefault(key)));
        var (mb, sb) = MeanSe(right.Select(r => r.Values.GetValueOrDefault(key)));
        rows.Add((key, ma, sa, mb, sb));
        csv.Append(string.Join(",", Quote(key), F(ma), F(sa), F(mb), F(sb), F(mb - ma), F(Pct(ma, mb)))).Append('\n');
    }
    void Table(string title, IEnumerable<(string Key, double A, double SeA, double B, double SeB)> part, string format)
    {
        md.Append($"## {title}\n\n| measure | A | B | B − A | % |\n|---|---|---|---|---|\n");
        foreach (var r in part)
            md.Append($"| {Cell(r.Key)} | {r.A.ToString(format, c)} ±{r.SeA.ToString(format, c)} | {r.B.ToString(format, c)} ±{r.SeB.ToString(format, c)} | {(r.B - r.A).ToString(format, c)} | {PctText(r.A, r.B)} |\n");
        md.Append('\n');
    }
    Table("Frame", rows.Where(r => !r.Key.Contains(' ')), "F3");
    Table("Rows, ms per frame (largest difference first)", rows.Where(r => r.Key.EndsWith(" ms_pf", StringComparison.Ordinal))
        .OrderByDescending(r => Math.Abs(r.B - r.A)), "F4");
    Table("Rows, ms per game second (sim rows)", rows.Where(r => r.Key.EndsWith(" ms_pgs", StringComparison.Ordinal))
        .OrderByDescending(r => Math.Abs(r.B - r.A)).Take(40), "F3");
    Table("Counts (largest difference in % first)", rows.Where(r => r.Key.EndsWith("_pgs", StringComparison.Ordinal) && !r.Key.EndsWith(" ms_pgs", StringComparison.Ordinal)
        || r.Key.EndsWith(" per_gs", StringComparison.Ordinal) || r.Key.EndsWith(" per_frame", StringComparison.Ordinal))
        .Where(r => r.A != 0 || r.B != 0).OrderByDescending(r => r.A == 0 ? double.PositiveInfinity : Math.Abs(Pct(r.A, r.B))), "F2");
}

void Set(List<Recording> all)
{
    var groups = groupBy == "none" ? all.GroupBy(_ => "all").ToList() : all.GroupBy(r => r.Label).ToList();
    bool grouped = groups.Count > 1;
    md.Append($"# perfdiff: {all.Count} recordings{(grouped ? $" in {groups.Count} groups by {groupBy}" : "")}\n\n{Describe(all)}\n\n");
    csv.Append("measure,mean,cv_pct" + (grouped ? ",cv_within_pct,cv_between_pct" : "") + "\n");
    md.Append(grouped ? "| measure | mean | CV % | CV within % | CV between % |\n|---|---|---|---|---|\n" : "| measure | mean | CV % |\n|---|---|---|\n");
    var keys = all.SelectMany(r => r.Values.Keys).Distinct().OrderBy(k => k.Contains(' ') ? 1 : 0).ThenBy(k => k, StringComparer.Ordinal);
    foreach (var key in keys)
    {
        var values = all.Select(r => r.Values.GetValueOrDefault(key)).ToList();
        double mean = values.Average(), cv = Cv(values);
        if (mean == 0) continue;
        if (!grouped)
        {
            md.Append($"| {Cell(key)} | {mean.ToString("G5", c)} | {cv.ToString("F2", c)} |\n");
            csv.Append(string.Join(",", Quote(key), F(mean), F(cv))).Append('\n');
            continue;
        }
        double within = groups.Select(g => Cv(g.Select(r => r.Values.GetValueOrDefault(key)).ToList())).Average();
        double between = Cv(groups.Select(g => g.Average(r => r.Values.GetValueOrDefault(key))).ToList());
        md.Append($"| {Cell(key)} | {mean.ToString("G5", c)} | {cv.ToString("F2", c)} | {within.ToString("F2", c)} | {between.ToString("F2", c)} |\n");
        csv.Append(string.Join(",", Quote(key), F(mean), F(cv), F(within), F(between))).Append('\n');
    }
}

string Describe(List<Recording> rs) => string.Join("; ", rs.Select(r => $"{Path.GetFileName(r.Path)} ({r.Label}, {r.Frames} frames, {r.Values.GetValueOrDefault("frame.fps").ToString("F1", c)} fps)"));

static (double Mean, double Se) MeanSe(IEnumerable<double> values)
{
    var v = values.ToList();
    double mean = v.Average();
    if (v.Count < 2) return (mean, 0);
    double sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1));
    return (mean, sd / Math.Sqrt(v.Count));
}

static double Cv(List<double> v)
{
    if (v.Count < 2) return 0;
    double mean = v.Average();
    if (mean == 0) return 0;
    return 100 * Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1)) / Math.Abs(mean);
}

static double Pct(double a, double b) => a != 0 ? 100 * (b - a) / Math.Abs(a) : 0;
static string PctText(double a, double b) => a == 0 ? (b == 0 ? "0.0" : "new") : Pct(a, b).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
// Bucket labels hold "|", which would end a Markdown table cell.
static string Cell(string s) => s.Replace("|", "\\|");
static string F(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
static string Quote(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

/// One recording's measures, by name.
sealed class Recording
{
    public string Path = "", Label = "";
    public long Frames;
    public readonly Dictionary<string, double> Values = new(StringComparer.Ordinal);

    public static Recording Read(string path)
    {
        // As text: a file saved by an editor may start with a byte order mark.
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.GetProperty("schema").GetString() != "moono-perf/1") throw new InvalidOperationException("not a moono-perf/1 file");
        var r = new Recording { Path = path, Label = root.GetProperty("run").GetProperty("label").GetString() ?? "" };
        r.Frames = root.GetProperty("recording").GetProperty("frames").GetInt64();
        void Section(string name, params string[] fields)
        {
            if (!root.TryGetProperty(name, out var section)) return;
            foreach (var field in fields)
                if (section.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number) r.Values[name + "." + field] = v.GetDouble();
        }
        Section("frame", "ms_pf", "fps", "p50_ms", "p90_ms", "p99_ms", "p999_ms", "low1_fps", "low01_fps", "max_ms", "frames_over_50ms", "frames_over_100ms");
        Section("recording", "speed_reached", "game_s", "sim_steps_pf");
        Section("gc", "pause_ms_pf", "gen0", "gen1", "gen2", "allocated_kb_pf", "allocated_mb_ps");
        Section("check", "unattributed_ms_pf");
        foreach (var row in root.GetProperty("rows").EnumerateArray())
        {
            string key = row.GetProperty("key").GetString() ?? "";
            foreach (var field in new[] { "ms_pf", "ms_pgs", "calls_pgs", "objects_pgs" })
                if (row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number) r.Values[key + " " + field] = v.GetDouble();
        }
        foreach (var counter in root.GetProperty("counters").EnumerateArray())
        {
            string key = counter.GetProperty("key").GetString() ?? "";
            foreach (var field in new[] { "per_gs", "per_frame" })
                if (counter.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number) r.Values[key + " " + field] = v.GetDouble();
        }
        return r;
    }
}
