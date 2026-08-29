using System.Text.RegularExpressions;

namespace WechatDashboard.Application.Capture;

internal static partial class CaptureErrorSanitizer
{
    [GeneratedRegex(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9])[A-Z]:\\[^\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex(@"\\\\[^\s\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex UncPathPattern();

    public static string Sanitize(string? value)
    {
        var redacted = KeyPattern().Replace(value ?? "采集源运行失败。", "<REDACTED_KEY>");
        redacted = WindowsPathPattern().Replace(redacted, "<REDACTED_PATH>");
        redacted = UncPathPattern().Replace(redacted, "<REDACTED_PATH>");
        return redacted.Length <= 500 ? redacted : redacted[..500];
    }
}
