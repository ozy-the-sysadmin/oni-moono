#nullable enable
using System;
using System.Collections.Generic;
using OniMoono.Engine;

namespace OniMoono.Host.Perf
{
    // Filled by PerfRecorder.Take; the unit tests compile this file without it.
#pragma warning disable CS0649
    /// What the recorder reads at a recording's bounds (PerfRecorder.Take), for its file
    /// (PerfJson). Plain data (the unit tests compile it in).
    internal sealed class PerfSnapshot
    {
        public long Timestamp;
        public DateTime Utc;
        public double GameTime;
        public List<(string Key, string Tweak, long Value, bool Gauge)> Counters = [];
        public string[] ColonyKeys = [];
        public double[] ColonyValues = [];
        public long[]? Game;
        public string[]? BucketLabels;
        public long[]? BucketTicks, BucketCalls, BucketEntries;
        public FrameStatsWindow.RuntimeSample Runtime;
        public long HeapBytes, CommittedBytes;
    }
#pragma warning restore CS0649
}
