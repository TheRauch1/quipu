using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Quipu.Config;
using Quipu.Services;
using Microsoft.Win32;

namespace Quipu.Tray;

/// <summary>System tray icon: open the dashboard, start/stop models, quick folders, start with Windows.</summary>
public sealed class TrayContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly NotifyIcon _icon;
    private readonly IServiceProvider _services;
    private readonly Action _shutdown;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _sleepEvents = new();

    public TrayContext(IServiceProvider services, Action shutdown)
    {
        _services = services;
        _shutdown = shutdown;
        _icon = new NotifyIcon
        {
            Icon = DrawIcon(),
            Text = "Quipu",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _icon.DoubleClick += (_, _) => OpenDashboard();
        BuildMenu();

        // sleep events arrive on llama-server's stdout thread; show them from the UI timer
        Instances.SleepChanged += (inst, asleep) =>
        {
            if (asleep) _sleepEvents.Enqueue($"{inst.Name} was idle for {inst.IdleUnloadMinutes ?? 0} min and was unloaded from VRAM. The next request reloads it.");
        };
        Instances.ModelEvicted += (victim, started) =>
            _sleepEvents.Enqueue($"Stopped {victim.Name} to start {started} (only {Config.Current.Server.MaxActiveModels} model(s) at a time).");

        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += (_, _) => UpdateTooltip();
        _timer.Start();
    }

    private ConfigStore Config => _services.GetRequiredService<ConfigStore>();
    private InstanceManager Instances => _services.GetRequiredService<InstanceManager>();

    private void BuildMenu()
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem("Open dashboard", null, (_, _) => OpenDashboard()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());

        var modelsItem = new ToolStripMenuItem("Models");
        foreach (var name in Config.Current.Models.Keys.OrderBy(k => k))
        {
            var inst = Instances.Get(name);
            var running = inst is { State: InstanceState.Starting or InstanceState.Ready };
            var label = running ? $"{name}  —  {(inst!.Asleep ? "Sleeping (unloaded)" : inst.State.ToString())} :{inst.Port}" : name;
            var item = new ToolStripMenuItem(label) { Checked = running };
            item.Click += async (_, _) =>
            {
                try
                {
                    if (running) await Instances.StopAsync(name);
                    else
                    {
                        await Instances.StartAsync(name);
                        _icon.ShowBalloonTip(2500, "Quipu", $"Starting {name}…", ToolTipIcon.Info);
                    }
                }
                catch (Exception ex)
                {
                    _icon.ShowBalloonTip(5000, $"Could not start {name}", ex.Message, ToolTipIcon.Error);
                }
            };
            modelsItem.DropDownItems.Add(item);
        }
        if (modelsItem.DropDownItems.Count == 0) modelsItem.DropDownItems.Add(new ToolStripMenuItem("(no models configured)") { Enabled = false });
        menu.Items.Add(modelsItem);
        menu.Items.Add("Stop all models", null, async (_, _) => await Instances.StopAllAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Install latest llama.cpp", null, (_, _) =>
        {
            _services.GetRequiredService<BuildManager>().Install(null, null, false, Instances.BuildDirsInUse);
            _icon.ShowBalloonTip(2500, "Quipu", "Installing the newest llama.cpp release…", ToolTipIcon.Info);
        });
        menu.Items.Add("Edit quipu.yaml", null, (_, _) => Shell(Config.FilePath));
        menu.Items.Add("Open models folder", null, (_, _) => Shell(Config.Current.Paths.Models));
        menu.Items.Add("API docs", null, (_, _) => Program.OpenBrowser(Config.Current.Server.Listen.TrimEnd('/') + "/docs"));
        menu.Items.Add(new ToolStripSeparator());
        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = IsAutostart() };
        autostart.Click += (_, _) => SetAutostart(!IsAutostart());
        menu.Items.Add(autostart);
        menu.Items.Add("Quit", null, (_, _) => Quit());
    }

    private void UpdateTooltip()
    {
        while (_sleepEvents.TryDequeue(out var msg))
            _icon.ShowBalloonTip(3000, "Quipu", msg, ToolTipIcon.Info);
        var all = Instances.All;
        var running = all.Count(i => i.State is InstanceState.Ready or InstanceState.Starting);
        var asleep = all.Count(i => i.State is InstanceState.Ready && i.Asleep);
        var text = running == 0 ? "Quipu — no models running"
            : $"Quipu — {running} model(s) running" + (asleep > 0 ? $", {asleep} unloaded" : "");
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void OpenDashboard() => Program.OpenBrowser(Config.Current.Server.Listen);

    private static void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Quipu"); }
    }

    private static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("Quipu") is string;
    }

    private void SetAutostart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable) key.SetValue("Quipu", $"\"{Environment.ProcessPath}\" --no-browser --config \"{Config.FilePath}\"");
        else key.DeleteValue("Quipu", false);
    }

    private void Quit()
    {
        _timer.Stop();
        _icon.Visible = false;
        try { _shutdown(); } catch { }
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Draws the tray icon at runtime (no binary assets needed).</summary>
    private static Icon DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var path = new GraphicsPath();
            const int r = 9;
            path.AddArc(0, 0, r * 2, r * 2, 180, 90);
            path.AddArc(31 - r * 2, 0, r * 2, r * 2, 270, 90);
            path.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(0, 31 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(124, 92, 255), Color.FromArgb(255, 122, 89), 45f);
            g.FillPath(brush, path);
            using var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString("Q", font);
            g.DrawString("Q", font, Brushes.White, (32 - size.Width) / 2 + 1, (32 - size.Height) / 2);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
