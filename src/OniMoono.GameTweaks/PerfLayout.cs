using System;
using System.Collections.Generic;

namespace OniMoono.GameTweaks
{
    /// A rectangle in the overlay's units, from the top left, y downwards.
    public struct PerfBox
    {
        public float X, Y, W, H;

        public PerfBox(float x, float y, float w, float h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        public float Right => X + W;
        public float Bottom => Y + H;
        public bool Contains(float x, float y) => x >= X && x <= Right && y >= Y && y <= Bottom;
        public bool Overlaps(PerfBox o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

        /// The area two rectangles share (0 when apart).
        public float Overlap(PerfBox o)
        {
            float w = Math.Min(Right, o.Right) - Math.Max(X, o.X), h = Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y);
            return w > 0 && h > 0 ? w * h : 0;
        }

        public override string ToString() => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F0},{1:F0},{2:F0}x{3:F0}", X, Y, W, H);
    }

    /// Where the performance window opens (plain .NET, PerfLayoutTests): clear of its badge and of
    /// the game's own top bar (the management buttons, the cycle clock and the speed controls,
    /// given as rectangles), inside the screen. It tries below the badge, above it (when the badge
    /// is in the lower half), beside it on the right and on the left, each pushed down below
    /// any of the game's rectangles in its way, and takes the first that covers neither the
    /// badge nor the game's bar; when none does (a small screen at a large UI scale), the one
    /// covering the least.
    public static class PerfLayout
    {
        public const float Margin = 8f, Gap = 8f;

        /// The top left inside the screen (`canvas`), `Margin` from its edges.
        public static PerfBox Clamp(PerfBox box, float canvasW, float canvasH, float margin = Margin)
        {
            box.X = Math.Min(Math.Max(box.X, margin), Math.Max(margin, canvasW - box.W - margin));
            box.Y = Math.Min(Math.Max(box.Y, margin), Math.Max(margin, canvasH - box.H - margin));
            return box;
        }

        public static PerfBox Place(float canvasW, float canvasH, float w, float h, PerfBox badge, IList<PerfBox> clear)
        {
            clear ??= Array.Empty<PerfBox>();
            var tries = new List<PerfBox>
            {
                new PerfBox(badge.X + badge.W / 2 - w / 2, badge.Bottom + Gap, w, h),
            };
            if (badge.Y + badge.H / 2 > canvasH / 2) tries.Add(new PerfBox(badge.X + badge.W / 2 - w / 2, badge.Y - Gap - h, w, h));
            float top = Margin;
            tries.Add(new PerfBox(badge.Right + Gap, Math.Min(badge.Y, top), w, h));
            tries.Add(new PerfBox(badge.X - Gap - w, Math.Min(badge.Y, top), w, h));
            PerfBox best = default;
            float bestScore = float.MaxValue;
            foreach (var t in tries)
            {
                var b = PushDown(Clamp(t, canvasW, canvasH), canvasW, canvasH, badge, clear);
                float score = b.Overlap(badge) * 4;
                foreach (var c in clear) score += b.Overlap(c);
                if (score <= 0) return b;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = b;
                }
            }
            return best;
        }

        /// Moves `b` down below each of the game's rectangles (and the badge) it covers, while it
        /// still fits on the screen.
        static PerfBox PushDown(PerfBox b, float canvasW, float canvasH, PerfBox badge, IList<PerfBox> clear)
        {
            for (int guard = 0; guard < 16; guard++)
            {
                float below = -1;
                foreach (var c in clear)
                    if (b.Overlaps(c)) below = Math.Max(below, c.Bottom);
                if (below < 0) return b;
                var moved = b;
                moved.Y = below + Gap;
                if (moved.Overlaps(badge)) moved.Y = Math.Max(moved.Y, badge.Bottom + Gap);
                if (moved.Bottom > canvasH - Margin) return b;
                b = moved;
            }
            return b;
        }
    }

    /// What holds the frame rate back, decided once a second from the engine's waits over the
    /// last seconds (Classify), kept until another answer has come several times in a row
    /// (Settle), so that it does not flick between answers near a threshold. The verdict's
    /// sentence about the screen and the "Held back by" line read the same Settled value.
    public enum PerfHeld : byte { None, Screen, Limit, Graphics, Processor }

    public sealed class PerfHeldDecision
    {
        /// Seconds an answer must come in a row before it replaces the shown one.
        public const int Settle = 3;
        public PerfHeld Shown { get; private set; }
        PerfHeld candidate;
        int run;

        /// The engine's numbers over the last seconds: frames a second, the refresh rate with vsync
        /// (0: vsync off or not known), the shares of the wall time the main thread spent waiting
        /// for the device's next frame, for queued pictures, for device results, and outside the
        /// loop (the frame cap's sleep). Not running (paused, no colony, no numbers): None.
        public static PerfHeld Classify(bool running, double fps, double hz, double deviceFrame, double queued, double deviceResults, double outside)
        {
            if (!running || fps <= 0) return PerfHeld.None;
            if (hz > 0)
            {
                // At the screen's rate, whatever the main thread waits for is the screen's pace.
                if (fps >= 0.95 * hz) return PerfHeld.Screen;
                if (queued > 0.2) return PerfHeld.Screen;
            }
            else if (queued > 0.2) return PerfHeld.Limit;
            if (outside > 0.2) return PerfHeld.Limit;
            if (deviceFrame + queued + deviceResults > 0.25) return PerfHeld.Graphics;
            return PerfHeld.Processor;
        }

        /// One second's answer; returns the shown one. None (not running) is shown at once.
        public PerfHeld Add(PerfHeld raw)
        {
            if (raw == PerfHeld.None || Shown == PerfHeld.None)
            {
                Shown = raw;
                candidate = raw;
                run = 0;
                return Shown;
            }
            if (raw == Shown)
            {
                run = 0;
                return Shown;
            }
            if (raw == candidate) run++;
            else
            {
                candidate = raw;
                run = 1;
            }
            if (run >= Settle)
            {
                Shown = raw;
                run = 0;
            }
            return Shown;
        }

        public void Reset()
        {
            Shown = candidate = PerfHeld.None;
            run = 0;
        }
    }

    /// The window's clicks by what was clicked, not by where the widget happens to be in the
    /// build: a press notes the control's id (a stable name such as "tab1", "row2", "copy") and
    /// its rectangle; the release counts as a click on that control when the pointer is inside
    /// the rectangle it had at the press or the one it has now (the layout may have moved in
    /// between); the next build hands the click to the control with that id, and a click whose
    /// control is not in that build is dropped.
    public sealed class PerfClicks
    {
        public string PressedId { get; private set; }
        PerfBox pressedBox;
        /// The click waiting for its control (null for none), where it was released (overlay units).
        public string ClickId { get; private set; }
        public float ClickX, ClickY;
        bool seen;

        public bool Pressing => PressedId != null;

        public void Press(string id, PerfBox box)
        {
            PressedId = id;
            pressedBox = box;
        }

        /// The release at (x, y); `now`: the pressed control's rectangle in the newest build (null
        /// when it is gone). True when it counts as a click.
        public bool Release(float x, float y, PerfBox? now)
        {
            string id = PressedId;
            PressedId = null;
            if (id == null || now == null) return false;
            if (!pressedBox.Contains(x, y) && !now.Value.Contains(x, y)) return false;
            ClickId = id;
            ClickX = x;
            ClickY = y;
            return true;
        }

        public void Cancel() => PressedId = null;

        /// A build begins.
        public void Begin() => seen = false;

        /// A control with `id` is built: true (once) when the waiting click is its.
        public bool Take(string id)
        {
            if (ClickId == null || !string.Equals(ClickId, id, StringComparison.Ordinal)) return false;
            ClickId = null;
            seen = true;
            return true;
        }

        /// A build ends: a click no control of it took is dropped. True when one was dropped.
        public bool End()
        {
            if (ClickId == null || seen) return false;
            ClickId = null;
            return true;
        }

        public bool Waiting => ClickId != null;
    }
}
