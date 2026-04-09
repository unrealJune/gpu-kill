using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using System.Windows.Forms;

namespace GPUKill;

public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly GpuController _controller = new();
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _autoDisableOnUnplugItem;
    private readonly ToolStripMenuItem _startWithWindowsItem;

    private string? _instanceId;
    private GpuState _lastState = GpuState.Unknown;
    private bool _busy;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeyName = "GPUKill";
    private const string SettingsKeyPath = @"Software\GPUKill";

    public TrayApp()
    {
        _menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("dGPU: …") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Disable dGPU", null, OnToggleClicked);
        _restartItem = new ToolStripMenuItem("Restart dGPU", null, OnRestartClicked);
        _autoDisableOnUnplugItem = new ToolStripMenuItem("Auto-disable on battery", null, OnAutoDisableToggled)
        {
            CheckOnClick = true,
        };
        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows", null, OnStartupToggled)
        {
            CheckOnClick = true,
        };
        var openNvcpItem = new ToolStripMenuItem("Open NVIDIA Control Panel", null, OnOpenNvcp);
        var refreshItem = new ToolStripMenuItem("Refresh", null, (_, _) => Refresh());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Application.Exit());

        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(_restartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_autoDisableOnUnplugItem);
        _menu.Items.Add(_startWithWindowsItem);
        _menu.Items.Add(openNvcpItem);
        _menu.Items.Add(refreshItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
            Text = "GPUKill",
            Icon = MakeStateIcon(GpuState.Unknown),
        };
        _icon.MouseClick += OnIconClicked;

        _pollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _pollTimer.Tick += (_, _) => Refresh();
        _pollTimer.Start();

        _autoDisableOnUnplugItem.Checked = LoadBoolSetting("AutoDisableOnBattery", false);
        _startWithWindowsItem.Checked = IsStartupEnabled();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _instanceId = _controller.FindNvidiaInstanceId();
        Refresh();
    }

    private void Refresh()
    {
        if (_busy) return;

        if (_instanceId == null)
        {
            _instanceId = _controller.FindNvidiaInstanceId();
        }

        GpuState state;
        if (_instanceId == null)
        {
            state = GpuState.NotFound;
        }
        else
        {
            state = _controller.GetState(_instanceId);
            // If the device disappeared from PnP after disable, the previous query may
            // return NotFound. Treat that as still-disabled so the UI doesn't flicker.
            if (state == GpuState.NotFound && _lastState == GpuState.Disabled)
            {
                state = GpuState.Disabled;
            }
        }

        if (state != _lastState)
        {
            _lastState = state;
            ApplyStateToUi(state);
        }
    }

    private void ApplyStateToUi(GpuState state)
    {
        _icon.Icon?.Dispose();
        _icon.Icon = MakeStateIcon(state);

        switch (state)
        {
            case GpuState.Enabled:
                _statusItem.Text = "dGPU: Enabled";
                _toggleItem.Text = "Disable dGPU";
                _toggleItem.Enabled = true;
                _restartItem.Enabled = true;
                _icon.Text = "GPUKill — dGPU enabled";
                break;
            case GpuState.Disabled:
                _statusItem.Text = "dGPU: Disabled";
                _toggleItem.Text = "Enable dGPU";
                _toggleItem.Enabled = true;
                _restartItem.Enabled = false;
                _icon.Text = "GPUKill — dGPU disabled";
                break;
            case GpuState.NotFound:
                _statusItem.Text = "dGPU: Not found";
                _toggleItem.Enabled = false;
                _restartItem.Enabled = false;
                _icon.Text = "GPUKill — no NVIDIA GPU found";
                break;
            default:
                _statusItem.Text = "dGPU: Unknown";
                _toggleItem.Enabled = false;
                _restartItem.Enabled = false;
                _icon.Text = "GPUKill";
                break;
        }
    }

    private async void OnIconClicked(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            await ToggleAsync();
        }
    }

    private async void OnToggleClicked(object? sender, EventArgs e) => await ToggleAsync();

    private async void OnRestartClicked(object? sender, EventArgs e)
    {
        if (_instanceId == null || _busy) return;
        _busy = true;
        try
        {
            var ok = await _controller.RestartAsync(_instanceId);
            ShowBalloon(ok ? "dGPU restarted" : "Restart failed", ok ? ToolTipIcon.Info : ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private async Task ToggleAsync()
    {
        if (_instanceId == null || _busy) return;
        _busy = true;
        try
        {
            bool ok;
            string message;
            if (_lastState == GpuState.Enabled)
            {
                ok = await _controller.DisableAsync(_instanceId);
                message = ok ? "dGPU disabled" : "Disable failed";
            }
            else if (_lastState == GpuState.Disabled)
            {
                ok = await _controller.EnableAsync(_instanceId);
                message = ok ? "dGPU enabled" : "Enable failed";
            }
            else
            {
                return;
            }
            ShowBalloon(message, ok ? ToolTipIcon.Info : ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private void OnOpenNvcp(object? sender, EventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvcplui.exe",
                UseShellExecute = true,
            });
        }
        catch
        {
            ShowBalloon("NVIDIA Control Panel not available", ToolTipIcon.Warning);
        }
    }

    private async void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (!_autoDisableOnUnplugItem.Checked) return;
        if (e.Mode != PowerModes.StatusChange) return;

        // Battery = unplugged. Auto-disable, but only if currently enabled.
        var status = SystemInformation.PowerStatus;
        if (status.PowerLineStatus == PowerLineStatus.Offline && _lastState == GpuState.Enabled)
        {
            await ToggleAsync();
        }
    }

    private void OnAutoDisableToggled(object? sender, EventArgs e) =>
        SaveBoolSetting("AutoDisableOnBattery", _autoDisableOnUnplugItem.Checked);

    private void OnStartupToggled(object? sender, EventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;
            if (_startWithWindowsItem.Checked)
            {
                var exe = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(RunKeyName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunKeyName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Ignore — toggle still reflects intent in current session.
        }
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunKeyName) != null;
        }
        catch { return false; }
    }

    private static bool LoadBoolSetting(string name, bool defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
            var v = key?.GetValue(name);
            return v is int i ? i != 0 : defaultValue;
        }
        catch { return defaultValue; }
    }

    private static void SaveBoolSetting(string name, bool value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
            key?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { }
    }

    private void ShowBalloon(string message, ToolTipIcon icon)
    {
        _icon.BalloonTipTitle = "GPUKill";
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(2000);
    }

    /// <summary>
    /// Draws a 32×32 colored circle and converts it to a Windows icon handle.
    /// Avoids shipping ICO assets in the repo.
    /// </summary>
    private static Icon MakeStateIcon(GpuState state)
    {
        var color = state switch
        {
            GpuState.Enabled => Color.FromArgb(76, 217, 100),   // green
            GpuState.Disabled => Color.FromArgb(220, 60, 60),   // red
            GpuState.NotFound => Color.FromArgb(140, 140, 140), // gray
            _ => Color.FromArgb(200, 200, 60),                  // yellow
        };

        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, size - 4, size - 4);
            using var pen = new Pen(Color.FromArgb(40, 0, 0, 0), 2);
            g.DrawEllipse(pen, 2, 2, size - 4, size - 4);
        }

        IntPtr hIcon = bmp.GetHicon();
        // Clone so we can destroy the bitmap-owned handle without leaking.
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
        _menu.Dispose();
    }
}
