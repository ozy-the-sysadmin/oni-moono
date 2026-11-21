using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniMoono.GameTweaks
{
    /// The performance window (the approved mock's layout, wording and colours): a game-styled
    /// window, dragged by its title bar, with a close button and three tabs. Colony (for
    /// players, in frames a second): a verdict, the measured advice (PerfAdvice), what runs now
    /// and the colony's size. Frame (for developers, in ms and %): the frame's parts, each
    /// opening to what is inside it, the spread of frame times, threads (with the vertical sync
    /// switch), memory, tweaks. Hitches: the frame-time graph with its stutters and freezes, the
    /// freezes with their causes (PerfTrack: only where the engine or the game reports one) and
    /// the loading times. With no colony loaded it shows what makes sense there: the frame rate,
    /// the Frame tab and the loading times.
    ///
    /// It refreshes once a second while it shows; closed, it measures nothing. The Colony and
    /// Frame tabs keep the engine's frame window open (PerfOverlay.Timing); Frame's "Game update"
    /// row, while open, the clock around the game's state machine updates (PerfBuckets).
    ///
    /// Its layout does not jump: the verdict always has room for two lines, changing values
    /// have fixed columns, the freeze list keeps its five rows, and while nothing is clicked
    /// the window does not get shorter (its footer stays where it was). What holds the frame
    /// rate back is one decision a second with hysteresis (PerfHeldDecision), read by the
    /// verdict, the Right now line and the Frame tab alike.
    internal sealed class PerfWindow
    {
        static string Ui(string name) => PerfOverlay.Ui(name);
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        const float Width = 880f, TitleHeight = 32f, TabsHeight = 33f, Pad = 16f, LeftWidth = 492f, Gap = 16f, RightWidth = 340f;
        const float RightX = Pad + LeftWidth + Gap;
        /// The freeze list's rows, always kept (empty ones blank).
        const int FreezeRows = 5;

        readonly PerfOverlay overlay;
        readonly RectTransform rect, body;
        readonly Image shadow;
        readonly LocText title;
        readonly PerfUi ui, chrome;
        float height = 400f;
        Vector2 topLeft;
        bool open, hidden, dirty, autoPlaced;
        /// The height it had when it was last placed (placed again only when it changed since).
        float placedHeight;
        float elapsed;
        int tab;  // 0 colony, 1 frame, 2 hitches
        int expanded = -1;  // the Frame tab's open row
        int range;  // Hitches: 2 min, 10 min, the session
        PerfTrack.Freeze selected;
        string copied;
        float copiedFor;
        /// The footer's lowest place since the last click: the window does not shrink by itself.
        float footFloor;
        readonly PerfHeldDecision held = new PerfHeldDecision();
        double busyShare = double.NaN, heldHz;

        internal bool Open => open;
        internal string TabName => tab == 0 ? "colony" : tab == 1 ? "frame" : "hitches";
        internal string RectText => string.Format(C, "{0:F0},{1:F0},{2:F0}x{3:F0}", topLeft.x, topLeft.y, Width, height);
        internal Vector2 TopLeft => topLeft;
        /// The window's rectangle in the overlay's units.
        internal PerfBox Box => new PerfBox(topLeft.x, topLeft.y, Width, height);
        internal string HeldName => held.Shown.ToString();

        internal PerfWindow(PerfOverlay overlay, Transform canvas)
        {
            this.overlay = overlay;
            var go = new GameObject("PerfWindow", typeof(RectTransform));
            go.transform.SetParent(canvas, false);
            rect = (RectTransform)go.transform;
            shadow = PerfUi.NewImage(go.transform, "Shadow", PerfUi.Round6, new Color(0, 0, 0, 0.45f));
            var back = PerfUi.NewImage(go.transform, "Back", PerfUi.Round6, PerfUi.Panel);
            back.raycastTarget = true;  // clicks on the window stay on it
            Stretch(back.rectTransform, 0, 0);
            var frame = PerfUi.NewImage(go.transform, "Frame", PerfUi.Round4Ring, new Color32(0x23, 0x26, 0x32, 0x99));
            Stretch(frame.rectTransform, 0, 0);
            var chromeRoot = new GameObject("Chrome", typeof(RectTransform));
            chromeRoot.transform.SetParent(go.transform, false);
            var cr = (RectTransform)chromeRoot.transform;
            cr.anchorMin = cr.anchorMax = cr.pivot = new Vector2(0, 1);
            chrome = new PerfUi(cr);
            var bar = PerfUi.NewImage(go.transform, "TitleBar", PerfUi.Gradient, Color.white);
            PerfUi.Place(bar.rectTransform, 2, 2, Width - 4, TitleHeight - 2);
            bar.raycastTarget = true;
            bar.gameObject.AddComponent<PerfPress>().Pressed = () => overlay.BeginPress(rect, null);
            // Centred in the bar (2 to TitleHeight from the top): TMP's default is the top of its
            // rectangle, where the title face's ascender reached above the bar.
            title = PerfUi.NewText(go.transform, "Title");
            PerfUi.Place(title.rectTransform, 14, 2, 400, TitleHeight - 2);
            title.alignment = TextAlignmentOptions.MidlineLeft;
            title.characterSpacing = 4f;
            cr.SetAsLastSibling();
            var content = new GameObject("Body", typeof(RectTransform));
            content.transform.SetParent(go.transform, false);
            body = (RectTransform)content.transform;
            PerfUi.Place(body, 0, TitleHeight + TabsHeight, Width, 100);
            ui = new PerfUi(body);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            go.SetActive(false);
        }

        static void Stretch(RectTransform r, float inset, float top)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(inset, inset);
            r.offsetMax = new Vector2(-inset, -top);
        }

        /// Opens on Colony where the player last dragged it, else clear of the badge and of the
        /// game's top bar (PerfOverlay.WindowBox).
        internal void Show()
        {
            open = true;
            tab = 0;
            expanded = -1;
            selected = null;
            tweaksOpen = 0;
            footFloor = 0;
            rect.gameObject.SetActive(!hidden);
            Measure();
            Build();
            PlaceAgain();
        }

        /// Placed again (opened, the places reset, or grown over its badge).
        internal void PlaceAgain()
        {
            var box = overlay.WindowBox(Width, height, out bool chosen);
            autoPlaced = !chosen;
            placedHeight = height;
            topLeft = new Vector2(box.X, box.Y);
            Clamp();
        }

        internal void Close()
        {
            if (!open) return;
            open = false;
            rect.gameObject.SetActive(false);
            ui.Cancel();
            chrome.Cancel();
            Measure();
        }

        /// A game dialog is up (hidden) or gone: the window steps out of its way and comes back.
        internal void SetHidden(bool value)
        {
            hidden = value;
            if (hidden)
            {
                ui.Cancel();
                chrome.Cancel();
            }
            rect.gameObject.SetActive(open && !hidden);
            if (open && !hidden) dirty = true;
        }

        /// A colony was left or loaded: what was read of the last one goes.
        internal void SceneChanged()
        {
            counts = NoCounts;
            countsAge = 99f;
            selected = null;
            takes.Clear();
            held.Reset();
            busyShare = double.NaN;
            gcLast = null;
            gcRated = false;
            allocPerFrame = double.NaN;
            footFloor = 0;
            dirty = true;
        }

        /// The window's title bar was dragged: `newTopLeft` is where its top left would go.
        internal void MoveTo(Vector2 newTopLeft)
        {
            topLeft = newTopLeft;
            autoPlaced = false;
            Clamp();
        }

        void Clamp()
        {
            var canvas = overlay.CanvasSize;
            var b = PerfLayout.Clamp(Box, canvas.x, canvas.y);
            topLeft = new Vector2(b.X, b.Y);
            var pos = new Vector2(topLeft.x, -topLeft.y);
            if (rect.anchoredPosition != pos) rect.anchoredPosition = pos;
        }

        // ---- What it measures, by tab ----

        bool timingOn;
        string bucketsWhy;

        /// The engine's frame window while the Colony or Frame tab shows; the bucket timing while
        /// Frame's "Game update" row is open; nothing when closed.
        void Measure()
        {
            bool timing = open && tab != 2;
            if (timing != timingOn)
            {
                timingOn = timing;
                PerfOverlay.WindowTiming(timing);
                takes.Clear();
                PerfOverlay.Take?.Invoke();
            }
            bool buckets = open && tab == 1 && expanded == 0;
            if (buckets != PerfBuckets.On)
            {
                bucketsWhy = PerfBuckets.Switch(buckets);
                if (bucketsWhy != null) global::Debug.LogWarning("[Moono] performance window: the look inside Game update is off: " + bucketsWhy);
                bucketsSince = Time.realtimeSinceStartup;
            }
        }

        float bucketsSince;
        // The engine's per-second sums (PerfOverlay.Take, copied), the last ten.
        readonly List<double[]> takes = new List<double[]>();
        double[] engineNow;
        static readonly PerfReads.ColonyCounts NoCounts = new PerfReads.ColonyCounts { Cycle = -1, Duplicants = -1, Critters = -1, LooseItems = -1, Buildings = -1, Utilities = -1, Asteroids = -1 };
        PerfReads.ColonyCounts counts = NoCounts;
        float countsAge = 99f;
        string[] lastRecording;
        float lastRecordingReadAt;
        bool lastRecordingRead, wasRecording;
        double[] gcLast;
        double gcLastAt;
        double gcSmall, gcFull;
        bool gcRated;
        double allocPerFrame = double.NaN;

        // Scan again: 1 the click's frame ("Scanning…"), 2 scanned (the label says so for a while).
        int scanState;
        float scannedFor;
        // Record 60 s: when it was started (realtime), and how long the button says it is saved.
        float recordStarted = -1f, recordSavedFor;
        /// The frame a click's result was built in (tests: the click-to-picture latency in
        /// frames, with PerfUi.ClickFrame).
        internal int ClickBuiltFrame = -1, ClickFrame = -1;

        internal void Update(float dt)
        {
            elapsed += dt;
            countsAge += dt;
            if (copiedFor > 0 && (copiedFor -= dt) <= 0)
            {
                copied = null;
                dirty = true;
            }
            if (scanState == 2 && (scannedFor -= dt) <= 0)
            {
                scanState = 0;
                dirty = true;
            }
            if (recordSavedFor > 0 && (recordSavedFor -= dt) <= 0) dirty = true;
            if (scanState == 1)
            {
                // The frame after the click showed "Scanning…": count and measure again now.
                Scan();
                dirty = true;
            }
            if (elapsed >= 1f)
            {
                elapsed = 0f;
                Sample();
                dirty = true;
            }
            if (!dirty || hidden) return;
            dirty = false;
            BuildSettled();
        }

        /// After the event system has sent this frame's presses (PerfOverlay.LateUpdate): a
        /// released press becomes its click, which is acted on and its result built now, so it
        /// shows in this very frame.
        internal void AfterInput()
        {
            if (!open || hidden) return;
            ui.Poll();
            chrome.Poll();
            if (!ui.PendingClick && !chrome.PendingClick) return;
            BuildSettled();
        }

        /// Builds until no click is left to act on (a click handled inside a build, a tab or a
        /// row, changes what the next build shows), at most a few times; notes a click's frames.
        /// A click lets the window find its height afresh.
        void BuildSettled()
        {
            bool click = ui.PendingClick || chrome.PendingClick;
            int clickFrame = Math.Max(ui.ClickFrame, chrome.ClickFrame);
            for (int pass = 0; pass < 3 && open; pass++)
            {
                Build();
                if (!ui.Clicked && !chrome.Clicked) break;
                click = true;
                footFloor = 0;
            }
            if (!click) return;
            ClickFrame = clickFrame;
            ClickBuiltFrame = Time.frameCount;
        }

        /// Scan again: the colony counted again, the engine's sums read now, and every "a minute"
        /// measured from here (PerfTrack.RestartMinute), so a change just made is judged alone.
        void Scan()
        {
            counts = PerfReads.Colony();
            countsAge = 0f;
            PerfOverlay.Track.RestartMinute();
            Sample();
            elapsed = 0f;
            scanState = 2;
            scannedFor = 2f;
        }

        /// Once a second: the engine's sums, its memory numbers, what holds the frame rate back,
        /// the colony's counts (every 5 s on Colony).
        void Sample()
        {
            if (timingOn && PerfOverlay.Take?.Invoke() is double[] n && n[0] > 0)
            {
                takes.Add((double[])n.Clone());
                if (takes.Count > 10) takes.RemoveAt(0);
            }
            var before = engineNow;
            engineNow = PerfOverlay.Engine?.Invoke();
            double now = Time.realtimeSinceStartupAsDouble;
            if (engineNow != null)
            {
                if (gcLast == null) { gcLast = (double[])engineNow.Clone(); gcLastAt = now; }
                else if (now - gcLastAt >= 10)
                {
                    // A collection of an older generation counts in the younger ones too: the
                    // small ones are those that were not full.
                    double minutes = (now - gcLastAt) / 60;
                    double all = engineNow[3] - gcLast[3], full = engineNow[5] - gcLast[5];
                    gcSmall = Math.Max(0, all - full) / minutes;
                    gcFull = full / minutes;
                    gcRated = true;
                    gcLast = (double[])engineNow.Clone();
                    gcLastAt = now;
                }
                if (before != null && takes.Count > 0) allocPerFrame = (engineNow[2] - before[2]) / Math.Max(1, takes[takes.Count - 1][0]);
            }
            if (tab == 0 && countsAge >= 5f && PerfGame.InColony())
            {
                counts = PerfReads.Colony();
                countsAge = 0f;
            }
            bool recordingNow = PerfOverlay.Recording;
            if (!lastRecordingRead || (wasRecording && !recordingNow))
            {
                lastRecordingRead = true;
                lastRecording = SafeLastRecording();
                lastRecordingReadAt = Time.realtimeSinceStartup;
            }
            wasRecording = recordingNow;
            Decide();
        }

        /// What holds the frame rate back, from the last seconds' engine sums (once a second).
        void Decide()
        {
            bool running = PerfGame.InColony() && !PerfGame.Paused();
            double refresh = PerfOverlay.RefreshMs();
            double hz = refresh > 0 ? 1000.0 / refresh : 0;
            if (hz > 0) heldHz = hz;
            if (!timingOn || takes.Count == 0)
            {
                if (!running) held.Add(PerfHeld.None);
                return;
            }
            var s = Sums();
            if (s[1] <= 0 || s[0] <= 0)
            {
                held.Add(PerfHeld.None);
                return;
            }
            double fps = s[0] * 1000 / s[1];
            double busy = Math.Max(0, s[1] - s[11] - (s[12] + s[13] + s[14] + s[15])) / s[1];
            busyShare = busy;
            held.Add(PerfHeldDecision.Classify(running, fps, hz, s[12] / s[1], s[15] / s[1], (s[13] + s[14]) / s[1], s[11] / s[1]));
        }

        static string[] SafeLastRecording()
        {
            try { return PerfOverlay.LastRecording?.Invoke(); }
            catch (Exception) { return null; }
        }

        /// The last ten seconds' engine sums (frames, wall, nine phases, outside, five waits,
        /// device calls, GC ms, frames with GC, longest frame, nine phase maxima, outside's).
        double[] Sums()
        {
            var s = new double[31];
            foreach (var t in takes)
                for (int i = 0; i < Math.Min(t.Length, s.Length); i++)
                    s[i] = i >= 20 ? Math.Max(s[i], t[i]) : s[i] + t[i];
            return s;
        }

        /// The screen holds the frame rate (vsync at the refresh rate): the settled decision.
        bool ScreenLimited => held.Shown == PerfHeld.Screen;
        bool VSyncOn => (PerfOverlay.VSyncInterval?.Invoke() ?? 0) > 0;

        string HeldText() => held.Shown switch
        {
            PerfHeld.Screen => heldHz > 0 ? string.Format(C, Ui("PERF_HELD_SCREEN"), Hz(heldHz)) : Ui("PERF_HELD_LIMIT"),
            PerfHeld.Limit => Ui("PERF_HELD_LIMIT"),
            PerfHeld.Graphics => Ui("PERF_HELD_GRAPHICS"),
            PerfHeld.Processor => Ui("PERF_HELD_PROCESSOR"),
            _ => "–",
        };

        // ---- Building ----

        void Build()
        {
            var track = PerfOverlay.Track;
            double now = track.Now;
            ui.Begin();
            float h;
            try
            {
                h = tab == 0 ? Colony(track, now) : tab == 1 ? FrameTab(track, now) : Hitches(track, now);
            }
            catch (Exception e)
            {
                global::Debug.LogWarning("[Moono] performance window: " + e);
                h = 60;
            }
            ui.End();
            height = TitleHeight + TabsHeight + h;
            rect.sizeDelta = new Vector2(Width, height);
            PerfUi.Place(body, 0, TitleHeight + TabsHeight, Width, h);
            shadow.rectTransform.anchorMin = Vector2.zero;
            shadow.rectTransform.anchorMax = Vector2.one;
            shadow.rectTransform.offsetMin = new Vector2(-3, -9);
            shadow.rectTransform.offsetMax = new Vector2(3, -3);
            Chrome();
            // Grown over its badge or past the screen's edge: placed again (unless the player put
            // it there), else kept inside the screen.
            var canvas = overlay.CanvasSize;
            if (autoPlaced && height != placedHeight && (Box.Overlaps(overlay.BadgeBox) || Box.Bottom > canvas.y - PerfLayout.Margin)) PlaceAgain();
            else Clamp();
        }

        void Chrome()
        {
            title.font = PerfUi.Title;
            title.fontSize = 18f;
            title.text = Ui("PERF_WINDOW_TITLE");
            chrome.Begin();
            // Centred in the bar (2 to TitleHeight), as the title.
            const float closeSize = 24f;
            if (chrome.Button("close", Width - 8 - closeSize, 2 + (TitleHeight - 2 - closeSize) / 2, closeSize, closeSize, "×", new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 22f)) Close();
            chrome.Box(0, TitleHeight, Width, TabsHeight, PerfUi.Panel2);
            chrome.Box(0, TitleHeight + TabsHeight - 1, Width, 1, PerfUi.Rule);
            string[] names = { Ui("PERF_TAB_COLONY"), Ui("PERF_TAB_FRAME"), Ui("PERF_TAB_HITCHES") };
            float x = 0;
            for (int i = 0; i < 3; i++)
            {
                float w = chrome.Width(names[i], 15f, PerfUi.Title) + 32;
                bool on = i == tab;
                // The active tab: lit, underlined in the accent, its name white.
                if (on)
                {
                    chrome.Box(x, TitleHeight, w, TabsHeight - 1, new Color(1, 1, 1, 0.09f));
                    chrome.Box(x, TitleHeight + TabsHeight - 4, w, 3, PerfUi.Accent);
                }
                chrome.Text(x, TitleHeight, w, TabsHeight - 2, names[i], 15f, on ? Color.white : PerfUi.Muted, TextAlignmentOptions.Center, PerfUi.Title);
                if (chrome.Area("tab" + i, x, TitleHeight, w, TabsHeight, out _, 0.12f) && tab != i) SwitchTab(i);
                x += w;
            }
            chrome.End();
            if (!open) Measure();
        }

        /// Another tab: what it measures switched now; its numbers from the last second, or
        /// "Measuring…" where it needs its own (they fill in at the next second).
        void SwitchTab(int i)
        {
            tab = i;
            expanded = -1;
            selected = null;
            tweaksOpen = 0;
            countsAge = 99f;
            footFloor = 0;
            Measure();
            if (tab == 0 && PerfGame.InColony())
            {
                counts = PerfReads.Colony();
                countsAge = 0f;
            }
        }

        // Common pieces.

        /// A heading in capitals up to its first number ("BY CAUSE, THIS SESSION (5 min)",
        /// "THE SELECTED FREEZE: UNKNOWN, 5 s ago"): values and their units stay as written.
        internal static string Heading(string text)
        {
            int digit = -1;
            for (int i = 0; i < text.Length; i++)
                if (char.IsDigit(text[i]))
                {
                    digit = i;
                    break;
                }
            return digit < 0 ? PerfUi.Upper(text) : PerfUi.Upper(text.Substring(0, digit)) + text.Substring(digit);
        }

        /// A heading: its words in capitals in the title face, and from its first number on (a
        /// value and its unit: "5 s ago", "(5 min)") in the body face, which has lower case (the
        /// title face draws every letter as a capital).
        void HeadText(float x, float y, float w, string text)
        {
            int digit = -1;
            for (int i = 0; i < text.Length; i++)
                if (char.IsDigit(text[i]))
                {
                    digit = i;
                    break;
                }
            // "(5 min)" stays together.
            if (digit > 0 && text[digit - 1] == '(') digit--;
            if (digit <= 0 || PerfUi.Title == PerfUi.Body)
            {
                ui.Text(x, y, w, 18, Heading(text), 13.5f, PerfUi.Muted, TextAlignmentOptions.MidlineLeft, PerfUi.Title);
                return;
            }
            string head = PerfUi.Upper(text.Substring(0, digit)), rest = text.Substring(digit);
            float hw = ui.Width(head, 13.5f, PerfUi.Title);
            ui.Text(x, y, hw + 2, 18, head, 13.5f, PerfUi.Muted, TextAlignmentOptions.MidlineLeft, PerfUi.Title);
            ui.Text(x + hw, y, Math.Max(10, w - hw), 18, rest, 12.5f, PerfUi.Muted, TextAlignmentOptions.MidlineLeft);
        }
        float Section(float x, float y, float w, string text)
        {
            HeadText(x, y, w, text);
            return y + 22;
        }

        /// A side panel: its heading and its stat lines (label, value, value's colour). Line
        /// `buttonLine` keeps `button(-1, 0)` units at its right for a button that `button(y,
        /// right)` draws there.
        float Side(float x, float y, float w, string heading, IList<(string Label, string Value, Color Colour)> lines, int buttonLine = -1, Func<float, float, float> button = null)
        {
            const float line = 24f;
            float h = 9 + 20 + lines.Count * line + 6;
            ui.Box(x, y, w, h, PerfUi.Panel2, PerfUi.Round6);
            HeadText(x + 12, y + 7, w - 24, heading);
            float ly = y + 29;
            for (int i = 0; i < lines.Count; i++)
            {
                var (label, value, colour) = lines[i];
                float bw = i == buttonLine && button != null ? button(-1, 0) + 8 : 0;
                float vw = ui.Width(value, 13f) + 2;
                ui.Text(x + 12, ly, w - 24 - vw - bw - 10, line, label, 13f, PerfUi.Muted, TextAlignmentOptions.MidlineLeft, null, false, true);
                ui.Text(x + w - 12 - bw - vw, ly, vw, line, "<b>" + value + "</b>", 13f, colour, TextAlignmentOptions.MidlineRight);
                if (bw > 0) button(ly + 2, x + w - 12);
                if (i < lines.Count - 1) ui.Box(x + 12, ly + line - 1, w - 24, 1, PerfUi.Line);
                ly += line;
            }
            return y + h + 8;
        }

        /// Four headline boxes in one line: a label, a number and its unit.
        float Headlines(float x, float y, float w, IList<(string Label, string Number, string Unit)> boxes)
        {
            float bw = (w - 3 * 8) / 4f, bh = 52f;
            for (int i = 0; i < boxes.Count; i++)
            {
                float bx = x + i * (bw + 8);
                ui.Box(bx, y, bw, bh, PerfUi.Panel2, PerfUi.Round4);
                ui.Text(bx + 10, y + 3, bw - 20, 20, boxes[i].Label, 11.5f, PerfUi.Muted, TextAlignmentOptions.MidlineLeft, null, false, true);
                // The number in the title face, its unit in the body face, on one baseline.
                string unit = boxes[i].Unit;
                float nw = ui.Width(boxes[i].Number, 22f, PerfUi.Title);
                // TMP puts a Baseline-aligned line's baseline at its rectangle's middle: both rectangles
                // centred on the baseline (y + 44).
                ui.Text(bx + 10, y + 32, nw + 2, 24, boxes[i].Number, 22f, PerfUi.Ink, TextAlignmentOptions.BaselineLeft, PerfUi.Title);
                if (unit.Length > 0) ui.Text(bx + 10 + nw + 4, y + 32, bw - 24 - nw, 24, unit, 12.5f, PerfUi.Muted, TextAlignmentOptions.BaselineLeft);
            }
            return y + bh + 12;
        }

        /// The footer: its note and its buttons, never higher than it was since the last click.
        float Foot(float y, string note, Func<float, float> buttons)
        {
            y = Math.Max(y, footFloor);
            footFloor = y;
            float buttonsWidth = buttons(-1);
            float noteWidth = Width - 2 * Pad - buttonsWidth - 12;
            float nh = Math.Max(26, Math.Max(ui.Height(note, 12.5f, noteWidth), ui.Height("A\nA", 12.5f, noteWidth)) + 2);
            ui.Text(Pad, y, noteWidth, nh, note, 12.5f, PerfUi.Muted, TextAlignmentOptions.MidlineLeft, null, true);
            buttons(y + (nh - 26) / 2);
            return y + nh + 14;
        }

        /// The footer's Record 60 s button, right-aligned at `right`; with y < 0 only its width.
        /// Armed while the game is paused or in the menus: "Waiting for play"; recording: the
        /// seconds left of the recording itself; then "Saved" for a few seconds.
        float RecordButton(float y, float right)
        {
            double progress = PerfOverlay.RecordProgress?.Invoke() ?? -1;
            bool busy = progress != -1;
            if (!busy && recordStarted >= 0)
            {
                recordStarted = -1f;
                recordSavedFor = 4f;
            }
            string label = progress == -2 ? Ui("PERF_RECORD_WAITING")
                : progress >= 0 ? string.Format(C, Ui("PERF_RECORDING_LEFT"), Math.Max(0, 60 - (int)Math.Floor(progress)).ToString(C))
                : recordSavedFor > 0 ? Ui("PERF_RECORD_SAVED") : Ui("PERF_RECORD");
            float w = 0;
            foreach (var text in new[] { Ui("PERF_RECORD"), string.Format(C, Ui("PERF_RECORDING_LEFT"), "60"), Ui("PERF_RECORD_WAITING"), Ui("PERF_RECORD_SAVED") })
                w = Math.Max(w, ui.Width(text, 14f, PerfUi.Title));
            w += 26;
            if (y >= 0 && ui.Button("record", right - w, y, w, 26, label, busy ? PerfUi.ButtonBack : PerfUi.Go, busy ? PerfUi.ButtonRule : PerfUi.GoRule) && !busy && PerfOverlay.Record != null)
            {
                global::Debug.Log("[Moono] perf record: " + PerfOverlay.Record());
                recordStarted = Time.realtimeSinceStartup;
                recordSavedFor = 0f;
                lastRecordingRead = false;
            }
            return w;
        }

        static string Fps(double fps) => double.IsNaN(fps) || fps <= 0 ? "–" : fps.ToString("F0", C);
        static string Ms(double ms, string format = "F1") => double.IsNaN(ms) ? "–" : ms.ToString(format, C) + " " + Ui("PERF_MS");
        /// A duration: under 0.1 s in ms ("61 ms"), else in seconds ("0.71 s", "17.3 s").
        static string Seconds(double ms) => double.IsNaN(ms) ? "–" : ms < 100 ? ms.ToString("F0", C) + " " + Ui("PERF_MS")
            : (ms / 1000).ToString(ms >= 10000 ? "F1" : "F2", C) + " " + Ui("PERF_S");
        static string Count(int n) => n < 0 ? "–" : n.ToString("N0", C);
        static string Gb(double bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("F1", C);
        /// A signed difference with the plain minus the game's fonts have ("-0.05").
        static string Signed(double v, string format) => (v >= 0 ? "+" : "-") + Math.Abs(v).ToString(format, C);

        /// "measuring… 12 of 20 s" while a minute's rate is not yet shown (PerfTrack.Rate.Measured).
        static string Measuring(PerfTrack.Rate last) => string.Format(C, Ui("PERF_MEASURING_PROGRESS"), Math.Floor(last.PlaySeconds).ToString("F0", C), PerfTrack.WarmUpSeconds.ToString("F0", C));

        // ---- Colony ----

        float Colony(PerfTrack track, double now)
        {
            float y = 14;
            // One source for the verdict, Right now, the advice and the Hitches tab: a minute of play.
            var last = track.LastMinute();
            bool inColony = PerfGame.InColony();
            bool paused = PerfGame.Paused();
            bool running = inColony && !paused;
            int speed = PerfReads.Speed();
            int stutters = last.PerMinute(last.Stutters), simWaits = last.PerMinute(last.SimWaits), unknown = last.PerMinute(last.Unknown);
            int light = track.Light;
            bool measured = last.Measured;
            bool screen = running && ScreenLimited;
            // Scan again starts every "a minute" afresh.
            bool rescanned = !double.IsNaN(track.RestartedAt) && now - track.RestartedAt < 90;
            string warming = rescanned
                ? string.Format(C, Ui("PERF_VERDICT_REMEASURING_TEXT"), Math.Floor(last.PlaySeconds).ToString("F0", C), PerfTrack.WarmUpSeconds.ToString("F0", C))
                : string.Format(C, Ui("PERF_VERDICT_WARMING_TEXT"), Math.Floor(last.PlaySeconds).ToString("F0", C), PerfTrack.WarmUpSeconds.ToString("F0", C));
            string verdictTitle, verdictText;
            Color back, rule, ink;
            if (!inColony)
            {
                verdictTitle = Ui("PERF_VERDICT_NO_COLONY");
                verdictText = Ui("PERF_VERDICT_NO_COLONY_TEXT");
                (back, rule, ink) = (PerfUi.Hex("2A2F3D"), PerfUi.Rule, PerfUi.Muted);
            }
            else if (paused)
            {
                verdictTitle = Ui("PERF_VERDICT_PAUSED");
                verdictText = Ui("PERF_VERDICT_PAUSED_TEXT");
                (back, rule, ink) = (PerfUi.Hex("2A2F3D"), PerfUi.Rule, PerfUi.Muted);
            }
            else if (light == 2)
            {
                verdictTitle = Ui("PERF_VERDICT_BEHIND");
                verdictText = string.Format(C, Ui("PERF_VERDICT_BEHIND_TEXT"), (track.KeepUp * track.Speed).ToString("F1", C), track.Speed.ToString("F0", C));
                (back, rule, ink) = (PerfUi.Hex("3A2220"), PerfUi.Hex("7A3D3D"), PerfUi.Hex("E6C8C4"));
            }
            else if (light == 1)
            {
                verdictTitle = Ui("PERF_VERDICT_STUTTERING");
                verdictText = measured ? string.Format(C, Ui("PERF_VERDICT_STUTTERING_TEXT"), Fps(track.Fps), stutters) : warming;
                (back, rule, ink) = (PerfUi.Hex("3A331F"), PerfUi.Hex("7A6A3D"), PerfUi.Hex("E6DCC4"));
            }
            else
            {
                verdictTitle = Ui("PERF_VERDICT_GOOD");
                // Held at the screen's refresh rate by vsync: as fast as the screen shows, with the
                // main thread's measured spare time (in steps of 5 %, so the text holds still).
                verdictText = screen
                    ? double.IsNaN(busyShare) ? string.Format(C, Ui("PERF_VERDICT_SCREEN_TEXT"), Fps(track.Fps), Hz(heldHz), speed)
                    : string.Format(C, Ui("PERF_VERDICT_SCREEN_ROOM_TEXT"), Fps(track.Fps), Hz(heldHz), speed, (Math.Round(20 * busyShare) * 5).ToString("F0", C))
                    : string.Format(C, Ui("PERF_VERDICT_GOOD_TEXT"), Fps(track.Fps), speed);
                if (!measured) verdictText += " " + warming;
                (back, rule, ink) = (PerfUi.Hex("1F3A2A"), PerfUi.Hex("3D7A52"), PerfUi.Hex("C4DCCB"));
            }
            float textW = Width - 2 * Pad - 24 - 18;
            // Room for two lines always: a verdict that changes between one and two lines does
            // not move what is below it.
            float th = Math.Max(ui.Height(verdictText, 13.5f, textW), ui.Height("A\nA", 13.5f, textW));
            float vh = 8 + 20 + th + 8;
            ui.Box(Pad, y, Width - 2 * Pad, vh, rule, PerfUi.Round6);
            ui.Box(Pad + 1, y + 1, Width - 2 * Pad - 2, vh - 2, back, PerfUi.Round6);
            ui.Box(Pad + 12, y + 13, 10, 10, PerfUi.LightColour(running ? light : 0), PerfUi.Dot);
            ui.Text(Pad + 12 + 18, y + 8, textW, 20, "<b>" + verdictTitle + "</b>", 15f, PerfUi.Ink);
            ui.Text(Pad + 12 + 18, y + 28, textW, th, verdictText, 13.5f, ink, TextAlignmentOptions.TopLeft, null, true);
            y += vh + 12;

            // Left: the advice (none without a colony).
            float ly = Section(Pad, y, LeftWidth, Ui("PERF_ADVICE_HEADING"));
            if (!inColony) ly = Fine(Pad, ly, LeftWidth, Ui("PERF_ADVICE_NO_COLONY")) + 6;
            else
            {
                var state = new PerfAdviceState
                {
                    Judged = measured,
                    Fps = track.Fps,
                    KeepUp = track.KeepUp,
                    Speed = track.Speed,
                    StuttersPerMinute = stutters,
                    SimWaitsPerMinute = simWaits,
                    UnknownPerMinute = unknown,
                    TweaksOn = new HashSet<string>(PerfOverlay.TweaksOn, StringComparer.Ordinal),
                    Duplicants = counts.Duplicants,
                    Critters = counts.Critters,
                    LooseItems = counts.LooseItems,
                    Buildings = counts.Buildings,
                    Utilities = counts.Utilities,
                };
                var (cards, nothing) = PerfAdvice.Evaluate(state, Ui);
                // The frame rate, held by vsync: nothing to gain, and where to see the uncapped number.
                if (screen) nothing.Insert(0, string.Format(C, Ui("PERF_NOTHING_VSYNC"), Hz(heldHz)));
                Color[] icons = { PerfUi.UiColour, PerfUi.Anim, PerfUi.Draw, PerfUi.Sim };
                if (cards.Count == 0)
                {
                    // While the verdict says it stutters, say what is known about the stutters instead of "nothing to do".
                    string none = !state.Judged ? string.Format(C, Ui("PERF_ADVICE_NOT_YET"), Math.Floor(last.PlaySeconds).ToString("F0", C), PerfTrack.WarmUpSeconds.ToString("F0", C))
                        : running && light == 1 ? string.Format(C, Ui("PERF_ADVICE_NONE_STUTTERS"), unknown, stutters)
                        : Ui("PERF_ADVICE_NONE");
                    ly = Fine(Pad, ly, LeftWidth, none) + 6;
                }
                for (int i = 0; i < cards.Count; i++)
                {
                    var (cardTitle, gain, reason) = cards[i];
                    float tx = Pad + 12 + 30 + 12, tw = LeftWidth - (tx - Pad) - 12;
                    float h1 = ui.Height("<b>" + cardTitle + "</b>", 15f, tw), h2 = ui.Height(gain, 13.5f, tw), h3 = ui.Height(reason, 12.5f, tw);
                    float ch = 9 + h1 + 2 + h2 + 2 + h3 + 9;
                    ui.Box(Pad, ly, LeftWidth, Math.Max(ch, 48), PerfUi.Panel2, PerfUi.Round6);
                    ui.Box(Pad + 12, ly + 9, 30, 30, icons[i % icons.Length], PerfUi.Round6);
                    ui.Text(Pad + 12, ly + 9, 30, 30, (i + 1).ToString(C), 19f, PerfUi.Hex("10131A"), TextAlignmentOptions.Center, PerfUi.Title);
                    float cy = ly + 9;
                    ui.Text(tx, cy, tw, h1, "<b>" + cardTitle + "</b>", 15f, PerfUi.Ink, TextAlignmentOptions.TopLeft, null, true);
                    cy += h1 + 2;
                    ui.Text(tx, cy, tw, h2, gain, 13.5f, PerfUi.GainInk, TextAlignmentOptions.TopLeft, null, true);
                    cy += h2 + 2;
                    ui.Text(tx, cy, tw, h3, reason, 12.5f, PerfUi.Muted, TextAlignmentOptions.TopLeft, null, true);
                    ly += Math.Max(ch, 48) + 8;
                }
                ly = Section(Pad, ly + 4, LeftWidth, Ui("PERF_NOTHING_HEADING"));
                ly = Fine(Pad, ly, LeftWidth, nothing.Count > 0 ? string.Join("\n", nothing) : Ui("PERF_NOTHING_NONE"));
            }

            // Right: now, and the colony.
            var nowLines = new List<(string, string, Color)>();
            nowLines.Add((Ui("PERF_NOW_FRAME_RATE"), Fps(track.Fps) + " " + Ui("PERF_FPS"), PerfUi.LightColour(running ? light : 0)));
            if (inColony)
            {
                if (running && !double.IsNaN(track.KeepUp))
                {
                    bool keeps = track.KeepUp >= 0.95;
                    nowLines.Add((string.Format(C, Ui("PERF_NOW_KEEPS_UP"), speed), keeps ? Ui("PERF_YES") : string.Format(C, Ui("PERF_NO_AT"), (track.KeepUp * track.Speed).ToString("F1", C)),
                        keeps ? PerfUi.Good : PerfUi.Bad));
                }
                else nowLines.Add((string.Format(C, Ui("PERF_NOW_KEEPS_UP"), speed), "–", PerfUi.Ink));
                nowLines.Add((Ui("PERF_NOW_STUTTERS"), measured ? string.Format(C, Ui("PERF_PER_MINUTE"), stutters) : Measuring(last),
                    running && light == 1 && measured ? PerfUi.Warn : measured ? PerfUi.Ink : PerfUi.Muted));
                nowLines.Add((Ui("PERF_NOW_AUTOSAVE"), double.IsNaN(track.LastSaveMs) ? Ui("PERF_NONE_YET") : Seconds(track.LastSaveMs), PerfUi.Ink));
            }
            if (engineNow != null && engineNow[1] > 0)
                nowLines.Add((Ui("PERF_NOW_MEMORY"), string.Format(C, Ui("PERF_GB_OF"), Gb(engineNow[0]), Gb(engineNow[1]).Replace(".0", "")), PerfUi.Ink));
            if (inColony) nowLines.Add((Ui("PERF_NOW_HELD_BACK"), running ? HeldText() : "–", PerfUi.Ink));
            float ry = Side(RightX, y, RightWidth, Ui("PERF_NOW_HEADING"), nowLines);
            if (inColony)
            {
                string colonyHeading = counts.Cycle > 0 ? string.Format(C, Ui("PERF_COLONY_HEADING_CYCLE"), counts.Cycle.ToString("N0", C)) : Ui("PERF_COLONY_HEADING");
                ry = Side(RightX, ry, RightWidth, colonyHeading, new List<(string, string, Color)>
                {
                    (Ui("PERF_COLONY_DUPLICANTS"), Count(counts.Duplicants), PerfUi.Ink),
                    (Ui("PERF_COLONY_CRITTERS"), Count(counts.Critters), PerfUi.Ink),
                    (Ui("PERF_COLONY_LOOSE"), Count(counts.LooseItems), PerfUi.Ink),
                    (Ui("PERF_COLONY_BUILDINGS"), Count(counts.Buildings), PerfUi.Ink),
                    (Ui("PERF_COLONY_UTILITIES"), Count(counts.Utilities), PerfUi.Ink),
                    (Ui("PERF_COLONY_ASTEROIDS"), Count(counts.Asteroids), PerfUi.Ink),
                });
            }
            y = Math.Max(ly, ry - 8) + 6;
            // After Scan again: when, and that the minute is measured from then.
            string foot = double.IsNaN(track.RestartedAt) || !inColony ? Ui("PERF_COLONY_FOOT")
                : string.Format(C, Ui("PERF_COLONY_FOOT_SCANNED"), now - track.RestartedAt < 2 ? Ui("PERF_JUST_NOW") : Ago(now - track.RestartedAt));
            return Foot(y, foot, fy =>
            {
                string label = scanState == 1 ? Ui("PERF_SCANNING") : scanState == 2 ? Ui("PERF_SCANNED") : Ui("PERF_SCAN_AGAIN");
                float w = Math.Max(ui.Width(Ui("PERF_SCAN_AGAIN"), 14f, PerfUi.Title), Math.Max(ui.Width(Ui("PERF_SCANNING"), 14f, PerfUi.Title), ui.Width(Ui("PERF_SCANNED"), 14f, PerfUi.Title))) + 26;
                bool done = scanState == 2;
                // Nothing to scan without a colony: shown, not clickable.
                if (fy >= 0 && ui.Button("scan", Width - Pad - w, fy, w, 26, label, done ? PerfUi.Go : PerfUi.ButtonBack, done ? PerfUi.GoRule : PerfUi.ButtonRule, 14f, inColony)
                    && scanState != 1)
                    scanState = 1;
                return w;
            });
        }

        static string Hz(double hz) => Math.Round(hz).ToString("F0", C);

        float Fine(float x, float y, float w, string text)
        {
            float h = ui.Height(text, 12.5f, w - 24);
            ui.Box(x, y, w, h + 16, PerfUi.Panel2, PerfUi.Round6);
            ui.Text(x + 12, y + 8, w - 24, h, text, 12.5f, PerfUi.Muted, TextAlignmentOptions.TopLeft, null, true);
            return y + h + 16;
        }

        // ---- Frame ----

        // The rows: the phases in them (FrameWindows' order: render 0, gui 1, end of frame 2,
        // present 3, early+fixed 4, update 5, late 6, post-late 7, particles 8; 9 the time between
        // frames), and their colours.
        static readonly int[][] RowPhases = { new[] { 5 }, new[] { 6 }, new[] { 0, 3, 2 }, new[] { 7, 1 }, new[] { 4, 8, 9 } };
        static readonly Color[] RowColours = { PerfUi.Sim, PerfUi.Anim, PerfUi.Draw, PerfUi.UiColour, PerfUi.Other, PerfUi.Wait };
        /// The sixth row: the main thread waiting for its picture to be taken (the device's next
        /// frame and, with vsync, the queued pictures), which happens inside "Showing the picture"
        /// and is taken out of Drawing: with vsync it is the screen's pace, not drawing work.
        const int WaitRow = 5, Rows = 6;
        static readonly string[] PhaseKeys = { "render", "gui", "end-of-frame", "present", "early-fixed", "update", "late", "post-late", "particles", "outside-loop" };

        /// The rows' time from the phases' (`phase(p)`) and the waits inside "Showing the picture"
        /// (`waits`): Drawing without the waits, then the waits. One split for the rows, the
        /// footer's comparison and the copy report.
        static double[] RowMs(Func<int, double> phase, double waits)
        {
            var rows = new double[Rows];
            for (int r = 0; r < 5; r++)
                foreach (int p in RowPhases[r]) rows[r] += phase(p);
            double w = Math.Min(phase(3), waits);
            rows[2] -= w;
            rows[WaitRow] = w;
            return rows;
        }

        static double[] RowMs(double[] s) => RowMs(p => PhaseMs(s, p), s[12] + s[15]);

        /// The waits' row name: the screen when vsync holds the frame rate, the graphics card
        /// without vsync, either with vsync but frames slower than the screen.
        string WaitRowName() => !VSyncOn ? Ui("PERF_ROW_WAIT_GPU") : ScreenLimited ? Ui("PERF_ROW_WAIT_SCREEN") : Ui("PERF_ROW_WAIT_EITHER");

        string RowName(int row) => row switch
        {
            0 => Ui("PERF_ROW_UPDATE"),
            1 => Ui("PERF_ROW_LATE"),
            2 => Ui("PERF_ROW_DRAWING"),
            3 => Ui("PERF_ROW_INTERFACE"),
            WaitRow => WaitRowName(),
            _ => Ui("PERF_ROW_ELSE"),
        };

        static string PhaseName(int phase) => phase switch
        {
            0 => Ui("PERF_PHASE_RENDER"),
            1 => Ui("PERF_PHASE_GUI"),
            2 => Ui("PERF_PHASE_END_OF_FRAME"),
            3 => Ui("PERF_PHASE_PRESENT"),
            4 => Ui("PERF_PHASE_EARLY"),
            5 => Ui("PERF_ROW_UPDATE"),
            6 => Ui("PERF_ROW_LATE"),
            7 => Ui("PERF_PHASE_POST_LATE"),
            8 => Ui("PERF_PHASE_PARTICLES"),
            _ => Ui("PERF_PHASE_OUTSIDE"),
        };

        static double PhaseMs(double[] s, int phase) => phase == 9 ? s[11] : s[2 + phase];
        static double PhaseMax(double[] s, int phase) => phase == 9 ? s[30] : s[21 + phase];

        /// A row opens to what is inside it: Game update to its state machine updates, the waits
        /// to their two parts, the rows of several phases to those. Animation is one phase: no.
        static bool Opens(int row) => row == 0 || row == WaitRow || row < 5 && RowPhases[row].Length > 1;

        float FrameTab(PerfTrack track, double now)
        {
            float y = 14;
            var sum = track.Summarize(10, now);
            var s = Sums();
            double frames = Math.Max(1, s[0]), wall = s[1];
            bool running = PerfGame.InColony() && !PerfGame.Paused();
            float ly = Headlines(Pad, y, LeftWidth, new List<(string, string, string)>
            {
                (Ui("PERF_KV_AVERAGE"), sum.Frames > 0 ? sum.MeanMs.ToString("F1", C) : "–", Ui("PERF_MS")),
                (Ui("PERF_KV_SLOWEST"), sum.Frames > 0 ? sum.P99Ms.ToString("F0", C) : "–", Ui("PERF_MS")),
                (Ui("PERF_KV_WORST"), sum.Frames > 0 ? sum.MaxMs.ToString("F0", C) : "–", Ui("PERF_MS")),
                (Ui("PERF_KV_SPEED"), running && !double.IsNaN(track.KeepUp) ? (track.KeepUp * track.Speed).ToString("F1", C) : "–", running && !double.IsNaN(track.KeepUp) ? "×" : ""),
            });
            if (wall <= 0)
            {
                ly = Fine(Pad, ly, LeftWidth, Ui("PERF_MEASURING"));
            }
            else
            {
                // The strip, then the rows.
                double[] rowMs = RowMs(s);
                double total = rowMs.Sum();
                float x = Pad;
                ui.Box(Pad, ly, LeftWidth, 16, PerfUi.Panel2, PerfUi.Round4);
                for (int r = 0; r < Rows; r++)
                {
                    float w = (float)(LeftWidth * rowMs[r] / Math.Max(1e-9, total));
                    if (w >= 1) ui.Box(x, ly, w, 16, RowColours[r]);
                    x += w;
                }
                ly += 16 + 10;
                double top = rowMs.Max();
                // The names' column fits the longest name (the waits' can be long).
                float nameW = 150;
                for (int r = 0; r < Rows; r++) nameW = Math.Max(nameW, ui.Width(RowName(r) + "  +", 13.5f) + 6);
                nameW = Math.Min(nameW, 250);
                for (int r = 0; r < Rows; r++)
                {
                    const float rh = 25f;
                    bool opens = Opens(r), isOpen = opens && expanded == r;
                    // The open row is lit; every row that opens lights under the pointer (PerfUi.Area).
                    if (isOpen) ui.Box(Pad - 4, ly, LeftWidth + 8, rh, new Color(1, 1, 1, 0.08f), PerfUi.Round4);
                    if (opens && ui.Area("row" + r, Pad - 4, ly, LeftWidth + 8, rh, out _))
                    {
                        expanded = isOpen ? -1 : r;
                        Measure();
                    }
                    float cx = Pad;
                    ui.Box(cx, ly + 7.5f, 10, 10, RowColours[r], PerfUi.Round4);
                    cx += 12 + 10;
                    string name = opens ? RowName(r) + "<color=#" + PerfUi.MutedHex + ">  " + (isOpen ? "–" : "+") + "</color>" : RowName(r);
                    ui.Text(cx, ly, nameW, rh, name, 13.5f, PerfUi.Ink, TextAlignmentOptions.MidlineLeft, null, false, true);
                    cx += nameW + 10;
                    float barW = LeftWidth - (cx - Pad) - 10 - 62 - 10 - 40;
                    ui.Box(cx, ly + 9.5f, barW, 6, PerfUi.Panel2, PerfUi.Round4);
                    float fill = (float)(barW * rowMs[r] / Math.Max(1e-9, top));
                    if (fill >= 2) ui.Box(cx, ly + 9.5f, fill, 6, RowColours[r], PerfUi.Round4);
                    cx += barW + 10;
                    ui.Text(cx, ly, 62, rh, (rowMs[r] / frames).ToString("F2", C) + " " + Ui("PERF_MS"), 13.5f, PerfUi.Ink, TextAlignmentOptions.MidlineRight);
                    cx += 62 + 10;
                    ui.Text(cx, ly, 40, rh, (100 * rowMs[r] / Math.Max(1e-9, total)).ToString("F0", C) + "%", 13.5f, PerfUi.Muted, TextAlignmentOptions.MidlineRight);
                    ly += rh;
                    if (isOpen) ly = Drill(r, s, frames, rowMs[r], ly);
                    ui.Box(Pad, ly - 1, LeftWidth, 1, PerfUi.Line);
                }
                ly += 6;
                // Waits for the graphics thread's results (left inside the rows they happen in).
                ly = SubRow(ly, Ui("PERF_SUB_WAIT_GRAPHICS"), ((s[13] + s[14]) / frames).ToString("F2", C) + " " + Ui("PERF_MS"));
                ly = SubRow(ly, Ui("PERF_SUB_GC"), (s[18] / frames).ToString("F2", C) + " " + Ui("PERF_MS"));
                if (expanded == 0)
                {
                    float nh = ui.Height(Ui("PERF_BUCKETS_NOTE"), 12f, LeftWidth);
                    ui.Text(Pad, ly + 4, LeftWidth, nh, Ui("PERF_BUCKETS_NOTE"), 12f, PerfUi.Muted, TextAlignmentOptions.TopLeft, null, true);
                    ly += nh + 8;
                }
            }

            // Right column.
            var spread = new List<(string, string, Color)>
            {
                (Ui("PERF_SPREAD_MEDIAN"), sum.Frames > 0 ? Ms(sum.MedianMs) : "–", PerfUi.Ink),
                (Ui("PERF_SPREAD_NINE_IN_TEN"), sum.Frames > 0 ? Ms(sum.P90Ms) : "–", PerfUi.Ink),
                (Ui("PERF_SPREAD_NINETY_NINE"), sum.Frames > 0 ? Ms(sum.P99Ms, "F0") : "–", sum.P99Ms >= PerfTrack.StutterMs ? PerfUi.Warn : PerfUi.Ink),
                (Ui("PERF_SPREAD_LONGEST"), sum.Frames > 0 ? Ms(sum.MaxMs, "F0") : "–", sum.MaxMs >= PerfTrack.FreezeMs ? PerfUi.Warn : PerfUi.Ink),
            };
            float ry = Side(RightX, y, RightWidth, Ui("PERF_SPREAD_HEADING"), spread);
            var threads = new List<(string, string, Color)>();
            if (wall > 0)
            {
                double waits = s[12] + s[13] + s[14] + s[15];
                double busy = Math.Max(0, wall - s[11] - waits) / wall;
                // An uncapped game keeps it near 100 %: not a warning in itself.
                threads.Add((Ui("PERF_THREADS_MAIN"), (100 * busy).ToString("F0", C) + "%", PerfUi.Ink));
                // With vsync holding the rate, the main thread mostly waits for the screen.
                threads.Add((ScreenLimited ? Ui("PERF_THREADS_WAIT_SCREEN") : Ui("PERF_THREADS_WAIT_GRAPHICS"), (100 * waits / wall).ToString("F0", C) + "%", PerfUi.Ink));
            }
            // Vertical sync, with its switch (the Moono Tweaks row's setting, applied now).
            int vsyncLine = threads.Count;
            double refresh = PerfOverlay.RefreshMs();
            threads.Add((Ui("PERF_THREADS_VSYNC"), !VSyncOn ? Ui("PERF_VSYNC_OFF") : refresh > 0 ? string.Format(C, Ui("PERF_VSYNC_ON_HZ"), Hz(1000 / refresh)) : Ui("PERF_VSYNC_ON"), PerfUi.Ink));
            if (GameFrameStats.Enabled && GameFrameStats.Frames > 0)
                threads.Add((Ui("PERF_THREADS_SIM"), (GameFrameStats.NativeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / GameFrameStats.Frames).ToString("F2", C) + " " + Ui("PERF_MS"), PerfUi.Ink));
            int workers = PerfReads.Workers();
            if (workers >= 0) threads.Add((Ui("PERF_THREADS_WORKERS"), string.Format(C, Ui("PERF_OF_N"), workers, Environment.ProcessorCount), PerfUi.Ink));
            int vsyncValue = MoonoTweaksMenu.Value("vsync", out bool vsyncLocked);
            ry = Side(RightX, ry, RightWidth, Ui("PERF_THREADS_HEADING"), threads, vsyncValue >= 0 ? vsyncLine : -1, (by, right) => VSyncButton(by, right, vsyncValue, vsyncLocked));
            var memory = new List<(string, string, Color)>();
            if (engineNow != null)
            {
                memory.Add((Ui("PERF_MEMORY_IN_USE"), Gb(engineNow[0]) + " " + Ui("PERF_GB"), PerfUi.Ink));
                if (!double.IsNaN(allocPerFrame)) memory.Add((Ui("PERF_MEMORY_PER_FRAME"), (allocPerFrame / 1024).ToString("F0", C) + " " + Ui("PERF_KB"), PerfUi.Ink));
                memory.Add((Ui("PERF_MEMORY_CLEANUPS"), gcRated ? string.Format(C, Ui("PERF_CLEANUPS_VALUE"), Math.Round(gcSmall), Math.Round(gcFull)) : "–", PerfUi.Ink));
            }
            memory.Add((Ui("PERF_MEMORY_LONGEST"), Ms(track.LongestGcMs, "F0"), PerfUi.Ink));
            ry = Side(RightX, ry, RightWidth, Ui("PERF_MEMORY_HEADING"), memory);
            ry = TweaksPanel(RightX, ry, RightWidth);
            y = Math.Max(ly, ry - 8) + 6;
            string note = Comparison(s, frames);
            return Foot(y, note, fy =>
            {
                float right = Width - Pad;
                float rw = RecordButton(fy, right);
                string label = copied ?? Ui("PERF_COPY_REPORT");
                float cw = Math.Max(ui.Width(Ui("PERF_COPY_REPORT"), 14f, PerfUi.Title), ui.Width(Ui("PERF_COPIED"), 14f, PerfUi.Title)) + 26;
                bool justCopied = copied != null;
                if (fy >= 0 && ui.Button("copy", right - rw - 8 - cw, fy, cw, 26, label, justCopied ? PerfUi.Go : PerfUi.ButtonBack, justCopied ? PerfUi.GoRule : PerfUi.ButtonRule)) CopyReport();
                return rw + 8 + cw;
            });
        }

        /// The vertical sync switch in the Threads panel: TURN OFF or TURN ON (the Moono Tweaks
        /// row's two values, Game default and Off, applied now and saved), or LAUNCH OPTION, not
        /// clickable, when `--vsync-count` sets it. With y < 0 only its width.
        float VSyncButton(float y, float right, int value, bool locked)
        {
            string label = locked ? Ui("PERF_VSYNC_LAUNCH_OPTION") : value == 1 ? Ui("PERF_VSYNC_TURN_ON") : Ui("PERF_VSYNC_TURN_OFF");
            float w = Math.Max(ui.Width(Ui("PERF_VSYNC_TURN_OFF"), 12f, PerfUi.Title), Math.Max(ui.Width(Ui("PERF_VSYNC_TURN_ON"), 12f, PerfUi.Title),
                ui.Width(Ui("PERF_VSYNC_LAUNCH_OPTION"), 12f, PerfUi.Title))) + 16;
            if (y < 0) return w;
            if (ui.Button("vsync", right - w, y, w, 20, label, PerfUi.ButtonBack, PerfUi.ButtonRule, 12f, !locked))
            {
                var why = MoonoTweaksMenu.SetLive("vsync", value == 1 ? 0 : 1);
                if (why != null) global::Debug.LogWarning("[Moono] performance window: vertical sync not changed: " + why);
                // Measured afresh with the new setting.
                takes.Clear();
                held.Reset();
                busyShare = double.NaN;
            }
            return w;
        }

        // The Tweaks panel's open list: 0 none, 1 adjusted for mods, 2 switched off by you.
        int tweaksOpen;

        /// The Tweaks panel: tweaks on, those adjusted for the player's mods and those the player
        /// switched off, the last two opening (a click) to what each one is, in plain words, with
        /// its effect where it was measured. Neutral colours: adjusting for a mod is normal.
        float TweaksPanel(float x, float y, float w)
        {
            const float line = 24f, itemSize = 12f;
            var adjusted = PerfReads.Adjusted();
            int stepped = PerfReads.SteppedAside();
            var off = PerfOverlay.TweaksSwitchedOff;
            var lines = new List<(string Label, string Value, int List, int Count)>
            {
                (Ui("PERF_TWEAKS_ACTIVE"), string.Format(C, Ui("PERF_OF_N"), PerfOverlay.TweaksOn.Length, PerfOverlay.TweaksAll), 0, 0),
                (Ui("PERF_TWEAKS_STEPPED"), stepped.ToString(C), 1, stepped),
                (Ui("PERF_TWEAKS_OFF"), off.Length.ToString(C), 2, off.Length),
            };
            if (tweaksOpen == 1 && stepped == 0 || tweaksOpen == 2 && off.Length == 0) tweaksOpen = 0;
            // The open list's items (and its note), measured first for the panel's height.
            var items = new List<string>();
            if (tweaksOpen == 1)
            {
                items.Add(Ui("PERF_TWEAKS_ADJUSTED_NOTE"));
                foreach (var o in adjusted) items.Add(AdjustedText(o));
            }
            else if (tweaksOpen == 2)
                foreach (var id in off)
                {
                    string title = TweakTitle(id, out string gain);
                    items.Add(string.Format(C, Ui("PERF_TWEAKS_OFF_ITEM"), title, gain.Length > 0 ? string.Format(C, Ui("PERF_EFFECT_GAIN"), gain) : Ui("PERF_EFFECT_NOT_MEASURED")));
                }
            float tw = w - 24;
            var heights = items.ConvertAll(t => ui.Height(t, itemSize, tw));
            float listH = 0;
            foreach (var hh in heights) listH += hh + 6;
            float h = 9 + 20 + lines.Count * line + (items.Count > 0 ? listH + 4 : 0) + 6;
            ui.Box(x, y, w, h, PerfUi.Panel2, PerfUi.Round6);
            HeadText(x + 12, y + 7, w - 24, Ui("PERF_TWEAKS_HEADING"));
            float ly = y + 29;
            for (int i = 0; i < lines.Count; i++)
            {
                var (label, value, list, count) = lines[i];
                bool opens = count > 0;
                bool isOpen = opens && tweaksOpen == list;
                if (isOpen) ui.Box(x + 6, ly, w - 12, line, new Color(1, 1, 1, 0.08f), PerfUi.Round4);
                if (opens && ui.Area("tweaks" + list, x + 6, ly, w - 12, line, out _)) tweaksOpen = isOpen ? 0 : list;
                string shown = opens ? value + "<color=#" + PerfUi.MutedHex + ">  " + (isOpen ? "–" : "+") + "</color>" : value;
                float vw = ui.Width(shown, 13f) + 2;
                ui.Text(x + 12, ly, w - 24 - vw - 10, line, label, 13f, PerfUi.Muted, TextAlignmentOptions.MidlineLeft, null, false, true);
                ui.Text(x + w - 12 - vw, ly, vw, line, "<b>" + shown + "</b>", 13f, PerfUi.Ink, TextAlignmentOptions.MidlineRight);
                ly += line;
                if (isOpen)
                {
                    float iy = ly + 2;
                    for (int k = 0; k < items.Count; k++)
                    {
                        ui.Text(x + 12, iy, tw, heights[k], items[k], itemSize, k == 0 && tweaksOpen == 1 ? PerfUi.Muted : PerfUi.Ink, TextAlignmentOptions.TopLeft, null, true);
                        iy += heights[k] + 6;
                    }
                    ly = iy + 2;
                }
                if (i < lines.Count - 1) ui.Box(x + 12, ly - 1, w - 24, 1, PerfUi.Line);
            }
            return y + h + 8;
        }

        /// One case of a speed-up adjusted for a mod, in plain words: which speed-up, which part,
        /// which mod, and the effect where it was measured (ColonySummaryWrite's 100-126 ms a save
        /// on a late-game colony), else "effect not measured".
        static string AdjustedText(Overlap o)
        {
            string title = TweakTitle(o.Tweak, out _);
            string effect = o.Tweak == "background-save" && o.Method == "RetireColonyUtility.SaveColonySummaryData"
                ? Ui("PERF_EFFECT_COLONY_SUMMARY") : Ui("PERF_EFFECT_NOT_MEASURED");
            return string.Format(C, Ui("PERF_TWEAKS_ADJUSTED_ITEM"), title, o.Method, ModOverlapsCore.StripTags(o.Mod), effect);
        }

        /// A tweak's title in the game's language (the Moono Tweaks rows), and its measured gain
        /// ("" for none); its name when the menu is off.
        static string TweakTitle(string id, out string gain)
        {
            gain = "";
            var ids = MoonoTweaksMenu.Ids;
            if (ids != null && MoonoTweaksMenu.Titles != null)
                for (int i = 0; i < ids.Length; i++)
                    if (ids[i] == id)
                    {
                        gain = MoonoTweaksMenu.Gains != null && i < MoonoTweaksMenu.Gains.Length ? MoonoTweaksMenu.Gains[i] ?? "" : "";
                        return MoonoTweaksMenu.Titles[i];
                    }
            return id;
        }

        float SubRow(float y, string name, string value)
        {
            ui.Text(Pad + 22, y, 300, 20, name, 12.5f, PerfUi.Muted);
            ui.Text(Pad + LeftWidth - 40 - 10 - 62, y, 62, 20, value, 12.5f, PerfUi.Muted, TextAlignmentOptions.MidlineRight);
            return y + 20;
        }

        /// The update rate's name ("Simulation, every 0.2 s").
        static string RateName(UpdateRate rate) => rate switch
        {
            UpdateRate.SIM_EVERY_TICK => Ui("PERF_RATE_SIM_TICK"),
            UpdateRate.SIM_33ms => Ui("PERF_RATE_SIM_FAST"),
            UpdateRate.SIM_200ms => Ui("PERF_RATE_SIM_FIFTH"),
            UpdateRate.SIM_1000ms => Ui("PERF_RATE_SIM_SECOND"),
            UpdateRate.SIM_4000ms => Ui("PERF_RATE_SIM_FOUR_SECONDS"),
            UpdateRate.RENDER_EVERY_TICK => Ui("PERF_RATE_RENDER_TICK"),
            UpdateRate.RENDER_200ms => Ui("PERF_RATE_RENDER_FIFTH"),
            UpdateRate.RENDER_1000ms => Ui("PERF_RATE_RENDER_SECOND"),
            _ => rate.ToString(),
        };

        /// The entries inside an open row: Game update's state machine updates by rate (ms a
        /// frame, runs a second, objects a run, the longest run), the others' phases (ms a frame,
        /// the longest frame).
        float Drill(int row, double[] s, double frames, double rowMs, float y)
        {
            float x = Pad + 22, w = LeftWidth - 22;
            var lines = new List<string[]>();
            string[] heads;
            float[] cols;
            string note = null;
            if (row == 0)
            {
                heads = new[] { Ui("PERF_MS"), Ui("PERF_DRILL_RUNS"), Ui("PERF_DRILL_OBJECTS"), Ui("PERF_DRILL_WORST") };
                cols = new float[] { 50, 66, 62, 50 };
                if (bucketsWhy != null) note = string.Format(C, Ui("PERF_DRILL_NOT_AVAILABLE"), bucketsWhy);
                else
                {
                    double framesSeen = Math.Max(1, PerfBuckets.Frames);
                    double secondsSeen = Math.Max(1e-3, Time.realtimeSinceStartup - bucketsSince);
                    var kinds = PerfBuckets.Kinds.Where(k => k.Calls > 0).OrderByDescending(k => k.Ticks).ToList();
                    double bucketsMs = 0;
                    foreach (var k in kinds)
                    {
                        double ms = PerfBuckets.TicksToMs(k.Ticks);
                        bucketsMs += ms;
                        lines.Add(new[] { RateName(k.Rate), (ms / framesSeen).ToString("F2", C), (k.Calls / secondsSeen).ToString("F0", C),
                            (k.Entries / (double)Math.Max(1, k.Calls)).ToString("N0", C), PerfBuckets.TicksToMs(k.MaxTicks).ToString("F1", C) });
                    }
                    if (kinds.Count > 0)
                        lines.Add(new[] { Ui("PERF_DRILL_NOT_IN_BUCKETS"), Math.Max(0, rowMs / frames - bucketsMs / framesSeen).ToString("F2", C), "", "", "" });
                }
            }
            else if (row == WaitRow)
            {
                heads = new[] { Ui("PERF_MS") };
                cols = new float[] { 50 };
                lines.Add(new[] { Ui("PERF_WAIT_DEVICE_FRAME"), (s[12] / frames).ToString("F2", C) });
                lines.Add(new[] { Ui("PERF_WAIT_QUEUED"), (s[15] / frames).ToString("F2", C) });
            }
            else
            {
                heads = new[] { Ui("PERF_MS"), Ui("PERF_DRILL_WORST") };
                cols = new float[] { 50, 50 };
                // "Showing the picture" without the waits shown in their own row (RowMs); its worst
                // frame includes them, so it is left out.
                double waits = Math.Min(PhaseMs(s, 3), s[12] + s[15]);
                foreach (int p in RowPhases[row])
                {
                    bool present = p == 3;
                    lines.Add(new[] { PhaseName(p), ((PhaseMs(s, p) - (present ? waits : 0)) / frames).ToString("F2", C), present ? "–" : PhaseMax(s, p).ToString("F1", C) });
                }
            }
            int count = note != null ? 1 : Math.Max(1, lines.Count);
            float h = 6 + 20 + count * 21 + 6;
            ui.Box(x, y + 4, w, h, PerfUi.Panel2, PerfUi.Round6);
            float cy = y + 4 + 6;
            float nameW = w - 20 - cols.Sum() - cols.Length * 8;
            void Cells(string[] v, float size, Color colour, Color first)
            {
                ui.Text(x + 10, cy, nameW, 20, v[0], size, first, TextAlignmentOptions.MidlineLeft, null, false, true);
                float cx = x + 10 + nameW + 8;
                for (int i = 0; i < cols.Length; i++)
                {
                    ui.Text(cx, cy, cols[i], 20, i + 1 < v.Length ? v[i + 1] : "", size, colour, TextAlignmentOptions.MidlineRight);
                    cx += cols[i] + 8;
                }
            }
            var head = new string[heads.Length + 1];
            head[0] = RowName(row);
            Array.Copy(heads, 0, head, 1, heads.Length);
            Cells(head, 12f, PerfUi.Muted, PerfUi.Muted);
            cy += 20;
            if (note != null)
            {
                ui.Text(x + 10, cy, w - 20, 21, note, 12.5f, PerfUi.Muted, TextAlignmentOptions.MidlineLeft, null, false, true);
                cy += 21;
            }
            else if (lines.Count == 0)
            {
                ui.Text(x + 10, cy, w - 20, 21, Ui("PERF_MEASURING"), 12.5f, PerfUi.Muted);
                cy += 21;
            }
            foreach (var l in lines)
            {
                ui.Box(x + 10, cy, w - 20, 1, PerfUi.Line);
                Cells(l, 12.5f, PerfUi.Ink, PerfUi.Ink);
                cy += 21;
            }
            return y + 4 + h + 6;
        }

        /// The newest recording's rows, split as the window's (RowMs: the waits out of Drawing);
        /// null without one. A recording older than the waits' rows has no waits: its Drawing and
        /// waits rows are left out (NaN).
        double[] RecordingRows()
        {
            var rec = lastRecording;
            if (rec == null || rec.Length < 3) return null;
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 2; i < rec.Length; i++)
            {
                int eq = rec[i].IndexOf('=');
                if (eq > 0 && double.TryParse(rec[i].Substring(eq + 1), NumberStyles.Float, C, out double v)) values[rec[i].Substring(0, eq)] = v;
            }
            bool missing = false;
            double Phase(int p)
            {
                if (values.TryGetValue(PhaseKeys[p], out double v)) return v;
                missing = true;
                return 0;
            }
            bool waitsKnown = values.TryGetValue("wait/device-frame", out double device) & values.TryGetValue("wait/queued-frames", out double queued);
            var rows = RowMs(Phase, waitsKnown ? device + queued : 0);
            if (missing) return null;
            if (!waitsKnown) rows[2] = rows[WaitRow] = double.NaN;
            return rows;
        }

        /// The recording's age now (it was read with its age then).
        double RecordingAge()
        {
            double.TryParse(lastRecording?.Length > 1 ? lastRecording[1] : null, NumberStyles.Float, C, out double age);
            return age + Math.Max(0, Time.realtimeSinceStartup - lastRecordingReadAt);
        }

        /// The footer's line: the six rows against the newest recording, the two that moved most.
        string Comparison(double[] s, double frames)
        {
            if (s[1] <= 0) return Ui("PERF_COMPARE_NONE");
            var then = RecordingRows();
            if (then == null) return Ui("PERF_COMPARE_NONE");
            var nowRows = RowMs(s);
            var diffs = new List<(double Delta, int Row)>();
            for (int r = 0; r < Rows; r++)
                if (!double.IsNaN(then[r])) diffs.Add((nowRows[r] / frames - then[r], r));
            if (diffs.Count == 0) return Ui("PERF_COMPARE_NONE");
            diffs.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)));
            string Part((double Delta, int Row) d) => RowName(d.Row) + " " + Signed(d.Delta, "F2") + " " + Ui("PERF_MS");
            return string.Format(C, Ui("PERF_COMPARE"), Ago(RecordingAge()), Part(diffs[0]) + (diffs.Count > 1 ? ", " + Part(diffs[1]) : ""));
        }

        static string Ago(double seconds)
        {
            if (seconds < 60) return string.Format(C, Ui("PERF_AGO_S"), Math.Max(0, Math.Round(seconds)));
            if (seconds < 3600) return string.Format(C, Ui("PERF_AGO_MIN"), Math.Round(seconds / 60));
            if (seconds < 86400 * 2) return string.Format(C, Ui("PERF_AGO_H"), Math.Round(seconds / 3600));
            return string.Format(C, Ui("PERF_AGO_DAYS"), Math.Round(seconds / 86400));
        }

        // ---- Hitches ----

        Texture2D graph;
        Color32[] pixels;
        const int GraphW = 492, GraphH = 120;
        readonly float[] columnMs = new float[GraphW];
        readonly byte[] columnKind = new byte[GraphW];

        static string CauseName(PerfCause cause) => cause switch
        {
            PerfCause.Load => Ui("PERF_CAUSE_LOAD"),
            PerfCause.FirstFrame => Ui("PERF_CAUSE_FIRST_FRAME"),
            PerfCause.Autosave => Ui("PERF_CAUSE_AUTOSAVE"),
            PerfCause.Save => Ui("PERF_CAUSE_SAVE"),
            PerfCause.Timelapse => Ui("PERF_CAUSE_TIMELAPSE"),
            PerfCause.Gc => Ui("PERF_CAUSE_GC"),
            PerfCause.SimWait => Ui("PERF_CAUSE_SIM_WAIT"),
            PerfCause.Jit => Ui("PERF_CAUSE_JIT"),
            PerfCause.World => Ui("PERF_CAUSE_WORLD"),
            PerfCause.Settling => Ui("PERF_CAUSE_SETTLING"),
            _ => Ui("PERF_CAUSE_UNKNOWN"),
        };

        double RangeSeconds(PerfTrack track) => range == 0 ? 120 : range == 1 ? 600 : Math.Max(120, track.HistorySeconds);

        float Hitches(PerfTrack track, double now)
        {
            float y = 14;
            bool inColony = PerfGame.InColony();
            float ly = y;
            if (!inColony)
            {
                // No colony: its loading times (and the session's causes, if a colony ran); the
                // stutters and freezes need one.
                ly = Fine(Pad, ly, LeftWidth, Ui("PERF_HITCHES_NO_COLONY"));
            }
            else ly = HitchesPlay(track, now, ly);

            // Right: by cause, time lost, loading.
            double minutes = track.ColonySeconds / 60;
            var causeLines = new List<(string, int, double, double)>();
            foreach (var cause in new[] { PerfCause.Autosave, PerfCause.Save, PerfCause.Timelapse, PerfCause.Gc, PerfCause.Jit, PerfCause.World, PerfCause.SimWait, PerfCause.Settling, PerfCause.FirstFrame, PerfCause.Load, PerfCause.Unknown })
            {
                int c = (int)cause;
                if (track.CauseTimes[c] > 0) causeLines.Add((cause == PerfCause.SimWait ? Ui("PERF_CAUSE_SIM_WAIT_SHORT") : CauseName(cause), track.CauseTimes[c], track.CauseWorst[c], track.CauseTotal[c]));
            }
            float ry = y;
            if (inColony || track.ColonySeconds > 0)
                ry = CauseTable(RightX, ry, RightWidth, string.Format(C, Ui("PERF_BY_CAUSE_HEADING"), Math.Round(minutes).ToString("N0", C)), causeLines);
            if (inColony)
            {
                // Time lost during play (unpaused, loading left out) per hour of play.
                double hours = Math.Max(track.PlaySeconds / 3600, 1e-9);
                double freezeMs = track.PlayFreezeMs, stutterMs = track.PlayStutterMs;
                bool enough = track.PlaySeconds >= 60;
                ry = Side(RightX, ry, RightWidth, Ui("PERF_LOST_HEADING"), new List<(string, string, Color)>
                {
                    (Ui("PERF_LOST_FREEZES"), enough ? PerHour(freezeMs / hours) : "–", PerfUi.Ink),
                    (Ui("PERF_LOST_STUTTERS"), enough ? PerHour(stutterMs / hours) : "–", stutterMs / hours > 60000 ? PerfUi.Warn : PerfUi.Ink),
                });
            }
            ry = Side(RightX, ry, RightWidth, Ui("PERF_LOADING_HEADING"), new List<(string, string, Color)>
            {
                (Ui("PERF_LOADING_MENU"), Seconds(track.StartToMenuMs), PerfUi.Ink),
                (Ui("PERF_LOADING_COLONY"), Seconds(track.LoadMs), PerfUi.Ink),
                (Ui("PERF_LOADING_FIRST_FRAME"), Seconds(track.FirstFrameMs), PerfUi.Ink),
                (Ui("PERF_LOADING_FIRST_SAVE"), double.IsNaN(track.FirstSaveMs) ? Ui("PERF_NONE_YET") : Seconds(track.FirstSaveMs), PerfUi.Ink),
            });
            y = Math.Max(ly, ry - 8) + 6;
            return Foot(y, Ui("PERF_HITCHES_FOOT"), fy => RecordButton(fy, Width - Pad));
        }

        /// The Hitches tab's left column in a colony: the headline boxes, the graph, the range,
        /// the freezes and the selected one's parts.
        float HitchesPlay(PerfTrack track, double now, float y)
        {
            double window = RangeSeconds(track);
            // The headline boxes describe play: unpaused colony frames, loading left out.
            var sum = track.Summarize(Math.Min(window, 120), now, play: true);
            var last = track.LastMinute();
            int perMinute = last.PerMinute(last.Stutters);
            bool worstInS = sum.MaxMs >= 1000;
            float ly = Headlines(Pad, y, LeftWidth, new List<(string, string, string)>
            {
                (Ui("PERF_KV_AVERAGE"), sum.Frames > 0 ? Fps(1000 / sum.MeanMs) : "–", Ui("PERF_FPS")),
                (Ui("PERF_KV_SLOWEST"), sum.Frames > 0 ? Fps(1000 / sum.P99Ms) : "–", Ui("PERF_FPS")),
                (Ui("PERF_KV_WORST"), sum.Frames == 0 ? "–" : worstInS ? (sum.MaxMs / 1000).ToString("F2", C) : sum.MaxMs.ToString("F0", C), worstInS ? Ui("PERF_S") : Ui("PERF_MS")),
                // Shown once enough play is measured (a rate from a few seconds is noise).
                (Ui("PERF_KV_STUTTERS"), last.Measured ? perMinute.ToString(C) : "–", last.Measured ? Ui("PERF_PER_MIN") : Ui("PERF_MEASURING_SHORT")),
            });

            // The graph.
            DrawGraph(track, window, now);
            ui.Box(Pad, ly, LeftWidth, GraphH, PerfUi.Panel2, PerfUi.Round4);
            ui.Picture(Pad, ly, LeftWidth, GraphH, graph);
            float k = (GraphH - 20) / 40f, baseY = GraphH - 10;
            // The 60 fps line's label on a backing of its own, so bars under it do not show through.
            string line60 = "60 " + Ui("PERF_FPS");
            float lw = ui.Width(line60, 11f) + 2;
            float labelY = ly + baseY - 16.7f * k - 17;
            ui.Box(Pad + 3, labelY, lw + 6, 15, new Color(0.18f, 0.2f, 0.27f, 0.92f), PerfUi.Round4);
            ui.Text(Pad + 6, labelY, lw, 15, line60, 11f, PerfUi.Muted);
            var inRange = track.Freezes.Where(f => now - f.At <= window).ToList();
            // One label only, the selected freeze's (labels of neighbouring bars overlapped).
            if (selected != null && inRange.Contains(selected))
            {
                float fx = Pad + LeftWidth - (float)((now - selected.At) / window * LeftWidth);
                string name = CauseName(selected.Cause);
                float nw = ui.Width(name, 11.5f) + 2;
                float lx = fx - nw - 6 >= Pad + 2 ? fx - nw - 6 : Math.Min(fx + 6, Pad + LeftWidth - nw - 2);
                ui.Box(lx - 3, ly + 3, nw + 6, 16, new Color(0.06f, 0.07f, 0.1f, 0.85f), PerfUi.Round4);
                ui.Text(lx, ly + 4, nw, 14, name, 11.5f, PerfUi.Ink);
            }
            if (ui.Area("graph", Pad, ly, LeftWidth, GraphH, out var at, 0.04f, false) && inRange.Count > 0)
            {
                double clickedAge = (LeftWidth - at.x) / LeftWidth * window;
                selected = inRange.OrderBy(f => Math.Abs((now - f.At) - clickedAge)).First();
            }
            ly += GraphH + 6;

            // Range and legend.
            string[] ranges = { Ui("PERF_RANGE_TWO_MIN"), Ui("PERF_RANGE_TEN_MIN"), Ui("PERF_RANGE_SESSION") };
            float rx = Pad;
            ui.Box(Pad, ly, 1, 20, PerfUi.Rule);
            for (int i = 0; i < 3; i++)
            {
                float w = ui.Width(ranges[i], 12f) + 18;
                ui.Box(rx, ly, w, 20, i == range ? new Color(1, 1, 1, 0.18f) : new Color(1, 1, 1, 0.03f));
                ui.Text(rx, ly, w, 20, ranges[i], 12f, i == range ? Color.white : PerfUi.Muted, TextAlignmentOptions.Center);
                if (ui.Area("range" + i, rx, ly, w, 20, out _, 0.14f)) range = i;
                rx += w;
                ui.Box(rx - 1, ly, 1, 20, PerfUi.Rule);
            }
            ui.Box(Pad, ly, rx - Pad, 1, PerfUi.Rule);
            ui.Box(Pad, ly + 19, rx - Pad, 1, PerfUi.Rule);
            rx += 14;
            foreach (var (colour, label) in new[] { (PerfUi.Accent, Ui("PERF_LEGEND_FRAME")), (PerfUi.Warn, Ui("PERF_LEGEND_STUTTER")), (PerfUi.Bad, Ui("PERF_LEGEND_FREEZE")) })
            {
                ui.Box(rx, ly + 5.5f, 9, 9, colour, PerfUi.Round4);
                float w = ui.Width(label, 12f);
                ui.Text(rx + 13, ly, w + 2, 20, label, 12f, PerfUi.Muted);
                rx += 13 + w + 14;
            }
            ly += 20 + 8;

            // The freezes, newest first, in five rows that are always there.
            var list = inRange.AsEnumerable().Reverse().Take(FreezeRows).ToList();
            float listTop = ly;
            if (list.Count == 0) Fine(Pad, ly, LeftWidth, Ui("PERF_NO_FREEZES"));
            foreach (var f in list)
            {
                bool isSel = selected == f;
                // The selected freeze: lit, with the accent bar at its left (as the graph's label).
                if (isSel)
                {
                    ui.Box(Pad - 4, ly + 1, LeftWidth + 8, 24, new Color(1, 1, 1, 0.12f), PerfUi.Round4);
                    ui.Box(Pad - 4, ly + 3, 3, 20, PerfUi.Accent);
                }
                if (ui.Area("freeze" + f.At.ToString("R", C), Pad - 4, ly + 1, LeftWidth + 8, 24, out _)) selected = isSel ? null : f;
                ui.Text(Pad, ly, 76, 26, Ago(now - f.At), 13.5f, PerfUi.Muted);
                string what = f.Paused ? string.Format(C, Ui("PERF_WHILE_PAUSED"), CauseName(f.Cause)) : CauseName(f.Cause);
                ui.Text(Pad + 82, ly, LeftWidth - 82 - 90, 26, what, 13.5f, PerfUi.Ink, TextAlignmentOptions.MidlineLeft, null, false, true);
                ui.Text(Pad + LeftWidth - 88, ly, 88, 26, Seconds(f.Ms), 13.5f, PerfUi.Ink, TextAlignmentOptions.MidlineRight);
                ui.Box(Pad, ly + 25, LeftWidth, 1, PerfUi.Line);
                ly += 26;
            }
            ly = listTop + FreezeRows * 26;
            // The stutters from waiting for the simulation, while they come.
            if (last.SimWaits > 0)
            {
                ui.Text(Pad, ly, 76, 26, Ui("PERF_ONGOING"), 13.5f, PerfUi.Muted);
                ui.Text(Pad + 82, ly, LeftWidth - 82 - 128, 26, CauseName(PerfCause.SimWait), 13.5f, PerfUi.Ink, TextAlignmentOptions.MidlineLeft, null, false, true);
                double typical = track.CauseTimes[(int)PerfCause.SimWait] > 0 ? track.CauseTotal[(int)PerfCause.SimWait] / track.CauseTimes[(int)PerfCause.SimWait] : 0;
                string rate = last.Measured ? last.PerMinute(last.SimWaits).ToString(C) + " " + Ui("PERF_PER_MIN") : Ui("PERF_MEASURING_SHORT");
                ui.Text(Pad + LeftWidth - 126, ly, 126, 26, typical.ToString("F0", C) + " " + Ui("PERF_MS") + " × " + rate, 13.5f, PerfUi.Ink, TextAlignmentOptions.MidlineRight);
                ly += 24;
                if (!PerfReads.SimStepPaceOn)
                {
                    float fh = ui.Height(Ui("PERF_SIM_WAIT_FIX"), 12.5f, LeftWidth - 82);
                    ui.Text(Pad + 82, ly, LeftWidth - 82, fh, Ui("PERF_SIM_WAIT_FIX"), 12.5f, PerfUi.Muted, TextAlignmentOptions.TopLeft, null, true);
                    ly += fh + 4;
                }
                ui.Box(Pad, ly + 1, LeftWidth, 1, PerfUi.Line);
                ly += 4;
            }
            if (selected != null)
            {
                string heading = string.Format(C, Ui("PERF_SELECTED_HEADING"), CauseName(selected.Cause), Ago(now - selected.At));
                ly = Section(Pad, ly + 8, LeftWidth, heading);
                var parts = Parts(selected);
                float ph = 8 + parts.Count * 24 + 4;
                ui.Box(Pad, ly, LeftWidth, ph, PerfUi.Panel2, PerfUi.Round6);
                float py = ly + 6;
                for (int i = 0; i < parts.Count; i++)
                {
                    if (i > 0) ui.Box(Pad + 10, py, LeftWidth - 20, 1, PerfUi.Line);
                    float vw = ui.Width(parts[i].Value, 13f) + 2;
                    ui.Text(Pad + 10, py, LeftWidth - 30 - vw, 24, parts[i].Name, 13f, PerfUi.Ink, TextAlignmentOptions.MidlineLeft, null, false, true);
                    ui.Text(Pad + LeftWidth - 10 - vw, py, vw, 24, "<b>" + parts[i].Value + "</b>", 13f, PerfUi.Ink, TextAlignmentOptions.MidlineRight);
                    py += 24;
                }
                ly += ph + 4;
            }
            return ly;
        }

        static string PerHour(double ms)
        {
            if (ms >= 60000) return string.Format(C, Ui("PERF_MIN_PER_HOUR"), (ms / 60000).ToString("F1", C));
            return string.Format(C, Ui("PERF_S_PER_HOUR"), (ms / 1000).ToString(ms >= 10000 ? "F0" : "F1", C));
        }

        float CauseTable(float x, float y, float w, string heading, List<(string Name, int Times, double Worst, double Total)> lines)
        {
            const float line = 23f;
            int rows = Math.Max(1, lines.Count);
            float h = 9 + 20 + 18 + rows * line + 6;
            ui.Box(x, y, w, h, PerfUi.Panel2, PerfUi.Round6);
            HeadText(x + 12, y + 7, w - 24, heading);
            float[] cols = { 44, 56, 50 };
            float nameW = w - 24 - cols.Sum() - 3 * 8;
            float ly = y + 29;
            void Cells(string a, string b, string c, string d, float size, Color colour, Color first)
            {
                ui.Text(x + 12, ly, nameW, line, a, size, first, TextAlignmentOptions.MidlineLeft, null, false, true);
                float cx = x + 12 + nameW + 8;
                string[] v = { b, c, d };
                for (int i = 0; i < 3; i++)
                {
                    ui.Text(cx, ly, cols[i], line, v[i], size, colour, TextAlignmentOptions.MidlineRight);
                    cx += cols[i] + 8;
                }
            }
            Cells(Ui("PERF_CAUSE_COLUMN"), Ui("PERF_TIMES_COLUMN"), Ui("PERF_WORST_COLUMN"), Ui("PERF_TOTAL_COLUMN"), 12f, PerfUi.Muted, PerfUi.Muted);
            ly += 18;
            if (lines.Count == 0)
            {
                ui.Text(x + 12, ly, w - 24, line, Ui("PERF_NO_CAUSES"), 12.5f, PerfUi.Muted);
                ly += line;
            }
            foreach (var l in lines)
            {
                ui.Box(x + 12, ly, w - 24, 1, PerfUi.Line);
                Cells(l.Name, l.Times.ToString("N0", C), l.Worst >= 1000 ? Seconds(l.Worst) : Ms(l.Worst, "F0"), Seconds(l.Total), 13f, PerfUi.Ink, PerfUi.Ink);
                ly += line;
            }
            return y + h + 8;
        }

        /// What a freeze was made of, where the engine or the game says. The background write's
        /// time only for the newest save, once its own write has finished.
        List<(string Name, string Value)> Parts(PerfTrack.Freeze f)
        {
            var parts = new List<(string, string)>();
            double frame = f.Ms - f.PreviewMs;
            double rest = Math.Max(0, frame - f.GcMs - f.JitMs);
            bool save = f.Cause == PerfCause.Autosave || f.Cause == PerfCause.Save;
            parts.Add((save ? Ui("PERF_PART_SAVE_DATA") : Ui("PERF_PART_FRAME"), Seconds(rest)));
            if (f.GcMs >= 1) parts.Add((Ui("PERF_PART_GC"), Seconds(f.GcMs)));
            if (f.JitMs >= 1) parts.Add((Ui("PERF_PART_JIT"), Seconds(f.JitMs)));
            if (f.PreviewMs > 0) parts.Add((Ui("PERF_PART_PREVIEW"), Seconds(f.PreviewMs)));
            if (save && f == LastSave() && PerfOverlay.Engine?.Invoke() is double[] e && e.Length > 8 && e[6] > 0 && e[7] == e[8])
                parts.Add((Ui("PERF_PART_BACKGROUND_WRITE"), Seconds(e[6])));
            return parts;
        }

        static PerfTrack.Freeze LastSave()
        {
            var list = PerfOverlay.Track.Freezes;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].Cause == PerfCause.Autosave || list[i].Cause == PerfCause.Save) return list[i];
            return null;
        }

        /// The frame-time graph (GraphW x GraphH pixels): the 60 fps line, the longest frame of
        /// each column as a line, stutters as yellow bars, freezes as red ones.
        void DrawGraph(PerfTrack track, double window, double now)
        {
            if (graph == null)
            {
                graph = new Texture2D(GraphW, GraphH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                pixels = new Color32[GraphW * GraphH];
            }
            track.Columns(window, now, columnMs, columnKind);
            var clear = new Color32(0, 0, 0, 0);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;
            float k = (GraphH - 20) / 40f, baseY = 10;  // rows from the bottom
            Color32 accent = PerfUi.Accent, warn = PerfUi.Warn, bad = PerfUi.Bad, dash = new Color32(255, 255, 255, 48);
            warn.a = 128;
            int y60 = (int)(baseY + 16.7f * k);
            for (int x = 0; x < GraphW; x += 6)
                for (int d = 0; d < 3 && x + d < GraphW; d++) pixels[y60 * GraphW + x + d] = dash;
            int prev = -1;
            for (int x = 0; x < GraphW; x++)
            {
                float m = columnMs[x];
                if (columnKind[x] == 2)
                {
                    for (int yy = 10; yy < GraphH - 10; yy++)
                        for (int d = -1; d <= 1; d++)
                            if (x + d >= 0 && x + d < GraphW) pixels[yy * GraphW + x + d] = bad;
                }
                else if (columnKind[x] == 1)
                {
                    int top = Math.Min(GraphH - 2, (int)(baseY + m * k));
                    for (int yy = (int)baseY; yy <= top; yy++) pixels[yy * GraphW + x] = warn;
                }
                if (m <= 0)
                {
                    prev = -1;
                    continue;
                }
                int yv = Math.Min(GraphH - 2, (int)(baseY + Math.Min(m, 39.5f) * k));
                int a = prev < 0 ? yv : prev, b = yv;
                if (a > b) (a, b) = (b, a);
                for (int yy = a; yy <= b; yy++) pixels[yy * GraphW + x] = accent;
                if (yv + 1 < GraphH) pixels[(yv + 1) * GraphW + x] = accent;
                prev = yv;
            }
            graph.SetPixels32(pixels);
            graph.Apply(false, false);
        }

        // ---- Copy report ----

        void CopyReport()
        {
            var track = PerfOverlay.Track;
            double now = track.Now;
            var r = new StringBuilder();
            var sum = track.Summarize(10, now);
            var s = Sums();
            double frames = Math.Max(1, s[0]);
            r.Append("Moono performance report, ").Append(System.DateTime.Now.ToString("yyyy-MM-dd HH:mm", C)).Append('\n');
            r.Append("Light: ").Append(track.Light == 2 ? "not keeping up" : track.Light == 1 ? "stuttering" : "good").Append('\n');
            r.Append(string.Format(C, "Last 10 s: {0} frames, mean {1:F2} ms ({2:F0} fps), median {3:F2}, p90 {4:F2}, p99 {5:F1}, longest {6:F0} ms\n",
                sum.Frames, sum.MeanMs, sum.MeanMs > 0 ? 1000 / sum.MeanMs : 0, sum.MedianMs, sum.P90Ms, sum.P99Ms, sum.MaxMs));
            r.Append(string.Format(C, "Game speed reached {0:F2}x of {1:F0}x\n", double.IsNaN(track.KeepUp) ? 0 : track.KeepUp * track.Speed, track.Speed));
            if (s[1] > 0)
            {
                // The rows as the window splits them (the waits out of Drawing).
                var rows = RowMs(s);
                for (int row = 0; row < Rows; row++)
                {
                    r.Append(string.Format(C, "  {0}: {1:F2} ms ({2:F0}%)\n", RowName(row), rows[row] / frames, 100 * rows[row] / s[1]));
                    if (row < 5 && RowPhases[row].Length > 1)
                        foreach (int p in RowPhases[row])
                        {
                            double ms = PhaseMs(s, p) - (p == 3 ? rows[WaitRow] : 0);
                            r.Append(string.Format(C, "    {0}: {1:F2} ms\n", PhaseName(p), ms / frames));
                        }
                    if (row == WaitRow)
                        r.Append(string.Format(C, "    the device's next frame {0:F2} ms, queued pictures {1:F2} ms\n", s[12] / frames, s[15] / frames));
                }
                r.Append(string.Format(C, "  inside the rows: waiting for graphics results {0:F2} ms, GC {1:F2} ms a frame\n", (s[13] + s[14]) / frames, s[18] / frames));
            }
            r.Append(string.Format(C, "Vertical sync: {0}, refresh {1}, held back by: {2}{3}\n", VSyncOn ? "on (interval " + (PerfOverlay.VSyncInterval?.Invoke() ?? 0) + ")" : "off",
                heldHz > 0 ? heldHz.ToString("F2", C) + " Hz" : "unknown", held.Shown,
                double.IsNaN(busyShare) ? "" : string.Format(C, ", main thread busy {0:F0}%", 100 * busyShare)));
            if (PerfBuckets.On)
            {
                double framesSeen = Math.Max(1, PerfBuckets.Frames);
                foreach (var k in PerfBuckets.Kinds.Where(k => k.Calls > 0).OrderByDescending(k => k.Ticks))
                    r.Append(string.Format(C, "    state machines {0}: {1:F3} ms a frame, {2} runs, {3} objects, longest {4:F2} ms\n", k.Rate,
                        PerfBuckets.TicksToMs(k.Ticks) / framesSeen, k.Calls, k.Entries, PerfBuckets.TicksToMs(k.MaxTicks)));
            }
            var last = track.LastMinute();
            r.Append(string.Format(C, "Last minute: {0:F0} s of play{1}, {2} stutters ({3} a minute: {4} waiting for the simulation, {5} unknown), {6} freezes\n",
                last.PlaySeconds, last.Measured ? "" : " (too little for a rate)", last.Stutters, last.PerMinute(last.Stutters), last.PerMinute(last.SimWaits), last.PerMinute(last.Unknown), last.Freezes));
            r.Append(string.Format(C, "Session: {0:F0} min in a colony, {1:F0} min of play; longest GC pause {2:F0} ms\n", track.ColonySeconds / 60, track.PlaySeconds / 60, track.LongestGcMs));
            foreach (PerfCause cause in Enum.GetValues(typeof(PerfCause)))
            {
                int c = (int)cause;
                if (c < track.CauseTimes.Length && track.CauseTimes[c] > 0)
                    r.Append(string.Format(C, "  {0}: {1} times, worst {2:F0} ms, total {3:F1} s\n", cause, track.CauseTimes[c], track.CauseWorst[c], track.CauseTotal[c] / 1000));
            }
            foreach (var f in track.Freezes.Skip(Math.Max(0, track.Freezes.Count - 10)))
                r.Append(string.Format(C, "  freeze {0:F0} s ago: {1}, {2:F0} ms (GC {3:F0}, JIT {4:F0}, preview {5:F0})\n", now - f.At, f.Cause, f.Ms, f.GcMs, f.JitMs, f.PreviewMs));
            string Opt(double ms, string format) => double.IsNaN(ms) ? "none yet" : ms.ToString(format, C);
            r.Append(string.Format(C, "Loading: start to menu {0} s, colony {1} s, first frames {2} ms, first save {3} ms\n",
                Opt(track.StartToMenuMs / 1000, "F1"), Opt(track.LoadMs / 1000, "F1"), Opt(track.FirstFrameMs, "F0"), Opt(track.FirstSaveMs, "F0")));
            if (engineNow != null) r.Append(string.Format(C, "Memory: {0:F1} GB in use of {1:F1} GB\n", engineNow[0] / 1073741824.0, engineNow[1] / 1073741824.0));
            if (gcRated) r.Append(string.Format(C, "Clean-ups a minute: {0:F0} small, {1:F0} full\n", gcSmall, gcFull));
            r.Append(string.Format(C, "Tweaks: {0} on of {1}, {2} adjusted for mods, {3} switched off\n", PerfOverlay.TweaksOn.Length, PerfOverlay.TweaksAll, PerfReads.SteppedAside(), PerfOverlay.TweaksSwitchedOff.Length));
            r.Append("Tweaks on: ").Append(string.Join(", ", PerfOverlay.TweaksOn)).Append('\n');
            foreach (var o in PerfReads.Adjusted()) r.Append(string.Format(C, "  adjusted: {0} | {1} | {2}\n", o.Tweak, o.Method, o.Mod));
            if (PerfOverlay.TweaksSwitchedOff.Length > 0) r.Append("Switched off: ").Append(string.Join(", ", PerfOverlay.TweaksSwitchedOff)).Append('\n');
            var then = RecordingRows();
            if (then != null)
            {
                r.Append(string.Format(C, "Last recording ({0}, {1:F0} s ago), ms a frame:", lastRecording[0], RecordingAge()));
                for (int row = 0; row < Rows; row++) r.Append(string.Format(C, " {0} {1}", RowName(row), double.IsNaN(then[row]) ? "–" : then[row].ToString("F2", C)));
                r.Append('\n');
            }
            if (track.LoadFrames.Count > 0)
                r.Append("Last load's frames in the colony (ms, GC, JIT; phases): ").Append(string.Join(", ", track.LoadFrames.Select(LoadLine))).Append('\n');
            r.Append("Newest slow frames (s ago, ms, cause; what the engine and the game reported):\n");
            foreach (var f in track.SlowFrames().Reverse().Take(20).Reverse()) r.Append("  ").Append(SlowLine(f, now)).Append('\n');
            report = r.ToString();
            try
            {
                GUIUtility.systemCopyBuffer = report;
                copied = Ui("PERF_COPIED");
            }
            catch (Exception e)
            {
                global::Debug.LogWarning("[Moono] performance window: the report could not be copied: " + e.Message);
            }
            copiedFor = 2f;
        }

        internal string report;

        /// A load's frame: its time, GC and JIT and, where the engine timed it, its phases. The
        /// first timed frame's early phase is not clocked from its start, so "early and pending
        /// Starts" is the frame's rest (its time less the other phases).
        static string LoadLine(PerfTrack.Slow f)
        {
            string s = string.Format(C, "{0:F0} ({1:F0}, {2:F0}", f.Ms, f.GcMs, f.JitMs);
            if (f.Phases != null)
            {
                double others = 0;
                for (int p = 0; p < f.Phases.Length; p++) if (p != 4) others += f.Phases[p];
                s += string.Format(C, "; update {0:F0}, late {1:F0}, canvases {2:F0}, render {3:F0}, present {4:F0}, end of frame {5:F0}, between frames {6:F0}, early and pending Starts {7:F0}",
                    f.Phases[5], f.Phases[6], f.Phases[7], f.Phases[0], f.Phases[3], f.Phases[2], f.Phases[9], Math.Max(0, f.Ms - others));
            }
            return s + ")";
        }

        /// A slow frame and its signs, for the copy report and the test runs.
        static string SlowLine(PerfTrack.Slow f, double now) => string.Format(C,
            "{0:F1} s ago {1:F1} ms {2}{3}{4}: gc {5:F1} jit {6:F1} game_dt {7:F3} subticks {8}{9}{10}{11}{12}",
            now - f.At, f.Ms, f.Cause, f.Stutter ? " stutter" : "", f.Freeze ? " freeze" : "", f.GcMs, f.JitMs, f.GameDt, f.SubTicks,
            f.Step200 ? " step200" : "", f.ZeroSubTick ? " zero-time" : "", f.Paused ? " paused" : "", f.Loading ? " loading" : f.Colony ? "" : " no-colony") + (f.World ? " world-switch" : "");

        // ---- Test runs ----

        internal string Test(string verb, string arg)
        {
            switch (verb)
            {
                case "tab":
                    tab = arg == "frame" ? 1 : arg == "hitches" ? 2 : 0;
                    expanded = -1;
                    selected = null;
                    countsAge = 99f;
                    footFloor = 0;
                    Measure();
                    Sample();
                    Build();
                    return "ok " + TabName;
                case "expand":
                    expanded = arg == "none" ? -1 : int.Parse(arg, C);
                    footFloor = 0;
                    Measure();
                    Build();
                    return "ok expanded=" + expanded + " buckets=" + PerfBuckets.On;
                case "range":
                    range = int.Parse(arg, C);
                    Build();
                    return "ok";
                case "select":
                    var list = PerfOverlay.Track.Freezes;
                    int i = int.Parse(arg, C);
                    selected = i >= 0 && i < list.Count ? list[list.Count - 1 - i] : null;
                    Build();
                    return "ok selected=" + (selected != null ? selected.Cause + " " + selected.Ms.ToString("F0", C) : "none");
                case "copy":
                    CopyReport();
                    return "ok " + (report ?? "").Replace('\n', '|');
                case "worlds":
                    return "ok " + PerfReads.Worlds() + " | asteroids=" + counts.Asteroids;
                case "slow":
                {
                    // slow [all|unknown|CAUSE]: the newest slow frames kept (up to 256), one a line.
                    var track = PerfOverlay.Track;
                    var lines = (arg == "load" ? (IEnumerable<PerfTrack.Slow>)track.LoadFrames : track.SlowFrames()).Where(f => string.IsNullOrEmpty(arg) || arg == "all" || arg == "load" || string.Equals(f.Cause.ToString(), arg, StringComparison.OrdinalIgnoreCase))
                        .Select(f => arg == "load" ? LoadLine(f) : SlowLine(f, track.Now));
                    return "ok " + string.Join(" | ", lines);
                }
                case "build":
                    Sample();
                    Build();
                    return "ok height=" + height.ToString("F0", C);
                case "buttons":
                    // What a click at each place would hit: the chrome's (close, tabs), then the tab's.
                    return "ok chrome " + chrome.Describe() + " | body " + ui.Describe();
                case "latency":
                {
                    // The newest click (a press and its release on one control) and the frame its
                    // result was built in; the screen shows that frame's canvases.
                    return string.Format(C, "ok click_frame={0} built_frame={1} frames={2} now_frame={3} tab={4} expanded={5} range={6} selected={7} tweaks_open={8} scan={9} copied={10} recording={11} vsync_on={12} hz={13:F2} screen_limited={14} busy={15:F2} held={16} dropped={17} hidden={18} buckets={19}",
                        ClickFrame, ClickBuiltFrame, ClickBuiltFrame - ClickFrame, Time.frameCount, TabName, expanded, range, selected != null ? selected.Cause + " " + selected.Ms.ToString("F0", C) : "none",
                        tweaksOpen, scanState, copied != null, PerfOverlay.Recording, VSyncOn, heldHz, ScreenLimited, busyShare, held.Shown, ui.Dropped + chrome.Dropped, hidden,
                        string.Join(",", PerfBuckets.Kinds.Where(k => k.Calls > 0).Select(k => k.Rate + ":" + k.Calls)));
                }
                default:
                    return "unknown " + verb;
            }
        }
    }
}
