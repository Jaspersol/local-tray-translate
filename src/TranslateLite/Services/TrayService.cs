using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;

namespace TranslateLite.Services;

public sealed class TrayService : IDisposable
{
    private readonly Action _showAction;
    private readonly Func<Task> _selectionAction;
    private readonly Func<Task> _screenOcrAction;
    private readonly Action _settingsAction;
    private readonly Action _exitAction;
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _menu;
    private ToolStripItem? _selectionItem;
    private ToolStripItem? _screenOcrItem;

    public TrayService(Action showAction, Func<Task> selectionAction, Func<Task> screenOcrAction, Action settingsAction, Action exitAction)
    {
        _showAction = showAction;
        _selectionAction = selectionAction;
        _screenOcrAction = screenOcrAction;
        _settingsAction = settingsAction;
        _exitAction = exitAction;
    }

    public void Initialize(string selectionHotkeyText, string screenOcrHotkeyText)
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("\u6253\u5f00", null, (_, _) => _showAction());
        _selectionItem = _menu.Items.Add($"\u5212\u8bcd\u7ffb\u8bd1  {selectionHotkeyText}", null, async (_, _) => await _selectionAction());
        _screenOcrItem = _menu.Items.Add($"\u622a\u56fe\u7ffb\u8bd1  {screenOcrHotkeyText}", null, async (_, _) => await _screenOcrAction());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("\u8bbe\u7f6e", null, (_, _) => _settingsAction());
        _menu.Items.Add("\u9000\u51fa", null, (_, _) => _exitAction());

        var icon = LoadTrayIcon();

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "TranslateLite",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => _showAction();
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/app.ico");
            var streamInfo = System.Windows.Application.GetResourceStream(uri);
            if (streamInfo is not null)
            {
                using var stream = streamInfo.Stream;
                return new Icon(stream);
            }
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    public void UpdateHotkeyTexts(string selectionHotkeyText, string screenOcrHotkeyText)
    {
        if (_selectionItem is not null)
        {
            _selectionItem.Text = $"\u5212\u8bcd\u7ffb\u8bd1  {selectionHotkeyText}";
        }

        if (_screenOcrItem is not null)
        {
            _screenOcrItem.Text = $"\u622a\u56fe\u7ffb\u8bd1  {screenOcrHotkeyText}";
        }
    }

    public void Dispose()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }
}
