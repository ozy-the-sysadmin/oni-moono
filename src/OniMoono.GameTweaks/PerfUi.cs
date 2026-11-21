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
    internal sealed class PerfUi
    {
        // The mock's colours.
        internal static readonly Color Ink = Hex("E9EBF2"), Muted = Hex("A3A9BD"), Panel = Hex("3A3F52"), Panel2 = Hex("2F3445"),
            Rule = Hex("555B73"), Head = Hex("7B3B5E"), HeadHi = Hex("A04F79"), Good = Hex("7FD66B"), Warn = Hex("F0C64A"),
            Bad = Hex("EF6B5B"), Accent = Hex("62C8E8"), Sim = Hex("62C8E8"), Anim = Hex("B58CF0"), Draw = Hex("F0A24A"),
            UiColour = Hex("7FD66B"), Other = Hex("8087A0"), GainInk = Hex("CFE9C9"), ButtonBack = Hex("4A5170"), ButtonRule = Hex("6B7396"),
            Go = Hex("3F7A3A"), GoRule = Hex("62A85B"), Line = new Color(1, 1, 1, 0.063f), BadgeBack = new Color32(0x20, 0x24, 0x2F, 0xE6);
        internal const string InkHex = "E9EBF2", MutedHex = "A3A9BD", GoodHex = "7FD66B", WarnHex = "F0C64A", BadHex = "EF6B5B";

        internal static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString("#" + hex, out var c);
            return c;
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
            float r = size == radius * 2 ? radius : radius;
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

        // ---- The builder ----

        readonly RectTransform root;
        readonly List<Image> boxes = new List<Image>();
        readonly List<LocText> texts = new List<LocText>();
        readonly List<PerfButton> buttons = new List<PerfButton>();
        readonly List<RawImage> pictures = new List<RawImage>();
        int boxUsed, textUsed, buttonUsed, pictureUsed, order;
        readonly LocText measure;
        /// A button was clicked during this build: the window builds again next frame.
        internal bool Clicked;

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
        }

        internal void End()
        {
            for (int i = boxUsed; i < boxes.Count; i++) Hide(boxes[i].gameObject);
            for (int i = textUsed; i < texts.Count; i++) Hide(texts[i].gameObject);
            for (int i = buttonUsed; i < buttons.Count; i++) Hide(buttons[i].gameObject);
            for (int i = pictureUsed; i < pictures.Count; i++) Hide(pictures[i].gameObject);
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

        /// One label: `align` within (x, y, w, h); `wrap` lets it take several lines.
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

        /// A button with a label; true once after it was clicked.
        internal bool Button(float x, float y, float w, float h, string label, Color back, Color rule, float size = 14f)
        {
            if (buttonUsed == buttons.Count) buttons.Add(PerfButton.Make(root));
            var b = buttons[buttonUsed++];
            b.Set(back, rule, label, size);
            Place(b.Rect, x, y, w, h);
            Order(b.transform);
            if (b.Clicks == 0) return false;
            b.Clicks = 0;
            Clicked = true;
            return true;
        }

        /// A clickable area with no look (a list row, a table row); true once after a click.
        internal bool Area(float x, float y, float w, float h, out Vector2 at)
        {
            if (buttonUsed == buttons.Count) buttons.Add(PerfButton.Make(root));
            var b = buttons[buttonUsed++];
            b.Set(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), "", 12f);
            Place(b.Rect, x, y, w, h);
            Order(b.transform);
            at = b.At;
            if (b.Clicks == 0) return false;
            b.Clicks = 0;
            Clicked = true;
            return true;
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

    /// A button of the window: a rounded panel, its outline and a label; counts its clicks and
    /// where the last one landed (local units from the top left).
    internal sealed class PerfButton : MonoBehaviour, IPointerClickHandler
    {
        internal int Clicks;
        internal Vector2 At;
        internal RectTransform Rect;
        Image back, rule;
        LocText label;

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

        internal void Set(Color backColour, Color ruleColour, string text, float size)
        {
            if (back.color != backColour) back.color = backColour;
            if (rule.color != ruleColour) rule.color = ruleColour;
            var font = PerfUi.Title;
            if (label.font != font) label.font = font;
            if (label.fontSize != size) label.fontSize = size;
            if (label.text != text) label.text = text;
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left) return;
            Clicks++;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(Rect, e.position, e.pressEventCamera, out var local))
                At = new Vector2(local.x, -local.y);
        }
    }
}
