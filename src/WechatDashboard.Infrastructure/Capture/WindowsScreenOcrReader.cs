using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace WechatDashboard.Infrastructure.Capture;

public sealed class WindowsScreenOcrReader : IScreenOcrReader
{
    private readonly Lazy<OcrEngine?> _ocrEngine = new(OcrEngine.TryCreateFromUserProfileLanguages);

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
