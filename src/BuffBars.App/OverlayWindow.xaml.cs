using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BuffBars.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace BuffBars.App;

public sealed class RowView
{
    public string Label { get; init; } = "";
    public string TimeText { get; init; } = "";
    public Brush LabelBrush { get; init; } = OverlayWindow.InkBrushShared;
    public Brush TimeBrush { get; init; } = Brushes.White;
    public Brush BarBrush { get; init; } = Brushes.Gold;
    public double BarWidth { get; init; }
}

public sealed class PanelView
{
    public string Display { get; init; } = "";
    public List<RowView> Rows { get; init; } = new();
}

public partial class OverlayWindow : Window
{
    private static readonly Brush GoldBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC9, 0xA8, 0x6A)));
    private static readonly Brush AmberBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xCA, 0x8A, 0x04)));
    private static readonly Brush RedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC5, 0x30, 0x35)));
    private static readonly Brush VioletBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x93, 0x33, 0xEA)));
    private static readonly Brush CyanBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0E, 0xA5, 0xC4)));
    public static readonly Brush EmeraldBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69)));
    private static readonly Brush SlateBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6)));
    private static readonly Brush InkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC7, 0xCB, 0xD6)));
    public static readonly Brush InkBrushShared = InkBrush;
    private static readonly Brush RedBrightBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private readonly bool _editMode;
    private readonly OverlayRect _rect;
    private readonly Action? _onSave;
    private bool _clickThroughApplied;

    public OverlayWindow(string title, OverlayRect rect, double opacity, bool editMode, Action? onSave = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        _rect = rect;
        _editMode = editMode;
        _onSave = onSave;
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;
        Opacity = opacity;

        if (editMode)
        {
            EditButtons.Visibility = Visibility.Visible;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            RootBorder.BorderBrush = GoldBrush;
            RootBorder.BorderThickness = new Thickness(2);
            MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        }
        else
        {
            IsHitTestVisible = false;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!_editMode && !_clickThroughApplied)
        {
            Win32.MakeClickThrough(this);
            _clickThroughApplied = true;
        }
    }

    public void ReassertTopmost() => Win32.AssertTopmost(this);

    public sealed class QuickBuffOptions
    {
        public bool Enabled { get; init; } = true;
        public int MinDurationSeconds { get; init; } = 600;
        public int CooldownSeconds { get; init; } = 300;
    }

    /// <summary>
    /// Rebuild the displayed panels from a tracker snapshot slice. beneficialFilter: null = all,
    /// false = detrimental only (the DoT panel), true = beneficial only (enemy-buff intel panel).
    /// </summary>
    public void Render(IReadOnlyList<ActorView> actors, DateTime now, int minDurationSeconds, bool isDotPanel,
        bool hideBardSongs = false, QuickBuffOptions? quickBuffs = null, bool? beneficialFilter = null,
        Brush? barBaseOverride = null)
    {
        var panels = new List<PanelView>();
        foreach (var actor in actors)
        {
            var rows = new List<(int Rank, double Remaining, RowView Row)>();
            var package = new List<double>();      // remaining seconds of grouped long buffs
            foreach (var t in actor.Timers)
            {
                if (t.Spell.BaseDurationSeconds < minDurationSeconds) continue;
                if (hideBardSongs && t.Spell.IsBardSong) continue;
                if (beneficialFilter is { } bf && t.Spell.Beneficial != bf) continue;
                var remaining = t.RemainingSeconds(now);
                if (remaining <= 0) continue;

                // permanent buffs never expire - they stay individual rows, not package members
                if (quickBuffs is { Enabled: true } qb && !isDotPanel && !t.Spell.IsVitalBuff &&
                    t.Spell.Beneficial && !double.IsInfinity(remaining) &&
                    t.Spell.BaseDurationSeconds >= qb.MinDurationSeconds)
                {
                    package.Add(remaining);
                    continue;
                }

                var total = Math.Max(1, (t.End - t.Start)?.TotalSeconds ?? 1);
                var frac = double.IsInfinity(remaining) ? 1.0 : Math.Clamp(remaining / total, 0, 1);
                var label = isDotPanel && t.CasterDisplay.Length > 0 && t.CasterDisplay != t.TargetDisplay
                    ? $"{t.Spell.Name}  ({t.CasterDisplay})"
                    : t.Spell.Name;
                var vital = !isDotPanel && t.Spell.IsVitalBuff;
                // a DEBUFF on a party member is the most actionable row there is - full alarm
                var alarm = !isDotPanel && !t.Spell.Beneficial;
                var barBase = barBaseOverride ?? (isDotPanel ? VioletBrush : (vital ? CyanBrush : GoldBrush));
                var (timeBrush, barBrush) = remaining switch
                {
                    < 20 => (RedBrush, RedBrush),
                    < 60 => (AmberBrush, AmberBrush),
                    _ => (vital ? CyanBrush : InkBrush, barBase),
                };
                if (alarm) (timeBrush, barBrush) = (RedBrightBrush, RedBrightBrush);
                rows.Add((alarm ? 2 : (vital ? 1 : 0), remaining, new RowView
                {
                    Label = alarm ? $"⚠ {label}" : label,
                    LabelBrush = alarm ? RedBrightBrush : InkBrushShared,
                    TimeText = FormatTime(remaining),
                    TimeBrush = double.IsInfinity(remaining) ? SlateBrush : timeBrush,
                    BarBrush = barBrush,
                    BarWidth = Math.Max(2, frac * Math.Max(40, Width - 30)),
                }));
            }
            // rank 2 = debuff alarm, 1 = vital, 0 = normal; soonest-expiring first within each
            var ordered = rows.OrderByDescending(r => r.Rank).ThenBy(r => r.Remaining)
                              .Select(r => r.Row).ToList();

            if (package.Count > 0)
            {
                var minRemaining = package.Min();
                var label = $"Quick Buffs ({package.Count})";
                if (actor.QuickBuffAt is { } qbAt && quickBuffs is { } qbo)
                {
                    var cd = qbo.CooldownSeconds - (now - qbAt).TotalSeconds;
                    if (cd > 0) label += $"  · cd {FormatTime(cd)}";
                }
                // amber when within one cooldown of losing a buff, red when it's imminent
                var (tb, bb) = minRemaining switch
                {
                    < 90 => (RedBrush, RedBrush),
                    < 300 => (AmberBrush, AmberBrush),
                    _ => (InkBrush, GoldBrush),
                };
                var totalRef = Math.Max(1, quickBuffs?.MinDurationSeconds ?? 600);
                var qfrac = double.IsInfinity(minRemaining) ? 1.0 : Math.Clamp(minRemaining / totalRef, 0, 1);
                ordered.Add(new RowView
                {
                    Label = label,
                    TimeText = FormatTime(minRemaining),
                    TimeBrush = double.IsInfinity(minRemaining) ? SlateBrush : tb,
                    BarBrush = bb,
                    BarWidth = Math.Max(2, qfrac * Math.Max(40, Width - 30)),
                });
            }

            if (ordered.Count > 0)
                panels.Add(new PanelView { Display = actor.Display, Rows = ordered });
        }
        Panels.ItemsSource = panels;
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsInfinity(seconds)) return "—";
        var s = (int)seconds;
        if (s >= 3600) return $"{s / 3600}h{(s % 3600) / 60:00}m";
        if (s >= 60) return $"{s / 60}:{s % 60:00}";
        return $"{s}s";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _rect.Left = Left;
        _rect.Top = Top;
        _rect.Width = Width;
        _rect.Height = Height;
        _onSave?.Invoke();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _onSave?.Invoke();
}
