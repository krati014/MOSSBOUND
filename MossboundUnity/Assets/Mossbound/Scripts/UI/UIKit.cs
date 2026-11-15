using UnityEngine;
using UnityEngine.UI;

namespace Mossbound
{
    /// <summary>Tiny code-only UI kit: rounded panels, outlines, labels, buttons. No external assets needed.</summary>
    public static class UIKit
    {
        static Font font;
        static Sprite rounded, outline, circle;

        public static Font Font
        {
            get
            {
                if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (font == null) font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial" }, 16);
                return font;
            }
        }

        static Sprite MakeRounded(bool ring)
        {
            const int S = 64; const float Rad = 22f; const float Line = 2.2f;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float px = Mathf.Clamp(x + 0.5f, Rad, S - Rad), py = Mathf.Clamp(y + 0.5f, Rad, S - Rad);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(px, py)); // 0 inside the inner rect
                    float sdf = d - Rad; // <0 inside shape
                    float a = Mathf.Clamp01(0.5f - sdf);
                    if (ring) a *= Mathf.Clamp01(sdf + Line + 0.5f);
                    tex.SetPixel(x, y, new Color(1, 1, 1, a));
                }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(26, 26, 26, 26));
        }

        public static Sprite Rounded { get { if (rounded == null) rounded = MakeRounded(false); return rounded; } }
        public static Sprite Outline { get { if (outline == null) outline = MakeRounded(true); return outline; } }

        public static Sprite Circle
        {
            get
            {
                if (circle != null) return circle;
                const int S = 64;
                var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(S / 2f, S / 2f));
                        tex.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(S / 2f - 1 - d)));
                    }
                tex.Apply();
                circle = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100);
                return circle;
            }
        }

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>Position a rect inside its parent. anchor = pivot = same corner/edge (0..1).</summary>
        public static RectTransform Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            return rt;
        }

        public static RectTransform Stretch(RectTransform rt, float l = 0, float r = 0, float top = 0, float b = 0)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(l, b); rt.offsetMax = new Vector2(-r, -top);
            return rt;
        }

        public static Image Panel(Transform parent, string name, Color c, float radiusScale = 2f, bool withOutline = true)
        {
            var rt = Rect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = Rounded; img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = radiusScale; img.color = c;
            if (withOutline)
            {
                var o = Stretch(Rect("Outline", rt));
                var oi = o.gameObject.AddComponent<Image>();
                oi.sprite = Outline; oi.type = Image.Type.Sliced; oi.pixelsPerUnitMultiplier = radiusScale; oi.color = Palette.UiLine; oi.raycastTarget = false;
            }
            return img;
        }

        public static Image Dot(Transform parent, string name, Color c, float size)
        {
            var rt = Rect(name, parent); rt.sizeDelta = new Vector2(size, size);
            var img = rt.gameObject.AddComponent<Image>(); img.sprite = Circle; img.color = c; img.raycastTarget = false;
            return img;
        }

        public static Image Line(Transform parent, Vector2 anchor, Vector2 pos, Vector2 size, Color c)
        {
            var rt = Place(Rect("Line", parent), anchor, pos, size);
            var img = rt.gameObject.AddComponent<Image>(); img.color = c; img.raycastTarget = false;
            return img;
        }

        public static Text Label(Transform parent, string text, int size, Color c, TextAnchor align = TextAnchor.MiddleLeft, FontStyle style = FontStyle.Normal)
        {
            var rt = Rect("Text", parent);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Font; t.text = text; t.fontSize = size; t.color = c; t.alignment = align; t.fontStyle = style;
            t.supportRichText = true; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
            return t;
        }

        public static Button Button(Transform parent, string name, string label, Color bg, Color fg, int fontSize, System.Action onClick, bool outlineOnly = false)
        {
            var img = Panel(parent, name, outlineOnly ? new Color(bg.r, bg.g, bg.b, 0.08f) : bg, 2.4f, outlineOnly);
            if (outlineOnly) img.transform.Find("Outline").GetComponent<Image>().color = new Color(bg.r, bg.g, bg.b, 0.55f);
            var b = img.gameObject.AddComponent<Button>();
            var cb = b.colors;
            cb.normalColor = Color.white; cb.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f); cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            cb.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f); cb.fadeDuration = 0.08f; cb.colorMultiplier = 1.4f;
            b.colors = cb;
            b.targetGraphic = img;
            b.onClick.AddListener(() => onClick());
            var t = Label(img.transform, label, fontSize, fg, TextAnchor.MiddleCenter, FontStyle.Bold);
            Stretch(t.rectTransform);
            return b;
        }
    }
}
