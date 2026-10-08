using System;
using System.Collections.Generic;
using System.Text;
using Assets.Scripts.Atmospherics;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Stationeers.RoverCargo.ScreenKit;

namespace Stationeers.RoverCargo;

/// <summary>
/// The HAB STATUS dashboard on the screen above the consoles (plan docs/superpowers/plans/2026-09-26-habitat-trailer-
/// dashboard.md), in the style of the reactor display: header with a state badge and clock, four ring gauges (room kPa,
/// room C, battery %, solar W), five cards (AIR SUPPLY, WASTE, WATER, DOOR, SYSTEMS), two 10-minute trends (room,
/// power), battery cells, warnings and a footer. A world-space canvas built at runtime on each hab instance in the
/// game's TMP font; every sprite and texture is generated here (nothing shipped). What it shows is HabRules.Dashboard;
/// numbers refresh once a second, trends sample every 5 s, on every machine from its own synced values. Dimmed while
/// stowed, dark when the battery is flat.
/// </summary>
public class HabStatusScreen : MonoBehaviour
{
    private const float W = HabRules.DashWidth, H = HabRules.DashHeight, M = 16f, G = 12f;
    private const float SampleSeconds = 5f;
    private const int Samples = 120, ChartW = 690, ChartH = 184;               // 120 x 5 s = the 10-minute trends; 1:1 with the chart box

    private sealed class GaugeView { public Image Arc; public RectTransform Marker; public TextMeshProUGUI Label, Value, Sub; }
    private sealed class CardView { public TextMeshProUGUI Title, Lines; public List<RectTransform> Fill = new(); public List<Image> FillImg = new(); }

    private CargoHab _hab;
    private Canvas _canvas;
    private CanvasGroup _group;
    private float _next, _nextSample, _lastT;
    private double _lastJ = double.NaN, _lastPct, _drawShown;
    private double? _drawW;
    private string _lastCells = "";
    private bool _failed;
    private readonly Trend _room = new(Samples, 2), _power = new(Samples, 2);
    private Texture2D _roomTex, _powerTex;
    private Color32[] _px;
    private TextMeshProUGUI _badge, _clock, _roomLegend, _powerLegend, _batteryText, _warnText, _footer;
    private Image _badgeBg;
    private readonly GaugeView[] _gauges = new GaugeView[4];
    private readonly CardView[] _cards = new CardView[5];
    private readonly Image[] _tagBg = new Image[5];
    private readonly TextMeshProUGUI[] _tagText = new TextMeshProUGUI[5];
    private RectTransform _cellsRow;
    private readonly List<(Image frame, RectTransform fill, Image fillImg, TextMeshProUGUI pct)> _cells = new();
    private readonly StringBuilder _sb = new();

    public void Init(CargoHab hab, Transform face)
    {
        _hab = hab;
        if (!FindFont())
        {
            Debug.Log("[RoverCargo] hab status screen: no TextMeshPro font found, screen off");
            Destroy(this);
            return;
        }
        var go = new GameObject("StatusCanvas", typeof(RectTransform)) { layer = face.gameObject.layer };
        go.transform.SetParent(face, false);
        _canvas = go.AddComponent<Canvas>();                              // world space first: a new canvas starts as a
        _canvas.renderMode = RenderMode.WorldSpace;                       // screen overlay, which drives its transform
        go.transform.localPosition = new Vector3(0f, 0f, 0.002f);         // 2 mm in front of the face
        go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);      // a UI front faces -z: turn it to the room
        go.transform.localScale = Vector3.one * HabRules.DashUnit;
        var root = (RectTransform)go.transform;
        root.sizeDelta = new Vector2(W, H);
        var bg = go.AddComponent<Image>();
        bg.color = Bg; bg.raycastTarget = false;
        var top = New<RectTransform>(root, "Layout", 0, 0, W, H);           // everything laid out from the top-left
        _group = top.gameObject.AddComponent<CanvasGroup>();                // dims the content, the background stays opaque
        BuildHeader(top);
        BuildGauges(top, M + 60f + G);
        BuildCards(top, M + 60f + G + 280f + G);
        BuildTrends(top, M + 60f + G + 280f + G + 170f + G);
        BuildBottom(top, M + 60f + G + 280f + G + 170f + G + 240f + G);
        _px = new Color32[ChartW * ChartH];
        _roomTex = NewChart(); _powerTex = NewChart();
        _roomChart.texture = _roomTex; _powerChart.texture = _powerTex;
        DrawChart(_roomTex, _room, 0, 150, -20, 50);
        DrawChart(_powerTex, _power, 0, 100, 0, 100);
    }

    private RawImage _roomChart, _powerChart;

    // ---- layout -------------------------------------------------------------------------------------------------

    private void BuildHeader(RectTransform p)
    {
        Label(p, M, M, 500, 60, 42, Fg, TextAlignmentOptions.Left, "HAB STATUS");
        _badgeBg = PanelAt(p, W / 2f - 120f, M + 6f, 240, 48, Off);
        _badge = Label(_badgeBg.rectTransform, 0, 0, 240, 48, 32, Bg, TextAlignmentOptions.Center, "");
        _clock = Label(p, W - M - 240f, M, 240, 60, 32, Dim, TextAlignmentOptions.Right, "");
    }

    private void BuildGauges(RectTransform p, float y)
    {
        float gw = (W - 2 * M - 3 * G) / 4f;
        for (int i = 0; i < 4; i++)
        {
            var box = PanelAt(p, M + i * (gw + G), y, gw, 280, Panel).rectTransform;
            var v = new GaugeView { Label = Label(box, 10, 8, gw - 20, 32, 26, Dim, TextAlignmentOptions.Center, "") };
            float d = 200f, cx = gw / 2f, cy = 44f + d / 2f;
            Arc(box, cx, cy, d, 0.75f, Off);
            v.Arc = Arc(box, cx, cy, d, 0f, Green);
            var pivot = New<RectTransform>(box, "Marker", cx, cy, 0, 0);    // turns about the ring centre
            var mark = New<Image>(pivot, "Tick", -3f, -(d / 2f + 4f), 6, 34);    // a tick across the ring
            mark.color = Fg; mark.raycastTarget = false;
            v.Marker = pivot;
            v.Value = Label(box, cx - 90f, cy - 30f, 180, 60, 40, Fg, TextAlignmentOptions.Center, "");
            v.Sub = Label(box, 10, 246, gw - 20, 28, 22, Dim, TextAlignmentOptions.Center, "");
            _gauges[i] = v;
        }
    }

    private void BuildCards(RectTransform p, float y)
    {
        float cw = (W - 2 * M - 4 * G) / 5f;
        for (int i = 0; i < 5; i++)
        {
            var box = PanelAt(p, M + i * (cw + G), y, cw, 170, Panel).rectTransform;
            var v = new CardView { Title = Label(box, 12, 8, cw - 24, 28, 22, Dim, TextAlignmentOptions.Left, "") };
            _cards[i] = v;
            if (i == 4)                                                         // SYSTEMS: the tags, 3 x 2
            {
                float tw = (cw - 24 - 16) / 3f;
                for (int t = 0; t < _tagBg.Length; t++)
                {
                    _tagBg[t] = PanelAt(box, 12 + (t % 3) * (tw + 8), 44 + (t / 3) * 62, tw, 54, Off);
                    _tagText[t] = Label(_tagBg[t].rectTransform, 4, 0, tw - 8, 54, 20, Fg, TextAlignmentOptions.Center, "");
                }
                continue;
            }
            int bars = i == 2 ? 2 : 1;
            v.Lines = Label(box, 12, 40, cw - 24, bars == 2 ? 72 : 60, bars == 2 ? 26 : 34, Fg, TextAlignmentOptions.TopLeft, "");
            for (int b = 0; b < bars; b++)
            {
                var track = PanelAt(box, 12, bars == 2 ? 120 + b * 24 : 128, cw - 24, bars == 2 ? 14 : 22, Off);
                var fill = New<Image>(track.rectTransform, "Fill", 0, 0, 0, 0);
                var r = fill.rectTransform;
                r.anchorMin = Vector2.zero; r.anchorMax = new Vector2(0f, 1f); r.pivot = Vector2.zero;
                r.offsetMin = r.offsetMax = Vector2.zero;
                fill.raycastTarget = false; Round(fill);
                v.Fill.Add(r); v.FillImg.Add(fill);
            }
        }
    }

    private void BuildTrends(RectTransform p, float y)
    {
        float tw = (W - 2 * M - G) / 2f;
        for (int i = 0; i < 2; i++)
        {
            var box = PanelAt(p, M + i * (tw + G), y, tw, 240, Panel).rectTransform;
            Label(box, 12, 8, 260, 30, 22, Dim, TextAlignmentOptions.Left, i == 0 ? "ROOM  10 min" : "POWER  10 min");
            var legend = Label(box, 270, 8, tw - 282, 30, 22, Fg, TextAlignmentOptions.Right, "");
            var chart = New<RawImage>(box, "Chart", 12, 44, tw - 24, 184);
            chart.raycastTarget = false;
            if (i == 0) { _roomLegend = legend; _roomChart = chart; } else { _powerLegend = legend; _powerChart = chart; }
        }
    }

    private void BuildBottom(RectTransform p, float y)
    {
        float h = H - M - y, bw = 470f;
        var bat = PanelAt(p, M, y, bw, h, Panel).rectTransform;
        Label(bat, 12, 8, bw - 24, 28, 22, Dim, TextAlignmentOptions.Left, "BATTERY CELLS");
        _cellsRow = New<RectTransform>(bat, "Cells", 12, 42, bw - 24, 76);
        _batteryText = Label(bat, 12, h - 44, bw - 24, 36, 26, Fg, TextAlignmentOptions.Left, "");
        float ww = W - 2 * M - bw - G;
        var warn = PanelAt(p, M + bw + G, y, ww, h, Panel).rectTransform;
        Label(warn, 12, 8, ww - 24, 28, 22, Dim, TextAlignmentOptions.Left, "WARNINGS");
        _warnText = Label(warn, 12, 40, ww - 24, h - 76, 24, Fg, TextAlignmentOptions.TopLeft, "");
        _footer = Label(warn, 12, h - 36, ww - 24, 28, 20, Dim, TextAlignmentOptions.Left, "");
    }

    // ---- refresh ------------------------------------------------------------------------------------------------

    private void Update()
    {
        if (!_hab || !_canvas || _failed || Time.time < _next) return;
        _next = Time.time + 1f;
        try
        {
            var inputs = _hab.StatusInputs();
            if (Time.time >= _nextSample) Sample(inputs);
            inputs.DrawW = _drawW;
            var d = HabRules.Dashboard(inputs);
            if (_canvas.enabled == d.Dark) _canvas.enabled = !d.Dark;
            if (d.Dark) return;
            _group.alpha = d.Dim ? 0.35f : 1f;
            Show(d, inputs);
        }
        catch (Exception e)
        {
            _failed = true;                                                    // once: a broken screen must not spam
            Debug.LogError($"[RoverCargo] hab status screen stopped: {e}");
        }
    }

    /// <summary>Every 5 s: the room (kPa, C) and power (solar W, draw W) trends. Draw is HabRules.DrawEstimate (per
    /// atmos tick, like the game's W); while it cannot be told the trend holds the last known draw.</summary>
    private void Sample(StatusInputs s)
    {
        _nextSample = Time.time + SampleSeconds;
        var cells = new StringBuilder();
        foreach (var c in s.Cells ?? Array.Empty<double>()) cells.Append(c < 0 ? '0' : '1');
        float tick = AtmosphericsManager.Instance ? AtmosphericsManager.Instance.TickSpeedSeconds : 0.5f;
        _drawW = double.IsNaN(_lastJ) ? null : HabRules.DrawEstimate(s.SolarW, s.BatteryJ - _lastJ, Time.time - _lastT, tick,
            s.BatteryPct >= 99.5 || _lastPct >= 99.5, cells.ToString() != _lastCells);
        _lastJ = s.BatteryJ; _lastT = Time.time; _lastPct = s.BatteryPct; _lastCells = cells.ToString();
        if (_drawW is double dw) _drawShown = dw;
        _room.Add(s.RoomKPa, s.RoomC);
        if (_drawW != null || _power.Count > 0) _power.Add(s.SolarW, _drawShown);
        DrawChart(_roomTex, _room, 0, 150, -20, 50);
        double top = Math.Max(100, Math.Max(_power.Max(0), _power.Max(1)) * 1.1);
        DrawChart(_powerTex, _power, 0, top, 0, top);
    }

    private void Show(DashModel d, StatusInputs s)
    {
        Set(_badge, d.Badge);
        _badgeBg.color = LevelColor(d.BadgeLevel);
        Set(_clock, DateTime.Now.ToString("HH:mm"));
        for (int i = 0; i < 4 && i < d.Gauges.Length; i++)
        {
            var g = d.Gauges[i]; var v = _gauges[i];
            float t = g.Max > g.Min ? Mathf.Clamp01((float)((g.Value - g.Min) / (g.Max - g.Min))) : 0f;
            Set(v.Label, g.Label); Set(v.Value, g.ValueText); Set(v.Sub, g.Sub);
            v.Arc.fillAmount = 0.75f * t;
            v.Arc.color = i == 3 ? Accent : LevelColor(g.Level);                // solar has no bands
            v.Marker.localEulerAngles = new Vector3(0f, 0f, -(225f + 270f * t));
        }
        for (int i = 0; i < 5 && i < d.Cards.Length; i++)
        {
            var c = d.Cards[i]; var v = _cards[i];
            Set(v.Title, c.Title);
            if (v.Lines)
            {
                _sb.Clear();
                for (int l = 0; l < c.Lines.Length; l++)
                {
                    if (l > 0) _sb.Append('\n');
                    var lv = c.LineLevels != null && l < c.LineLevels.Length ? c.LineLevels[l] : StatusLevel.Normal;
                    if (lv == StatusLevel.Normal) _sb.Append(c.Lines[l]);
                    else _sb.Append("<color=").Append(Html(LevelColor(lv))).Append('>').Append(c.Lines[l]).Append("</color>");
                }
                Set(v.Lines, _sb.ToString());
            }
            for (int b = 0; b < v.Fill.Count; b++)
            {
                var bar = b < c.Bars.Length ? c.Bars[b] : default;
                v.Fill[b].anchorMax = new Vector2((float)bar.Ratio, 1f);
                v.FillImg[b].color = LevelColor(bar.Level);
            }
        }
        for (int t = 0; t < _tagBg.Length && t < d.Tags.Length; t++)
        {
            Set(_tagText[t], d.Tags[t].Name);
            _tagBg[t].color = d.Tags[t].On ? Accent : Off;
            _tagText[t].color = d.Tags[t].On ? Bg : Dim;
        }
        Set(_roomLegend, $"<color={Html(Accent)}>{s.RoomKPa:0.0} kPa</color>   <color={Html(Yellow)}>{s.RoomC:0.0} C</color>");
        Set(_powerLegend, $"<color={Html(Yellow)}>solar {s.SolarW:0} W</color>   <color={Html(Accent)}>draw {(_drawW is double dw ? dw.ToString("0") : "--")} W</color>");
        ShowCells(d.Cells);
        Set(_batteryText, d.BatteryText);
        _sb.Clear();
        if (d.Warnings.Length == 0) _sb.Append($"<color={Html(Green)}>All systems normal</color>");
        for (int i = 0; i < d.Warnings.Length && i < 3; i++)
            _sb.Append(i > 0 ? "\n" : "").Append($"<color={Html(LevelColor(d.Warnings[i].Level))}>{d.Warnings[i].Text}</color>");
        if (d.Warnings.Length > 3) _sb.Append($"  <color={Html(Dim)}>+{d.Warnings.Length - 3} more</color>");
        Set(_warnText, _sb.ToString());
        Set(_footer, d.Footer);
    }

    private void ShowCells(double[] cells)
    {
        if (_cells.Count != cells.Length)
        {
            foreach (var c in _cells) Destroy(c.frame.gameObject);
            _cells.Clear();
            float rw = _cellsRow.sizeDelta.x, cw = cells.Length == 0 ? 0 : Mathf.Min(140f, (rw - (cells.Length - 1) * 10f) / cells.Length);
            for (int i = 0; i < cells.Length; i++)
            {
                var frame = PanelAt(_cellsRow, i * (cw + 10f), 0, cw, 76, Off);
                var inset = New<RectTransform>(frame.rectTransform, "Inset", 0, 0, 0, 0);   // 4 px inside the frame
                inset.anchorMin = Vector2.zero; inset.anchorMax = Vector2.one;
                inset.offsetMin = new Vector2(4f, 4f); inset.offsetMax = new Vector2(-4f, -4f);
                var fill = New<Image>(inset, "Fill", 0, 0, 0, 0);
                var r = fill.rectTransform;                                     // grows up from the inset's bottom
                r.anchorMin = Vector2.zero; r.anchorMax = new Vector2(1f, 0f); r.pivot = Vector2.zero;
                r.offsetMin = r.offsetMax = Vector2.zero;
                fill.raycastTarget = false; Round(fill);
                var pct = Label(frame.rectTransform, 0, 0, cw, 76, 26, Fg, TextAlignmentOptions.Center, "");
                _cells.Add((frame, r, fill, pct));
            }
        }
        for (int i = 0; i < cells.Length; i++)
        {
            var (_, fill, img, pct) = _cells[i];
            double v = cells[i];
            fill.anchorMax = new Vector2(1f, v < 0 ? 0f : Mathf.Clamp01((float)v));
            img.enabled = v > 0;
            img.color = v < 0.2 ? Red : v < 0.4 ? Yellow : Green;
            Set(pct, v < 0 ? "empty" : $"{v * 100:0}%");
        }
    }

    // ---- charts -------------------------------------------------------------------------------------------------

    private static Texture2D NewChart() =>
        new(ChartW, ChartH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "HabTrend" };

    /// <summary>Redraws a trend: grid, then series 0 (accent) and 1 (yellow on the room chart, accent/yellow swapped on
    /// power so solar is yellow), each scaled to its own range, newest sample at the right edge.</summary>
    private void DrawChart(Texture2D tex, Trend tr, double lo0, double hi0, double lo1, double hi1)
    {
        if (!tex) return;
        Color32 bg = Bg, grid = Off;
        for (int i = 0; i < _px.Length; i++) _px[i] = bg;
        for (int k = 1; k < 4; k++) { int y = k * (ChartH - 1) / 4; for (int x = 0; x < ChartW; x++) _px[y * ChartW + x] = grid; }
        bool power = tex == _powerTex;
        Series(tr, 0, lo0, hi0, power ? Yellow : Accent);
        Series(tr, 1, lo1, hi1, power ? Accent : Yellow);
        tex.SetPixels32(_px);
        tex.Apply(false);
    }

    private void Series(Trend tr, int k, double lo, double hi, Color32 c)
    {
        if (tr.Count < 2 || hi <= lo) return;
        int Px(int i) => (ChartW - 1) - (tr.Count - 1 - i) * (ChartW - 1) / (Samples - 1);
        int Py(int i) => Mathf.Clamp((int)Math.Round((tr.Get(i, k) - lo) / (hi - lo) * (ChartH - 3)) + 1, 1, ChartH - 2);
        for (int i = 1; i < tr.Count; i++) Line(Px(i - 1), Py(i - 1), Px(i), Py(i), c);
    }

    private void Line(int x0, int y0, int x1, int y1, Color32 c)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, e = dx + dy;
        while (true)
        {
            Plot(x0, y0, c); Plot(x0, y0 + 1, c); Plot(x0 + 1, y0, c); Plot(x0 + 1, y0 + 1, c);   // 2 px thick either way
            if (x0 == x1 && y0 == y1) return;
            int e2 = 2 * e;
            if (e2 >= dy) { e += dy; x0 += sx; }
            if (e2 <= dx) { e += dx; y0 += sy; }
        }
    }

    private void Plot(int x, int y, Color32 c) { if (x >= 0 && x < ChartW && y >= 0 && y < ChartH) _px[y * ChartW + x] = c; }

    private void OnDestroy()
    {
        if (_roomTex) Destroy(_roomTex);
        if (_powerTex) Destroy(_powerTex);
    }
}
