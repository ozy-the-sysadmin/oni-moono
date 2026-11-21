using System;
using System.Collections.Generic;

namespace OniMoono.GameTweaks
{
    /// Why a slow frame was slow, as the engine or the game reports it (PerfReads). Unknown when
    /// nothing reported a cause: the monitor never guesses.
    public enum PerfCause : byte
    {
        None, Load, FirstFrame, Autosave, Save, Timelapse, Gc, SimWait, Unknown,
    }

    /// One frame as the monitor sees it (PerfReads fills it at the next frame's start).
    public struct PerfFrame
    {
        /// Real seconds since start-up at the frame's end, and its wall time (ms).
        public double Now, Ms;
        /// The engine's GC pause and the main thread's JIT time in the frame (ms).
        public double GcMs, JitMs;
        /// A colony runs (not loading), the game was paused for the frame, the game is between
        /// scenes or loading, the main menu shows.
        public bool InColony, Paused, Loading, InMenu;
        /// Game seconds the frame advanced, and the speed it ran at (Time.timeScale).
        public double GameDt, Scale;
        /// The game asked the simulation for its results with no time to step (Game.SimEveryTick's
        /// zero-time UnsafeSim200ms): the frame waited for the step under way (session AB).
        public bool ZeroSubTick;
        /// A save finished in the frame (the game's "Saved to" log line) and was an autosave; the
        /// timelapse or a save's preview picture was being taken.
        public bool Saved, AutoSave, Timelapse, Preview;
    }

    /// The frame rate counter's and the performance window's memory (OniMoono.GameTweaks.
    /// PerfOverlay): plain .NET, fed one PerfFrame a frame while the counter is on. Everything
    /// here is O(1) a frame, except the light's check every half second (a walk over the last
    /// 10 s of frames into a histogram) and the window's reads, made only while it shows.
    ///
    /// The light (Light): red when the game does not keep up with the chosen speed (game seconds
    /// per real second more than 5 % below the speed over the last 5 s of unpaused play, frames
    /// over 1 s left out), yellow when it stutters (6 or more unpaused frames of 20 ms or more, at
    /// least twice the median and after a normal frame, in the last 10 s), green otherwise, and always while paused or
    /// outside a colony. Each state needs two checks in a row to start (one second) and red two,
    /// yellow four (two seconds) to end, so it does not flicker.
    public sealed class PerfTrack
    {
        public const double StutterMs = 20, FreezeMs = 100;
        const int Capacity = 1 << 16;

        // The frame ring: wall ms, game seconds, speed, the frame's end, flags.
        readonly float[] ms = new float[Capacity], gameDt = new float[Capacity], scale = new float[Capacity];
        readonly double[] end = new double[Capacity];
        readonly byte[] flags = new byte[Capacity];
        const byte FRunning = 1, FColony = 2, FLoading = 4, FStutter = 8, FFreeze = 16;
        long count;

        /// The light: 0 green, 1 yellow (stuttering), 2 red (not keeping up).
        public int Light { get; private set; }
        int redRun, yellowRun, clearRun, yellowClearRun;
        double nextCheck;
        /// The last check's numbers: game speed reached against the speed chosen (1 = keeps up;
        /// NaN without 3 s of unpaused play), the chosen speed, the median frame (ms) and the
        /// stutters in the last 10 s.
        public double KeepUp = double.NaN, Speed, MedianMs, Stutters10s;

        /// The badge's rate: frames over the last half second.
        public double Fps { get; private set; }
        double fpsMs; int fpsFrames;

        /// One second of play: frames, their time, the longest, stutters (20-100 ms), freezes
        /// (100 ms or more), stutters from waiting for the simulation, whether a colony ran.
        public struct Second
        {
            public int Frames, Stutters, Freezes, SimWaits;
            public float SumMs, MaxMs;
            public bool Colony;
            public PerfCause Worst;
        }

        readonly List<Second> seconds = new List<Second>(4096);
        Second current;
        double secondEnds = -1;
        /// The session's seconds (oldest first), one entry a second.
        public IReadOnlyList<Second> Seconds => seconds;

        /// A freeze (a frame of 100 ms or more) or a load, with what it was made of where known.
        public sealed class Freeze
        {
            public double At, Ms, GcMs, JitMs, PreviewMs;
            public PerfCause Cause;
        }

        readonly List<Freeze> freezes = new List<Freeze>();
        public IReadOnlyList<Freeze> Freezes => freezes;
        const int MaxFreezes = 500;

        /// Per cause for the session (frames of 20 ms or more): times, the worst, the total (ms).
        public readonly int[] CauseTimes = new int[9];
        public readonly double[] CauseWorst = new double[9], CauseTotal = new double[9];
        /// Real seconds of colony play this session (paused included), and the longest GC pause.
        public double ColonySeconds, LongestGcMs;

        /// Loading: start-up to the first main menu, the last colony load, the first frame after
        /// it, the first save after it (ms; NaN until known).
        public double StartToMenuMs = double.NaN, LoadMs = double.NaN, FirstFrameMs = double.NaN;
        public double FirstSaveMs => firstSave != null ? firstSave.Ms : double.NaN;
        /// The last save's pause (its frame and preview frames, ms; NaN before one).
        public double LastSaveMs => lastSave != null ? lastSave.Ms : double.NaN;
        double loadStarted = -1;
        bool wasColony, wasMenu, awaitFirstFrame, awaitFirstSave, menuSeen;
        Freeze lastSave, firstSave;
        int previewFramesLeft;

        /// Start-up time in seconds on the same clock as PerfFrame.Now (the first main menu is
        /// measured from it).
        public double StartedAt;

        public void Add(in PerfFrame f)
        {
            long i = count++ & (Capacity - 1);
            ms[i] = (float)f.Ms;
            gameDt[i] = (float)f.GameDt;
            scale[i] = (float)f.Scale;
            end[i] = f.Now;
            byte bits = 0;
            if (f.InColony) bits |= FColony;
            if (f.InColony && !f.Paused) bits |= FRunning;
            // The frame that arrives in a colony holds the end of its load.
            bool arriving = f.InColony && !wasColony;
            if (f.Loading || arriving) bits |= FLoading;
            if (f.GcMs > LongestGcMs && f.InColony && !f.Loading && !arriving) LongestGcMs = f.GcMs;

            fpsMs += f.Ms;
            fpsFrames++;
            if (fpsMs >= 500)
            {
                Fps = 1000.0 * fpsFrames / fpsMs;
                fpsMs = 0;
                fpsFrames = 0;
            }

            Transitions(f);
            var cause = PerfCause.None;
            if (f.Ms >= StutterMs || f.Saved)
            {
                cause = Classify(f, arriving);
                if (cause != PerfCause.None)
                {
                    bits |= f.Ms >= FreezeMs ? FFreeze : FStutter;
                    int c = (int)cause;
                    CauseTimes[c]++;
                    CauseTotal[c] += f.Ms;
                    if (f.Ms > CauseWorst[c]) CauseWorst[c] = f.Ms;
                }
            }
            flags[i] = bits;
            Seconds_(f, cause);
            if (f.Now >= nextCheck)
            {
                nextCheck = f.Now + 0.5;
                Check(f);
            }
        }

        /// Loads, the first main menu, the first unpaused frame and the first save after a load.
        void Transitions(in PerfFrame f)
        {
            if (f.InMenu && !menuSeen)
            {
                menuSeen = true;
                StartToMenuMs = (f.Now - StartedAt) * 1000;
            }
            bool between = !f.InColony && !f.InMenu;
            if ((wasColony || wasMenu) && between && loadStarted < 0) loadStarted = f.Now - f.Ms / 1000;
            if (f.InMenu) loadStarted = -1;
            if (f.InColony && !wasColony)
            {
                if (loadStarted >= 0) LoadMs = (f.Now - loadStarted) * 1000;
                loadStarted = -1;
                awaitFirstFrame = awaitFirstSave = true;
                FirstFrameMs = double.NaN;
                firstSave = null;
            }
            wasColony = f.InColony;
            wasMenu = f.InMenu;
        }

        /// A slow frame's cause (None for frames that count as no stutter: the menus).
        PerfCause Classify(in PerfFrame f, bool arriving)
        {
            if (f.Loading || arriving) return PerfCause.Load;
            if (!f.InColony) return PerfCause.None;
            if (awaitFirstFrame && f.GameDt > 0)
            {
                awaitFirstFrame = false;
                FirstFrameMs = f.Ms;
                AddFreeze(f, PerfCause.FirstFrame);
                return PerfCause.FirstFrame;
            }
            if (f.Saved)
            {
                var kind = f.AutoSave ? PerfCause.Autosave : PerfCause.Save;
                lastSave = AddFreeze(f, kind, always: true);
                previewFramesLeft = 120;
                if (awaitFirstSave)
                {
                    awaitFirstSave = false;
                    firstSave = lastSave;
                }
                return kind;
            }
            if (f.Preview && lastSave != null && previewFramesLeft > 0)
            {
                // The save's preview picture: part of the save's pause.
                lastSave.PreviewMs += f.Ms;
                lastSave.Ms += f.Ms;
                return lastSave.Cause;
            }
            PerfCause cause;
            if (f.Timelapse || f.Preview) cause = PerfCause.Timelapse;
            else if (f.GcMs >= 0.5 * Math.Max(1, f.Ms - Math.Max(MedianMs, 1))) cause = PerfCause.Gc;
            else if (f.ZeroSubTick && !f.Paused) cause = PerfCause.SimWait;
            else cause = PerfCause.Unknown;
            if (f.Ms >= FreezeMs) AddFreeze(f, cause);
            return cause;
        }

        Freeze AddFreeze(in PerfFrame f, PerfCause cause, bool always = false)
        {
            if (!always && f.Ms < FreezeMs) return null;
            var freeze = new Freeze { At = f.Now, Ms = f.Ms, GcMs = f.GcMs, JitMs = f.JitMs, Cause = cause };
            freezes.Add(freeze);
            if (freezes.Count > MaxFreezes) freezes.RemoveAt(0);
            return freeze;
        }

        void Seconds_(in PerfFrame f, PerfCause cause)
        {
            if (previewFramesLeft > 0) previewFramesLeft--;
            if (secondEnds < 0) secondEnds = f.Now + 1;
            while (f.Now >= secondEnds)
            {
                seconds.Add(current);
                current = default;
                secondEnds += 1;
                // A long frame spans several seconds: the empty ones are kept as such.
                if (f.Now - secondEnds > 3600) secondEnds = f.Now + 1;
            }
            current.Frames++;
            current.SumMs += (float)f.Ms;
            if (f.Ms > current.MaxMs)
            {
                current.MaxMs = (float)f.Ms;
                current.Worst = cause;
            }
            if (f.InColony)
            {
                current.Colony = true;
                ColonySeconds += f.Ms / 1000;
            }
            if (cause == PerfCause.None || cause == PerfCause.Load) return;
            if (f.Ms >= FreezeMs) current.Freezes++;
            else current.Stutters++;
            if (cause == PerfCause.SimWait) current.SimWaits++;
        }

        // The light's histogram: 0.25 ms bins up to 100 ms.
        readonly int[] bins = new int[401];

        void Check(in PerfFrame f)
        {
            double now = f.Now;
            double realMs = 0, gameS = 0, expectS = 0;
            Array.Clear(bins, 0, bins.Length);
            int n = 0;
            long last = count - 1, first = Math.Max(0, count - Capacity);
            for (long k = last; k >= first; k--)
            {
                long i = k & (Capacity - 1);
                double age = now - end[i];
                if (age > 10) break;
                if ((flags[i] & FColony) == 0 || (flags[i] & FLoading) != 0) continue;
                bins[Math.Min(400, (int)(ms[i] * 4))]++;
                n++;
                if (age <= 5 && (flags[i] & FRunning) != 0 && ms[i] <= 1000)
                {
                    realMs += ms[i];
                    gameS += gameDt[i];
                    expectS += ms[i] / 1000.0 * scale[i];
                }
            }
            MedianMs = n > 0 ? Quantile(bins, n, 0.5) / 4 : 0;
            int stutters = 0;
            double over = Math.Max(StutterMs, 2 * MedianMs);
            for (long k = last; k >= first; k--)
            {
                long i = k & (Capacity - 1);
                if (now - end[i] > 10) break;
                // A spike: the frame before was a normal one (a run of slow frames is a low rate, not stutters).
                if ((flags[i] & FRunning) != 0 && (flags[i] & FLoading) == 0 && ms[i] >= over && ms[i] < 1000 && k > first && ms[(k - 1) & (Capacity - 1)] < over) stutters++;
            }
            Stutters10s = stutters;
            KeepUp = realMs >= 3000 && expectS > 0 ? gameS / expectS : double.NaN;
            Speed = f.Scale;
            bool running = f.InColony && !f.Paused;
            bool red = running && KeepUp < 0.95;
            bool redClear = !running || double.IsNaN(KeepUp) || KeepUp > 0.97;
            bool yellow = running && stutters >= 6;
            bool yellowClear = !running || stutters <= 3;
            redRun = red ? redRun + 1 : 0;
            clearRun = redClear ? clearRun + 1 : 0;
            yellowRun = yellow ? yellowRun + 1 : 0;
            yellowClearRun = yellowClear ? yellowClearRun + 1 : 0;
            if (!running) Light = 0;
            else if (Light == 2)
            {
                if (clearRun >= 2) Light = yellowRun >= 1 ? 1 : 0;
            }
            else if (redRun >= 2) Light = 2;
            else if (Light == 1)
            {
                if (yellowClearRun >= 4) Light = 0;
            }
            else if (yellowRun >= 2) Light = 1;
        }

        /// The bin holding the q-th frame (bins of a histogram of n frames).
        static double Quantile(int[] bins, int n, double q)
        {
            long target = (long)Math.Ceiling(q * n);
            long seen = 0;
            for (int b = 0; b < bins.Length; b++)
            {
                seen += bins[b];
                if (seen >= target) return b + 0.5;
            }
            return bins.Length;
        }

        /// The frames of the last `window` seconds: their count, mean, median, 90th and 99th
        /// percentiles and the longest (ms), the stutters and freezes, and whether all of them
        /// ran unpaused in a colony. A histogram (0.1 ms bins up to 50 ms, 1 ms up to 1 s), so
        /// a 2-minute window costs a walk over its frames and no sort.
        public Summary Summarize(double window, double now)
        {
            var s = new Summary();
            Array.Clear(fine, 0, fine.Length);
            long last = count - 1, first = Math.Max(0, count - Capacity);
            double sum = 0;
            for (long k = last; k >= first; k--)
            {
                long i = k & (Capacity - 1);
                if (now - end[i] > window) break;
                if ((flags[i] & FLoading) != 0) continue;
                double m = ms[i];
                s.Frames++;
                sum += m;
                if (m > s.MaxMs) s.MaxMs = m;
                fine[Bin(m)]++;
                if ((flags[i] & FStutter) != 0) s.Stutters++;
                if ((flags[i] & FFreeze) != 0) s.Freezes++;
                if ((flags[i] & FRunning) != 0) s.Running++;
                s.Seconds += m / 1000;
            }
            if (s.Frames == 0) return s;
            s.MeanMs = sum / s.Frames;
            s.MedianMs = FineQuantile(s.Frames, 0.5);
            s.P90Ms = FineQuantile(s.Frames, 0.9);
            s.P99Ms = FineQuantile(s.Frames, 0.99);
            return s;
        }

        readonly int[] fine = new int[500 + 951];
        static int Bin(double m) => m < 50 ? (int)(m * 10) : Math.Min(500 + 950, 500 + (int)(m - 50));
        double FineQuantile(int n, double q)
        {
            long target = (long)Math.Ceiling(q * n), seen = 0;
            for (int b = 0; b < fine.Length; b++)
            {
                seen += fine[b];
                if (seen >= target) return b < 500 ? (b + 0.5) / 10 : 50 + (b - 500) + 0.5;
            }
            return 1000;
        }

        public struct Summary
        {
            public int Frames, Stutters, Freezes, Running;
            public double Seconds, MeanMs, MedianMs, P90Ms, P99Ms, MaxMs;
        }

        /// Per-pixel columns for the frame-time graph over the last `window` seconds: the longest
        /// frame in each column (ms; 0 for none) and its flags' worst (0 normal, 1 stutter, 2
        /// freeze). Seconds older than the frame ring come from the per-second history.
        public void Columns(double window, double now, float[] maxMs, byte[] kind)
        {
            int w = maxMs.Length;
            Array.Clear(maxMs, 0, w);
            Array.Clear(kind, 0, w);
            long last = count - 1, first = Math.Max(0, count - Capacity);
            double oldest = now;
            for (long k = last; k >= first; k--)
            {
                long i = k & (Capacity - 1);
                double age = now - end[i];
                if (age > window) break;
                oldest = end[i];
                int x = w - 1 - (int)(Math.Max(0, age) / window * w);
                if (x < 0) continue;
                if (ms[i] > maxMs[x]) maxMs[x] = ms[i];
                byte kd = (flags[i] & FFreeze) != 0 ? (byte)2 : (flags[i] & FStutter) != 0 ? (byte)1 : (byte)0;
                if (kd > kind[x]) kind[x] = kd;
            }
            // Older than the ring: whole seconds.
            for (int sIdx = seconds.Count - 1; sIdx >= 0; sIdx--)
            {
                double secEnd = secondEnds - (seconds.Count - sIdx);
                if (secEnd >= oldest) continue;
                double age = now - secEnd;
                if (age > window) break;
                int x = w - 1 - (int)(Math.Max(0, age) / window * w);
                if (x < 0) break;
                var sec = seconds[sIdx];
                if (sec.MaxMs > maxMs[x]) maxMs[x] = sec.MaxMs;
                byte kd = sec.Freezes > 0 ? (byte)2 : sec.Stutters > 0 ? (byte)1 : (byte)0;
                if (kd > kind[x]) kind[x] = kd;
            }
        }

        /// The last `n` whole seconds summed: stutters, stutters from waiting for the simulation,
        /// freezes, and the seconds a colony ran in.
        public (int Stutters, int SimWaits, int Freezes, int ColonySeconds) LastSeconds(int n)
        {
            int st = 0, sw = 0, fr = 0, col = 0;
            for (int i = seconds.Count - 1; i >= 0 && i >= seconds.Count - n; i--)
            {
                var s = seconds[i];
                st += s.Stutters;
                sw += s.SimWaits;
                fr += s.Freezes;
                if (s.Colony) col++;
            }
            return (st, sw, fr, col);
        }

        /// The newest frame's end (seconds, PerfFrame.Now's clock: the host's frame clock): the
        /// window's "now".
        public double Now => count > 0 ? end[(count - 1) & (Capacity - 1)] : 0;

        /// The time the per-second history's newest entry ended (seconds, PerfFrame.Now's clock).
        public double SecondsEnd => secondEnds - 1;
    }
}
