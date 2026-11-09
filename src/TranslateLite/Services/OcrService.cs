using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace TranslateLite.Services;

/// <summary>用 Windows.Media.Ocr 离线识别截图文字；识别语言跟随用户配置的 OCR 语言列表。</summary>
public sealed class OcrService
{
    // 引擎可复用，没必要每次识别都重新探测已安装的 OCR 语言并新建一个引擎。
    private readonly Lazy<OcrEngine?> _engine = new(CreateEngine);

    public async Task<string> ReadTextAsync(Bitmap bitmap)
    {
        try
        {
            // 全程在内存里把 GDI 位图交给 WinRT 解码：省掉一份 PNG 临时文件（写盘/读盘/删除），
            // 也避免临时文件删除失败后在 %TEMP% 留下垃圾。
            using var stream = new InMemoryRandomAccessStream();
            byte[] pngBytes;
            using (var memory = new MemoryStream())
            {
                bitmap.Save(memory, ImageFormat.Png);
                pngBytes = memory.ToArray();
            }

            var writer = new DataWriter(stream);
            try
            {
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }
            finally
            {
                // DataWriter.Dispose 会关闭底层流，后面还要从 stream 解码，先 detach 再释放。
                writer.DetachStream();
                writer.Dispose();
            }

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);

            var engine = _engine.Value;
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
        }
    }

    private static OcrEngine? CreateEngine() =>
        OcrEngine.TryCreateFromUserProfileLanguages()
        ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
}
