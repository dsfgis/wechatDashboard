using System.Text.RegularExpressions;

namespace WechatDashboard.Infrastructure.Capture;

internal static class WeChatReaderDiagnosticSanitizer
{
    private static readonly Regex KeyPattern = new(
        @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Sanitize(string? value)
    {
        var redacted = KeyPattern.Replace(value ?? "", "<REDACTED_KEY>");
        return redacted.Length <= 500 ? redacted : redacted[..500];
    }
}
