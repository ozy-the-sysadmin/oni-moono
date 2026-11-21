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
    /// (PerfUi). No patches, except the window's look inside "Game update" while that row is
    /// open (PerfBuckets).
    ///
    /// The counter is a badge: a status light (PerfTrack.Light) and the frame rate. Drag it
    /// with the left button anywhere on the screen; it stays inside the screen and keeps its
    /// place, as a fraction of the screen, in the oni-moono data folder (PositionFile) across
    /// restarts. A click without movement opens or closes the window (PerfWindow); a drag never
    /// does. Neither reaches the world: the badge is a raycast target of our canvas, and the
    /// game's tools ignore clicks over any UI (PlayerController.OnKeyDown).
    ///
    /// Cost: with the counter on, one call a frame (Frame: a few reads of the game's state into
    /// PerfTrack's ring) and the badge's text twice a second. The window measures more only
    /// while it shows: the engine's frame window for the Colony and Frame tabs, the bucket
    /// profile while Frame's "Game update" row is open.
    public sealed class PerfOverlay : MonoBehaviour
    {
        // ---- From the host (Install) ----
        internal static string PositionFile;
        internal static Action<bool> Timing;
        internal static Func<double[]> Take;
        internal static Func<string> Record;
        internal static Func<bool> Recording;
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

        /// From the host: where the badge's place is kept; the engine's frame window
        /// (`timing(true)` opens it, `take` gives its sums since the last call: see PerfWindow.
        /// Phases); the recorder (`record` arms 60 s and says what it did, `recording`); the
        /// engine's memory numbers (`engine`: working set, installed memory, bytes allocated,
        /// gen0, gen1 and gen2 collections, the last background save write's ms); the newest
        /// recording's phases (`lastRecording`: name, age in seconds, then "phase=ms" pairs; null
        /// for none); the tweaks on, the tweak count, the default-on tweaks the player turned off;
        /// the window's strings' names and English (TweakStrings.Ui), used when the Moono Tweaks
        /// menu is off; the start-up time on the frame clock (seconds); the Harmony the look
        /// inside "Game update" patches with (PerfBuckets); the swap interval and the paced
        /// refresh period (OniMoono.Engine.VSync, FramePacing.PacedPeriodMs).
        public static void Install(string positionFile, Action<bool> timing, Func<double[]> take, Func<string> record, Func<bool> recording,
            Func<double[]> engine, Func<string[]> lastRecording, string[] tweaksOn, int tweaksAll, string[] tweaksSwitchedOff,
            string[] uiNames, string[] uiEnglish, double startedAt, HarmonyLib.Harmony harmony, Func<int> vsyncInterval, Func<double> pacedPeriodMs)
        {
            VSyncInterval = vsyncInterval;
            PacedPeriodMs = pacedPeriodMs;
            PositionFile = positionFile;
            Timing = timing;
            Take = take;
            Record = record;
            Recording = recording;
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

        /// The host's frame-start call: the frame that just ended (see PerfReads.Frame).
        public static void Frame(double now, double ms, double gcMs, double jitMs)
        {
            long start = Stopwatch.GetTimestamp();
            var f = PerfReads.Frame(now, ms, gcMs, jitMs);
            Track.RefreshMs = RefreshMs();
            Track.Add(f);
            // Outside a colony (menus, between scenes) and until a load settles, the engine's frame
            // window times each frame's phases (it counts only from the second frame after it
            // opens, so it is open before a load starts): the copy report's account of the
            // arriving frame and those after it. A few µs a frame, in the menus and loads only.
            // (Not while the window's own tabs read the frame window: its sums are theirs then.)
            if (loadTiming && !windowTiming && Take?.Invoke() is double[] phases && phases[0] == 1) Track.LoadPhases(f.Now, phases);
            bool want = !f.InColony || Track.Settling;
            if (want != loadTiming)
            {
                loadTiming = want;
                ApplyTiming();
                if (loadTiming) Take?.Invoke();
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

        CanvasScaler scaler;
        RectTransform canvasRect, badge;
        Image badgeRing, light, glow;
        LocText counter;
        PerfWindow window;
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
            badgeWidth = Mathf.Ceil(TextWidth("<b>888</b> <size=12>fps</size>") + 9 + 8 + 9 + 11);
            LayoutBadge();
            place = ReadPlace();
            ApplyPlace();
            ShowRate();
            window = new PerfWindow(this, transform);
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

        Vector2 Clamp(Vector2 centre, Vector2 size)
        {
            var canvas = CanvasSize;
            centre.x = Mathf.Clamp(centre.x, size.x / 2f + 2f, Math.Max(size.x / 2f + 2f, canvas.x - size.x / 2f - 2f));
            centre.y = Mathf.Clamp(centre.y, size.y / 2f + 2f, Math.Max(size.y / 2f + 2f, canvas.y - size.y / 2f - 2f));
            return centre;
        }

        void ApplyPlace()
        {
            var c = Centre();
            var pos = new Vector2(c.x, -c.y);
            if (badge.anchoredPosition != pos) badge.anchoredPosition = pos;
        }

        /// The badge's centre in screen pixels from the top left (tests).
        internal Vector2 BadgePixels => Centre() * scale;

        static Vector2? ReadPlace()
        {
            try
            {
                if (PositionFile == null || !File.Exists(PositionFile)) return null;
                var parts = File.ReadAllText(PositionFile).Trim().Split(' ');
                if (parts.Length == 2 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                    && x >= 0 && x <= 1 && y >= 0 && y <= 1) return new Vector2(x, y);
            }
            catch (Exception e)
            {
                global::Debug.LogWarning("[Moono] fps counter: its place could not be read: " + e.Message);
            }
            return null;
        }

        void WritePlace()
        {
            var path = PositionFile;
            if (path == null || !place.HasValue) return;
            string text = place.Value.x.ToString("F5", CultureInfo.InvariantCulture) + " " + place.Value.y.ToString("F5", CultureInfo.InvariantCulture);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, text);
                }
                catch (Exception e)
                {
                    global::Debug.LogWarning("[Moono] fps counter: its place could not be saved: " + e.Message);
                }
            });
        }

        /// Moono Tweaks' "Reset position": the badge goes back to the top centre, now and after a
        /// restart. `positionFile`: the place's file when the counter is not running (the menu
        /// knows the settings folder).
        public static void ResetPosition(string positionFile = null)
        {
            var path = PositionFile ?? positionFile;
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
                instance.ApplyPlace();
            }
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
                if (dragging) WritePlace();
                else
                {
                    PerfUi.Sound(PerfUi.ClickSound);
                    ToggleWindow();
                }
            }
            else if (!dragging) clicked?.Invoke();
        }

        internal void ToggleWindow()
        {
            if (window.Open) window.Close();
            else window.Show(Centre(), new Vector2(badgeWidth, BadgeHeight));
        }

        // ---- Every frame ----

        void Update()
        {
            long start = Stopwatch.GetTimestamp();
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

        /// After every script's Update (the event system's clicks among them) and before the
        /// canvases are laid out: a click's result is built now, so it shows in this frame.
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
        /// copy | cost | reset | press X Y (screen pixels from the top left: what is there).
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
                    return string.Format(c, "light={0} fps={1:F0} badge={2:F0},{3:F0} scale={4:F3} window={5} tab={6} rect={7} keepup={8:F3} median={9:F2} stutters10s={10} place={11} minute: play_s={12:F0} stutters={13}/min simwait={14}/min unknown={15}/min sim-step-pace={16}",
                        Track.Light, Track.Fps, px.x, px.y, o.scale, o.window.Open, o.window.TabName, o.window.RectText, Track.KeepUp, Track.MedianMs, Track.Stutters10s,
                        o.place.HasValue ? o.place.Value.x.ToString("F4", c) + "," + o.place.Value.y.ToString("F4", c) : "default",
                        rate.PlaySeconds, rate.PerMinute(rate.Stutters), rate.PerMinute(rate.SimWaits), rate.PerMinute(rate.Unknown), PerfReads.SimStepPaceOn);
                case "open": if (o != null && !o.window.Open) o.ToggleWindow(); return "ok";
                case "close": if (o != null && o.window.Open) o.window.Close(); return "ok";
                case "reset": ResetPosition(); return "ok";
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
                    return string.Format(c, "ok mouse={0:F0},{1:F0} button0={2} eventsystem={3} hits=[{4}] badge downs={5} enters={6}", Input.mousePosition.x, Input.mousePosition.y,
                        Input.GetMouseButton(0), es != null ? es.name : "none", string.Join(",", parts), PerfPress.Downs, PerfPress.Enters);
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
