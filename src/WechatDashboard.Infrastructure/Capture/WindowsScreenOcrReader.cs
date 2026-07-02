using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 基于 Windows.Media.Ocr 的屏幕 OCR 读取器。
/// 通过截取指定窗口的位图（按微信聊天面板区域裁剪），
/// 调用 Windows OCR 引擎识别文本，作为 UIA 不可用时的兜底文本来源。
/// </summary>
public sealed class WindowsScreenOcrReader : IScreenOcrReader
{
    // 懒加载的 OCR 引擎，按当前用户语言创建
    private readonly Lazy<OcrEngine?> _ocrEngine = new(OcrEngine.TryCreateFromUserProfileLanguages);

    /// <summary>
    /// 读取指定窗口的文本：截屏 -> 裁剪聊天面板 -> 转 PNG -> OCR 识别 -> 拼接行文本。
    /// 任何环节失败均返回空字符串，不抛异常。
    /// </summary>
    public async Task<string> ReadWindowTextAsync(int nativeWindowHandle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (nativeWindowHandle == 0 || _ocrEngine.Value is not { } ocrEngine)
        {
            return "";
        }

        try
        {
            using var bitmap = CaptureWindow(nativeWindowHandle);
            if (bitmap is null)
            {
                return "";
            }

            using var stream = await ToRandomAccessStreamAsync(bitmap, cancellationToken);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            using var softwareBitmap = await decoder
                .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                .AsTask(cancellationToken);
            var result = await ocrEngine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);

            return string.Join(
                Environment.NewLine,
                result.Lines
                    .Select(line => line.Text?.Trim() ?? "")
                    .Where(line => !string.IsNullOrWhiteSpace(line)));
        }
        catch
        {
            return "";
        }
    }

    /// <summary>截取窗口指定区域：获取窗口矩形，按微信聊天面板比例裁剪，从屏幕拷贝像素。</summary>
    private static Bitmap? CaptureWindow(int nativeWindowHandle)
    {
        var handle = new IntPtr(nativeWindowHandle);
        if (!GetWindowRect(handle, out var rect))
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var crop = WindowOcrCropCalculator.CalculateWeChatChatPanel(width, height);
        var bitmap = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(
            rect.Left + crop.X,
            rect.Top + crop.Y,
            0,
            0,
            new Size(crop.Width, crop.Height),
            CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>将 Bitmap 转为 IRandomAccessStream（PNG），供 WinRT BitmapDecoder 使用。</summary>
    private static async Task<IRandomAccessStream> ToRandomAccessStreamAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream, ImageFormat.Png);
        var bytes = memoryStream.ToArray();

        var randomAccessStream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(randomAccessStream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync().AsTask(cancellationToken);
        await writer.FlushAsync().AsTask(cancellationToken);
        writer.DetachStream();
        randomAccessStream.Seek(0);
        return randomAccessStream;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
