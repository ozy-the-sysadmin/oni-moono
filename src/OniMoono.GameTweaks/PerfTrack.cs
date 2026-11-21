using System;
using System.Collections.Generic;

namespace OniMoono.GameTweaks
{
    /// Why a slow frame was slow, as the engine or the game reports it (PerfReads). Unknown when
    /// nothing reported a cause: the monitor never guesses.
    public enum PerfCause : byte
    {
        None, Load, FirstFrame, Autosave, Save, Timelapse, Gc, SimWait, Unknown,
        /// The runtime compiling code on its first use (the host's JIT time for the frame).
        Jit,
        /// The player switched to another asteroid (ClusterManager's active world changed).
        World,
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
        /// The simulation sub-ticks the frame advanced, and whether one of them was the 200 ms
        /// step (Game.simSubTick passed 0); the active asteroid changed in the frame.
        public int SubTicks;
        public bool Step200, WorldSwitch;
        /// A save finished in the frame (the game's "Saved to" log line) and was an autosave; the
        /// timelapse or a save's preview picture was being taken.
        public bool Saved, AutoSave, Timelapse, Preview;
    }


    /// The frame rate counter's and the performance window's memory (OniMoono.GameTweaks.
    /// PerfOverlay): plain .NET, fed one PerfFrame a frame while the counter is on. Everything
    /// here is O(1) a frame, except the light's check every half second (a walk over the last
    /// 10 s of frames into a histogram) and the window's reads, made only while it shows.
    ///
    /// One definition of a stutter serves everything (the light, the Colony tab's verdict, its
    /// Right now line and its advice, the Hitches tab): a frame of colony play (unpaused, not
    /// loading) of 20 ms or more and at least twice the median frame, under 100 ms, that follows
    /// a normal frame (a run of slow frames is a low frame rate, not stutters). A freeze is any
    /// colony frame of 100 ms or more outside loading; only those of play count in the rates.
    /// Rates are a minute of play: the last 60 s, per second of unpaused play in them (Rate).
    ///
    /// Loading is the load itself, the frame that arrives in the colony and the frames after it
    /// until the first one under 50 ms (or the first with game time, or 10 s): the game is still
    /// finishing the load then. The first frames after loading (the first with game time and the
    /// first that runs the 200 ms simulation step) are the Loading panel's too. Neither ever
    /// counts as play (rates, summaries, time lost).
    ///
    /// The light (Light): red when the game does not keep up with the chosen speed (game seconds
    /// per real second more than 5 % below the speed over the last 5 s of unpaused play, frames
    /// over 1 s left out), yellow with 6 or more stutters in the last 10 s, green otherwise, and
    /// always while paused or outside a colony. Each state needs two checks in a row to start
    /// (one second) and red two, yellow four (two seconds) to end, so it does not flicker.
    public sealed class PerfTrack
    {
        public const double StutterMs = 20, FreezeMs = 100;
        /// The settling after a load ends at the first frame under this (ms).
        public const double SettledMs = 50;
        public const int CauseCount = 11;
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

        /// With vsync, the time a picture is shown (the display's refresh period times the swap
        /// interval, ms) while the engine knows it; 0 without vsync or an unknown period. Set each
        /// frame by the overlay (OniMoono.Engine.FramePacing.PacedPeriodMs).
        public double RefreshMs;

        /// The badge's rate: frames over the last half second.
        public double Fps { get; private set; }
        double fpsMs; int fpsFrames;

        /// One second: frames, their time, the longest and its cause; play's stutters, the colony's
        /// freezes and play's, play's stutters from waiting for the simulation and of no known cause;
        /// play's time in it (ms); whether a colony ran.
        public struct Second
        {
            public int Frames, Stutters, Freezes, PlayFreezes, SimWaits, Unknown;
            public float SumMs, MaxMs, PlayMs;
            public bool Colony;
            public PerfCause Worst;
        }

        readonly List<Second> seconds = new List<Second>(4096);
        Second current;
        double secondEnds = -1;
        /// The session's seconds (oldest first), one entry a second.
        public IReadOnlyList<Second> Seconds => seconds;

        /// A freeze (a frame of 100 ms or more) or a save, with what it was made of where known.
        public sealed class Freeze
        {
            public double At, Ms, GcMs, JitMs, PreviewMs;
            public PerfCause Cause;
            /// The game was paused (not play: listed, not in the rates).
            public bool Paused;
        }

        readonly List<Freeze> freezes = new List<Freeze>();
        public IReadOnlyList<Freeze> Freezes => freezes;
        const int MaxFreezes = 500;

        /// Per cause for the session (stutters, freezes, saves and loading frames): times, the
        /// worst, the total (ms).
        public readonly int[] CauseTimes = new int[CauseCount];
        public readonly double[] CauseWorst = new double[CauseCount], CauseTotal = new double[CauseCount];
        /// Real seconds of colony play this session (paused included) and of unpaused play, the
        /// longest GC pause, and the time of play's freezes and stutters (ms).
        public double ColonySeconds, PlaySeconds, LongestGcMs, PlayFreezeMs, PlayStutterMs;

        /// Loading: start-up to the first main menu, the last colony load (until it settled), the
        /// first frame with game time after it, the first save after it (ms; NaN until known).
        public double StartToMenuMs = double.NaN, LoadMs = double.NaN, FirstFrameMs = double.NaN;
        public double FirstSaveMs => firstSave != null ? firstSave.Ms : double.NaN;
        /// The last save's pause (its frame and preview frames, ms; NaN before one).
        public double LastSaveMs => lastSave != null ? lastSave.Ms : double.NaN;
        double loadStarted = -1, settleUntil, firstStepUntil;
        bool wasColony, wasMenu, awaitFirstFrame, awaitFirstStep, awaitFirstSave, menuSeen, settling;
        /// The last load's frames in the colony: the arriving frame and those until it settled
        /// (the copy report: what the end of the load was made of).
        public readonly List<Slow> LoadFrames = new List<Slow>();
        /// A load is settling: the frames now are still the load's.
        public bool Settling => settling;

        /// The engine's phase sums (PerfOverlay.Take's layout) for the load's frame that ended at
        /// `at`, kept with it.
        public void LoadPhases(double at, double[] take)
        {
            int last = LoadFrames.Count - 1;
            if (last < 0 || LoadFrames[last].At != at || take == null || take.Length < 12) return;
            var s = LoadFrames[last];
            s.Phases = new double[10];
            Array.Copy(take, 2, s.Phases, 0, 10);
            LoadFrames[last] = s;
        }
        Freeze lastSave, firstSave;
        int previewFramesLeft;
        double prevMs;

        /// The newest slow frames (20 ms or more, and saves) with what the engine and the game
        /// reported for them: the copy report's last lines, and the test runs' check of causes.
        public struct Slow
        {
            public double At, Ms, GcMs, JitMs, GameDt;
            public bool Paused, Colony, Loading, ZeroSubTick, Step200, World, Stutter, Freeze;
            public int SubTicks;
            public PerfCause Cause;
            /// A load's frame: the engine's phases (ms; render, GUI, end of frame, present, early
            /// and fixed (pending Starts included), update, late, post-late, particles, outside the
            /// loop), when the frame window timed it.
            public double[] Phases;
        }

        const int SlowCapacity = 256;
        readonly Slow[] slow = new Slow[SlowCapacity];
        long slowCount;
        /// The newest slow frames, oldest first.
        public IEnumerable<Slow> SlowFrames()
        {
            for (long k = Math.Max(0, slowCount - SlowCapacity); k < slowCount; k++) yield return slow[k % SlowCapacity];
        }

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

            fpsMs += f.Ms;
            fpsFrames++;
            if (fpsMs >= 500)
            {
                Fps = 1000.0 * fpsFrames / fpsMs;
                fpsMs = 0;
                fpsFrames = 0;
            }

            // The frame that arrives in a colony holds the end of its load; the frames after it
            // until the game settles are the load's too.
            bool arriving = f.InColony && !wasColony;
            Transitions(f, arriving);
            bool loading = f.Loading || arriving || settling;
            // The first frame with game time after a load: the Loading panel's, not play.
            // So is the first that runs the 200 ms simulation step (the sim's first full step after
            // the load), when it comes within 5 s.
            bool firstFrame = false;
            if (awaitFirstFrame && f.InColony && !loading && f.GameDt > 0)
            {
                awaitFirstFrame = false;
                firstFrame = true;
                FirstFrameMs = f.Ms;
                awaitFirstStep = !f.Step200;
                firstStepUntil = f.Now + 5;
            }
            else if (awaitFirstStep && f.InColony && !loading && !f.Paused && f.Step200 && f.Now <= firstStepUntil)
            {
                awaitFirstStep = false;
                firstFrame = true;
                FirstFrameMs += f.Ms;
            }
            if (loading && f.InColony && !f.Loading)
            {
                if (arriving) LoadFrames.Clear();
                if (LoadFrames.Count < 32) LoadFrames.Add(new Slow { At = f.Now, Ms = f.Ms, GcMs = f.GcMs, JitMs = f.JitMs, Paused = f.Paused, Colony = true, Loading = true, Cause = PerfCause.Load });
            }
            bool notPlay = loading || firstFrame;
            bool play = f.InColony && !f.Paused && !notPlay;
            byte bits = 0;
            if (f.InColony) bits |= FColony;
            if (f.InColony && !f.Paused) bits |= FRunning;
            if (notPlay) bits |= FLoading;
            if (f.GcMs > LongestGcMs && f.InColony && !notPlay) LongestGcMs = f.GcMs;

            double over = StutterThreshold();
            bool stutter = play && f.Ms >= over && f.Ms < FreezeMs && prevMs < over;
            bool freeze = f.InColony && !notPlay && f.Ms >= FreezeMs;
            prevMs = f.Ms;
            var cause = PerfCause.None;
            if (f.Ms >= StutterMs || f.Saved)
            {
                cause = Classify(f, loading, firstFrame, freeze);
                // Loading and saves are always named; other slow frames only when they are a
                // stutter or a freeze (a run of slow frames is a low rate; the menus are not play).
                bool named = cause == PerfCause.Load || cause == PerfCause.FirstFrame || cause == PerfCause.Autosave || cause == PerfCause.Save || stutter || freeze;
                if (!named) cause = PerfCause.None;
                if (cause != PerfCause.None)
                {
                    int c = (int)cause;
                    CauseTimes[c]++;
                    CauseTotal[c] += f.Ms;
                    if (f.Ms > CauseWorst[c]) CauseWorst[c] = f.Ms;
                }
                if (f.Ms >= StutterMs || f.Saved) LogSlow(f, cause, loading, stutter, freeze);
            }
            if (stutter) bits |= FStutter;
            if (freeze) bits |= FFreeze;
            flags[i] = bits;
            if (play)
            {
                PlaySeconds += f.Ms / 1000;
                if (freeze) PlayFreezeMs += f.Ms;
                else if (stutter) PlayStutterMs += f.Ms;
            }
            Seconds_(f, cause, play, stutter, freeze);
            if (f.Now >= nextCheck)
            {
                nextCheck = f.Now + 0.5;
                Check(f);
            }
        }

        /// A stutter's length: 20 ms or twice the median frame. With vsync and frames at the
        /// refresh rate, a frame that misses its refresh lasts two refreshes, which twice the median
        /// (itself a refresh, in 0.25 ms bins) would put on either side of the line at random: any
        /// frame over 1.75 refreshes counts then (a picture shown twice). Frames at the refresh rate
        /// themselves never count, so vsync holding the rate is no stutter.
        public double StutterThreshold()
        {
            double over = Math.Max(StutterMs, 2 * MedianMs);
            if (RefreshMs > 0 && MedianMs > 0 && Math.Abs(MedianMs - RefreshMs) <= 0.1 * RefreshMs) over = Math.Max(StutterMs, 1.75 * RefreshMs);
            return over;
        }

        void LogSlow(in PerfFrame f, PerfCause cause, bool loading, bool stutter, bool freeze)
        {
            slow[slowCount++ % SlowCapacity] = new Slow
            {
                At = f.Now, Ms = f.Ms, GcMs = f.GcMs, JitMs = f.JitMs, GameDt = f.GameDt, Paused = f.Paused, Colony = f.InColony,
                Loading = loading, ZeroSubTick = f.ZeroSubTick, Step200 = f.Step200, World = f.WorldSwitch, Stutter = stutter,
                Freeze = freeze, SubTicks = f.SubTicks, Cause = cause,
            };
        }

        /// Loads (until the colony settles), the first main menu, the first frame with game time
        /// and the first save after a load.
        void Transitions(in PerfFrame f, bool arriving)
        {
            if (f.InMenu && !menuSeen)
            {
                menuSeen = true;
                StartToMenuMs = (f.Now - StartedAt) * 1000;
            }
            bool between = !f.InColony && !f.InMenu;
            if ((wasColony || wasMenu) && between && loadStarted < 0) loadStarted = f.Now - f.Ms / 1000;
            if (f.InMenu)
            {
                loadStarted = -1;
                settling = false;
            }
            if (arriving)
            {
                if (loadStarted >= 0) LoadMs = (f.Now - loadStarted) * 1000;
                awaitFirstFrame = awaitFirstSave = true;
                awaitFirstStep = false;
                FirstFrameMs = double.NaN;
                firstSave = null;
                settling = true;
                settleUntil = f.Now + 10;
            }
            else if (settling)
            {
                // Settled: the first frame under 50 ms, the first with game time, or 10 s on.
                if (!f.InColony || f.Ms < SettledMs || f.GameDt > 0 || f.Now > settleUntil) settling = false;
                else if (loadStarted >= 0) LoadMs = (f.Now - loadStarted) * 1000;
            }
            if (!settling && !arriving && f.InColony) loadStarted = -1;
            wasColony = f.InColony;
            wasMenu = f.InMenu;
        }

        /// A slow frame's cause (None for frames that count as no stutter: the menus).
        PerfCause Classify(in PerfFrame f, bool loading, bool firstFrame, bool freeze)
        {
            if (loading) return PerfCause.Load;
            if (!f.InColony) return PerfCause.None;
            if (firstFrame)
            {
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
            double excess = Math.Max(1, f.Ms - Math.Max(MedianMs, 1));
            PerfCause cause;
            if (f.Timelapse || f.Preview) cause = PerfCause.Timelapse;
            else if (f.GcMs >= 0.5 * excess) cause = PerfCause.Gc;
            else if (f.JitMs >= 0.5 * excess) cause = PerfCause.Jit;
            else if (f.WorldSwitch) cause = PerfCause.World;
            else if (f.ZeroSubTick && !f.Paused) cause = PerfCause.SimWait;
            else cause = PerfCause.Unknown;
            if (freeze) AddFreeze(f, cause);
            return cause;
        }

        Freeze AddFreeze(in PerfFrame f, PerfCause cause, bool always = false)
        {
            if (!always && f.Ms < FreezeMs) return null;
            var freeze = new Freeze { At = f.Now, Ms = f.Ms, GcMs = f.GcMs, JitMs = f.JitMs, Cause = cause, Paused = f.Paused };
            freezes.Add(freeze);
            if (freezes.Count > MaxFreezes) freezes.RemoveAt(0);
            return freeze;
        }

        void Seconds_(in PerfFrame f, PerfCause cause, bool play, bool stutter, bool freeze)
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
            if (freeze) current.Freezes++;
            if (!play) return;
            current.PlayMs += (float)f.Ms;
            if (stutter) current.Stutters++;
            if (freeze) current.PlayFreezes++;
            // Of the stutters (so they compare with Stutters): from the simulation wait, unknown.
            if (!stutter) return;
            if (cause == PerfCause.SimWait) current.SimWaits++;
            else if (cause == PerfCause.Unknown) current.Unknown++;
        }

        // The light's histogram: 0.25 ms bins up to 100 ms.
        readonly int[] bins = new int[401];

        void Check(in PerfFrame f)
        {
            double now = f.Now;
            double realMs = 0, gameS = 0, expectS = 0;
            Array.Clear(bins, 0, bins.Length);
            int n = 0, stutters = 0;
            long last = count - 1, first = Math.Max(0, count - Capacity);
            for (long k = last; k >= first; k--)
            {
                long i = k & (Capacity - 1);
                double age = now - end[i];
                if (age > 10) break;
                if ((flags[i] & FStutter) != 0) stutters++;
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

        /// The frames of the last `window` seconds outside loading (with `play`, only those of
        /// unpaused colony play): their count, mean, median, 90th and 99th percentiles and the
        /// longest (ms), the stutters and freezes. A histogram (0.1 ms bins up to 50 ms, 1 ms up
        /// to 1 s; the longest kept exactly), so a 2-minute window costs a walk and no sort.
        public Summary Summarize(double window, double now, bool play = false)
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
                if (play && (flags[i] & FRunning) == 0) continue;
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

        /// Play over a span of whole seconds: its stutters, its freezes, the stutters from waiting
        /// for the simulation and those of no known cause, and its seconds of unpaused
        /// play. PerMinute scales a count to a minute of play.
        public struct Rate
        {
            public int Stutters, Freezes, SimWaits, Unknown;
            public double PlaySeconds;
            public int PerMinute(int n) => PlaySeconds >= 1 ? (int)Math.Round(n * 60.0 / PlaySeconds) : 0;
        }

        /// The last `n` whole seconds (a minute by default), none from before RestartMinute: the
        /// one source of every "a minute".
        public Rate LastMinute(int n = 60)
        {
            var r = new Rate();
            double playMs = 0;
            for (int i = seconds.Count - 1; i >= 0 && i >= seconds.Count - n && i >= rateFrom; i--)
            {
                var s = seconds[i];
                r.Stutters += s.Stutters;
                r.Freezes += s.PlayFreezes;
                r.SimWaits += s.SimWaits;
                r.Unknown += s.Unknown;
                playMs += s.PlayMs;
            }
            r.PlaySeconds = playMs / 1000;
            return r;
        }

        int rateFrom;
        /// When the rates last started again (RestartMinute; Now's clock), NaN before.
        public double RestartedAt = double.NaN;

        /// The Colony tab's Scan again: every "a minute" (the verdict's stutters, Right now, the
        /// advice, the Hitches tab's stutters box) counts from the next whole second, so a change
        /// the player just made is measured alone. The session's totals, the light and the graph
        /// are not reset.
        public void RestartMinute()
        {
            rateFrom = seconds.Count + 1;
            RestartedAt = Now;
        }

        /// The newest frame's end (seconds, PerfFrame.Now's clock: the host's frame clock): the
        /// window's "now".
        public double Now => count > 0 ? end[(count - 1) & (Capacity - 1)] : 0;

        /// The time the per-second history's newest entry ended (seconds, PerfFrame.Now's clock).
        public double SecondsEnd => secondEnds - 1;
    }
}
