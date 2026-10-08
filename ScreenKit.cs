using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Stationeers.RoverCargo;

/// <summary>
/// The drawing kit the dash screens share (HabStatusScreen, RoverStatusScreen): the reactor-display palette, the
/// game's TMP font (found once), rounded panels, labels, 270-degree gauge rings and a disc. Everything is generated at
/// runtime (no shipped sprites or fonts). Moved unchanged from HabStatusScreen; Disc is new.
/// </summary>
internal static class ScreenKit
{
    internal static TMP_FontAsset Font => _font;
    private static TMP_FontAsset _font;                                         // found once, shared by every screen
    private static Sprite _ring, _round, _disc;                                 // shared procedural sprites

    internal static readonly Color Bg = Hex(0x070B14), Panel = Hex(0x0E1626), Fg = Hex(0xE2E8F0), Dim = Hex(0x7C8BA5),
        Accent = Hex(0x38BDF8), Green = Hex(0x22C55E), Yellow = Hex(0xEAB308), Red = Hex(0xEF4444), Off = Hex(0x334155);

    // ---- widgets ------------------------------------------------------------------------------------------------

    internal static T New<T>(Transform parent, string name, float x, float y, float w, float h) where T : Component
    {
        var go = new GameObject(name, typeof(RectTransform)) { layer = parent.gameObject.layer };
        var r = (RectTransform)go.transform;
        r.SetParent(parent, false);
        r.anchorMin = r.anchorMax = new Vector2(0f, 1f);                      // from the parent's top-left corner
        r.pivot = new Vector2(0f, 1f);
        r.anchoredPosition = new Vector2(x, -y);
        r.sizeDelta = new Vector2(w, h);
        return typeof(T) == typeof(RectTransform) ? (T)(Component)r : go.AddComponent<T>();
    }

    internal static Image PanelAt(Transform p, float x, float y, float w, float h, Color c)
    {
        var img = New<Image>(p, "Panel", x, y, w, h);
        img.color = c; img.raycastTarget = false;
        Round(img);
        return img;
    }

    internal static TextMeshProUGUI Label(Transform p, float x, float y, float w, float h, float size, Color c, TextAlignmentOptions al, string text)
    {
        var t = New<TextMeshProUGUI>(p, "Text", x, y, w, h);
        t.font = _font;
        t.fontSize = size; t.enableAutoSizing = true; t.fontSizeMax = size; t.fontSizeMin = size * 0.5f;   // long values shrink to fit
        t.enableWordWrapping = false; t.overflowMode = TextOverflowModes.Truncate; t.richText = true;
        t.alignment = al; t.color = c; t.raycastTarget = false; t.text = text;
        return t;
    }

    /// <summary>A 270 degree ring (gap at the bottom) filled clockwise from lower left.</summary>
    internal static Image Arc(Transform p, float cx, float cy, float d, float fill, Color c)
    {
        var img = New<Image>(p, "Arc", cx - d / 2f, cy - d / 2f, d, d);
        img.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        img.rectTransform.anchoredPosition = new Vector2(cx, -cy);
        img.sprite = Ring();
        img.type = Image.Type.Filled; img.fillMethod = Image.FillMethod.Radial360;
        img.fillOrigin = (int)Image.Origin360.Bottom; img.fillClockwise = true; img.fillAmount = fill;
        img.rectTransform.localEulerAngles = new Vector3(0f, 0f, -45f);         // start at 7:30, end at 4:30
        img.color = c; img.raycastTarget = false;
        return img;
    }

    /// <summary>The gauge ring: a 128 px anti-aliased annulus (outer radius 63, inner 49), made once.</summary>
    internal static Sprite Ring()
    {
        if (_ring) return _ring;
        const int n = 128; const float ro = 63f, ri = 49f;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "HabGaugeRing" };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float r = Mathf.Sqrt((x + 0.5f - n / 2f) * (x + 0.5f - n / 2f) + (y + 0.5f - n / 2f) * (y + 0.5f - n / 2f));
                float a = Mathf.Clamp01(ro - r + 0.5f) * Mathf.Clamp01(r - ri + 0.5f);
                px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return _ring = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
    }

    /// <summary>Rounded corners: a 9-sliced 32 px rounded square (radius 10, anti-aliased), made once; Unity shrinks
    /// the corners on boxes smaller than them (thin bars come out as pills).</summary>
    internal static void Round(Image img)
    {
        if (!_round)
        {
            const int n = 32; const float rad = 10f;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "HabPanelRound" };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = Mathf.Max(0f, Mathf.Abs(x + 0.5f - n / 2f) - (n / 2f - rad)), dy = Mathf.Max(0f, Mathf.Abs(y + 0.5f - n / 2f) - (n / 2f - rad));
                    float a = Mathf.Clamp01(rad - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            _round = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(12, 12, 12, 12));
        }
        img.sprite = _round;
        img.type = Image.Type.Sliced;
    }

    internal static void Set(TMP_Text t, string s) { if (t && t.text != s) t.text = s; }

    internal static Color LevelColor(StatusLevel l) => l switch
    {
        StatusLevel.Fault => Red, StatusLevel.Warning => Yellow, StatusLevel.Off => Off, _ => Green,
    };

    internal static Color Hex(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);

    internal static string Html(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);

    /// <summary>The game's font, found once: its default TMP font if that has every character the dashboard uses, else
    /// a monospace/console one, else any game font that has them all (never an icon or digit-only font).</summary>
    internal static TMP_FontAsset FindFont()
    {
        if (_font) return _font;
        const string need = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .%/-+:";
        bool Usable(TMP_FontAsset f) => f && f.HasCharacters(need, out _, false, false);
        if (Usable(TMP_Settings.defaultFontAsset)) return _font = TMP_Settings.defaultFontAsset;
        TMP_FontAsset any = null;
        foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (!Usable(f)) continue;
            if (f.name.IndexOf("mono", StringComparison.OrdinalIgnoreCase) >= 0 ||
                f.name.IndexOf("console", StringComparison.OrdinalIgnoreCase) >= 0) return _font = f;
            any ??= f;
        }
        return _font = any;
    }

    /// <summary>A filled anti-aliased disc (64 px), made once: the rover pictures' wheels and the standby POWER button.</summary>
    internal static Sprite Disc()
    {
        if (_disc) return _disc;
        const int n = 64; const float r0 = 31f;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "ScreenDisc" };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float r = Mathf.Sqrt((x + 0.5f - n / 2f) * (x + 0.5f - n / 2f) + (y + 0.5f - n / 2f) * (y + 0.5f - n / 2f));
                px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(r0 - r + 0.5f) * 255));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return _disc = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
    }
}
