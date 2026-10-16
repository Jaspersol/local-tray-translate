using Forms = System.Windows.Forms;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataObject = System.Windows.IDataObject;

namespace TranslateLite.Services;

public sealed class ClipboardSelectionService
{
    public async Task<string> GetSelectedTextAsync()
    {
        WpfDataObject? previous = null;
        try
        {
            previous = WpfClipboard.GetDataObject();
        }
        catch
        {
            // Some applications temporarily lock the clipboard; selection lookup can still proceed.
        }

        try
        {
            WpfClipboard.Clear();
            Forms.SendKeys.SendWait("^c");
            await Task.Delay(160);
            return WpfClipboard.ContainsText() ? WpfClipboard.GetText().Trim() : string.Empty;
        }
        finally
        {
            if (previous is not null)
            {
                try
                {
                    WpfClipboard.SetDataObject(previous, true);
                }
                catch
                {
                    // Clipboard restoration is best effort because the source app may keep it locked.
                }
            }
        }
    }
}
