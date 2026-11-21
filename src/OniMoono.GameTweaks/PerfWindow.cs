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
    /// opening to what is inside it, the spread of frame times, threads, memory, tweaks. Hitches:
    /// the frame-time graph with its stutters and freezes, the freezes with their causes
    /// (PerfTrack: only where the engine or the game reports one) and the loading times.
    ///
    /// It refreshes once a second while it shows; closed, it measures nothing. The Colony and
    /// Frame tabs keep the engine's frame window open (PerfOverlay.Timing); Frame's "Game update"
    /// row, while open, the bucket profile (PerfBuckets), which costs some frame rate.
    internal sealed class PerfWindow
    {
        static string Ui(string name) => PerfOverlay.Ui(name);
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        const float Width = 880f, TitleHeight = 32f, TabsHeight = 33f, Pad = 16f, LeftWidth = 492f, Gap = 16f, RightWidth = 340f;
        const float RightX = Pad + LeftWidth + Gap;

        readonly PerfOverlay overlay;
        readonly RectTransform rect, body;
        readonly Image shadow;
        readonly LocText title;
        readonly PerfUi ui, chrome;
        float height = 400f;
        Vector2 topLeft;
        bool open, dirty;
        float elapsed;
        int tab;  // 0 colony, 1 frame, 2 hitches
        int expanded = -1;  // the Frame tab's open row
        int range;  // Hitches: 2 min, 10 min, the session
        PerfTrack.Freeze selected;
        string copied;
        float copiedFor;

        internal bool Open => open;
        internal string TabName => tab == 0 ? "colony" : tab == 1 ? "frame" : "hitches";
        internal string RectText => string.Format(C, "{0:F0},{1:F0},{2:F0}x{3:F0}", topLeft.x, topLeft.y, Width, height);

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
            title = PerfUi.NewText(go.transform, "Title");
            PerfUi.Place(title.rectTransform, 14, 0, 400, TitleHeight);
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

        /// Opens below the badge (above it when the badge is low on the screen), on Colony.
        internal void Show(Vector2 badgeCentre, Vector2 badgeSize)
        {
            open = true;
            tab = 0;
            expanded = -1;
            selected = null;
            rect.gameObject.SetActive(true);
            Measure();
            Build();
            var canvas = overlay.CanvasSize;
            float y = badgeCentre.y + badgeSize.y / 2 + 8;
            if (y + height > canvas.y && badgeCentre.y > canvas.y / 2) y = badgeCentre.y - badgeSize.y / 2 - 8 - height;
            topLeft = new Vector2(badgeCentre.x - Width / 2, y);
            Clamp();
        }

        internal void Close()
        {
            if (!open) return;
            open = false;
            rect.gameObject.SetActive(false);
            Measure();
        }

        /// The window's title bar was dragged: `centre` is where its top left would go.
        internal void MoveTo(Vector2 newTopLeft)
        {
            topLeft = newTopLeft;
            Clamp();
        }

        void Clamp()
        {
            var canvas = overlay.CanvasSize;
            topLeft.x = Mathf.Clamp(topLeft.x, 2, Math.Max(2, canvas.x - Width - 2));
            topLeft.y = Mathf.Clamp(topLeft.y, 2, Math.Max(2, canvas.y - height - 2));
            var pos = new Vector2(topLeft.x, -topLeft.y);
            if (rect.anchoredPosition != pos) rect.anchoredPosition = pos;
        }

        // ---- What it measures, by tab ----

        bool timingOn;

        /// The engine's frame window while the Colony or Frame tab shows; the bucket profile while
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
                var why = PerfBuckets.Switch(buckets);
                if (why != null) global::Debug.LogWarning("[Moono] performance window: the look inside Game update is off: " + why);
                bucketsSince = Time.realtimeSinceStartup;
            }
        }

        float bucketsSince;
        // The engine's per-second sums (PerfOverlay.Take), the last ten.
        readonly List<double[]> takes = new List<double[]>();
        double[] engineNow;
        PerfReads.ColonyCounts counts = new PerfReads.ColonyCounts { Cycle = -1, Duplicants = -1, Critters = -1, LooseItems = -1, Buildings = -1, Utilities = -1, Asteroids = -1 };
        float countsAge = 99f;
        string[] lastRecording;
        bool lastRecordingRead, wasRecording;
        double[] gcLast;
        double gcLastAt;
        double[] gcRates = new double[3];
        bool gcRated;
        double allocPerFrame = double.NaN;

        internal void Update(float dt)
        {
            elapsed += dt;
            countsAge += dt;
            if (copiedFor > 0 && (copiedFor -= dt) <= 0)
            {
                copied = null;
                dirty = true;
            }
            if (elapsed >= 1f)
            {
                elapsed = 0f;
                Sample();
                dirty = true;
            }
            if (!dirty && !ui.Clicked && !chrome.Clicked) return;
            dirty = false;
            Build();
        }

        /// Once a second: the engine's sums, its memory numbers, the colony's counts (every 5 s on
        /// Colony).
        void Sample()
        {
            if (timingOn && PerfOverlay.Take?.Invoke() is double[] n && n[0] > 0)
            {
                takes.Add(n);
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
                    for (int g = 0; g < 3; g++) gcRates[g] = (engineNow[3 + g] - gcLast[3 + g]) * 60 / (now - gcLastAt);
                    gcRated = true;
                    gcLast = (double[])engineNow.Clone();
                    gcLastAt = now;
                }
                if (before != null && takes.Count > 0) allocPerFrame = (engineNow[2] - before[2]) / Math.Max(1, takes[takes.Count - 1][0]);
            }
            if (tab == 0 && countsAge >= 5f)
            {
                counts = PerfReads.Colony();
                countsAge = 0f;
            }
            bool recordingNow = PerfOverlay.Recording != null && PerfOverlay.Recording();
            if (!lastRecordingRead || (wasRecording && !recordingNow))
            {
                lastRecordingRead = true;
                lastRecording = SafeLastRecording();
            }
            wasRecording = recordingNow;
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
                    s[i] = i == 20 || i >= 21 ? Math.Max(s[i], t[i]) : s[i] + t[i];
            return s;
        }

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
            Clamp();
        }

        void Chrome()
        {
            title.font = PerfUi.Title;
            title.fontSize = 18f;
            title.text = Ui("PERF_WINDOW_TITLE");
            chrome.Begin();
            if (chrome.Button(Width - 34, 4, 26, 24, "×", new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 22f)) Close();
            chrome.Box(0, TitleHeight, Width, TabsHeight, PerfUi.Panel2);
            chrome.Box(0, TitleHeight + TabsHeight - 1, Width, 1, PerfUi.Rule);
            string[] names = { Ui("PERF_TAB_COLONY"), Ui("PERF_TAB_FRAME"), Ui("PERF_TAB_HITCHES") };
            float x = 0;
            for (int i = 0; i < 3; i++)
            {
                float w = chrome.Width(names[i], 15f, PerfUi.Title) + 32;
                bool on = i == tab;
                if (on) chrome.Box(x, TitleHeight + TabsHeight - 4, w, 3, PerfUi.Accent);
                chrome.Text(x, TitleHeight, w, TabsHeight - 2, names[i], 15f, on ? Color.white : PerfUi.Muted, TextAlignmentOptions.Center, PerfUi.Title);
                if (chrome.Area(x, TitleHeight, w, TabsHeight, out _) && tab != i)
                {
                    tab = i;
                    expanded = -1;
                    selected = null;
                    countsAge = 99f;
                    Measure();
                    Sample();
                }
                x += w;
            }
            chrome.End();
            if (!open) Measure();
        }

        // Common pieces.

        float Section(float x, float y, float w, string text)
        {
            ui.Text(x, y, w, 18, text.ToUpperInvariant(), 13.5f, PerfUi.Muted, TextAlignmentOptions.MidlineLeft, PerfUi.Title);
            return y + 22;
        }

        /// A side panel: its heading and its stat lines (label, value, value's colour).
        float Side(float x, float y, float w, string heading, IList<(string Label, string Value, Color Colour)> lines)
        {
            const float line = 24f;
            float h = 9 + 20 + lines.Count * line + 6;
            ui.Box(x, y, w, h, PerfUi.Panel2, PerfUi.Round6);
            ui.Text(x + 12, y + 7, w - 24, 18, heading.ToUpperInvariant(), 13.5f, PerfUi.Muted, TextAlignmentOptions.MidlineLeft, PerfUi.Title);
            float ly = y + 29;
            for (int i = 0; i < lines.Count; i++)
            {
                var (label, value, colour) = lines[i];
                float vw = ui.Width(value, 13f) + 2;
                ui.Text(x + 12, ly, w - 24 - vw - 10, line, label, 13f, PerfUi.Muted, TextAlignmentOptions.MidlineLeft, null, false, true);
                ui.Text(x + w - 12 - vw, ly, vw, line, "<b>" + value + "</b>", 13f, colour, TextAlignmentOptions.MidlineRight);
                if (i < lines.Count - 1) ui.Box(x + 12, ly + line - 1, w - 24, 1, PerfUi.Line);
                ly += line;
            }
            return y + h + 8;
        }

        /// Four headline boxes in one line.
        float Headlines(float x, float y, float w, IList<(string Label, string Number, string Unit)> boxes)
        {
            float bw = (w - 3 * 8) / 4f, bh = 50f;
            for (int i = 0; i < boxes.Count; i++)
            {
                float bx = x + i * (bw + 8);
                ui.Box(bx, y, bw, bh, PerfUi.Panel2, PerfUi.Round4);
                ui.Text(bx + 10, y + 5, bw - 20, 16, boxes[i].Label, 11.5f, PerfUi.Muted, TextAlignmentOptions.MidlineLeft, null, false, true);
                // The number in the title face, its unit in the body face, on one baseline.
                string unit = boxes[i].Unit;
                float nw = ui.Width(boxes[i].Number, 22f, PerfUi.Title);
                // TMP puts a Baseline-aligned line's baseline at its rectangle's middle: both rectangles
                // centred on the baseline (y + 42).
                ui.Text(bx + 10, y + 30, nw + 2, 24, boxes[i].Number, 22f, PerfUi.Ink, TextAlignmentOptions.BaselineLeft, PerfUi.Title);
                if (unit.Length > 0) ui.Text(bx + 10 + nw + 4, y + 30, bw - 24 - nw, 24, unit, 12.5f, PerfUi.Muted, TextAlignmentOptions.BaselineLeft);
            }
            return y + bh + 12;
        }

        float Foot(float y, string note, Func<float, float> buttons)
        {
            float buttonsWidth = buttons(-1);
            float noteWidth = Width - 2 * Pad - buttonsWidth - 12;
            float nh = Math.Max(26, ui.Height(note, 12.5f, noteWidth) + 2);
            ui.Text(Pad, y, noteWidth, nh, note, 12.5f, PerfUi.Muted, TextAlignmentOptions.MidlineLeft, null, true);
            buttons(y + (nh - 26) / 2);
            return y + nh + 14;
        }

        /// The footer's buttons, right-aligned; with y < 0 only their width.
        float RecordButton(float y, float right)
        {
            bool busy = PerfOverlay.Recording != null && PerfOverlay.Recording();
            string label = busy ? Ui("PERF_RECORDING") : Ui("PERF_RECORD");
            float w = ui.Width(label, 14f, PerfUi.Title) + 26;
            if (y >= 0 && ui.Button(right - w, y, w, 26, label, PerfUi.Go, PerfUi.GoRule) && !busy && PerfOverlay.Record != null)
                global::Debug.Log("[Moono] perf record: " + PerfOverlay.Record());
            return w;
        }

        static string Fps(double fps) => double.IsNaN(fps) || fps <= 0 ? "–" : fps.ToString("F0", C);
        static string Ms(double ms, string format = "F1") => double.IsNaN(ms) ? "–" : ms.ToString(format, C) + " " + Ui("PERF_MS");
        /// A duration: under 0.1 s in ms ("61 ms"), else in seconds ("0.71 s", "17.3 s").
        static string Seconds(double ms) => double.IsNaN(ms) ? "–" : ms < 100 ? ms.ToString("F0", C) + " " + Ui("PERF_MS")
            : (ms / 1000).ToString(ms >= 10000 ? "F1" : "F2", C) + " " + Ui("PERF_S");
        static string Count(int n) => n < 0 ? "–" : n.ToString("N0", C);
        static string Gb(double bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("F1", C);

        // ---- Colony ----

        float Colony(PerfTrack track, double now)
        {
            float y = 14;
            // One source for the verdict, Right now, the advice and the Hitches tab: a minute of play.
            var last = track.LastMinute();
            bool inColony = PerfGame.InColony();
            bool paused = PerfGame.Paused();
            int speed = PerfReads.Speed();
            int stutters = last.PerMinute(last.Stutters), simWaits = last.PerMinute(last.SimWaits), unknown = last.PerMinute(last.Unknown);
            int light = track.Light;
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
                verdictText = string.Format(C, Ui("PERF_VERDICT_STUTTERING_TEXT"), Fps(track.Fps), stutters);
                (back, rule, ink) = (PerfUi.Hex("3A331F"), PerfUi.Hex("7A6A3D"), PerfUi.Hex("E6DCC4"));
            }
            else
            {
                verdictTitle = Ui("PERF_VERDICT_GOOD");
                verdictText = string.Format(C, Ui("PERF_VERDICT_GOOD_TEXT"), Fps(track.Fps), speed);
                (back, rule, ink) = (PerfUi.Hex("1F3A2A"), PerfUi.Hex("3D7A52"), PerfUi.Hex("C4DCCB"));
            }
            float textW = Width - 2 * Pad - 24 - 18;
            float th = ui.Height(verdictText, 13.5f, textW);
            float vh = 8 + 20 + th + 8;
            ui.Box(Pad, y, Width - 2 * Pad, vh, rule, PerfUi.Round6);
            ui.Box(Pad + 1, y + 1, Width - 2 * Pad - 2, vh - 2, back, PerfUi.Round6);
            ui.Box(Pad + 12, y + 13, 10, 10, PerfUi.LightColour(inColony && !paused ? light : 0), PerfUi.Dot);
            ui.Text(Pad + 12 + 18, y + 8, textW, 20, "<b>" + verdictTitle + "</b>", 15f, PerfUi.Ink);
            ui.Text(Pad + 12 + 18, y + 28, textW, th, verdictText, 13.5f, ink, TextAlignmentOptions.TopLeft, null, true);
            y += vh + 12;

            // Left: the advice.
            var state = new PerfAdviceState
            {
                Judged = inColony && last.PlaySeconds >= 20,
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
            float ly = Section(Pad, y, LeftWidth, Ui("PERF_ADVICE_HEADING"));
            Color[] icons = { PerfUi.UiColour, PerfUi.Anim, PerfUi.Draw, PerfUi.Sim };
            if (cards.Count == 0)
            {
                // While the verdict says it stutters, say what is known about the stutters instead of "nothing to do".
                string none = !state.Judged ? Ui("PERF_ADVICE_NOT_YET")
                    : inColony && !paused && light == 1 ? string.Format(C, Ui("PERF_ADVICE_NONE_STUTTERS"), unknown, stutters)
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

            // Right: now, and the colony.
            var nowLines = new List<(string, string, Color)>();
            nowLines.Add((Ui("PERF_NOW_FRAME_RATE"), Fps(track.Fps) + " " + Ui("PERF_FPS"), PerfUi.LightColour(inColony && !paused ? light : 0)));
            if (inColony && !paused && !double.IsNaN(track.KeepUp))
            {
                bool keeps = track.KeepUp >= 0.95;
                nowLines.Add((string.Format(C, Ui("PERF_NOW_KEEPS_UP"), speed), keeps ? Ui("PERF_YES") : string.Format(C, Ui("PERF_NO_AT"), (track.KeepUp * track.Speed).ToString("F1", C)),
                    keeps ? PerfUi.Good : PerfUi.Bad));
            }
            else nowLines.Add((string.Format(C, Ui("PERF_NOW_KEEPS_UP"), speed), "–", PerfUi.Ink));
            nowLines.Add((Ui("PERF_NOW_STUTTERS"), inColony ? string.Format(C, Ui("PERF_PER_MINUTE"), stutters) : "–", inColony && !paused && light == 1 ? PerfUi.Warn : PerfUi.Ink));
            nowLines.Add((Ui("PERF_NOW_AUTOSAVE"), double.IsNaN(track.LastSaveMs) ? Ui("PERF_NONE_YET") : Seconds(track.LastSaveMs), PerfUi.Ink));
            if (engineNow != null && engineNow[1] > 0)
                nowLines.Add((Ui("PERF_NOW_MEMORY"), string.Format(C, Ui("PERF_GB_OF"), Gb(engineNow[0]), Gb(engineNow[1]).Replace(".0", "")), PerfUi.Ink));
            nowLines.Add((Ui("PERF_NOW_HELD_BACK"), HeldBack(inColony && !paused), PerfUi.Ink));
            float ry = Side(RightX, y, RightWidth, Ui("PERF_NOW_HEADING"), nowLines);
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
            y = Math.Max(ly, ry - 8) + 6;
            return Foot(y, Ui("PERF_COLONY_FOOT"), fy =>
            {
                string label = Ui("PERF_SCAN_AGAIN");
                float w = ui.Width(label, 14f, PerfUi.Title) + 26;
                if (fy >= 0 && ui.Button(Width - Pad - w, fy, w, 26, label, PerfUi.ButtonBack, PerfUi.ButtonRule)) countsAge = 99f;
                return w;
            });
        }

        float Fine(float x, float y, float w, string text)
        {
            float h = ui.Height(text, 12.5f, w - 24);
            ui.Box(x, y, w, h + 16, PerfUi.Panel2, PerfUi.Round6);
            ui.Text(x + 12, y + 8, w - 24, h, text, 12.5f, PerfUi.Muted, TextAlignmentOptions.TopLeft, null, true);
            return y + h + 16;
        }

        /// What limits the frame rate, from the engine's waits over the last seconds: the main
        /// thread waiting for queued frames (a frame rate cap or vsync), for the graphics thread,
        /// or neither (the processor).
        string HeldBack(bool running)
        {
            if (!running || takes.Count == 0) return "–";
            var s = Sums();
            if (s[1] <= 0) return "–";
            double queued = s[12 + 3] / s[1], device = (s[12] + s[13] + s[14]) / s[1];
            if (queued > 0.2) return Ui("PERF_HELD_LIMIT");
            if (device > 0.25) return Ui("PERF_HELD_GRAPHICS");
            return Ui("PERF_HELD_PROCESSOR");
        }

        // ---- Frame ----

        // The rows: their name, colour and the phases in them (FrameWindows' order: render 0, gui 1,
        // end of frame 2, present 3, early+fixed 4, update 5, late 6, post-late 7, particles 8;
        // 9 the time between frames).
        static readonly int[][] RowPhases = { new[] { 5 }, new[] { 6 }, new[] { 0, 3, 2 }, new[] { 7, 1 }, new[] { 4, 8, 9 } };
        static readonly Color[] RowColours = { PerfUi.Sim, PerfUi.Anim, PerfUi.Draw, PerfUi.UiColour, PerfUi.Other };

        static string RowName(int row) => row switch
        {
            0 => Ui("PERF_ROW_UPDATE"),
            1 => Ui("PERF_ROW_LATE"),
            2 => Ui("PERF_ROW_DRAWING"),
            3 => Ui("PERF_ROW_INTERFACE"),
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

        float FrameTab(PerfTrack track, double now)
        {
            float y = 14;
            var sum = track.Summarize(10, now);
            var s = Sums();
            double frames = Math.Max(1, s[0]), wall = s[1];
            float ly = Headlines(Pad, y, LeftWidth, new List<(string, string, string)>
            {
                (Ui("PERF_KV_AVERAGE"), sum.Frames > 0 ? sum.MeanMs.ToString("F1", C) : "–", Ui("PERF_MS")),
                (Ui("PERF_KV_SLOWEST"), sum.Frames > 0 ? sum.P99Ms.ToString("F0", C) : "–", Ui("PERF_MS")),
                (Ui("PERF_KV_WORST"), sum.Frames > 0 ? sum.MaxMs.ToString("F0", C) : "–", Ui("PERF_MS")),
                (Ui("PERF_KV_SPEED"), PerfGame.InColony() && !PerfGame.Paused() && !double.IsNaN(track.KeepUp) ? (track.KeepUp * track.Speed).ToString("F1", C) : "–",
                    PerfGame.InColony() && !PerfGame.Paused() && !double.IsNaN(track.KeepUp) ? "×" : ""),
            });
            if (wall <= 0)
            {
                ly = Fine(Pad, ly, LeftWidth, Ui("PERF_MEASURING"));
            }
            else
            {
                // The strip, then the rows.
                double[] rowMs = new double[5];
                for (int r = 0; r < 5; r++)
                    foreach (int p in RowPhases[r]) rowMs[r] += PhaseMs(s, p);
                double total = rowMs.Sum();
                float x = Pad;
                ui.Box(Pad, ly, LeftWidth, 16, PerfUi.Panel2, PerfUi.Round4);
                for (int r = 0; r < 5; r++)
                {
                    float w = (float)(LeftWidth * rowMs[r] / Math.Max(1e-9, total));
                    if (w >= 1) ui.Box(x, ly, w, 16, RowColours[r]);
                    x += w;
                }
                ly += 16 + 10;
                double top = rowMs.Max();
                for (int r = 0; r < 5; r++)
                {
                    const float rh = 25f;
                    bool isOpen = expanded == r;
                    if (ui.Area(Pad, ly, LeftWidth, rh, out _))
                    {
                        expanded = isOpen ? -1 : r;
                        Measure();
                    }
                    float cx = Pad;
                    ui.Box(cx, ly + 7.5f, 10, 10, RowColours[r], PerfUi.Round4);
                    cx += 12 + 10;
                    string name = RowName(r) + "<color=#" + PerfUi.MutedHex + ">  " + (isOpen ? "−" : "+") + "</color>";
                    ui.Text(cx, ly, 168, rh, name, 13.5f, PerfUi.Ink);
                    cx += 168 + 10;
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
                double waits = s[12] + s[13] + s[14] + s[15];
                ly = SubRow(ly, Ui("PERF_SUB_WAIT_GRAPHICS"), (waits / frames).ToString("F2", C) + " " + Ui("PERF_MS"));
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
                threads.Add((Ui("PERF_THREADS_WAIT_GRAPHICS"), (100 * waits / wall).ToString("F0", C) + "%", PerfUi.Ink));
            }
            if (GameFrameStats.Enabled && GameFrameStats.Frames > 0)
                threads.Add((Ui("PERF_THREADS_SIM"), (GameFrameStats.NativeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / GameFrameStats.Frames).ToString("F2", C) + " " + Ui("PERF_MS"), PerfUi.Ink));
            int workers = PerfReads.Workers();
            if (workers >= 0) threads.Add((Ui("PERF_THREADS_WORKERS"), string.Format(C, Ui("PERF_OF_N"), workers, Environment.ProcessorCount), PerfUi.Ink));
            if (threads.Count > 0) ry = Side(RightX, ry, RightWidth, Ui("PERF_THREADS_HEADING"), threads);
            var memory = new List<(string, string, Color)>();
            if (engineNow != null)
            {
                memory.Add((Ui("PERF_MEMORY_IN_USE"), Gb(engineNow[0]) + " " + Ui("PERF_GB"), PerfUi.Ink));
                if (!double.IsNaN(allocPerFrame)) memory.Add((Ui("PERF_MEMORY_PER_FRAME"), (allocPerFrame / 1024).ToString("F0", C) + " " + Ui("PERF_KB"), PerfUi.Ink));
                memory.Add((Ui("PERF_MEMORY_CLEANUPS"), gcRated
                    ? string.Format(C, Ui("PERF_CLEANUPS_VALUE"), Math.Round(gcRates[0]), Math.Round(gcRates[2])) : "–", PerfUi.Ink));
            }
            memory.Add((Ui("PERF_MEMORY_LONGEST"), Ms(track.LongestGcMs, "F0"), PerfUi.Ink));
            ry = Side(RightX, ry, RightWidth, Ui("PERF_MEMORY_HEADING"), memory);
            int stepped = PerfReads.SteppedAside();
            int active = Math.Max(0, PerfOverlay.TweaksOn.Length - stepped);
            ry = Side(RightX, ry, RightWidth, Ui("PERF_TWEAKS_HEADING"), new List<(string, string, Color)>
            {
                (Ui("PERF_TWEAKS_ACTIVE"), string.Format(C, Ui("PERF_OF_N"), active, PerfOverlay.TweaksAll), PerfUi.Good),
                (Ui("PERF_TWEAKS_STEPPED"), stepped.ToString(C), stepped > 0 ? PerfUi.Warn : PerfUi.Ink),
                (Ui("PERF_TWEAKS_OFF"), PerfOverlay.TweaksSwitchedOff.ToString(C), PerfUi.Ink),
            });
            y = Math.Max(ly, ry - 8) + 6;
            string note = Comparison(s, frames);
            return Foot(y, note, fy =>
            {
                float right = Width - Pad;
                float rw = RecordButton(fy, right);
                string label = copied ?? Ui("PERF_COPY_REPORT");
                float cw = ui.Width(Ui("PERF_COPY_REPORT"), 14f, PerfUi.Title) + 26;
                if (fy >= 0 && ui.Button(right - rw - 8 - cw, fy, cw, 26, label, PerfUi.ButtonBack, PerfUi.ButtonRule)) CopyReport();
                return rw + 8 + cw;
            });
        }

        float SubRow(float y, string name, string value)
        {
            ui.Text(Pad + 22, y, 260, 20, name, 12.5f, PerfUi.Muted);
            ui.Text(Pad + LeftWidth - 40 - 10 - 62, y, 62, 20, value, 12.5f, PerfUi.Muted, TextAlignmentOptions.MidlineRight);
            return y + 20;
        }

        /// The entries inside an open row: Game update's from the bucket profile (ms, calls,
        /// objects and the longest call, a frame's worth), the others' phases.
        float Drill(int row, double[] s, double frames, double rowMs, float y)
        {
            float x = Pad + 22, w = LeftWidth - 22;
            var lines = new List<(string Name, string Ms, string Calls, string Objects, string Worst)>();
            if (row == 0)
            {
                long bucketFrames = Math.Max(1, PerfOverlay.Frames > 0 ? (long)Math.Round((Time.realtimeSinceStartup - bucketsSince) * Math.Max(1, PerfOverlay.Track.Fps)) : 1);
                var kinds = PerfBuckets.Kinds.Where(k => k.Calls > 0).OrderByDescending(k => k.Ticks).ToList();
                double framesSeen = Math.Max(1, bucketFrames);
                double bucketsMs = 0;
                foreach (var k in kinds) bucketsMs += PerfBuckets.TicksToMs(k.Ticks);
                foreach (var k in kinds.Take(6))
                    lines.Add((k.Label, (PerfBuckets.TicksToMs(k.Ticks) / framesSeen).ToString("F2", C), (k.Calls / framesSeen).ToString("F1", C),
                        (k.Entries / framesSeen).ToString("N0", C), PerfBuckets.TicksToMs(k.MaxTicks).ToString("F1", C)));
                if (kinds.Count > 6)
                {
                    double rest = kinds.Skip(6).Sum(k => PerfBuckets.TicksToMs(k.Ticks)) / framesSeen;
                    lines.Add((string.Format(C, Ui("PERF_DRILL_SMALLER"), kinds.Count - 6), rest.ToString("F2", C), "", "", ""));
                }
                double outside = Math.Max(0, rowMs / frames - bucketsMs / framesSeen);
                lines.Add((Ui("PERF_DRILL_NOT_IN_BUCKETS"), outside.ToString("F2", C), "", "", ""));
                if (kinds.Count == 0) lines.Clear();
            }
            else
            {
                foreach (int p in RowPhases[row])
                    lines.Add((PhaseName(p), (PhaseMs(s, p) / frames).ToString("F2", C), "–", "–", PhaseMax(s, p).ToString("F1", C)));
            }
            float h = 6 + 20 + Math.Max(1, lines.Count) * 21 + 6;
            ui.Box(x, y + 4, w, h, PerfUi.Panel2, PerfUi.Round6);
            float cy = y + 4 + 6;
            float[] cols = { 46, 46, 62, 46 };
            float nameW = w - 20 - cols.Sum() - 4 * 8;
            void Cells(string a, string b, string c2, string d, string e, float size, Color colour, Color first)
            {
                ui.Text(x + 10, cy, nameW, 20, a, size, first, TextAlignmentOptions.MidlineLeft, null, false, true);
                float cx = x + 10 + nameW + 8;
                string[] v = { b, c2, d, e };
                for (int i = 0; i < 4; i++)
                {
                    ui.Text(cx, cy, cols[i], 20, v[i], size, colour, TextAlignmentOptions.MidlineRight);
                    cx += cols[i] + 8;
                }
            }
            Cells(string.Format(C, Ui("PERF_DRILL_INSIDE"), RowName(row)), Ui("PERF_MS"), Ui("PERF_DRILL_CALLS"), Ui("PERF_DRILL_OBJECTS"), Ui("PERF_DRILL_WORST"), 12f, PerfUi.Muted, PerfUi.Ink);
            cy += 20;
            if (lines.Count == 0)
            {
                ui.Text(x + 10, cy, w - 20, 21, Ui("PERF_MEASURING"), 12.5f, PerfUi.Muted);
                cy += 21;
            }
            foreach (var l in lines)
            {
                ui.Box(x + 10, cy, w - 20, 1, PerfUi.Line);
                Cells(l.Name, l.Ms, l.Calls, l.Objects, l.Worst, 12.5f, PerfUi.Ink, PerfUi.Ink);
                cy += 21;
            }
            return y + 4 + h + 6;
        }

        /// The footer's line: the five rows against the newest recording (PerfOverlay.LastRecording),
        /// the two that moved most.
        string Comparison(double[] s, double frames)
        {
            if (s[1] <= 0) return Ui("PERF_COMPARE_NONE");
            var rec = lastRecording;
            if (rec == null || rec.Length < 3) return Ui("PERF_COMPARE_NONE");
            var phases = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 2; i < rec.Length; i++)
            {
                int eq = rec[i].IndexOf('=');
                if (eq > 0 && double.TryParse(rec[i].Substring(eq + 1), NumberStyles.Float, C, out double v)) phases[rec[i].Substring(0, eq)] = v;
            }
            string[] keys = { "render", "gui", "end-of-frame", "present", "early-fixed", "update", "late", "post-late", "particles", "outside-loop" };
            var diffs = new List<(double Delta, int Row)>();
            for (int r = 0; r < 5; r++)
            {
                double then = 0, nowMs = 0;
                bool known = true;
                foreach (int p in RowPhases[r])
                {
                    if (!phases.TryGetValue(keys[p], out double v)) known = false;
                    then += v;
                    nowMs += PhaseMs(s, p) / frames;
                }
                if (known) diffs.Add((nowMs - then, r));
            }
            if (diffs.Count == 0) return Ui("PERF_COMPARE_NONE");
            diffs.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)));
            string Part((double Delta, int Row) d) => RowName(d.Row) + " " + (d.Delta >= 0 ? "+" : "−") + Math.Abs(d.Delta).ToString("F2", C) + " " + Ui("PERF_MS");
            double.TryParse(rec[1], NumberStyles.Float, C, out double age);
            return string.Format(C, Ui("PERF_COMPARE"), Ago(age), Part(diffs[0]) + (diffs.Count > 1 ? ", " + Part(diffs[1]) : ""));
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
            _ => Ui("PERF_CAUSE_UNKNOWN"),
        };

        double RangeSeconds(PerfTrack track) => range == 0 ? 120 : range == 1 ? 600 : Math.Max(120, track.Seconds.Count + 1);

        float Hitches(PerfTrack track, double now)
        {
            float y = 14;
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
                (Ui("PERF_KV_STUTTERS"), last.PlaySeconds >= 1 ? perMinute.ToString(C) : "–", Ui("PERF_PER_MIN")),
            });

            // The graph.
            DrawGraph(track, window, now);
            ui.Box(Pad, ly, LeftWidth, GraphH, PerfUi.Panel2, PerfUi.Round4);
            ui.Picture(Pad, ly, LeftWidth, GraphH, graph);
            float k = (GraphH - 20) / 40f, baseY = GraphH - 10;
            ui.Text(Pad + 6, ly + baseY - 16.7f * k - 16, 80, 14, "60 " + Ui("PERF_FPS"), 11f, PerfUi.Muted);
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
            if (ui.Area(Pad, ly, LeftWidth, GraphH, out var at) && inRange.Count > 0)
            {
                double clickedAge = (LeftWidth - at.x) / LeftWidth * window;
                selected = inRange.OrderBy(f => Math.Abs((now - f.At) - clickedAge)).First();
            }
            ly += GraphH + 6;

            // Range and legend.
            string[] ranges = { Ui("PERF_RANGE_TWO_MIN"), Ui("PERF_RANGE_TEN_MIN"), Ui("PERF_RANGE_SESSION") };
            float rx = Pad;
            for (int i = 0; i < 3; i++)
            {
                float w = ui.Width(ranges[i], 12f) + 16;
                ui.Box(rx, ly, w, 20, i == range ? new Color(1, 1, 1, 0.13f) : new Color(0, 0, 0, 0));
                ui.Text(rx, ly, w, 20, ranges[i], 12f, i == range ? Color.white : PerfUi.Muted, TextAlignmentOptions.Center);
                if (ui.Area(rx, ly, w, 20, out _)) range = i;
                rx += w;
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

            // The freezes, newest first, and the stutters from waiting for the simulation.
            var list = inRange.AsEnumerable().Reverse().Take(5).ToList();
            if (list.Count == 0 && last.SimWaits == 0) ly = Fine(Pad, ly, LeftWidth, Ui("PERF_NO_FREEZES")) + 4;
            foreach (var f in list)
            {
                bool isSel = selected == f;
                if (ui.Area(Pad, ly, LeftWidth, 26, out _)) selected = isSel ? null : f;
                if (isSel) ui.Box(Pad - 4, ly + 1, LeftWidth + 8, 24, new Color(1, 1, 1, 0.06f), PerfUi.Round4);
                ui.Text(Pad, ly, 70, 26, Ago(now - f.At), 13.5f, PerfUi.Muted);
                string what = f.Paused ? string.Format(C, Ui("PERF_WHILE_PAUSED"), CauseName(f.Cause)) : CauseName(f.Cause);
                ui.Text(Pad + 82, ly, LeftWidth - 82 - 90, 26, what, 13.5f, PerfUi.Ink, TextAlignmentOptions.MidlineLeft, null, false, true);
                ui.Text(Pad + LeftWidth - 88, ly, 88, 26, Seconds(f.Ms), 13.5f, PerfUi.Ink, TextAlignmentOptions.MidlineRight);
                ui.Box(Pad, ly + 25, LeftWidth, 1, PerfUi.Line);
                ly += 26;
            }
            if (last.SimWaits > 0)
            {
                ui.Text(Pad, ly, 70, 26, Ui("PERF_ONGOING"), 13.5f, PerfUi.Muted);
                ui.Text(Pad + 82, ly, LeftWidth - 82 - 120, 26, CauseName(PerfCause.SimWait), 13.5f, PerfUi.Ink, TextAlignmentOptions.MidlineLeft, null, false, true);
                double typical = track.CauseTimes[(int)PerfCause.SimWait] > 0 ? track.CauseTotal[(int)PerfCause.SimWait] / track.CauseTimes[(int)PerfCause.SimWait] : 0;
                ui.Text(Pad + LeftWidth - 118, ly, 118, 26, typical.ToString("F0", C) + " " + Ui("PERF_MS") + " × " + last.PerMinute(last.SimWaits).ToString(C) + " " + Ui("PERF_PER_MIN"), 13.5f, PerfUi.Ink, TextAlignmentOptions.MidlineRight);
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

            // Right: by cause, time lost, loading.
            double minutes = track.ColonySeconds / 60;
            var causeLines = new List<(string, int, double, double)>();
            foreach (var cause in new[] { PerfCause.Autosave, PerfCause.Save, PerfCause.Timelapse, PerfCause.Gc, PerfCause.Jit, PerfCause.World, PerfCause.SimWait, PerfCause.FirstFrame, PerfCause.Load, PerfCause.Unknown })
            {
                int c = (int)cause;
                if (track.CauseTimes[c] > 0) causeLines.Add((cause == PerfCause.SimWait ? Ui("PERF_CAUSE_SIM_WAIT_SHORT") : CauseName(cause), track.CauseTimes[c], track.CauseWorst[c], track.CauseTotal[c]));
            }
            float ry = CauseTable(RightX, y, RightWidth, string.Format(C, Ui("PERF_BY_CAUSE_HEADING"), Math.Round(minutes).ToString("N0", C)), causeLines);
            // Time lost during play (unpaused, loading left out) per hour of play.
            double hours = Math.Max(track.PlaySeconds / 3600, 1e-9);
            double freezeMs = track.PlayFreezeMs, stutterMs = track.PlayStutterMs;
            bool enough = track.PlaySeconds >= 60;
            ry = Side(RightX, ry, RightWidth, Ui("PERF_LOST_HEADING"), new List<(string, string, Color)>
            {
                (Ui("PERF_LOST_FREEZES"), enough ? PerHour(freezeMs / hours) : "–", PerfUi.Ink),
                (Ui("PERF_LOST_STUTTERS"), enough ? PerHour(stutterMs / hours) : "–", stutterMs / hours > 60000 ? PerfUi.Warn : PerfUi.Ink),
            });
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
            ui.Text(x + 12, y + 7, w - 24, 18, heading.ToUpperInvariant(), 13.5f, PerfUi.Muted, TextAlignmentOptions.MidlineLeft, PerfUi.Title);
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

        /// What a freeze was made of, where the engine or the game says.
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
            if (save && f == LastSave() && PerfOverlay.Engine?.Invoke() is double[] e && e.Length > 6 && e[6] > 0)
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
                for (int row = 0; row < 5; row++)
                {
                    double ms = RowPhases[row].Sum(p => PhaseMs(s, p));
                    r.Append(string.Format(C, "  {0}: {1:F2} ms ({2:F0}%)\n", RowName(row), ms / frames, 100 * ms / s[1]));
                    foreach (int p in RowPhases[row])
                        if (RowPhases[row].Length > 1) r.Append(string.Format(C, "    {0}: {1:F2} ms\n", PhaseName(p), PhaseMs(s, p) / frames));
                }
                r.Append(string.Format(C, "  waiting for graphics {0:F2} ms, GC {1:F2} ms a frame\n", (s[12] + s[13] + s[14] + s[15]) / frames, s[18] / frames));
            }
            if (PerfBuckets.On)
                foreach (var k in PerfBuckets.Kinds.Where(k => k.Calls > 0).OrderByDescending(k => k.Ticks).Take(15))
                    r.Append(string.Format(C, "    bucket {0}: {1:F1} ms in all, {2} calls, {3} objects, longest {4:F2} ms\n", k.Label, PerfBuckets.TicksToMs(k.Ticks), k.Calls, k.Entries, PerfBuckets.TicksToMs(k.MaxTicks)));
            var last = track.LastMinute();
            r.Append(string.Format(C, "Last minute: {0:F0} s of play, {1} stutters ({2} a minute: {3} waiting for the simulation, {4} unknown), {5} freezes\n",
                last.PlaySeconds, last.Stutters, last.PerMinute(last.Stutters), last.PerMinute(last.SimWaits), last.PerMinute(last.Unknown), last.Freezes));
            r.Append(string.Format(C, "Session: {0:F0} min in a colony, {1:F0} min of play; longest GC pause {2:F0} ms\n", track.ColonySeconds / 60, track.PlaySeconds / 60, track.LongestGcMs));
            foreach (PerfCause cause in Enum.GetValues(typeof(PerfCause)))
            {
                int c = (int)cause;
                if (track.CauseTimes[c] > 0)
                    r.Append(string.Format(C, "  {0}: {1} times, worst {2:F0} ms, total {3:F1} s\n", cause, track.CauseTimes[c], track.CauseWorst[c], track.CauseTotal[c] / 1000));
            }
            foreach (var f in track.Freezes.Skip(Math.Max(0, track.Freezes.Count - 10)))
                r.Append(string.Format(C, "  freeze {0:F0} s ago: {1}, {2:F0} ms (GC {3:F0}, JIT {4:F0}, preview {5:F0})\n", now - f.At, f.Cause, f.Ms, f.GcMs, f.JitMs, f.PreviewMs));
            r.Append(string.Format(C, "Loading: start to menu {0:F1} s, colony {1:F1} s, first frame {2:F0} ms, first save {3:F0} ms\n",
                track.StartToMenuMs / 1000, track.LoadMs / 1000, track.FirstFrameMs, track.FirstSaveMs));
            if (engineNow != null) r.Append(string.Format(C, "Memory: {0:F1} GB in use of {1:F1} GB\n", engineNow[0] / 1073741824.0, engineNow[1] / 1073741824.0));
            r.Append(string.Format(C, "Tweaks: {0} on of {1}, {2} stepped aside, {3} switched off\n", PerfOverlay.TweaksOn.Length, PerfOverlay.TweaksAll, PerfReads.SteppedAside(), PerfOverlay.TweaksSwitchedOff));
            r.Append("Tweaks on: ").Append(string.Join(", ", PerfOverlay.TweaksOn)).Append('\n');
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
                    Measure();
                    Sample();
                    Build();
                    return "ok " + TabName;
                case "expand":
                    expanded = arg == "none" ? -1 : int.Parse(arg, C);
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
                    var lines = (arg == "load" ? (IEnumerable<PerfTrack.Slow>)track.LoadFrames : track.SlowFrames()).Where(f => string.IsNullOrEmpty(arg) || arg == "all" || string.Equals(f.Cause.ToString(), arg, StringComparison.OrdinalIgnoreCase))
                        .Select(f => arg == "load" ? LoadLine(f) : SlowLine(f, track.Now));
                    return "ok " + string.Join(" | ", lines);
                }
                case "build":
                    Sample();
                    Build();
                    return "ok height=" + height.ToString("F0", C);
                default:
                    return "unknown " + verb;
            }
        }
    }
}
