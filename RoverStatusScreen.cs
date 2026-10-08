using System;
using System.Collections.Generic;
using Assets.Scripts.Atmospherics;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Stationeers.RoverCargo.ScreenKit;

namespace Stationeers.RoverCargo;

/// <summary>
/// The original rover's touch dash screen (spec docs/superpowers/specs/2026-09-27-original-rover-cab-design.md): a
/// world-space canvas built at runtime on each rover instance, on its Screen anchor, in the hab status screen's style
/// (ScreenKit). What it shows is RoverDashboard.Build. The controls' tap colliders (the builder makes them under
/// the anchor) are moved and switched per page from RoverDashboard.TapRect. A missing font or a refresh error turns
/// the screen off, once, and leaves the targets on the driving layout, so the rover can still be driven and switched.
/// </summary>
public class RoverStatusScreen : MonoBehaviour
{
    private const float W = RoverDashboard.Width, H = RoverDashboard.Height, M = 16f, G = 12f, CardsY = 382f;
    private CargoRover _rover;
    private Canvas _canvas;
    private RectTransform _drive, _standby;
    private bool _failed, _degree, _thrusters;
    private float _nextFast, _nextSlow, _lastT;
    private RoverPage? _page;
    private double _lastJ = double.NaN;
    private int _lastCells;
    private double? _drawW;
    private TextMeshProUGUI _badge, _tow, _clock, _standbyText, _speedValue, _speedSub, _battValue, _battCells, _battSub,
        _pitchValue, _pitchWords, _rollValue, _rollWords;
    private Image _badgeBg, _towBg, _speedArc, _battArc;
    private RectTransform _pitchPivot, _rollPivot;
    private readonly List<Image> _pitchParts = new(), _rollParts = new();
    private readonly List<(Image box, TextMeshProUGUI title, TextMeshProUGUI lines, RectTransform fill, Image fillImg)> _cards = new();
    private readonly (Image bg, TextMeshProUGUI name, TextMeshProUGUI state)[] _buttons = new (Image, TextMeshProUGUI, TextMeshProUGUI)[RoverDashboard.Controls.Length];

    public void Init(CargoRover rover, Transform face)
    {
        _rover = rover;
        if (!FindFont())
        {
            Debug.Log("[RoverCargo] rover dash screen: no TextMeshPro font found, screen off (the tap targets still work)");
            Destroy(this);
            return;
        }
        _degree = ScreenKit.Font.HasCharacter('°');           // qualified: UnityEngine.Font is a type
        var go = new GameObject("DashCanvas", typeof(RectTransform)) { layer = face.gameObject.layer };
        go.transform.SetParent(face, false);
        _canvas = go.AddComponent<Canvas>();                              // world space first: a new canvas starts as a
        _canvas.renderMode = RenderMode.WorldSpace;                       // screen overlay, which drives its transform
        go.transform.localPosition = new Vector3(0f, 0f, 0.002f);         // 2 mm in front of the face
        go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);      // a UI front faces -z: turn it to the cab
        go.transform.localScale = Vector3.one * RoverDashboard.Unit;
        var root = (RectTransform)go.transform;
        root.sizeDelta = new Vector2(W, H);
        var bg = go.AddComponent<Image>();
        bg.color = Bg; bg.raycastTarget = false;
        _drive = New<RectTransform>(root, "Driving", 0, 0, W, H);
        _standby = New<RectTransform>(root, "Standby", 0, 0, W, H);
        BuildHeader(_drive);
        BuildGauges(_drive, 84f);
        BuildCards(_drive);
        BuildButtons(_drive);
        BuildStandby(_standby);
    }

    // ---- layout (the mockup: docs/superpowers/specs/2026-09-27-original-rover-dash-mockup.html) ------------------

    private void BuildHeader(RectTransform p)
    {
        Label(p, 18, 14, 300, 56, 38, Fg, TextAlignmentOptions.Left, "ROVER");
        _badgeBg = PanelAt(p, W / 2f - 120f, 18f, 240, 46, Off);
        _badge = Label(_badgeBg.rectTransform, 0, 0, 240, 46, 28, Bg, TextAlignmentOptions.Center, "");
        _towBg = PanelAt(p, W - 18f - 120f - 12f - 210f, 24f, 210, 34, Off);
        _tow = Label(_towBg.rectTransform, 0, 0, 210, 34, 20, Fg, TextAlignmentOptions.Center, "");
        _clock = Label(p, W - 18f - 120f, 18f, 120, 46, 30, Dim, TextAlignmentOptions.Right, "");
    }

    private void BuildGauges(RectTransform p, float y)
    {
        float gw = (W - 2 * M - 3 * G) / 4f;
        RectTransform Box(int i, string title)
        {
            var box = PanelAt(p, M + i * (gw + G), y, gw, 286, Panel).rectTransform;
            Label(box, 10, 10, gw - 20, 30, 20, Dim, TextAlignmentOptions.Center, title);
            return box;
        }
        var sp = Box(0, "SPEED");
        Arc(sp, gw / 2f, 144f, 176f, 0.75f, Off);
        _speedArc = Arc(sp, gw / 2f, 144f, 176f, 0f, Accent);
        _speedValue = Label(sp, gw / 2f - 90f, 110f, 180, 60, 52, Fg, TextAlignmentOptions.Center, "");
        Label(sp, gw / 2f - 90f, 168f, 180, 28, 20, Dim, TextAlignmentOptions.Center, "km/h");
        _speedSub = Label(sp, 10, 250, gw - 20, 26, 18, Dim, TextAlignmentOptions.Center, "");
        var pt = Box(1, "PITCH");
        _pitchPivot = Silhouette(pt, gw / 2f, 124f, true, _pitchParts);
        _pitchValue = Label(pt, 10, 186, gw - 20, 52, 44, Fg, TextAlignmentOptions.Center, "");
        _pitchWords = Label(pt, 10, 250, gw - 20, 26, 18, Dim, TextAlignmentOptions.Center, "");
        var rl = Box(2, "ROLL");
        _rollPivot = Silhouette(rl, gw / 2f, 124f, false, _rollParts);
        _rollValue = Label(rl, 10, 186, gw - 20, 52, 44, Fg, TextAlignmentOptions.Center, "");
        _rollWords = Label(rl, 10, 250, gw - 20, 26, 18, Dim, TextAlignmentOptions.Center, "");
        var bt = Box(3, "BATTERY");
        Arc(bt, gw / 2f, 144f, 176f, 0.75f, Off);
        _battArc = Arc(bt, gw / 2f, 144f, 176f, 0f, Green);
        _battValue = Label(bt, gw / 2f - 90f, 110f, 180, 60, 52, Fg, TextAlignmentOptions.Center, "");
        _battCells = Label(bt, gw / 2f - 90f, 168f, 180, 28, 20, Dim, TextAlignmentOptions.Center, "");
        _battSub = Label(bt, 10, 250, gw - 20, 26, 18, Dim, TextAlignmentOptions.Center, "");
    }

    /// <summary>A small rover picture on a pivot the refresh turns (side view: pitch, nose to the right; rear view:
    /// roll), over a ground line.</summary>
    private static RectTransform Silhouette(RectTransform box, float cx, float cy, bool side, List<Image> parts)
    {
        var ground = New<Image>(box, "Ground", 20f, cy + 30f, box.sizeDelta.x - 40f, 2f);
        ground.color = Off; ground.raycastTarget = false;
        var pivot = New<RectTransform>(box, "Pivot", cx, cy, 0, 0);
        void Part(float x, float y, float w, float h, bool wheel)
        {
            var img = New<Image>(pivot, wheel ? "Wheel" : "Body", x, y, w, h);
            img.raycastTarget = false;
            if (wheel) img.sprite = Disc(); else Round(img);
            parts.Add(img);
        }
        if (side)
        {
            Part(-80f, -26f, 150f, 30f, false);                                 // body
            Part(8f, -38f, 62f, 18f, false);                                    // cab
            Part(-66f, -4f, 26f, 26f, true); Part(-34f, -4f, 26f, 26f, true); Part(46f, -4f, 26f, 26f, true);
        }
        else
        {
            Part(-46f, -40f, 92f, 46f, false);                                  // rear body
            Part(-64f, -2f, 22f, 30f, true); Part(42f, -2f, 22f, 30f, true);
        }
        return pivot;
    }

    private void BuildCards(RectTransform p)
    {
        for (int i = 0; i < 5; i++)                                             // up to 4 data cards + WARNINGS, laid out per refresh
        {
            var box = PanelAt(p, 0, CardsY, 10, 146, Panel);
            var title = Label(box.rectTransform, 14, 10, 200, 26, 20, Dim, TextAlignmentOptions.Left, "");
            var lines = Label(box.rectTransform, 14, 40, 200, 70, 28, Fg, TextAlignmentOptions.TopLeft, "");
            var track = PanelAt(box.rectTransform, 14, 118, 196, 14, Off);
            var fill = New<Image>(track.rectTransform, "Fill", 0, 0, 0, 0);
            var r = fill.rectTransform;
            r.anchorMin = Vector2.zero; r.anchorMax = new Vector2(0f, 1f); r.pivot = Vector2.zero;
            r.offsetMin = r.offsetMax = Vector2.zero;
            fill.raycastTarget = false; Round(fill);
            _cards.Add((box, title, lines, r, fill));
        }
    }

    private void BuildButtons(RectTransform p)
    {
        for (int i = 0; i < _buttons.Length; i++)
        {
            var (x, y, w, h) = RoverDashboard.DriveButton(i);
            var bg = PanelAt(p, x, y, w, h, Off);
            var name = Label(bg.rectTransform, 0, 22, w, 30, 21, Fg, TextAlignmentOptions.Center, RoverDashboard.Controls[i].Label);
            var state = Label(bg.rectTransform, 0, 58, w, 42, 28, Fg, TextAlignmentOptions.Center, "");
            _buttons[i] = (bg, name, state);
        }
    }

    private void BuildStandby(RectTransform p)
    {
        Label(p, 18, 14, 300, 56, 38, Off, TextAlignmentOptions.Left, "ROVER");
        var badge = PanelAt(p, W / 2f - 120f, 18f, 240, 46, Off);
        Label(badge.rectTransform, 0, 0, 240, 46, 28, Fg, TextAlignmentOptions.Center, "STANDBY");
        var (x, y, w, h) = RoverDashboard.StandbyPower;
        var ring = New<Image>(p, "PowerRing", x, y, w, h);
        ring.sprite = Disc(); ring.color = Accent; ring.raycastTarget = false;
        var hole = New<Image>(ring.rectTransform, "PowerHole", 6, 6, w - 12, h - 12);
        hole.sprite = Disc(); hole.color = Bg; hole.raycastTarget = false;
        var icon = Arc(hole.rectTransform, (w - 12) / 2f, 104f, 92f, 0.8f, Accent);   // the power symbol: an open ring
        icon.rectTransform.localEulerAngles = new Vector3(0f, 0f, 144f);              // with its gap at the top
        PanelAt(hole.rectTransform, (w - 12) / 2f - 5f, 48f, 10, 56, Accent);         // and the stroke through the gap
        Label(hole.rectTransform, 0, 176, w - 12, 50, 34, Accent, TextAlignmentOptions.Center, "POWER");
        _standbyText = Label(p, 0, 500, W, 40, 26, Dim, TextAlignmentOptions.Center, "");
    }

    // ---- refresh ------------------------------------------------------------------------------------------------

    private void Update()
    {
        if (!_rover || !_canvas || _failed) return;
        try
        {
            var s = _rover.DashInputs();
            bool slow = Time.time >= _nextSlow;
            if (slow) { _nextSlow = Time.time + 1f; SampleDraw(s); }
            s.DrawW = _drawW;
            var d = RoverDashboard.Build(s);
            if (d.Page != _page || s.ThrustersFitted != _thrusters)
            {
                bool entering = d.Page == RoverPage.Driving && _page != RoverPage.Driving;
                ShowPage(d.Page, s.ThrustersFitted);
                if (entering) { slow = true; _nextFast = 0f; }                 // a new driving page is filled in its first frame
            }
            if (d.Page == RoverPage.Dark) return;
            if (d.Page == RoverPage.Standby) { Set(_standbyText, d.StandbyText); return; }
            ShowButtons(d);                                                     // every frame
            if (Time.time >= _nextFast) { _nextFast = Time.time + 0.2f; ShowMotion(d); }
            if (slow) ShowSystems(d);
        }
        catch (Exception e)
        {
            _failed = true;                                                     // once: a broken screen must not spam
            Debug.LogError($"[RoverCargo] rover dash screen stopped (the tap targets stay on the driving layout): {e}");
            if (_canvas) _canvas.enabled = false;
            PlaceTargets(RoverPage.Driving, true);
        }
    }

    /// <summary>Power use in the game's W (joules per atmos tick) from the batteries' change over the last second (the
    /// rover does not charge); null while it cannot be told (starting, or the power cells changed).</summary>
    private void SampleDraw(RoverInputs s)
    {
        if (s.Remote) { _drawW = null; return; }                            // a client sees the charge in 1 % steps only
        float tick = AtmosphericsManager.Instance ? AtmosphericsManager.Instance.TickSpeedSeconds : 0.5f;
        _drawW = !double.IsNaN(_lastJ) && s.Cells == _lastCells && Time.time > _lastT && tick > 0
            ? Math.Max(0, (_lastJ - s.BatteryJ) / ((Time.time - _lastT) / tick)) : null;
        _lastJ = s.BatteryJ; _lastT = Time.time; _lastCells = s.Cells;
    }

    private void ShowPage(RoverPage page, bool thrusters)
    {
        _page = page; _thrusters = thrusters;
        _canvas.enabled = page != RoverPage.Dark;
        _drive.gameObject.SetActive(page == RoverPage.Driving);
        _standby.gameObject.SetActive(page == RoverPage.Standby);
        PlaceTargets(page, thrusters);
    }

    /// <summary>Each control's trigger onto its button for this page (RoverDashboard.TapRect), or off without one.</summary>
    private void PlaceTargets(RoverPage page, bool thrusters)
    {
        foreach (var (_, action) in RoverDashboard.Controls)
        {
            var it = _rover.Interactables.Find(i => i.Action.ToString() == action);
            if (!(it?.Collider is BoxCollider bc)) continue;
            var rect = RoverDashboard.TapRect(action, page, thrusters);
            bc.enabled = rect != null;
            if (rect is not { } r) continue;
            var (c, z) = RoverDashboard.TapBox(r);
            bc.center = new Vector3(c.X, c.Y, c.Z);
            bc.size = new Vector3(z.X, z.Y, z.Z);
            it.Bounds = new Bounds(bc.center, bc.size);
        }
    }

    private void ShowButtons(RoverDash d)
    {
        for (int i = 0; i < _buttons.Length && i < d.Buttons.Length; i++)
        {
            var b = d.Buttons[i];
            var (bg, name, state) = _buttons[i];
            bg.color = b.State == ScreenButtonState.On ? Green : b.State == ScreenButtonState.Off ? Off : Panel;
            name.color = state.color = b.State == ScreenButtonState.On ? Bg : b.State == ScreenButtonState.Off ? Fg : Off;
            state.fontSizeMax = b.State == ScreenButtonState.NotFitted ? 20f : 28f;
            Set(state, b.State == ScreenButtonState.On ? "ON" : b.State == ScreenButtonState.Off ? "OFF" : "not fitted");
        }
    }

    private void ShowMotion(RoverDash d)
    {
        Set(_badge, d.Badge);
        _badgeBg.color = d.BadgeLevel == StatusLevel.Off ? Off : Green;
        _badge.color = d.BadgeLevel == StatusLevel.Off ? Fg : Bg;
        Set(_speedValue, d.Speed.ValueText);
        _speedArc.fillAmount = 0.75f * (float)(d.Speed.Max > d.Speed.Min ? (d.Speed.Value - d.Speed.Min) / (d.Speed.Max - d.Speed.Min) : 0);
        _pitchPivot.localEulerAngles = new Vector3(0f, 0f, Mathf.Clamp((float)d.PitchDeg, -45f, 45f));    // nose (right) up: anticlockwise
        _rollPivot.localEulerAngles = new Vector3(0f, 0f, -Mathf.Clamp((float)d.RollDeg, -45f, 45f));    // right side down: clockwise
        Tint(_pitchParts, d.PitchLevel);
        Tint(_rollParts, d.RollLevel);
        Set(_pitchValue, RoverDashboard.Degrees(d.PitchText, _degree));
        _pitchValue.color = d.PitchLevel == StatusLevel.Normal ? Fg : LevelColor(d.PitchLevel);
        Set(_pitchWords, d.PitchWords);
        Set(_rollValue, RoverDashboard.Degrees(d.RollText, _degree));
        _rollValue.color = d.RollLevel == StatusLevel.Normal ? Fg : LevelColor(d.RollLevel);
        Set(_rollWords, d.RollWords);
    }

    private static void Tint(List<Image> parts, StatusLevel level)
    {
        foreach (var img in parts) img.color = img.name == "Wheel" ? Fg : level == StatusLevel.Normal ? Dim : LevelColor(level);
    }

    private void ShowSystems(RoverDash d)
    {
        Set(_clock, DateTime.Now.ToString("HH:mm"));
        _towBg.gameObject.SetActive(d.TowTag != null);
        if (d.TowTag != null) Set(_tow, d.TowTag);
        Set(_speedSub, d.Speed.Sub);
        Set(_battValue, d.Battery.ValueText);
        Set(_battCells, d.BatteryCells);
        Set(_battSub, d.Battery.Sub);
        _battArc.fillAmount = 0.75f * (float)(d.Battery.Value / 100.0);
        _battArc.color = LevelColor(d.Battery.Level);
        int n = d.Cards.Length + 1;                                             // the data cards, then WARNINGS
        float cw = (W - 2 * M - (n - 1) * G) / n;
        for (int i = 0; i < _cards.Count; i++)
        {
            var (box, title, lines, fill, fillImg) = _cards[i];
            box.gameObject.SetActive(i < n);
            if (i >= n) continue;
            var r = box.rectTransform;
            r.anchoredPosition = new Vector2(M + i * (cw + G), -CardsY);
            r.sizeDelta = new Vector2(cw, 146f);
            title.rectTransform.sizeDelta = new Vector2(cw - 28f, 26f);
            lines.rectTransform.sizeDelta = new Vector2(cw - 28f, 70f);
            var track = (RectTransform)fill.parent;
            track.sizeDelta = new Vector2(cw - 28f, 14f);
            bool warnings = i == n - 1;
            track.gameObject.SetActive(!warnings);
            if (warnings) { Set(title, "WARNINGS"); Set(lines, WarningText(d)); continue; }
            var c = d.Cards[i];
            Set(title, c.Title);
            var l0 = c.LineLevels != null && c.LineLevels.Length > 0 ? c.LineLevels[0] : StatusLevel.Normal;
            string first = l0 == StatusLevel.Normal ? c.Lines[0] : $"<color={Html(LevelColor(l0))}>{c.Lines[0]}</color>";
            Set(lines, c.Lines.Length > 1 && c.Lines[1] != "" ? $"{first}\n<size=65%><color={Html(Dim)}>{c.Lines[1]}</color></size>" : first);
            var bar = c.Bars.Length > 0 ? c.Bars[0] : default;
            fill.anchorMax = new Vector2((float)bar.Ratio, 1f);
            fillImg.color = LevelColor(bar.Level);
        }
    }

    private string WarningText(RoverDash d)
    {
        if (d.Warnings.Length == 0) return $"<size=75%><color={Html(Green)}>All systems normal</color></size>";
        var lines = new List<string>();
        for (int i = 0; i < d.Warnings.Length && i < 3; i++)
            lines.Add($"<size=75%><color={Html(LevelColor(d.Warnings[i].Level))}>{RoverDashboard.Degrees(d.Warnings[i].Text, _degree)}</color></size>");
        return string.Join("\n", lines);
    }
}
