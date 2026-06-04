using System.Globalization;
using System.Text.RegularExpressions;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Capture;

public sealed class WindowTextCaptureAdapter : IMessageCaptureAdapter
{
    private readonly WindowTextCaptureOptions _options;
    private readonly IWindowTextSnapshotProvider _snapshotProvider;

    public WindowTextCaptureAdapter(WindowTextCaptureOptions options, IWindowTextSnapshotProvider snapshotProvider)
    {
        _options = options;
        _snapshotProvider = snapshotProvider;
    }

    public string Name => $"{_options.Source}.WindowText";

    public async Task<CaptureBatch> CaptureAsync(CaptureContext context, CancellationToken cancellationToken)
    {
        var snapshots = await _snapshotProvider.GetSnapshotsAsync(_options, cancellationToken);
        var matchedSnapshots = snapshots
            .Where(snapshot => string.IsNullOrWhiteSpace(_options.WindowTitleContains) ||
                               snapshot.WindowTitle.Contains(_options.WindowTitleContains, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var normalizedSnapshot = string.Join("\n", matchedSnapshots.Select(snapshot => snapshot.WindowTitle + "\n" + NormalizeLine(snapshot.Text)));
        var nextOffset = StableHash(normalizedSnapshot);
        if (string.Equals(context.GetOffset(Name), nextOffset, StringComparison.Ordinal))
        {
            return new CaptureBatch(Name, Array.Empty<CapturedMessage>(), nextOffset);
        }

        var messages = new List<CapturedMessage>();
        foreach (var snapshot in matchedSnapshots)
        {
            foreach (var line in snapshot.Text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                var normalizedLine = NormalizeLine(line);
                if (!TryParseMessageLine(snapshot, normalizedLine, out var senderName, out var content, out var sentAt))
                {
                    continue;
                }

                var chatName = InferChatName(snapshot.WindowTitle);
                messages.Add(new CapturedMessage(
                    Source: _options.Source,
                    SourceMessageKey: $"{_options.Source}:window:{StableHash(snapshot.WindowTitle + "|" + normalizedLine)}",
                    ChatId: string.IsNullOrWhiteSpace(_options.ChatId) ? chatName : _options.ChatId,
                    ChatName: chatName,
                    SenderName: senderName,
                    Content: content,
                    SentAt: sentAt,
                    MessageType: MessageType.Text));
            }
        }

        return new CaptureBatch(Name, messages, nextOffset);
    }

    private bool TryParseMessageLine(
        WindowTextSnapshot snapshot,
        string line,
        out string senderName,
        out string content,
        out DateTimeOffset sentAt)
    {
        senderName = "";
        content = "";
        sentAt = snapshot.CapturedAt;

        if (string.IsNullOrWhiteSpace(line) ||
            string.Equals(line, snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(line, _options.ChatName, StringComparison.OrdinalIgnoreCase) ||
            _options.IgnoreLinePrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var match = MessageLineRegex.Match(line);
        if (!match.Success)
        {
            return false;
        }

        senderName = match.Groups["sender"].Value.Trim();
        content = match.Groups["content"].Value.Trim();
        if (string.IsNullOrWhiteSpace(senderName) || string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        if (match.Groups["time"].Success &&
            TimeSpan.TryParseExact(
                match.Groups["time"].Value,
                new[] { @"h\:mm", @"hh\:mm" },
                CultureInfo.InvariantCulture,
                out var time))
        {
            sentAt = new DateTimeOffset(
                snapshot.CapturedAt.Year,
                snapshot.CapturedAt.Month,
                snapshot.CapturedAt.Day,
                time.Hours,
                time.Minutes,
                0,
                snapshot.CapturedAt.Offset);
        }

        return true;
    }

    private string InferChatName(string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return _options.ChatName;
        }

        var separators = new[] { " - ", " | ", " — " };
        foreach (var separator in separators)
        {
            var index = windowTitle.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                return windowTitle[..index].Trim();
            }
        }

        return _options.ChatName;
    }

    private static string NormalizeLine(string value)
    {
        return string.Join(" ", value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string StableHash(string value)
    {
        var hash = 1469598103934665603UL;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static readonly Regex MessageLineRegex = new(
        @"^(?:(?<time>\d{1,2}:\d{2})\s+)?(?<sender>[^:：]{1,40})[:：]\s*(?<content>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
