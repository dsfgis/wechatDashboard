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
            .Where(snapshot => !_options.IgnoreWindowTitleContains.Any(ignored =>
                snapshot.WindowTitle.Contains(ignored, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var normalizedSnapshot = string.Join("\n", matchedSnapshots.Select(snapshot => snapshot.WindowTitle + "\n" + NormalizeLine(snapshot.Text)));
        var nextOffset = StableHash(normalizedSnapshot);
        if (string.Equals(context.GetOffset(Name), nextOffset, StringComparison.Ordinal))
        {
            return new CaptureBatch(Name, Array.Empty<CapturedMessage>(), nextOffset);
        }

        var messages = new Dictionary<string, CapturedMessage>(StringComparer.Ordinal);
        foreach (var snapshot in matchedSnapshots)
        {
            var lines = snapshot.Text
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                .Select(NormalizeLine)
                .Where(line => !IsSkippableLine(snapshot, line))
                .ToArray();
            var chatName = InferChatName(snapshot.WindowTitle, lines);

            foreach (var line in lines)
            {
                if (!TryParseMessageLine(snapshot, line, chatName, out var senderName, out var content, out var sentAt))
                {
                    continue;
                }

                AddMessage(messages, snapshot, chatName, senderName, content, sentAt, line);
            }

            foreach (var parsedMessage in ParseSplitMessageBlocks(snapshot, lines, chatName))
            {
                AddMessage(
                    messages,
                    snapshot,
                    chatName,
                    parsedMessage.SenderName,
                    parsedMessage.Content,
                    parsedMessage.SentAt,
                    $"{parsedMessage.SentAt:HH:mm}|{parsedMessage.SenderName}|{parsedMessage.Content}");
            }
        }

        return new CaptureBatch(Name, messages.Values.ToArray(), nextOffset);
    }

    private bool TryParseMessageLine(
        WindowTextSnapshot snapshot,
        string line,
        string chatName,
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
            string.Equals(line, chatName, StringComparison.OrdinalIgnoreCase) ||
            TryParseVisibleTime(line, out _))
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
            TryParseVisibleTime(match.Groups["time"].Value, out var time))
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

    private IEnumerable<ParsedVisibleMessage> ParseSplitMessageBlocks(
        WindowTextSnapshot snapshot,
        IReadOnlyList<string> lines,
        string chatName)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (TryParseVisibleTime(lines[index], out var leadingTime) &&
                index + 2 < lines.Count &&
                IsSenderLine(lines[index + 1], chatName) &&
                IsContentLine(lines[index + 2], chatName))
            {
                yield return new ParsedVisibleMessage(
                    lines[index + 1],
                    lines[index + 2],
                    ToSnapshotDateTime(snapshot.CapturedAt, leadingTime));
                index += 2;
                continue;
            }

            if (!IsSenderLine(lines[index], chatName))
            {
                continue;
            }

            if (index + 2 < lines.Count &&
                TryParseVisibleTime(lines[index + 1], out var middleTime) &&
                IsContentLine(lines[index + 2], chatName))
            {
                yield return new ParsedVisibleMessage(
                    lines[index],
                    lines[index + 2],
                    ToSnapshotDateTime(snapshot.CapturedAt, middleTime));
                index += 2;
                continue;
            }

            if (index + 1 < lines.Count &&
                IsContentLine(lines[index + 1], chatName) &&
                LooksLikeMessageContent(lines[index + 1]))
            {
                yield return new ParsedVisibleMessage(lines[index], lines[index + 1], snapshot.CapturedAt);
                index += 1;
            }
        }
    }

    private void AddMessage(
        IDictionary<string, CapturedMessage> messages,
        WindowTextSnapshot snapshot,
        string chatName,
        string senderName,
        string content,
        DateTimeOffset sentAt,
        string keyMaterial)
    {
        var sourceMessageKey = $"{_options.Source}:window:{StableHash(snapshot.WindowTitle + "|" + chatName + "|" + keyMaterial)}";
        messages.TryAdd(
            sourceMessageKey,
            new CapturedMessage(
                Source: _options.Source,
                SourceMessageKey: sourceMessageKey,
                ChatId: string.IsNullOrWhiteSpace(_options.ChatId) ? chatName : _options.ChatId,
                ChatName: chatName,
                SenderName: senderName,
                Content: content,
                SentAt: sentAt,
                MessageType: MessageType.Text));
    }

    private string InferChatName(string windowTitle, IReadOnlyList<string> lines)
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

        var firstTitleLine = lines.FirstOrDefault(line =>
            !string.Equals(line, windowTitle, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(line, _options.ChatName, StringComparison.OrdinalIgnoreCase) &&
            !TryParseVisibleTime(line, out _) &&
            !MessageLineRegex.IsMatch(line) &&
            !LooksLikeCommonUiLabel(line));
        if (!string.IsNullOrWhiteSpace(firstTitleLine))
        {
            return firstTitleLine;
        }

        return _options.ChatName;
    }

    private bool IsSkippableLine(WindowTextSnapshot snapshot, string line)
    {
        return string.IsNullOrWhiteSpace(line) ||
               string.Equals(line, snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(line, _options.ChatName, StringComparison.OrdinalIgnoreCase) ||
               _options.IgnoreLinePrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSenderLine(string line, string chatName)
    {
        return !string.IsNullOrWhiteSpace(line) &&
               !string.Equals(line, chatName, StringComparison.OrdinalIgnoreCase) &&
               line.Length <= 40 &&
               !TryParseVisibleTime(line, out _) &&
               !MessageLineRegex.IsMatch(line) &&
               !LooksLikeCommonUiLabel(line) &&
               !LooksLikeMessageContent(line);
    }

    private static bool IsContentLine(string line, string chatName)
    {
        return !string.IsNullOrWhiteSpace(line) &&
               !string.Equals(line, chatName, StringComparison.OrdinalIgnoreCase) &&
               !TryParseVisibleTime(line, out _) &&
               !MessageLineRegex.IsMatch(line) &&
               !LooksLikeCommonUiLabel(line);
    }

    private static bool LooksLikeMessageContent(string line)
    {
        return line.Length > 8 ||
               line.Contains('@') ||
               line.Contains("请", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("同步", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("处理", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("确认", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("问题", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("故障", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("今天", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCommonUiLabel(string line)
    {
        return CommonUiLabels.Contains(line);
    }

    private static string NormalizeLine(string value)
    {
        return string.Join(" ", value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool TryParseVisibleTime(string value, out TimeSpan time)
    {
        return TimeSpan.TryParseExact(
            value,
            new[] { @"h\:mm", @"hh\:mm" },
            CultureInfo.InvariantCulture,
            out time);
    }

    private static DateTimeOffset ToSnapshotDateTime(DateTimeOffset capturedAt, TimeSpan time)
    {
        return new DateTimeOffset(
            capturedAt.Year,
            capturedAt.Month,
            capturedAt.Day,
            time.Hours,
            time.Minutes,
            0,
            capturedAt.Offset);
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

    private static readonly HashSet<string> CommonUiLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "微信",
        "聊天",
        "通讯录",
        "收藏",
        "设置",
        "搜索",
        "发送",
        "表情",
        "图片",
        "文件",
        "语音",
        "视频",
        "朋友圈",
        "订阅号",
        "服务号",
        "小程序",
        "看一看",
        "搜一搜",
        "新的朋友",
        "公众号"
    };

    private sealed record ParsedVisibleMessage(string SenderName, string Content, DateTimeOffset SentAt);
}
