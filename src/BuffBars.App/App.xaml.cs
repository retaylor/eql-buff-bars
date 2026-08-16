using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using BuffBars.Core;
using WinForms = System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace BuffBars.App;

public partial class App : Application
{
    private AppConfig _config = null!;
    private Tracker _tracker = null!;
    private LogWatcher _watcher = null!;
    private WinForms.NotifyIcon? _tray;
    private OverlayWindow? _buffWindow;
    private OverlayWindow? _dotWindow;
    private OverlayWindow? _enemyBuffWindow;
    private DispatcherTimer? _renderTimer;
    private int _topmostCounter;
    private bool _editMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _config = AppConfig.Load();

        SpellDb db;
        try
        {
            db = SpellDb.LoadFromGameDir(_config.GameDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not load spell data from:\n{_config.GameDir}\n\n{ex.Message}",
                "EQL Buff Bars", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _tracker = new Tracker(db)
        {
            ExtendBeneficialPercent = _config.ExtendBeneficialPercent,
            DurationOverridesSeconds = new Dictionary<string, int>(
                _config.DurationOverridesSeconds, StringComparer.OrdinalIgnoreCase),
        };
        var parser = new LineParser(db);
        var channel = Channel.CreateBounded<TailedLine>(new BoundedChannelOptions(100_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });

        // consumer: lines -> events -> tracker (one LogLineReader per character, they're stateful)
        _ = Task.Run(async () =>
        {
            var readers = new Dictionary<string, LogLineReader>(StringComparer.OrdinalIgnoreCase);
            var cutoff = DateTime.Now.AddMinutes(-_config.BackfillMinutes);
            await foreach (var item in channel.Reader.ReadAllAsync())
            {
                if (!readers.TryGetValue(item.Character, out var reader))
                    readers[item.Character] = reader = new LogLineReader();
                if (!reader.TryParse(item.Line, out var ts, out var action)) continue;
                if (ts < cutoff) continue;
                var evt = parser.Parse(action);
                if (evt is null) continue;
                _tracker.OnEvent(evt with { Ts = ts, Observer = item.Character });
            }
        });

        // ~64KB of backfill bytes per minute is generous for typical logging rates
        var backfillBytes = (long)_config.BackfillMinutes * 64 * 1024;
        _watcher = new LogWatcher(_config.LogsDir, channel.Writer, backfillBytes);
        _watcher.Start();

        OpenOverlays(editMode: false);
        SetupTray();

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _renderTimer.Tick += (_, _) => RenderTick();
        _renderTimer.Start();
    }

    private void RenderTick()
    {
        var now = DateTime.Now;
        var snap = _tracker.GetSnapshot(now);
        var qb = new OverlayWindow.QuickBuffOptions
        {
            Enabled = _config.GroupQuickBuffs,
            MinDurationSeconds = _config.QuickBuffMinDurationSeconds,
            CooldownSeconds = _config.QuickBuffCooldownSeconds,
        };
        _buffWindow?.Render(snap.Characters, now, _config.MinDurationSeconds, isDotPanel: false,
            hideBardSongs: !_config.ShowBardSongs, quickBuffs: qb);
        _dotWindow?.Render(snap.Mobs, now, _config.MinDurationSeconds, isDotPanel: true,
            beneficialFilter: false);
        _enemyBuffWindow?.Render(snap.Mobs, now, _config.MinDurationSeconds, isDotPanel: true,
            beneficialFilter: true, barBaseOverride: OverlayWindow.EmeraldBrush);

        if (!_editMode && ++_topmostCounter >= 8)      // every ~2s
        {
            _topmostCounter = 0;
            _buffWindow?.ReassertTopmost();
            _dotWindow?.ReassertTopmost();
        }
    }

    private void OpenOverlays(bool editMode)
    {
        _editMode = editMode;
        _buffWindow?.Close();
        _dotWindow?.Close();
        _enemyBuffWindow?.Close();

        _buffWindow = new OverlayWindow("Party Buffs", _config.BuffWindow, _config.Opacity, editMode, OnEditSaved);
        _buffWindow.Show();
        _dotWindow = null;
        if (_config.ShowDotPanel)
        {
            _dotWindow = new OverlayWindow("DoTs / Debuffs", _config.DotWindow, _config.Opacity, editMode, OnEditSaved);
            _dotWindow.Show();
        }
        _enemyBuffWindow = null;
        if (_config.ShowEnemyBuffPanel)
        {
            _enemyBuffWindow = new OverlayWindow("Enemy Buffs", _config.EnemyBuffWindow, _config.Opacity, editMode, OnEditSaved);
            _enemyBuffWindow.Show();
        }
    }

    private void OnEditSaved()
    {
        _config.Save();
        OpenOverlays(editMode: false);
        if (_tray?.ContextMenuStrip?.Items[0] is WinForms.ToolStripMenuItem m) m.Checked = false;
    }

    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "EQL Buff Bars",
        };
        var menu = new WinForms.ContextMenuStrip();
        var edit = new WinForms.ToolStripMenuItem("Edit layout (drag/resize)") { CheckOnClick = true };
        edit.CheckedChanged += (_, _) => OpenOverlays(edit.Checked);
        var dots = new WinForms.ToolStripMenuItem("Show DoT panel") { Checked = _config.ShowDotPanel, CheckOnClick = true };
        dots.CheckedChanged += (_, _) => { _config.ShowDotPanel = dots.Checked; _config.Save(); OpenOverlays(_editMode); };
        var songs = new WinForms.ToolStripMenuItem("Show bard songs") { Checked = _config.ShowBardSongs, CheckOnClick = true };
        songs.CheckedChanged += (_, _) => { _config.ShowBardSongs = songs.Checked; _config.Save(); };
        var group = new WinForms.ToolStripMenuItem("Group Quick Buff package") { Checked = _config.GroupQuickBuffs, CheckOnClick = true };
        group.CheckedChanged += (_, _) => { _config.GroupQuickBuffs = group.Checked; _config.Save(); };
        var enemy = new WinForms.ToolStripMenuItem("Show enemy buffs panel") { Checked = _config.ShowEnemyBuffPanel, CheckOnClick = true };
        enemy.CheckedChanged += (_, _) => { _config.ShowEnemyBuffPanel = enemy.Checked; _config.Save(); OpenOverlays(_editMode); };
        var exit = new WinForms.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Shutdown();
        menu.Items.Add(edit);
        menu.Items.Add(dots);
        menu.Items.Add(songs);
        menu.Items.Add(group);
        menu.Items.Add(enemy);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exit);
        _tray.ContextMenuStrip = menu;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _watcher?.DisposeAsync().AsTask().Wait(2000);
        base.OnExit(e);
    }
}
