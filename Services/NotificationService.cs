using System.Drawing;
using System.Windows.Forms;

namespace UpdateHub.Services;

/// <summary>
/// Native Windows notifications via a tray icon. A shell notification is used rather than
/// the WinRT toast API because toasts require a registered AppUserModelID backed by a Start
/// Menu shortcut, which a portable single-file executable has no business installing.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private NotifyIcon? _icon;
    private readonly LogService _log;
    private readonly object _gate = new();

    public NotificationService(LogService log) => _log = log;

    public bool Enabled { get; set; } = true;

    private NotifyIcon EnsureIcon()
    {
        if (_icon is not null) return _icon;
        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "UpdateHub",
        };
        return _icon;
    }

    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch (Exception) { }
        return SystemIcons.Application;
    }

    public void Show(string title, string message)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            try
            {
                var icon = EnsureIcon();
                icon.BalloonTipTitle = title;
                icon.BalloonTipText = message;
                icon.BalloonTipIcon = ToolTipIcon.Info;
                icon.ShowBalloonTip(8000);
            }
            catch (Exception ex)
            {
                _log.Warn($"Notification failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_icon is null) return;
            try { _icon.Visible = false; _icon.Dispose(); } catch { }
            _icon = null;
        }
    }
}
