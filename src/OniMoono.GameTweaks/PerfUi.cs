using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OniMoono.GameTweaks
{
    /// The performance window's drawing kit: the colours and fonts of the approved mock
    /// (mocks/perf-monitor-mocks.html in the oni-multicore project), rounded panels made from a
    /// small generated texture, the game's LocText for text, and a builder that lays a tab out
    /// again each refresh reusing its widgets (no objects made or destroyed once a tab has been
    /// shown, text set only when it changes). Positions are in the window's units, from the top
    /// left, y downwards.
    ///
    /// Clicks go by what was clicked, not by which widget it was: every button and area is
    /// built with an id ("tab1", "row2", "copy"), a press notes the id under the pointer, its
    /// release makes the click when the pointer is still on that control (where it was at the
    /// press or where it is now), and the next build hands the click to the control with that
    /// id; a click whose control is not in that build is dropped (PerfClicks).
    internal sealed class PerfUi
    {
        // The mock's colours.
        internal static readonly Color Ink = Hex("E9EBF2"), Muted = Hex("A3A9BD"), Panel = Hex("3A3F52"), Panel2 = Hex("2F3445"),
            Rule = Hex("555B73"), Head = Hex("7B3B5E"), HeadHi = Hex("A04F79"), Good = Hex("7FD66B"), Warn = Hex("F0C64A"),
            Bad = Hex("EF6B5B"), Accent = Hex("62C8E8"), Sim = Hex("62C8E8"), Anim = Hex("B58CF0"), Draw = Hex("F0A24A"),
            UiColour = Hex("7FD66B"), Other = Hex("8087A0"), GainInk = Hex("CFE9C9"), ButtonBack = Hex("4A5170"), ButtonRule = Hex("6B7396"),
            Go = Hex("3F7A3A"), GoRule = Hex("62A85B"), Line = new Color(1, 1, 1, 0.063f), BadgeBack = new Color32(0x20, 0x24, 0x2F, 0xE6);
        /// The Frame tab's waits row (waiting for the screen or the graphics card): a dim slate.
        internal static readonly Color Wait = Hex("596079");
        /// The badge's outline under the pointer.
        internal static readonly Color RuleHover = Hex("A9B0CC");
        internal const string InkHex = "E9EBF2", MutedHex = "A3A9BD", GoodHex = "7FD66B", WarnHex = "F0C64A", BadHex = "EF6B5B";

        internal static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString("#" + hex, out var c);
            return c;
        }

        /// The game's own button sounds (KButton's ButtonSoundPlayer defaults: HUD_Click_Open when
        /// pressed, HUD_Mouseover when the pointer enters).
        internal const string ClickSound = "HUD_Click_Open", HoverSound = "HUD_Mouseover";

        /// Plays one of the game's interface sounds; nothing when the game's sound is not up.
        internal static void Sound(string name)
        {
            try
            {
                KFMOD.PlayUISound(GlobalAssets.GetSound(name));
            }
            catch (Exception)
            {
            }
        }

        /// The light's colour: green, yellow, red.
        internal static Color LightColour(int light) => light == 2 ? Bad : light == 1 ? Warn : Good;
        internal static string LightHex(int light) => light == 2 ? BadHex : light == 1 ? WarnHex : GoodHex;

        static Sprite round4, round4Ring, round6, pill, ring, dot, gradient;
        /// Rounded rectangles (corner radius 4 and 6), a pill (radius 11), its outline, a dot and
        /// the title bar's gradient (the mock's --headhi to --head), made once.
        internal static Sprite Round4 => round4 ??= Rounded(4, false);
        internal static Sprite Round4Ring => round4Ring ??= Rounded(4, true);
        internal static Sprite Round6 => round6 ??= Rounded(6, false);
        internal static Sprite Pill => pill ??= Rounded(11, false);
        internal static Sprite PillRing => ring ??= Rounded(11, true);
        internal static Sprite Dot => dot ??= Rounded(8, false, 16);
        internal static Sprite Gradient => gradient ??= MakeGradient();

        static Sprite Rounded(int radius, bool outline, int size = 0)
        {
            if (size == 0) size = radius * 2 + 4;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            float r = radius;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // Distance outside the rounded rectangle inset by nothing, corners of radius r.
                    float cx = Mathf.Clamp(x + 0.5f, r, size - r), cy = Mathf.Clamp(y + 0.5f, r, size - r);
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    float inside = Mathf.Clamp01(r - d + 0.5f);
                    float a = outline ? Mathf.Clamp01(inside - Mathf.Clamp01(r - 1f - d + 0.5f)) : inside;
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            float b = Math.Min(radius + 1, size / 2 - 1);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }

        static Sprite MakeGradient()
        {
            var tex = new Texture2D(1, 16, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < 16; y++) tex.SetPixel(0, y, Color.Lerp(Head, HeadHi, y / 15f));
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, 1, 16), new Vector2(0.5f, 0.5f), 100f);
        }

        static TMP_FontAsset titleFont;
        /// The body font: the game's for its language (Localization.FontAsset).
        internal static TMP_FontAsset Body => Localization.FontAsset;
        /// Headings, tabs, buttons and the big numbers: the game's title face (GRAYSTROKE, as its
        /// screens' titles) in English; the body font in other languages, whose letters it lacks.
        internal static TMP_FontAsset Title
        {
            get
            {
                if (!English) return Body;
                if (titleFont == null)
                    foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                        if (font != null && font.name.IndexOf("GRAYSTROKE", StringComparison.OrdinalIgnoreCase) >= 0) titleFont = font;
                return titleFont != null ? titleFont : Body;
            }
        }

        internal static bool English
        {
            get
            {
                try { return string.IsNullOrEmpty(Localization.GetLocale()?.Code); }
                catch (Exception) { return true; }
            }
        }

        /// A heading's own words in capitals (the values put into it stay as they are: "5 s ago",
        /// not "5 S AGO"). Languages without capitals are unchanged.
        internal static string Upper(string text) => text.ToUpperInvariant();

        /// A LocText made inactive, so that it wakes with no key and the game's default style.
        internal static LocText NewText(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<LocText>();
            label.key = "";
            label.textStyleSetting = PluginAssets.Instance.defaultTextStyleSetting;
            go.SetActive(true);
            label.font = Body;
            label.color = Ink;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.richText = true;
            label.raycastTarget = false;
            label.text = "";
            return label;
        }

        internal static Image NewImage(Transform parent, string name, Sprite sprite, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            image.color = colour;
            image.raycastTarget = false;
            return image;
        }

        /// Places a rectangle from the top left of its parent.
        internal static void Place(RectTransform rect, float x, float y, float w, float h)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            var pos = new Vector2(x, -y);
            if (rect.anchoredPosition != pos) rect.anchoredPosition = pos;
            var size = new Vector2(w, h);
            if (rect.sizeDelta != size) rect.sizeDelta = size;
        }

        /// A rectangle's place on the screen, in pixels from the top left (the overlay's canvas
        /// draws in screen pixels).
        static readonly Vector3[] corners = new Vector3[4];

        internal static PerfBox ScreenBox(RectTransform r)
        {
            r.GetWorldCorners(corners);
            float x0 = Math.Min(corners[0].x, corners[2].x), x1 = Math.Max(corners[0].x, corners[2].x);
            float y0 = Math.Min(corners[0].y, corners[2].y), y1 = Math.Max(corners[0].y, corners[2].y);
            return new PerfBox(x0, Screen.height - y1, x1 - x0, y1 - y0);
        }

        // ---- The builder ----

        readonly RectTransform root;
        readonly List<Image> boxes = new List<Image>();
        readonly List<LocText> texts = new List<LocText>();
        readonly List<PerfButton> buttons = new List<PerfButton>();
        readonly List<RawImage> pictures = new List<RawImage>();
        int boxUsed, textUsed, buttonUsed, pictureUsed, order;
        readonly LocText measure;
        /// A control took a click during this build: the window builds again at once.
        internal bool Clicked;
        readonly PerfClicks clicks = new PerfClicks();
        /// This build's controls by id: where a pressed control is now.
        readonly Dictionary<string, PerfButton> byId = new Dictionary<string, PerfButton>(StringComparer.Ordinal);
        /// A click waits for its control: the window builds at once, so its result shows in the
        /// frame of the release. `ClickFrame`: the frame of the newest click; `Dropped`: clicks
        /// whose control was gone by the next build (tests).
        internal bool PendingClick => clicks.Waiting;
        internal int ClickFrame = -1, Dropped;

        internal PerfUi(RectTransform root)
        {
            this.root = root;
            measure = NewText(root, "Measure");
            measure.gameObject.SetActive(false);
        }

        internal void Begin()
        {
            boxUsed = textUsed = buttonUsed = pictureUsed = order = 0;
            Clicked = false;
            byId.Clear();
            clicks.Begin();
        }

        internal void End()
        {
            if (clicks.End()) Dropped++;
            for (int i = boxUsed; i < boxes.Count; i++) Hide(boxes[i].gameObject);
            for (int i = textUsed; i < texts.Count; i++) Hide(texts[i].gameObject);
            for (int i = buttonUsed; i < buttons.Count; i++) Hide(buttons[i].gameObject);
            for (int i = pictureUsed; i < pictures.Count; i++) Hide(pictures[i].gameObject);
        }

        /// The pointer went down on one of this builder's controls.
        internal void Press(PerfButton b)
        {
            if (string.IsNullOrEmpty(b.Id)) return;
            clicks.Press(b.Id, ScreenBox(b.Rect));
            ShowPressed();
        }

        /// Each frame before the window acts on clicks: a press whose button is up again is
        /// released where the pointer is now.
        internal void Poll()
        {
            if (!clicks.Pressing || Input.GetMouseButton(0)) return;
            var mouse = Input.mousePosition;
            PerfBox? now = byId.TryGetValue(clicks.PressedId, out var b) && b != null && b.gameObject.activeInHierarchy ? ScreenBox(b.Rect) : (PerfBox?)null;
            if (clicks.Release(mouse.x, Screen.height - mouse.y, now)) ClickFrame = Time.frameCount;
            ShowPressed();
        }

        /// Forgets a press (the window closed or went under a game dialog).
        internal void Cancel()
        {
            clicks.Cancel();
            ShowPressed();
        }

        void ShowPressed()
        {
            foreach (var b in buttons) b.SetPressed(clicks.PressedId != null && b.Id == clicks.PressedId);
        }

        static void Hide(GameObject go)
        {
            if (go.activeSelf) go.SetActive(false);
        }

        /// Draw order is build order.
        void Order(Transform t)
        {
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            if (t.GetSiblingIndex() != order) t.SetSiblingIndex(order);
            order++;
        }

        internal Image Box(float x, float y, float w, float h, Color colour, Sprite sprite = null)
        {
            if (boxUsed == boxes.Count) boxes.Add(NewImage(root, "Box", null, colour));
            var box = boxes[boxUsed++];
            if (box.sprite != sprite)
            {
                box.sprite = sprite;
                box.type = sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            }
            if (box.color != colour) box.color = colour;
            Place(box.rectTransform, x, y, w, h);
            Order(box.transform);
            return box;
        }

        /// One label: `align` within (x, y, w, h); `wrap` lets it take several lines; `ellipsis`
        /// cuts a line too long for `w` (its rectangle is made at least a line of the font
        /// high: TMP drops a line that does not fit in height, which left the Chinese headline
        /// boxes empty).
        internal LocText Text(float x, float y, float w, float h, string text, float size, Color colour,
            TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, TMP_FontAsset font = null, bool wrap = false, bool ellipsis = false)
        {
            if (textUsed == texts.Count) texts.Add(NewText(root, "Text"));
            var label = texts[textUsed++];
            font = font ?? Body;
            if (label.font != font) label.font = font;
            if (label.fontSize != size) label.fontSize = size;
            if (label.color != colour) label.color = colour;
            if (label.alignment != align) label.alignment = align;
            var mode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            if (label.textWrappingMode != mode) label.textWrappingMode = mode;
            var overflow = ellipsis ? TextOverflowModes.Ellipsis : TextOverflowModes.Overflow;
            if (label.overflowMode != overflow) label.overflowMode = overflow;
            if (label.text != text) label.text = text;
            if (ellipsis && h < size * 1.7f)
            {
                y -= (size * 1.7f - h) / 2;
                h = size * 1.7f;
            }
            Place(label.rectTransform, x, y, w, h);
            Order(label.transform);
            return label;
        }

        /// The height a wrapped text takes in `width`, and the width of one line.
        internal float Height(string text, float size, float width, TMP_FontAsset font = null)
        {
            Setup(size, font);
            return measure.GetPreferredValues(text, width, 0f).y;
        }

        internal float Width(string text, float size, TMP_FontAsset font = null)
        {
            Setup(size, font);
            return measure.GetPreferredValues(text, 10000f, 0f).x;
        }

        void Setup(float size, TMP_FontAsset font)
        {
            font = font ?? Body;
            if (measure.font != font) measure.font = font;
            measure.fontSize = size;
            measure.textWrappingMode = TextWrappingModes.Normal;
        }

        PerfButton NextButton(string id)
        {
            if (buttonUsed == buttons.Count)
            {
                var made = PerfButton.Make(root);
                made.Owner = this;
                buttons.Add(made);
            }
            var b = buttons[buttonUsed++];
            b.Id = id ?? "";
            if (b.Id.Length > 0) byId[b.Id] = b;
            b.SetPressed(clicks.PressedId != null && clicks.PressedId == b.Id);
            return b;
        }

        /// The waiting click, when it is this control's; `at`: where it landed, in the control's
        /// units from its top left.
        bool Take(PerfButton b, out Vector2 at)
        {
            at = Vector2.zero;
            if (b.Id.Length == 0 || !clicks.Take(b.Id)) return false;
            var box = ScreenBox(b.Rect);
            float sx = box.W / Math.Max(1f, b.Rect.rect.width), sy = box.H / Math.Max(1f, b.Rect.rect.height);
            at = new Vector2((clicks.ClickX - box.X) / Math.Max(1e-3f, sx), (clicks.ClickY - box.Y) / Math.Max(1e-3f, sy));
            Clicked = true;
            return true;
        }

        /// A button with a label; true once after it was clicked. `id`: what it is, the same from
        /// build to build. It lightens under the pointer and darkens while pressed, with the
        /// game's button sounds. Not `enabled`: dimmed, no highlight, never clicked.
        internal bool Button(string id, float x, float y, float w, float h, string label, Color back, Color rule, float size = 14f, bool enabled = true)
        {
            var b = NextButton(enabled ? id : "");
            b.Set(back, rule, label, size, 0.16f, enabled, enabled ? 1f : 0.45f);
            Place(b.Rect, x, y, w, h);
            Order(b.transform);
            return Take(b, out _);
        }

        /// A clickable area with no look of its own (a tab, a list row, a table row); true once
        /// after a click, `at` where it landed. Under the pointer it gets a highlight (`hover`:
        /// its strength; the graph's is fainter), and the game's sounds when `sounds`.
        internal bool Area(string id, float x, float y, float w, float h, out Vector2 at, float hover = 0.10f, bool sounds = true)
        {
            var b = NextButton(id);
            b.Set(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), "", 12f, hover, sounds, 1f);
            Place(b.Rect, x, y, w, h);
            Order(b.transform);
            return Take(b, out at);
        }

        /// The buttons and areas shown now, for the test runs: their order, id, label, centre in
        /// screen pixels from the top left, and state (hover, pressed).
        internal string Describe()
        {
            var parts = new List<string>();
            for (int i = 0; i < buttonUsed && i < buttons.Count; i++)
            {
                var b = buttons[i];
                if (!b.gameObject.activeInHierarchy) continue;
                var box = ScreenBox(b.Rect);
                parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1}:{2}@{3:F0},{4:F0}{5}", i, b.Id, b.LabelText,
                    box.X + box.W / 2, box.Y + box.H / 2, b.StateText));
            }
            return string.Join(";", parts);
        }

        internal RawImage Picture(float x, float y, float w, float h, Texture texture)
        {
            if (pictureUsed == pictures.Count)
            {
                var go = new GameObject("Picture", typeof(RectTransform));
                go.transform.SetParent(root, false);
                var raw = go.AddComponent<RawImage>();
                raw.raycastTarget = false;
                pictures.Add(raw);
            }
            var p = pictures[pictureUsed++];
            if (p.texture != texture) p.texture = texture;
            Place(p.rectTransform, x, y, w, h);
            Order(p.transform);
            return p;
        }
    }

    /// A button of the window: a rounded panel, its outline, a highlight and a label. Its press
    /// goes to its builder (PerfUi.Press), which makes the click by the control's id. The
    /// highlight shows at once, without the window building again: lighter under the pointer,
    /// darker while pressed.
    internal sealed class PerfButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler
    {
        /// What the control is in this build ("" for none: not clickable), and its builder.
        internal string Id = "";
        internal PerfUi Owner;
        internal RectTransform Rect;
        Image back, rule, highlight;
        LocText label;
        CanvasGroup group;
        bool over, down, sounds = true;
        float hover = 0.08f;

        internal static PerfButton Make(Transform parent)
        {
            var go = new GameObject("Button", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var b = go.AddComponent<PerfButton>();
            b.Rect = (RectTransform)go.transform;
            b.back = go.AddComponent<Image>();
            b.back.sprite = PerfUi.Round4;
            b.back.type = Image.Type.Sliced;
            b.back.raycastTarget = true;
            b.rule = PerfUi.NewImage(go.transform, "Rule", PerfUi.Round4Ring, Color.white);
            Stretch(b.rule.rectTransform);
            b.highlight = PerfUi.NewImage(go.transform, "Highlight", PerfUi.Round4, new Color(1, 1, 1, 0));
            Stretch(b.highlight.rectTransform);
            b.label = PerfUi.NewText(go.transform, "Label");
            Stretch(b.label.rectTransform);
            b.label.alignment = TextAlignmentOptions.Center;
            return b;
        }

        static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
        }

        internal string LabelText => label.text;
        internal string StateText => down ? " pressed" : over ? " hover" : "";

        internal void Set(Color backColour, Color ruleColour, string text, float size, float hoverAlpha, bool playSounds, float alpha)
        {
            sounds = playSounds;
            if (hover != hoverAlpha)
            {
                hover = hoverAlpha;
                ShowState();
            }
            if (alpha < 1f && group == null) group = gameObject.AddComponent<CanvasGroup>();
            if (group != null && group.alpha != alpha) group.alpha = alpha;
            if (back.color != backColour) back.color = backColour;
            if (rule.color != ruleColour) rule.color = ruleColour;
            var font = PerfUi.Title;
            if (label.font != font) label.font = font;
            if (label.fontSize != size) label.fontSize = size;
            if (label.text != text) label.text = text;
        }

        public void OnPointerEnter(PointerEventData e)
        {
            over = true;
            ShowState();
            if (sounds && Id.Length > 0) PerfUi.Sound(PerfUi.HoverSound);
        }

        public void OnPointerExit(PointerEventData e)
        {
            over = false;
            ShowState();
        }

        /// The press goes to the builder, which keeps it by the control's id until the button is
        /// up again (PerfUi.Poll).
        public void OnPointerDown(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left || Id.Length == 0) return;
            if (sounds) PerfUi.Sound(PerfUi.ClickSound);
            Owner?.Press(this);
        }

        /// Darker while its press is held (the builder says).
        internal void SetPressed(bool pressed)
        {
            if (down == pressed) return;
            down = pressed;
            if (highlight != null) ShowState();
        }

        void OnDisable()
        {
            over = down = false;
            if (highlight != null) ShowState();
        }

        /// Pressed: a dark veil; under the pointer: a light one.
        void ShowState()
        {
            var c = down ? new Color(0, 0, 0, 0.28f) : over && Id.Length > 0 ? new Color(1, 1, 1, hover) : new Color(1, 1, 1, 0);
            if (highlight.color != c) highlight.color = c;
        }
    }
}
