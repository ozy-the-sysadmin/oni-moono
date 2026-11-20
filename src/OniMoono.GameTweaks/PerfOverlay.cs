using System;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OniMoono.GameTweaks
{
    /// The HUD's frame rate counter and its frame time panel (the Frame rate counter row of Moono
    /// Tweaks; OniMoono.Host.Perf.PerfHud), not a tweak: our own small canvas over the game's,
    /// in every scene, with the game's text widget (LocText) in the game's default text style.
    ///
    /// The counter shows the frame rate and the frame time from Time.unscaledDeltaTime, written
    /// twice a second. Clicking it opens the panel: the frame's time by part (PlayerLoop's
    /// phases and the time between frames, which add up to the frame), the engine's waits and
    /// counts, refreshed every second from a frame window the host opens only while the panel
    /// shows (closed, nothing is measured), and a button that records 60 seconds into a file
    /// (OniMoono.Host.Perf.PerfRecorder). No patches.
    public sealed class PerfOverlay : MonoBehaviour
    {
        static string corner;
        static Action<bool> timing;
        static Func<double[]> take;
        static Func<string> record;
        static Func<bool> recording;

        /// From the host: where the counter goes (EffectiveOptions.FpsCounterChoices, not "off"),
        /// the panel's frame window (`panelTiming(true)` opens it, false closes it; `panelTake`
        /// gives the frames since the last call: see Panel), and the recorder (`recordNow`
        /// arms 60 s, returning what the log says; `isRecording`).
        public static void Install(string position, Action<bool> panelTiming, Func<double[]> panelTake, Func<string> recordNow, Func<bool> isRecording)
        {
            corner = position;
            timing = panelTiming;
            take = panelTake;
            record = recordNow;
            recording = isRecording;
        }

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

        LocText counter, panelText, recordLabel;
        GameObject panel;
        CanvasScaler scaler;
        float elapsed, panelElapsed;
        int frames;
        readonly StringBuilder text = new StringBuilder(64);

        void Build()
        {
            scaler = GetComponent<CanvasScaler>();
            bool right = corner.EndsWith("right", StringComparison.Ordinal), top = corner.StartsWith("top", StringComparison.Ordinal);
            var anchor = new Vector2(right ? 1f : 0f, top ? 1f : 0f);

            counter = Label(transform, "Counter", 13, TextAnchorFor(right, top));
            var rect = counter.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.sizeDelta = new Vector2(150f, 20f);
            rect.anchoredPosition = new Vector2(right ? -6f : 6f, top ? -3f : 3f);
            counter.raycastTarget = true;
            counter.gameObject.AddComponent<PerfClick>().Clicked = Toggle;

            panel = new GameObject("Panel", typeof(RectTransform));
            panel.transform.SetParent(transform, false);
            var back = panel.AddComponent<Image>();
            back.color = new Color(0.1f, 0.11f, 0.14f, 0.88f);
            var panelRect = (RectTransform)panel.transform;
            panelRect.anchorMin = panelRect.anchorMax = panelRect.pivot = anchor;
            panelRect.sizeDelta = new Vector2(480f, 330f);  // 420 cut the counts line short at 1440p (session AA, C1)
            panelRect.anchoredPosition = new Vector2(right ? -6f : 6f, top ? -24f : 24f);
            panelText = Label(panel.transform, "Text", 12, TMPro.TextAlignmentOptions.TopLeft);
            var textRect = panelText.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10f, 34f);
            textRect.offsetMax = new Vector2(-10f, -8f);
            panelText.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            panelText.richText = true;

            recordLabel = Label(panel.transform, "Record", 13, TMPro.TextAlignmentOptions.MidlineLeft);
            var recordRect = recordLabel.rectTransform;
            recordRect.anchorMin = recordRect.anchorMax = recordRect.pivot = new Vector2(0f, 0f);
            recordRect.sizeDelta = new Vector2(300f, 24f);
            recordRect.anchoredPosition = new Vector2(10f, 6f);
            recordLabel.raycastTarget = true;
            recordLabel.color = Util.ColorFromHex("9DE07A");
            recordLabel.gameObject.AddComponent<PerfClick>().Clicked = Record;
            panel.SetActive(false);
        }

        static TMPro.TextAlignmentOptions TextAnchorFor(bool right, bool top) =>
            right ? (top ? TMPro.TextAlignmentOptions.TopRight : TMPro.TextAlignmentOptions.BottomRight)
                : (top ? TMPro.TextAlignmentOptions.TopLeft : TMPro.TextAlignmentOptions.BottomLeft);

        /// A LocText made inactive, so that it wakes with no key and the game's default style.
        static LocText Label(Transform parent, string name, float size, TMPro.TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<LocText>();
            label.key = "";
            label.textStyleSetting = PluginAssets.Instance.defaultTextStyleSetting;
            go.SetActive(true);
            label.font = Localization.FontAsset;
            label.fontSize = size;
            label.color = Color.white;
            label.alignment = alignment;
            label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            label.raycastTarget = false;
            label.text = "";
            return label;
        }

        void Update()
        {
            // The game's UI scale is for its own canvases; this one follows the screen's height.
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            if (scaler != null && !Mathf.Approximately(scaler.scaleFactor, scale)) scaler.scaleFactor = scale;
            frames++;
            elapsed += Time.unscaledDeltaTime;
            if (elapsed >= 0.5f)
            {
                var c = CultureInfo.InvariantCulture;
                text.Clear();
                text.Append((frames / elapsed).ToString("F0", c)).Append(" fps  ").Append((elapsed * 1000f / frames).ToString("F1", c)).Append(" ms");
                counter.text = text.ToString();
                frames = 0;
                elapsed = 0;
            }
            if (!panel.activeSelf) return;
            panelElapsed += Time.unscaledDeltaTime;
            if (panelElapsed < 1f) return;
            panelElapsed = 0;
            var numbers = take?.Invoke();
            if (numbers != null) panelText.text = Panel(numbers);
            recordLabel.text = recording != null && recording() ? Ui("PERF_RECORDING") : Ui("PERF_RECORD");
        }

        void Toggle()
        {
            bool show = !panel.activeSelf;
            panel.SetActive(show);
            timing?.Invoke(show);
            panelElapsed = 0;
            if (!show) return;
            panelText.text = Ui("PERF_MEASURING");
            recordLabel.text = recording != null && recording() ? Ui("PERF_RECORDING") : Ui("PERF_RECORD");
        }

        void Record()
        {
            if (record == null || (recording != null && recording())) return;
            Debug.Log("[Moono] perf record: " + record());
            recordLabel.text = Ui("PERF_RECORDING");
        }

        /// The host's numbers (OniMoono.Host.Perf.PerfHud.Take): frames, wall ms, the 9 phases'
        /// ms (FrameWindows' order: render, gui, end of frame, present, early+fixed, update, late,
        /// post-late, particles), outside the loop, the 5 waits' ms (device frame, device calls,
        /// texel copies, queued frames, presenter), device calls, GC pause ms, frames with a GC
        /// pause, the longest frame.
        internal static string Panel(double[] n)
        {
            double frames = Math.Max(1, n[0]), wall = n[1];
            if (n[0] < 1 || wall <= 0) return Ui("PERF_MEASURING");
            var c = CultureInfo.InvariantCulture;
            var s = new StringBuilder(1024);
            s.Append("<b>").Append(string.Format(c, Ui("PERF_TITLE"), (1000 * frames / wall).ToString("F0", c), (wall / frames).ToString("F2", c))).Append("</b>\n");
            void Line(string label, double ms, string extra = null)
            {
                s.Append(label).Append("<pos=62%>").Append((ms / frames).ToString("F2", c)).Append(" ms<pos=82%>")
                    .Append((100 * ms / wall).ToString("F0", c)).Append('%');
                if (extra != null) s.Append("  ").Append(extra);
                s.Append('\n');
            }
            // The frame's order: input, game update, animation, interface, drawing, presenting.
            Line(Ui("PERF_EARLY"), n[2 + 4]);
            Line(Ui("PERF_UPDATE"), n[2 + 5]);
            Line(Ui("PERF_LATE"), n[2 + 6]);
            Line(Ui("PERF_POST_LATE"), n[2 + 7]);
            Line(Ui("PERF_PARTICLES"), n[2 + 8]);
            Line(Ui("PERF_RENDER"), n[2 + 0]);
            Line(Ui("PERF_GUI"), n[2 + 1]);
            Line(Ui("PERF_END_OF_FRAME"), n[2 + 2]);
            Line(Ui("PERF_PRESENT"), n[2 + 3]);
            Line(Ui("PERF_OUTSIDE"), n[11]);
            s.Append("<alpha=#B0>");
            Line(Ui("PERF_WAIT_DEVICE"), n[12] + n[13] + n[14] + n[15]);
            Line(Ui("PERF_GC"), n[18]);
            s.Append(string.Format(c, Ui("PERF_COUNTS"), n[0].ToString("F0", c), (n[17] / frames).ToString("F1", c), n[19].ToString("F0", c), n[20].ToString("F1", c)));
            return s.ToString();
        }

        /// One of the panel's labels in the game's language (MoonoTweaksMenu's strings), English
        /// when the Moono Tweaks menu is off.
        static string Ui(string name)
        {
            try
            {
                var text = MoonoTweaksMenu.Ui(name);
                if (!string.IsNullOrEmpty(text) && !text.StartsWith("STRINGS.", StringComparison.Ordinal)) return text;
            }
            catch (Exception)
            {
            }
            return English(name);
        }

        static string English(string name) => name switch
        {
            "PERF_TITLE" => "Frame time: {0} fps, {1} ms a frame",
            "PERF_RECORD" => "Record 60 s",
            "PERF_RECORDING" => "Recording…",
            "PERF_MEASURING" => "Measuring…",
            "PERF_EARLY" => "Input and physics",
            "PERF_UPDATE" => "Game update",
            "PERF_LATE" => "Animation and late update",
            "PERF_POST_LATE" => "Interface layout",
            "PERF_PARTICLES" => "Particles",
            "PERF_RENDER" => "Drawing",
            "PERF_GUI" => "Debug interface",
            "PERF_END_OF_FRAME" => "End of frame",
            "PERF_PRESENT" => "Showing the picture",
            "PERF_OUTSIDE" => "Between frames",
            "PERF_WAIT_DEVICE" => "Of these, waiting for the graphics thread",
            "PERF_GC" => "Of these, memory cleanup",
            "PERF_COUNTS" => "{0} frames; {1} graphics calls waited for a frame; {2} memory cleanups; longest frame {3} ms",
            _ => name,
        };
    }

    /// A click on one of the overlay's labels.
    sealed class PerfClick : MonoBehaviour, IPointerClickHandler
    {
        internal System.Action Clicked;

        public void OnPointerClick(PointerEventData eventData) => Clicked?.Invoke();
    }
}
