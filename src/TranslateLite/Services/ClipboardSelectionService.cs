using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataObject = System.Windows.IDataObject;

namespace TranslateLite.Services;

public sealed class ClipboardSelectionService
{
    /// <summary>修饰键松开后的稳定时间：焦点还要从热键消费方转回前台窗口。</summary>
    private const int ModifierSettleDelayMs = 40;

    /// <summary>修饰键等待上限：用户一直按着键时不要无限挂住热键回调。</summary>
    private const int ModifierReleaseTimeoutMs = 1500;

    private const int ModifierPollIntervalMs = 20;

    /// <summary>剪贴板被别的进程短暂占用时（COMException 0x800401D0）重试的次数与间隔。</summary>
    private const int ClipboardRetryCount = 5;

    private const int ClipboardRetryDelayMs = 40;

    // GetAsyncKeyState 的虚拟键码：读物理按键状态，与应用自身是否有焦点、是否在消息循环里无关。
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    public async Task<string> GetSelectedTextAsync()
    {
        // 热键触发时修饰键仍被物理按住。此时 SendWait("^c") 会带着尚未松开的 Alt 一起发出，
        // 目标程序收到的是 Ctrl+Alt+C（菜单命令）而不是复制；必须先等用户松手。
        await WaitForModifiersReleasedAsync();

        var snapshot = SnapshotClipboard();
        try
        {
            if (!await TryClipboardActionAsync(() => WpfClipboard.Clear()))
            {
                return string.Empty;
            }

            Forms.SendKeys.SendWait("^c");
            await Task.Delay(160);

            return await TryClipboardQueryAsync(
                () => WpfClipboard.ContainsText() ? WpfClipboard.GetText().Trim() : string.Empty,
                string.Empty);
        }
        catch (InvalidOperationException)
        {
            // SendKeys 在消息循环被阻塞时抛 InvalidOperationException；划词失败退化成“没选中”，
            // 交由上层走“无内容”分支，不要让异常穿透 async void 事件处理器。
            return string.Empty;
        }
        finally
        {
            if (snapshot is not null)
            {
                await RestoreClipboardAsync(snapshot);
            }
        }
    }

    /// <summary>
    /// 还原用户原来的剪贴板。还原是尽力而为：这里不再让异常穿透 finally，
    /// 否则一次“剪贴板被占用”会覆盖掉已经拿到的选中文本结果。
    /// </summary>
    private static async Task RestoreClipboardAsync(WpfDataObject snapshot)
    {
        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            try
            {
                WpfClipboard.SetDataObject(snapshot, true);
                return;
            }
            catch (ExternalException)
            {
                // 剪贴板此刻被另一个进程持有；等一小会儿再试。
            }
            catch (Exception)
            {
                // 快照里某个私有格式不能写回系统剪贴板；继续尝试也没用，放弃还原。
                // 这里吞掉所有异常是刻意的：还原失败不应盖掉已经读到的选中文本。
                return;
            }

            if (attempt + 1 < ClipboardRetryCount)
            {
                await Task.Delay(ClipboardRetryDelayMs);
            }
        }
    }

    private static async Task WaitForModifiersReleasedAsync()
    {
        var deadline = Environment.TickCount64 + ModifierReleaseTimeoutMs;
        while (AnyModifierDown() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(ModifierPollIntervalMs);
        }

        if (!AnyModifierDown())
        {
            await Task.Delay(ModifierSettleDelayMs);
        }
    }

    private static bool AnyModifierDown() =>
        IsKeyDown(VkControl) || IsKeyDown(VkMenu) || IsKeyDown(VkShift)
        || IsKeyDown(VkLWin) || IsKeyDown(VkRWin);

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// 复制剪贴板内容到本地快照。Clipboard.GetDataObject() 拿到的是 OLE 的惰性句柄，
    /// Clear() 之后原来的数据就读不出来了，因此必须在清空前按格式逐项把数据取出来。
    /// </summary>
    private static WpfDataObject? SnapshotClipboard()
    {
        WpfDataObject? source = null;
        if (!TryClipboardAction(() => source = WpfClipboard.GetDataObject()) || source is null)
        {
            return null;
        }

        var clone = new System.Windows.DataObject();
        string[] formats;
        try
        {
            formats = source.GetFormats();
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
        {
            return null;
        }

        foreach (var format in formats)
        {
            try
            {
                if (source.GetData(format) is { } data)
                {
                    clone.SetData(format, data);
                }
            }
            catch (Exception)
            {
                // 单个格式读不出来（源程序已退出、格式是其私有类型）就跳过，其余格式仍可还原。
            }
        }

        return clone.GetFormats().Length > 0 ? clone : null;
    }

    private static bool TryClipboardAction(Action action)
    {
        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (ExternalException)
            {
                if (attempt + 1 < ClipboardRetryCount)
                {
                    Thread.Sleep(ClipboardRetryDelayMs);
                }
            }
        }

        return false;
    }

    private static async Task<bool> TryClipboardActionAsync(Action action)
    {
        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (ExternalException)
            {
                if (attempt + 1 < ClipboardRetryCount)
                {
                    await Task.Delay(ClipboardRetryDelayMs);
                }
            }
        }

        return false;
    }

    private static async Task<T> TryClipboardQueryAsync<T>(Func<T> query, T fallback)
    {
        for (var attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            try
            {
                return query();
            }
            catch (ExternalException)
            {
                if (attempt + 1 < ClipboardRetryCount)
                {
                    await Task.Delay(ClipboardRetryDelayMs);
                }
            }
        }

        return fallback;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
