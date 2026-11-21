#nullable enable
using System.Collections.Generic;
using OniMoono.GameTweaks;
using Xunit;

namespace OniMoono.Tests
{
    /// The performance window's plain parts (PerfLayout.cs): where it opens and how it stays on
    /// the screen, what holds the frame rate back with its hysteresis, and clicks by control.
    public class PerfLayoutTests
    {
        // A 2560x1440 screen at the game's scale 1.33: about 1925 by 1083 overlay units.
        const float W = 1925, H = 1083, WinW = 880, WinH = 600;
        static readonly PerfBox TopBadge = new(W / 2 - 50, 3, 100, 22);
        // The management buttons across the top centre, the clock and speed controls at the top left.
        static readonly List<PerfBox> Bar = new() { new PerfBox(560, 0, 800, 46), new PerfBox(0, 0, 230, 120) };

        [Fact]
        public void OpensBelowTheBadgeAndClearOfTheGamesBar()
        {
            var b = PerfLayout.Place(W, H, WinW, WinH, TopBadge, Bar);
            Assert.False(b.Overlaps(TopBadge));
            foreach (var c in Bar) Assert.False(b.Overlaps(c));
            Assert.True(b.Y >= 46 + PerfLayout.Gap);
            Assert.True(b.Bottom <= H - PerfLayout.Margin);
            // Centred under the badge.
            Assert.InRange(b.X + WinW / 2, W / 2 - 1, W / 2 + 1);
        }

        [Fact]
        public void ABadgeLowOnTheScreenGetsTheWindowAboveIt()
        {
            var badge = new PerfBox(400, 900, 100, 22);
            var b = PerfLayout.Place(W, H, WinW, WinH, badge, Bar);
            Assert.False(b.Overlaps(badge));
            Assert.True(b.Bottom <= badge.Y);
            foreach (var c in Bar) Assert.False(b.Overlaps(c));
        }

        [Fact]
        public void WhenAboveAndBelowDoNotFitItOpensBesideTheBadge()
        {
            // A badge in the middle of a short screen (1920x1080 at UI scale 150 %: 1280 by 720).
            const float w = 1280, h = 720, winH = 640;
            var badge = new PerfBox(150, 340, 100, 22);
            var b = PerfLayout.Place(w, h, WinW, winH, badge, new List<PerfBox>());
            Assert.False(b.Overlaps(badge));
            Assert.True(b.X >= badge.Right || b.Right <= badge.X);
            Assert.True(b.X >= PerfLayout.Margin && b.Right <= w - PerfLayout.Margin + 0.01f);
        }

        [Fact]
        public void ClampKeepsItOnTheScreenAndTheSmallestCoverWins()
        {
            var c = PerfLayout.Clamp(new PerfBox(-100, 2000, WinW, WinH), W, H);
            Assert.Equal(PerfLayout.Margin, c.X);
            Assert.Equal(H - WinH - PerfLayout.Margin, c.Y);
            // Taller than the screen: its top stays on it.
            var tall = PerfLayout.Clamp(new PerfBox(10, 10, WinW, H + 50), W, H);
            Assert.Equal(PerfLayout.Margin, tall.Y);
            // Nowhere is clear: whatever it picks never covers the badge if it can avoid it.
            var cramped = PerfLayout.Place(1000, 700, WinW, 650, new PerfBox(450, 340, 100, 22), new List<PerfBox> { new PerfBox(0, 0, 1000, 60) });
            Assert.True(cramped.X >= 0 && cramped.Right <= 1000);
        }

        [Fact]
        public void HeldBackClassifiesTheEnginesWaits()
        {
            Assert.Equal(PerfHeld.None, PerfHeldDecision.Classify(false, 60, 60, 0.5, 0, 0, 0));
            // At the screen's rate, whatever it waits for is the screen.
            Assert.Equal(PerfHeld.Screen, PerfHeldDecision.Classify(true, 59.5, 60, 0.4, 0.1, 0, 0));
            Assert.Equal(PerfHeld.Screen, PerfHeldDecision.Classify(true, 59.5, 60, 0.05, 0.02, 0, 0));
            // Under the refresh rate with vsync: queued pictures are still the screen's pace.
            Assert.Equal(PerfHeld.Screen, PerfHeldDecision.Classify(true, 50, 60, 0, 0.3, 0, 0));
            Assert.Equal(PerfHeld.Graphics, PerfHeldDecision.Classify(true, 40, 60, 0.3, 0, 0, 0));
            Assert.Equal(PerfHeld.Processor, PerfHeldDecision.Classify(true, 40, 60, 0.02, 0, 0.01, 0));
            // No vsync: a frame cap's sleep or queue, the graphics card, else the processor.
            Assert.Equal(PerfHeld.Limit, PerfHeldDecision.Classify(true, 144, 0, 0, 0, 0, 0.4));
            Assert.Equal(PerfHeld.Graphics, PerfHeldDecision.Classify(true, 300, 0, 0.3, 0, 0, 0));
            Assert.Equal(PerfHeld.Processor, PerfHeldDecision.Classify(true, 170, 0, 0.01, 0, 0.02, 0));
        }

        [Fact]
        public void HeldBackNeedsThreeSecondsInARowToChange()
        {
            var d = new PerfHeldDecision();
            Assert.Equal(PerfHeld.Screen, d.Add(PerfHeld.Screen));
            // A flicker to Graphics, to None-free answers in between: the shown answer holds.
            var flicker = new[] { PerfHeld.Graphics, PerfHeld.Screen, PerfHeld.Graphics, PerfHeld.Graphics, PerfHeld.Screen, PerfHeld.Processor, PerfHeld.Graphics };
            foreach (var raw in flicker) Assert.Equal(PerfHeld.Screen, d.Add(raw));
            // Three in a row: it changes.
            d.Add(PerfHeld.Graphics);
            d.Add(PerfHeld.Graphics);
            Assert.Equal(PerfHeld.Graphics, d.Add(PerfHeld.Graphics));
            // Paused (None) shows at once, and the next answer starts fresh.
            Assert.Equal(PerfHeld.None, d.Add(PerfHeld.None));
            Assert.Equal(PerfHeld.Screen, d.Add(PerfHeld.Screen));
        }

        [Fact]
        public void APressAndItsReleaseAreOneControlEvenIfTheLayoutMoved()
        {
            var clicks = new PerfClicks();
            // Pressed on "scan" at y 500..526; the layout moves it 16 down before the release at y 520.
            clicks.Press("scan", new PerfBox(700, 500, 120, 26));
            Assert.True(clicks.Release(760, 520, new PerfBox(700, 516, 120, 26)));
            clicks.Begin();
            // The control that took the press's place does not take its click.
            Assert.False(clicks.Take("copy"));
            Assert.True(clicks.Take("scan"));
            Assert.False(clicks.End());
            // Released inside where it is now (but not where it was) also counts.
            clicks.Press("row2", new PerfBox(10, 100, 480, 25));
            Assert.True(clicks.Release(20, 140, new PerfBox(10, 130, 480, 25)));
            clicks.Begin();
            Assert.True(clicks.Take("row2"));
            // Released away from it: no click.
            clicks.Press("tab1", new PerfBox(0, 0, 80, 30));
            Assert.False(clicks.Release(300, 300, new PerfBox(0, 0, 80, 30)));
            Assert.False(clicks.Waiting);
        }

        [Fact]
        public void AClickWhoseControlIsGoneIsDropped()
        {
            var clicks = new PerfClicks();
            clicks.Press("freeze12.5", new PerfBox(10, 300, 480, 24));
            Assert.True(clicks.Release(50, 310, new PerfBox(10, 300, 480, 24)));
            // The next build has no such row (the list changed): dropped at its end, never taken later.
            clicks.Begin();
            Assert.False(clicks.Take("freeze13.0"));
            Assert.True(clicks.End());
            clicks.Begin();
            Assert.False(clicks.Take("freeze12.5"));
            // A control gone before the release: no click at all.
            clicks.Press("copy", new PerfBox(0, 0, 50, 20));
            Assert.False(clicks.Release(10, 10, null));
            Assert.False(clicks.Waiting);
        }
    }
}
