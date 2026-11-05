using System.Drawing;
using System.IO;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace TranslateLite.Services;

/// <summary>用 Windows.Media.Ocr 离线识别截图文字；识别语言跟随用户配置的 OCR 语言列表。</summary>
public sealed class OcrService
{
    public async Task<string> ReadTextAsync(Bitmap bitmap)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"translatelite-ocr-{Guid.NewGuid():N}.png");
        try
        {
            bitmap.Save(tempFile, System.Drawing.Imaging.ImageFormat.Png);

            var storageFile = await StorageFile.GetFileFromPathAsync(tempFile);
            using var randomAccessStream = await storageFile.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);

            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            if (engine is null)
            {
                return string.Empty;
            }

            var result = await engine.RecognizeAsync(softwareBitmap);
            return result.Text.Trim();
        }
        finally
        {
            bitmap.Dispose();
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // Temporary OCR images are disposable; deletion can be retried by the OS later.
            }
        }
    }
}
