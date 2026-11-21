using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OniMoono.GameTweaks
{
    /// The frame rate counter and its performance window (the Frame rate counter row of Moono
    /// Tweaks; OniMoono.Host.Perf.PerfHud), not a tweak: our own canvas over the game's, in every
    /// scene, drawn with the game's text widget (LocText) and the approved mock's colours
    /// (PerfUi). No patch changes the game: the look inside "Game update" reads the clock around
    /// the game's own method while that row is open (PerfBuckets).
    ///
    /// The counter is a badge: a status light (PerfTrack.Light) and the frame rate. Drag it
    /// with the left button anywhere on the screen; it stays inside the screen and keeps its
    /// place, as a fraction of the screen, in the oni-moono data folder (PositionFile) across
    /// restarts. A click without movement opens or closes the window (PerfWindow); a drag never
    /// does. The window keeps the place it was dragged to the same way (WindowFile).
    ///
    /// Input: the overlay is a screen of the game's own screen stack (PerfScreen, a KScreen),
    /// so the game's rules apply: while the pointer is over the badge or the window, the wheel,
    /// the middle and right buttons and clicks are taken there and never reach the camera or
    /// the tools; Escape closes the window. When one of the game's modal dialogs is up (the
    /// pause menu, Options, a save dialog, the mod events) the window is hidden until it goes
    /// and the badge is dimmed and inert, so neither ever covers or competes with the dialog.
    ///
    /// Cost: with the counter on, one call a frame (Frame: a few reads of the game's state into
    /// PerfTrack's ring), the badge's text twice a second and a look at the screen stack. In the
    /// menus and loads the engine's frame window times each frame's phases too (the copy
    /// report's account of a load; no allocation). The window measures more only while it
    /// shows: the frame window for the Colony and Frame tabs, the clock around the game's state
    /// machine updates while Frame's "Game update" row is open.
    public sealed class PerfOverlay : MonoBehaviour
    {
        // ---- From the host (Install) ----
        internal static string PositionFile, WindowFile;
        internal static Action<bool> Timing;
        internal static Func<double[]> Take;
        internal static Func<string> Record;
        /// The recorder: -1 off, -2 armed and waiting for play, else the seconds recorded.
        internal static Func<double> RecordProgress;
        internal static Func<double[]> Engine;
        internal static Func<string[]> LastRecording;
        internal static string[] TweaksOn = Array.Empty<string>(), TweaksSwitchedOff = Array.Empty<string>();
        internal static int TweaksAll;
        /// The swap interval the devices present with (0: vsync off), and the display's refresh
        /// period (ms) while vsynced pictures are presented and it is known, else 0.
        internal static Func<int> VSyncInterval;
        internal static Func<double> PacedPeriodMs;
        static Dictionary<string, string> english = new Dictionary<string, string>(StringComparer.Ordinal);

        internal static readonly PerfTrack Track = new PerfTrack();

        /// The recorder is busy (armed, waiting or recording).
        internal static bool Recording => RecordProgress != null && RecordProgress() != -1;

        /// From the host: where the badge's place is kept (the window's beside it); the engine's
        /// frame window (`timing(true)` opens it, `take` gives its sums since the last call, in
        /// one buffer it fills again: see PerfWindow.Phases); the recorder (`record` arms 60 s
        /// and says what it did, `recordProgress`); the engine's memory numbers (`engine`: working
        /// set, installed memory, bytes allocated, gen0, gen1 and gen2 collections, the last
        /// background save write's ms, its save's number, the saves handed to the writer); the
        /// newest recording's phases (`lastRecording`: name, age in seconds, then "phase=ms"
        /// pairs; null for none); the tweaks on, the tweak count, the default-on tweaks the
        /// player turned off; the window's strings' names and English (TweakStrings.Ui), used
        /// when the Moono Tweaks menu is off; the start-up time on the frame clock (seconds);
        /// the Harmony the look inside "Game update" patches with (PerfBuckets); the swap
        /// interval and the paced refresh period (OniMoono.Engine.VSync, FramePacing.PacedPeriodMs).
        public static void Install(string positionFile, Action<bool> timing, Func<double[]> take, Func<string> record, Func<double> recordProgress,
            Func<double[]> engine, Func<string[]> lastRecording, string[] tweaksOn, int tweaksAll, string[] tweaksSwitchedOff,
            string[] uiNames, string[] uiEnglish, double startedAt, HarmonyLib.Harmony harmony, Func<int> vsyncInterval, Func<double> pacedPeriodMs)
        {
            VSyncInterval = vsyncInterval;
            PacedPeriodMs = pacedPeriodMs;
            PositionFile = positionFile;
            WindowFile = positionFile != null ? Path.Combine(Path.GetDirectoryName(positionFile) ?? "", "perf-window-position.txt") : null;
            Timing = timing;
            Take = take;
            Record = record;
            RecordProgress = recordProgress;
            Engine = engine;
            LastRecording = lastRecording;
            TweaksOn = tweaksOn ?? Array.Empty<string>();
            TweaksAll = tweaksAll;
            TweaksSwitchedOff = tweaksSwitchedOff ?? Array.Empty<string>();
            english = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < uiNames.Length; i++) english[uiNames[i]] = uiEnglish[i];
            PerfReads.SimStepPaceOn = Array.IndexOf(TweaksOn, PerfAdvice.SimStepPace) >= 0;
            PerfReads.Listen(true);
            Track.StartedAt = startedAt;
            PerfBuckets.Harmony = harmony;
            MoonoTweaksMenu.CounterReset = () => ResetPosition();
        }

        // ---- Self-timing (the costs the report gives; read by Test("cost")) ----
        internal static long FrameTicks, UpdateTicks, RefreshTicks, Frames;
        static bool wasColony;

        /// The host's frame-start call: the frame that just ended (see PerfReads.Frame).
        public static void Frame(double now, double ms, double gcMs, double jitMs)
        {
            long start = Stopwatch.GetTimestamp();
            var f = PerfReads.Frame(now, ms, gcMs, jitMs);
            Track.RefreshMs = RefreshMs();
            Track.Add(f);
            if (PerfBuckets.On) PerfBuckets.Frames++;
            // A colony left or arrived: what the window kept of the last one goes.
            if (f.InColony != wasColony)
            {
                wasColony = f.InColony;
                PerfBuckets.Clear();
                if (instance != null) instance.window?.SceneChanged();
            }
            // Outside a colony (menus, between scenes) and until a load settles, the engine's frame
            // window times each frame's phases (it counts only from the second frame after it
            // opens, so it is open before a load starts): the copy report's account of the
            // arriving frame and those after it. A few µs a frame, in the menus and loads only,
            // into the host's one buffer. (Not while the window's own tabs read the frame window:
            // its sums are theirs then.)
            if (loadTiming && !windowTiming && Take?.Invoke() is double[] phases && phases[0] == 1) Track.LoadPhases(f.Now, phases);
            bool want = !f.InColony || Track.Settling;
            if (want != loadTiming)
            {
                loadTiming = want;
                ApplyTiming();
                if (loadTiming && !windowTiming) Take?.Invoke();
            }
            Frames++;
            FrameTicks += Stopwatch.GetTimestamp() - start;
        }

        static bool loadTiming, windowTiming;

        /// How long a picture is shown with vsync (the refresh period times the swap interval,
        /// ms), 0 without vsync or while the period is not known.
        internal static double RefreshMs()
        {
            int interval = VSyncInterval?.Invoke() ?? 0;
            double period = interval > 0 ? PacedPeriodMs?.Invoke() ?? 0 : 0;
            return period > 0 ? period * interval : 0;
        }

        /// The window's Colony and Frame tabs keep the engine's frame window open; so does a load
        /// settling. One window serves both.
        internal static void WindowTiming(bool on)
        {
            windowTiming = on;
            ApplyTiming();
        }

        static void ApplyTiming() => Timing?.Invoke(windowTiming || loadTiming);

        static PerfOverlay instance;

        /// Makes the overlay once the game's fonts and styles are loaded; false until then.
        public static bool TryCreate()
        {
            if (instance != null) return true;
            if (Global.Instance == null || Localization.FontAsset == null || PluginAssets.Instance == null) return false;
            var root = new GameObject("MoonoPerfOverlay", typeof(RectTransform));
            DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            root.AddComponent<CanvasScaler>();
            root.AddComponent<GraphicRaycaster>();
            instance = root.AddComponent<PerfOverlay>();
            instance.Build();
            return true;
        }

        /// The Moono Tweaks row while the game runs: shown or hidden now (hidden: the window
        /// closes and nothing is drawn).
        public static void SetShown(bool shown)
        {
            if (instance == null) return;
            if (!shown) instance.window?.Close();
            instance.gameObject.SetActive(shown);
        }

        CanvasScaler scaler;
        RectTransform canvasRect, badge;
        CanvasGroup badgeGroup;
        Image badgeRing, light, glow;
        LocText counter;
        PerfWindow window;
        PerfScreen screen;
        float scale = 1f, textElapsed, scaleElapsed = 10f;
        int shownFps = -1, shownLight = -1;
        /// The badge's centre as a fraction of the screen (null: the default, top centre).
        Vector2? place;
        const float BadgeHeight = 22f;
        float badgeWidth = 80f;

        void Build()
        {
            scaler = GetComponent<CanvasScaler>();
            canvasRect = (RectTransform)transform;
            UpdateScale();
            var go = new GameObject("Badge", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            badge = (RectTransform)go.transform;
            badge.anchorMin = badge.anchorMax = new Vector2(0f, 1f);
            badge.pivot = new Vector2(0.5f, 0.5f);
            badgeGroup = go.AddComponent<CanvasGroup>();
            var back = go.AddComponent<Image>();
            back.sprite = PerfUi.Pill;
            back.type = Image.Type.Sliced;
            back.color = PerfUi.BadgeBack;
            back.raycastTarget = true;
            badgeRing = PerfUi.NewImage(go.transform, "Ring", PerfUi.PillRing, PerfUi.Rule);
            Fill(badgeRing.rectTransform);
            glow = PerfUi.NewImage(go.transform, "Glow", PerfUi.Dot, PerfUi.Good);
            light = PerfUi.NewImage(go.transform, "Light", PerfUi.Dot, PerfUi.Good);
            counter = PerfUi.NewText(go.transform, "Rate");
            counter.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            counter.fontSize = 15f;
            var press = go.AddComponent<PerfPress>();
            press.Pressed = () => BeginPress(badge, null);
            press.Hover = on =>
            {
                badgeHover = on;
                if (pressed != badge) badgeRing.color = on ? PerfUi.RuleHover : PerfUi.Rule;
                if (on) PerfUi.Sound(PerfUi.HoverSound);
            };
            // Room for four digits (1000 fps and more with vsync off), with the left's padding.
            badgeWidth = Mathf.Ceil(TextWidth("<b>8888</b> <size=12>fps</size>") + 9 + 8 + 9 + 11);
            LayoutBadge();
            place = ReadPlace(PositionFile);
            ApplyPlace();
            ShowRate();
            window = new PerfWindow(this, transform);
            windowPlace = ReadPlace(WindowFile);
            screen = gameObject.AddComponent<PerfScreen>();
            screen.Overlay = this;
        }

        static void Fill(RectTransform r)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
        }

        float TextWidth(string text) => counter.GetPreferredValues(text, 10000f, 0f).x;

        void LayoutBadge()
        {
            badge.sizeDelta = new Vector2(badgeWidth, BadgeHeight);
            PerfUi.Place(glow.rectTransform, 6f, (BadgeHeight - 15f) / 2f, 15f, 15f);
            glow.color = new Color(1, 1, 1, 0.3f) * PerfUi.Good;
            PerfUi.Place(light.rectTransform, 9f, (BadgeHeight - 9f) / 2f, 9f, 9f);
            PerfUi.Place(counter.rectTransform, 9f + 9f + 8f, 0f, badgeWidth - 26f - 8f, BadgeHeight);
        }

        /// The canvas follows the game's own UI scale (KCanvasScaler: the player's UI scale
        /// option times the screen's step, 1 at 1080 lines, 1.33 at 2160).
        void UpdateScale()
        {
            float user = 1f;
            try
            {
                if (KPlayerPrefs.HasKey(KCanvasScaler.UIScalePrefKey)) user = KPlayerPrefs.GetFloat(KCanvasScaler.UIScalePrefKey) / 100f;
            }
            catch (Exception)
            {
            }
            float h = Screen.height, aspect = (float)Screen.width / Math.Max(1, Screen.height);
            float step = h <= 720f || aspect < 1.6f ? 0.86f : h > 2160f ? 1.33f : h <= 1080f ? Mathf.Lerp(0.86f, 1f, (h - 720f) / 360f) : Mathf.Lerp(1f, 1.33f, (h - 1080f) / 1080f);
            float s = Mathf.Clamp(user, 0.5f, 3f) * step;
            if (!Mathf.Approximately(s, scale) || !Mathf.Approximately(scaler.scaleFactor, s))
            {
                scale = s;
                scaler.scaleFactor = s;
            }
        }

        internal float Scale => scale;
        internal Vector2 CanvasSize => new Vector2(Screen.width / scale, Screen.height / scale);

        // ---- The badge's place ----

        Vector2 DefaultCentre() => new Vector2(CanvasSize.x / 2f, 3f + BadgeHeight / 2f);

        Vector2 Centre()
        {
            var size = CanvasSize;
            var c = place.HasValue ? new Vector2(place.Value.x * size.x, place.Value.y * size.y) : DefaultCentre();
            return Clamp(c, new Vector2(badgeWidth, BadgeHeight));
        }

        /// The badge's rectangle in the overlay's units.
        internal PerfBox BadgeBox
        {
            get
            {
                var c = Centre();
                return new PerfBox(c.x - badgeWidth / 2, c.y - BadgeHeight / 2, badgeWidth, BadgeHeight);
            }
        }

        Vector2 Clamp(Vector2 centre, Vector2 size)
        {
            var canvas = CanvasSize;
            var b = PerfLayout.Clamp(new PerfBox(centre.x - size.x / 2, centre.y - size.y / 2, size.x, size.y), canvas.x, canvas.y, 2f);
            return new Vector2(b.X + size.x / 2, b.Y + size.y / 2);
        }

        void ApplyPlace()
        {
            var c = Centre();
            var pos = new Vector2(c.x, -c.y);
            if (badge.anchoredPosition != pos) badge.anchoredPosition = pos;
        }

        /// The badge's centre in screen pixels from the top left (tests).
        internal Vector2 BadgePixels => Centre() * scale;

        /// A place kept as two fractions of the screen ("0.25000 0.84970": the badge's centre, the
        /// window's top left); null for none.
        static Vector2? ReadPlace(string path)
        {
            try
            {
                if (path == null || !File.Exists(path)) return null;
                var parts = File.ReadAllText(path).Trim().Split(' ');
                if (parts.Length == 2 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                    && x >= 0 && x <= 1 && y >= 0 && y <= 1) return new Vector2(x, y);
            }
            catch (Exception e)
            {
                global::Debug.LogWarning("[Moono] fps counter: a place could not be read: " + e.Message);
            }
            return null;
        }

        /// Written on the main thread at the drag's end (a few bytes): a later reset or drag can
        /// never be overtaken by an earlier write.
        static void WritePlace(string path, Vector2 value)
        {
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, value.x.ToString("F5", CultureInfo.InvariantCulture) + " " + value.y.ToString("F5", CultureInfo.InvariantCulture));
            }
            catch (Exception e)
            {
                global::Debug.LogWarning("[Moono] fps counter: a place could not be saved: " + e.Message);
            }
        }

        /// Moono Tweaks' "Reset position": the badge goes back to the top centre and the window
        /// back to its automatic place, now and after a restart.
        public static void ResetPosition()
        {
            foreach (var path in new[] { PositionFile, WindowFile })
                try
                {
                    if (path != null && File.Exists(path)) File.Delete(path);
                }
                catch (Exception e)
                {
                    global::Debug.LogWarning("[Moono] fps counter: its place could not be reset: " + e.Message);
                }
            if (instance != null)
            {
                instance.place = null;
                instance.windowPlace = null;
                instance.ApplyPlace();
                if (instance.window.Open) instance.window.PlaceAgain();
            }
        }

        // ---- The window's place ----

        /// Where the player dragged the window (its top left as fractions of the screen), null
        /// for the automatic place (PerfLayout.Place).
        Vector2? windowPlace;

        /// Where the window goes now, `w` by `h`: the player's place, else clear of the badge and
        /// of the game's top bar.
        internal PerfBox WindowBox(float w, float h, out bool chosen)
        {
            var canvas = CanvasSize;
            chosen = windowPlace.HasValue;
            if (chosen) return PerfLayout.Clamp(new PerfBox(windowPlace.Value.x * canvas.x, windowPlace.Value.y * canvas.y, w, h), canvas.x, canvas.y);
            return PerfLayout.Place(canvas.x, canvas.y, w, h, BadgeBox, GameBar());
        }

        /// The game's top bar where it is drawn now (the management buttons, the cycle clock and
        /// speed controls, the planetoid list), from its widgets' rectangles, in the overlay's
        /// units. Read when the window is placed, not every frame.
        internal List<PerfBox> GameBar()
        {
            var boxes = new List<PerfBox>();
            try
            {
                var canvas = CanvasSize;
                foreach (var part in new Component[] { ManagementMenu.Instance, TopLeftControlScreen.Instance, SpeedControlScreen.Instance, WorldSelector.Instance })
                {
                    if (part == null || !part.gameObject.activeInHierarchy) continue;
                    bool any = false;
                    float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                    foreach (var g in part.GetComponentsInChildren<Graphic>(false))
                    {
                        if (g == null || !g.isActiveAndEnabled || g.color.a <= 0.01f || g.canvas == null) continue;
                        var b = OnScreen(g.rectTransform, g.canvas);
                        // Full-screen backgrounds and blockers are not the bar.
                        if (b.W <= 0 || b.H <= 0 || b.W > canvas.x * 0.6f || b.H > canvas.y * 0.6f) continue;
                        any = true;
                        x0 = Math.Min(x0, b.X);
                        y0 = Math.Min(y0, b.Y);
                        x1 = Math.Max(x1, b.Right);
                        y1 = Math.Max(y1, b.Bottom);
                    }
                    if (any) boxes.Add(new PerfBox(x0, y0, x1 - x0, y1 - y0));
                }
            }
            catch (Exception)
            {
                // A changed game: placed by the badge alone.
            }
            return boxes;
        }

        readonly Vector3[] corners = new Vector3[4];

        /// A widget's rectangle in the overlay's units (any canvas mode).
        PerfBox OnScreen(RectTransform r, Canvas canvas)
        {
            r.GetWorldCorners(corners);
            var root = canvas.rootCanvas;
            var camera = root != null && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var c in corners)
            {
                var p = RectTransformUtility.WorldToScreenPoint(camera, c);
                x0 = Math.Min(x0, p.x);
                x1 = Math.Max(x1, p.x);
                y0 = Math.Min(y0, p.y);
                y1 = Math.Max(y1, p.y);
            }
            return new PerfBox(x0 / scale, (Screen.height - y1) / scale, (x1 - x0) / scale, (y1 - y0) / scale);
        }

        // ---- Pressing: the badge (drag or click) and the window's title bar (drag) ----

        RectTransform pressed;
        bool badgeHover;
        System.Action clicked;
        Vector2 pressMouse, pressCentre;
        bool dragging;
        const float DragPixels = 5f;

        /// A press on `target` begins (PerfPress, on the pointer's down): followed in Update until
        /// the button is up. `click`: what a press without movement does (null: the badge's).
        internal void BeginPress(RectTransform target, System.Action click)
        {
            if (inert) return;
            pressed = target;
            clicked = click;
            pressMouse = Input.mousePosition;
            var pos = target.anchoredPosition;
            pressCentre = new Vector2(pos.x, -pos.y);
            dragging = false;
            if (target == badge) badgeRing.color = PerfUi.Accent;
        }

        void FollowPress()
        {
            if (pressed == null) return;
            Vector2 mouse = Input.mousePosition;
            var delta = mouse - pressMouse;
            if (!dragging && delta.magnitude > DragPixels) dragging = true;
            if (dragging)
            {
                var c = pressCentre + new Vector2(delta.x, -delta.y) / scale;
                if (pressed == badge)
                {
                    c = Clamp(c, new Vector2(badgeWidth, BadgeHeight));
                    var size = CanvasSize;
                    place = new Vector2(c.x / size.x, c.y / size.y);
                    ApplyPlace();
                }
                else window.MoveTo(c);
            }
            if (Input.GetMouseButton(0)) return;
            // Released.
            var target = pressed;
            pressed = null;
            badgeRing.color = badgeHover ? PerfUi.RuleHover : PerfUi.Rule;
            if (target == badge)
            {
                if (dragging) WritePlace(PositionFile, place ?? Vector2.zero);
                else
                {
                    PerfUi.Sound(PerfUi.ClickSound);
                    ToggleWindow();
                }
            }
            else if (dragging)
            {
                // The window's title bar: the place the player chose is kept.
                var canvas = CanvasSize;
                var tl = window.TopLeft;
                windowPlace = new Vector2(Mathf.Clamp01(tl.x / canvas.x), Mathf.Clamp01(tl.y / canvas.y));
                WritePlace(WindowFile, windowPlace.Value);
            }
            else clicked?.Invoke();
        }

        internal bool WindowOpen => window.Open;

        internal void CloseWindow() => window.Close();

        internal void ToggleWindow()
        {
            if (window.Open) window.Close();
            else window.Show();
        }

        // ---- The game's dialogs and screen stack ----

        /// One of the game's modal dialogs is up: the window hides, the badge is dimmed and inert.
        bool inert;
        internal bool Inert => inert;

        /// Whether a modal screen of the game is active above everything (KModalScreen,
        /// KModalButtonMenu: the pause menu, Options, saves, the mod events).
        static bool ModalUp(KScreen self)
        {
            var manager = KScreenManager.Instance;
            if (manager == null) return false;
            var stack = manager.screenStack;
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                var s = stack[i];
                if (s == null || s == self) continue;
                if (s.IsModal() && s.IsScreenActive()) return true;
            }
            return false;
        }

        /// The pointer is over the badge or the open window (the screen's input rules).
        internal bool PointerOver()
        {
            var mouse = Input.mousePosition;
            float x = mouse.x / scale, y = (Screen.height - mouse.y) / scale;
            if (BadgeBox.Contains(x, y)) return true;
            return window.Open && window.Box.Contains(x, y);
        }

        void UpdateInert()
        {
            bool now = false;
            try
            {
                now = ModalUp(screen);
            }
            catch (Exception)
            {
            }
            if (now == inert) return;
            inert = now;
            badgeGroup.alpha = inert ? 0.4f : 1f;
            badgeGroup.blocksRaycasts = !inert;
            badgeGroup.interactable = !inert;
            if (inert)
            {
                pressed = null;
                badgeRing.color = PerfUi.Rule;
            }
            window.SetHidden(inert);
        }

        // ---- Every frame ----

        void Update()
        {
            long start = Stopwatch.GetTimestamp();
            screen.Join();
            UpdateInert();
            scaleElapsed += Time.unscaledDeltaTime;
            if (scaleElapsed >= 1f)
            {
                scaleElapsed = 0f;
                UpdateScale();
                ApplyPlace();
            }
            FollowPress();
            textElapsed += Time.unscaledDeltaTime;
            if (textElapsed >= 0.5f)
            {
                textElapsed = 0f;
                ShowRate();
            }
            UpdateTicks += Stopwatch.GetTimestamp() - start;
            if (window.Open)
            {
                long w = Stopwatch.GetTimestamp();
                window.Update(Time.unscaledDeltaTime);
                RefreshTicks += Stopwatch.GetTimestamp() - w;
            }
        }

        /// After every script's Update (the event system's presses among them) and before the
        /// canvases are laid out: a release is turned into its click and the click's result
        /// built now, so it shows in this frame.
        void LateUpdate()
        {
            if (!window.Open) return;
            long w = Stopwatch.GetTimestamp();
            window.AfterInput();
            RefreshTicks += Stopwatch.GetTimestamp() - w;
        }

        /// "164 fps", the number in the light's colour when yellow or red; text set only when it
        /// changes.
        void ShowRate()
        {
            int fps = (int)Math.Round(Track.Fps);
            int lit = Track.Light;
            if (fps == shownFps && lit == shownLight) return;
            shownFps = fps;
            string number = lit == 0 ? "<b>" + fps.ToString(CultureInfo.InvariantCulture) + "</b>"
                : "<b><color=#" + PerfUi.LightHex(lit) + ">" + fps.ToString(CultureInfo.InvariantCulture) + "</color></b>";
            counter.text = number + " <size=12><color=#" + PerfUi.MutedHex + ">" + Ui("PERF_FPS") + "</color></size>";
            if (lit != shownLight)
            {
                shownLight = lit;
                light.color = PerfUi.LightColour(lit);
                glow.color = new Color(light.color.r, light.color.g, light.color.b, 0.3f);
            }
        }

        void OnDestroy()
        {
            try
            {
                window?.Close();
            }
            catch (Exception)
            {
            }
            if (instance == this) instance = null;
        }

        // ---- Strings ----

        /// One of the window's strings in the game's language (the Moono Tweaks menu's), English
        /// from the catalog when the menu is off.
        internal static string Ui(string name)
        {
            try
            {
                var text = MoonoTweaksMenu.Ui(name);
                if (!string.IsNullOrEmpty(text) && !text.StartsWith("STRINGS.", StringComparison.Ordinal)) return text;
            }
            catch (Exception)
            {
            }
            return english.TryGetValue(name, out var e) ? e : name;
        }

        // ---- Test runs (the oni-multicore bench's McDrive, by reflection) ----

        /// state | open | close | tab colony|frame|hitches | expand NAME|none | select N | range 0|1|2 |
        /// copy | cost | reset | hits | bar | press X Y (screen pixels from the top left: what is there).
        public static string Test(string verb, string arg)
        {
            var o = instance;
            var c = CultureInfo.InvariantCulture;
            switch (verb)
            {
                case "state":
                    if (o == null) return "no overlay";
                    var px = o.BadgePixels;
                    var rate = Track.LastMinute();
                    return string.Format(c, "light={0} fps={1:F0} badge={2:F0},{3:F0} scale={4:F3} window={5} tab={6} rect={7} keepup={8:F3} median={9:F2} stutters10s={10} place={11} minute: play_s={12:F0} stutters={13}/min simwait={14}/min unknown={15}/min sim-step-pace={16} inert={17} shown={18} since_load={19:F0} held={20}",
                        Track.Light, Track.Fps, px.x, px.y, o.scale, o.window.Open, o.window.TabName, o.window.RectText, Track.KeepUp, Track.MedianMs, Track.Stutters10s,
                        o.place.HasValue ? o.place.Value.x.ToString("F4", c) + "," + o.place.Value.y.ToString("F4", c) : "default",
                        rate.PlaySeconds, rate.PerMinute(rate.Stutters), rate.PerMinute(rate.SimWaits), rate.PerMinute(rate.Unknown), PerfReads.SimStepPaceOn,
                        o.inert, o.gameObject.activeSelf, Track.PlaySinceLoad, o.window.HeldName);
                case "open": if (o != null && !o.window.Open) o.ToggleWindow(); return "ok";
                case "close": if (o != null && o.window.Open) o.window.Close(); return "ok";
                case "reset": ResetPosition(); return "ok";
                case "bar":
                {
                    // The game's top bar as the window's placement sees it, and the badge (overlay units).
                    if (o == null) return "no overlay";
                    return "ok badge=" + o.BadgeBox + " bar=" + string.Join(";", o.GameBar()) + " canvas=" + o.CanvasSize.x.ToString("F0", c) + "x" + o.CanvasSize.y.ToString("F0", c);
                }
                case "hits":
                {
                    // What the event system's raycast finds under the pointer now, top first, and the
                    // badge's pointer events so far (test runs: a click that does nothing).
                    var es = UnityEngine.EventSystems.EventSystem.current;
                    var parts = new List<string>();
                    if (es != null)
                    {
                        var data = new PointerEventData(es) { position = Input.mousePosition };
                        var hits = new List<RaycastResult>();
                        es.RaycastAll(data, hits);
                        foreach (var h in hits) parts.Add(h.gameObject != null ? h.gameObject.name + "(" + h.sortingOrder + ")" : "null");
                    }
                    return string.Format(c, "ok mouse={0:F0},{1:F0} button0={2} eventsystem={3} hits=[{4}] badge downs={5} enters={6} over={7} taken={8}", Input.mousePosition.x, Input.mousePosition.y,
                        Input.GetMouseButton(0), es != null ? es.name : "none", string.Join(",", parts), PerfPress.Downs, PerfPress.Enters, o != null && o.PointerOver(), PerfScreen.Taken);
                }
                case "cost":
                {
                    double f = Math.Max(1, Frames), us = 1e6 / Stopwatch.Frequency;
                    string r = string.Format(c, "frames={0} frame_us={1:F2} update_us={2:F2} window_us={3:F2}", Frames, FrameTicks * us / f, UpdateTicks * us / f, RefreshTicks * us / f);
                    Frames = FrameTicks = UpdateTicks = RefreshTicks = 0;
                    return r;
                }
                default:
                    return o == null ? "no overlay" : o.window.Test(verb, arg);
            }
        }
    }

    /// The overlay's place in the game's screen stack (KScreenManager): a KScreen, so that the
    /// game's input rules apply to it as to its own screens. Above the HUD's screens (the tool
    /// menu, the management screens) and below the pause menu and the modal dialogs, which sit
    /// above it and take their input first. While the pointer is over the badge or the window
    /// it takes the wheel's zoom, the middle and right buttons (the camera's pan) and clicks;
    /// Escape closes the open window and does nothing else. Key releases are left alone (a pan
    /// begun elsewhere ends normally). Each scene has its own manager: the overlay, which lives
    /// across scenes, joins each one (Join).
    internal sealed class PerfScreen : KScreen
    {
        internal PerfOverlay Overlay;
        KScreenManager joined;
        /// Events taken so far (test runs).
        internal static int Taken;

        public override float GetSortKey() => 22f;

        /// Pushed onto the scene's screen stack once.
        internal void Join()
        {
            var manager = KScreenManager.Instance;
            if (manager == null || manager == joined) return;
            joined = manager;
            manager.PushScreen(this);
        }

        public override void OnKeyDown(KButtonEvent e)
        {
            if (e.Consumed || Overlay == null || Overlay.Inert) return;
            if (Overlay.WindowOpen && e.TryConsume(global::Action.Escape))
            {
                Overlay.CloseWindow();
                Taken++;
                return;
            }
            if (!Overlay.PointerOver()) return;
            if (e.TryConsume(global::Action.ZoomIn) || e.TryConsume(global::Action.ZoomOut) || e.TryConsume(global::Action.MouseMiddle)
                || e.TryConsume(global::Action.MouseRight) || e.TryConsume(global::Action.MouseLeft) || e.TryConsume(global::Action.ShiftMouseLeft))
                Taken++;
        }

        public override void OnKeyUp(KButtonEvent e)
        {
        }

        /// Never destroys the overlay (the game deactivates every screen when it quits).
        public override void Deactivate()
        {
            try
            {
                var manager = KScreenManager.Instance;
                if (manager != null) manager.PopScreen(this);
            }
            catch (Exception)
            {
            }
        }
    }

    /// The pointer went down on the badge or the window's title bar; it entered or left it.
    internal sealed class PerfPress : MonoBehaviour, IPointerDownHandler, IPointerEnterHandler, IPointerExitHandler
    {
        internal System.Action Pressed;
        internal Action<bool> Hover;

        /// Pointer events the badge and title bar received (test runs).
        internal static int Downs, Enters;

        public void OnPointerDown(PointerEventData e)
        {
            Downs++;
            if (e.button == PointerEventData.InputButton.Left) Pressed?.Invoke();
        }

        public void OnPointerEnter(PointerEventData e)
        {
            Enters++;
            Hover?.Invoke(true);
        }

        public void OnPointerExit(PointerEventData e) => Hover?.Invoke(false);
    }
}
