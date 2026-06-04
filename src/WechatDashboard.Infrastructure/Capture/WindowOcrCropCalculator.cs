namespace WechatDashboard.Infrastructure.Capture;

public static class WindowOcrCropCalculator
{
    public static WindowOcrCropRectangle CalculateWeChatChatPanel(int windowWidth, int windowHeight)
    {
        if (windowWidth < 900 || windowHeight < 500)
        {
            return new WindowOcrCropRectangle(0, 0, windowWidth, windowHeight);
        }

        var left = Math.Clamp((int)Math.Round(windowWidth * 0.28), 360, windowWidth - 320);
        var top = Math.Clamp((int)Math.Round(windowHeight * 0.05), 40, 80);

        return new WindowOcrCropRectangle(
            X: left,
            Y: top,
            Width: windowWidth - left,
            Height: windowHeight - top);
    }
}

public sealed record WindowOcrCropRectangle(int X, int Y, int Width, int Height);
