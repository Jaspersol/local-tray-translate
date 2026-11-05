using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;

namespace TranslateLite.Services;

/// <summary>
/// 托盘图标与右键菜单：承载“打开主窗口 / 划词翻译 / 截图翻译 / 设置 / 退出”五个入口。
/// 菜单文案里的快捷键文本由调用方格式化后传入，切换快捷键时调用 <see cref="UpdateHotkeyTexts"/> 刷新。
/// </summary>
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

    public TrayService(
        Action showAction,
        Func<Task> selectionAction,
        Func<Task> screenOcrAction,
        Action settingsAction,
        Action exitAction)
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
        _menu.Items.Add("打开", null, (_, _) => _showAction());
        _selectionItem = _menu.Items.Add(
            $"划词翻译  {selectionHotkeyText}",
            null,
            async (_, _) => await _selectionAction());
        _screenOcrItem = _menu.Items.Add(
            $"截图翻译  {screenOcrHotkeyText}",
            null,
            async (_, _) => await _screenOcrAction());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("设置", null, (_, _) => _settingsAction());
        _menu.Items.Add("退出", null, (_, _) => _exitAction());

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

    /// <summary>优先用内嵌的 app.ico；取不到资源时退回系统默认图标，保证托盘始终可见。</summary>
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
            // 资源 URI 解析或图标字节损坏都会抛异常；托盘不是核心功能，宁可降级也不让启动失败。
        }

        return SystemIcons.Application;
    }

    public void UpdateHotkeyTexts(string selectionHotkeyText, string screenOcrHotkeyText)
    {
        if (_selectionItem is not null)
        {
            _selectionItem.Text = $"划词翻译  {selectionHotkeyText}";
        }

        if (_screenOcrItem is not null)
        {
            _screenOcrItem.Text = $"截图翻译  {screenOcrHotkeyText}";
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
