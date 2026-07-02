using System.Globalization;
using System.Text.RegularExpressions;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 可见窗口文本采集适配器：通过 UIA/OCR 读取可见窗口的文本快照，
/// 解析其中的聊天消息（发送人、内容、时间），转换为 CapturedMessage。
/// 采用基于偏移的增量策略：快照内容哈希不变时跳过，避免重复入库。
/// 适用于微信等未提供本地数据库的即时通讯窗口的实时采集。
/// </summary>
public sealed class WindowTextCaptureAdapter : IMessageCaptureAdapter
{
    // 采集选项：窗口标题过滤、聊天名称、忽略前缀等
    private readonly WindowTextCaptureOptions _options;
    // 窗口文本快照提供者（UIA + OCR 组合实现）
    private readonly IWindowTextSnapshotProvider _snapshotProvider;

    /// <summary>构造适配器：传入采集选项与快照提供者。</summary>
    public WindowTextCaptureAdapter(WindowTextCaptureOptions options, IWindowTextSnapshotProvider snapshotProvider)
    {
        _options = options;
        _snapshotProvider = snapshotProvider;
    }

    /// <summary>适配器名称，格式为 {Source}.WindowText。</summary>
    public string Name => $"{_options.Source}.WindowText";

    /// <summary>
    /// 执行一次采集：获取窗口文本快照，按标题过滤，解析消息行，
    /// 与上次快照偏移对比实现增量，返回新增消息批次与新的偏移哈希。
    /// </summary>
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

    /// <summary>
    /// 尝试将单行文本解析为消息：匹配"发送人:内容"或"时间 发送人:内容"格式。
    /// 成功时输出发送人、内容与时间（时间缺省时使用快照采集时间）。
    /// </summary>
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

    /// <summary>
    /// 解析分行排列的消息块：支持"时间/发送人/内容"三行、"发送人/时间/内容"三行、
    /// "发送人/内容"两行三种布局。返回解析后的消息序列。
    /// </summary>
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

    /// <summary>将解析出的消息加入字典（按 SourceMessageKey 去重）。</summary>
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

    /// <summary>
    /// 从窗口标题推断聊天名：优先按分隔符（-、|、—）截取标题前缀，
    /// 其次取第一个非标题/非时间/非消息行的文本，最终回退到选项中的 ChatName。
    /// </summary>
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

    /// <summary>判断是否为可跳过的行：空白、等于窗口标题/聊天名，或匹配忽略前缀。</summary>
    private bool IsSkippableLine(WindowTextSnapshot snapshot, string line)
    {
        return string.IsNullOrWhiteSpace(line) ||
               string.Equals(line, snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(line, _options.ChatName, StringComparison.OrdinalIgnoreCase) ||
               _options.IgnoreLinePrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>判断是否为发送人行：长度≤40、非时间、非消息行、非 UI 标签、非内容。</summary>
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

    /// <summary>判断是否为内容行：非空、非聊天名、非时间、非消息行、非 UI 标签。</summary>
    private static bool IsContentLine(string line, string chatName)
    {
        return !string.IsNullOrWhiteSpace(line) &&
               !string.Equals(line, chatName, StringComparison.OrdinalIgnoreCase) &&
               !TryParseVisibleTime(line, out _) &&
               !MessageLineRegex.IsMatch(line) &&
               !LooksLikeCommonUiLabel(line);
    }

    /// <summary>启发式判断是否为消息内容：长度>8 或包含常见关键词。</summary>
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

    /// <summary>判断是否为常见 UI 标签（如"微信"、"聊天"、"通讯录"等）。</summary>
    private static bool LooksLikeCommonUiLabel(string line)
    {
        return CommonUiLabels.Contains(line);
    }

    /// <summary>规范化行文本：去除首尾空白并将连续空白合并为单个空格。</summary>
    private static string NormalizeLine(string value)
    {
        return string.Join(" ", value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>尝试解析可见时间格式（h:mm 或 hh:mm）。</summary>
    private static bool TryParseVisibleTime(string value, out TimeSpan time)
    {
        return TimeSpan.TryParseExact(
            value,
            new[] { @"h\:mm", @"hh\:mm" },
            CultureInfo.InvariantCulture,
            out time);
    }

    /// <summary>将可见时间与快照日期组合为完整的时间戳。</summary>
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

    /// <summary>计算字符串的稳定哈希（FNV-1a 64 位），返回 16 位十六进制字符串，用于偏移对比。</summary>
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

    // 消息行正则：可选时间 + 发送人 + 内容，兼容中英文冒号
    private static readonly Regex MessageLineRegex = new(
        @"^(?:(?<time>\d{1,2}:\d{2})\s+)?(?<sender>[^:：]{1,40})[:：]\s*(?<content>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 常见 UI 标签集合：用于过滤非消息文本
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

    /// <summary>解析后的可见消息：发送人、内容、时间。</summary>
    private sealed record ParsedVisibleMessage(string SenderName, string Content, DateTimeOffset SentAt);
}
