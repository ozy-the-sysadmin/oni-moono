#nullable enable
using System;
using System.Collections.Generic;

namespace OniMoono.Host.Perf
{
    /// A recording's frame times summed up: mean, percentiles, the lows and the long frames.
    /// Plain .NET (the unit tests compile it in).
    internal sealed class FrameSummary
    {
        public int Frames;
        public double MeanMs, Fps, P50Ms, P90Ms, P99Ms, P999Ms, MaxMs;
        /// Frames per second over the slowest 1 % and 0.1 % of frames (their mean time).
        public double Low1Fps, Low01Fps;
        /// Frames longer than 33, 50, 100 and 250 ms.
        public int Over33, Over50, Over100, Over250;

        /// `frames`: each frame's time in milliseconds (not changed).
        public static FrameSummary Of(IReadOnlyList<float> frames)
        {
            var s = new FrameSummary { Frames = frames.Count };
            if (frames.Count == 0) return s;
            var sorted = new float[frames.Count];
            double sum = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                float f = frames[i];
                sorted[i] = f;
                sum += f;
                if (f > 33) s.Over33++;
                if (f > 50) s.Over50++;
                if (f > 100) s.Over100++;
                if (f > 250) s.Over250++;
            }
            Array.Sort(sorted);
            s.MeanMs = sum / frames.Count;
            s.Fps = s.MeanMs > 0 ? 1000 / s.MeanMs : 0;
            s.P50Ms = Percentile(sorted, 0.50);
            s.P90Ms = Percentile(sorted, 0.90);
            s.P99Ms = Percentile(sorted, 0.99);
            s.P999Ms = Percentile(sorted, 0.999);
            s.MaxMs = sorted[^1];
            s.Low1Fps = LowFps(sorted, 0.01);
            s.Low01Fps = LowFps(sorted, 0.001);
            return s;
        }

        /// The nearest-rank percentile of sorted values.
        public static double Percentile(float[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            int rank = (int)Math.Ceiling(p * sorted.Length);
            return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
        }

        /// 1000 over the mean of the slowest `share` of frames (at least one frame).
        public static double LowFps(float[] sorted, double share)
        {
            if (sorted.Length == 0) return 0;
            int n = Math.Max(1, (int)Math.Round(sorted.Length * share));
            double sum = 0;
            for (int i = sorted.Length - n; i < sorted.Length; i++) sum += sorted[i];
            return sum > 0 ? 1000 * n / sum : 0;
        }
    }
}
