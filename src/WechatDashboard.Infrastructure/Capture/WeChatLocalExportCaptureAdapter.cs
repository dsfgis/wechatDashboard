using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 微信本地导出采集适配器：扫描指定目录下的 .jsonl/.json 导出文件，
/// 按文件路径与记录号实现增量读取（JsonlOffset），解析为 CapturedMessage。
/// 支持多种字段别名（msgId/content/StrContent 等）与非文本消息的摘要化处理。
/// </summary>
public sealed class WeChatLocalExportCaptureAdapter : IMessageCaptureAdapter
{
    // 采集选项：源名称、目录路径等
    private readonly WeChatLocalExportOptions _options;

    /// <summary>构造适配器：校验目录路径非空。</summary>
    public WeChatLocalExportCaptureAdapter(WeChatLocalExportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.DirectoryPath))
        {
            throw new ArgumentException("Directory path is required.", nameof(options));
        }
    }

    /// <summary>适配器名称，格式为 {Source}.LocalExport。</summary>
    public string Name => $"{_options.Source}.LocalExport";

    /// <summary>
    /// 执行一次采集：创建目录、解析偏移、遍历导出文件，
    /// 跳过已读记录，将新记录解析为消息并返回新批次与更新后的偏移。
    /// </summary>
    public async Task<CaptureBatch> CaptureAsync(CaptureContext context, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.DirectoryPath);

        var offset = JsonlOffset.Parse(context.GetOffset(Name));
        var messages = new List<CapturedMessage>();
        var nextOffset = offset;

        foreach (var filePath in EnumerateExportFiles(_options.DirectoryPath))
        {
            var lastWriteTicks = File.GetLastWriteTimeUtc(filePath).Ticks;
            if (lastWriteTicks < offset.LastWriteUtcTicks)
            {
                continue;
            }

            if (Path.GetExtension(filePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var result = await ReadJsonFileAsync(filePath, offset, lastWriteTicks, cancellationToken);
                messages.AddRange(result.Messages);
                nextOffset = result.NextOffset ?? nextOffset;
                continue;
            }

            var lineResult = await ReadJsonLinesFileAsync(filePath, offset, lastWriteTicks, cancellationToken);
            messages.AddRange(lineResult.Messages);
            nextOffset = lineResult.NextOffset ?? nextOffset;
        }

        return new CaptureBatch(Name, messages, nextOffset.ToString());
    }

    /// <summary>读取 JSONL 文件（每行一条记录），跳过已读行号，返回新消息与下次偏移。</summary>
    private async Task<FileReadResult> ReadJsonLinesFileAsync(
        string filePath,
        JsonlOffset offset,
        long lastWriteTicks,
        CancellationToken cancellationToken)
    {
        var messages = new List<CapturedMessage>();
        JsonlOffset? nextOffset = null;
        var lineNumber = 0;

        await foreach (var line in ReadLinesAsync(filePath, cancellationToken))
        {
            lineNumber++;
            if (ShouldSkipRecord(filePath, lineNumber, offset))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            messages.Add(ParseRecord(document.RootElement, filePath, lineNumber));
            nextOffset = new JsonlOffset(lastWriteTicks, filePath, lineNumber);
        }

        return new FileReadResult(messages, nextOffset);
    }

    /// <summary>读取 JSON 文件（数组或单对象），跳过已读记录号，返回新消息与下次偏移。</summary>
    private async Task<FileReadResult> ReadJsonFileAsync(
        string filePath,
        JsonlOffset offset,
        long lastWriteTicks,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var messages = new List<CapturedMessage>();
        JsonlOffset? nextOffset = null;
        var recordNumber = 0;

        foreach (var element in EnumerateJsonRecords(document.RootElement))
        {
            recordNumber++;
            if (ShouldSkipRecord(filePath, recordNumber, offset))
            {
                continue;
            }

            messages.Add(ParseRecord(element, filePath, recordNumber));
            nextOffset = new JsonlOffset(lastWriteTicks, filePath, recordNumber);
        }

        return new FileReadResult(messages, nextOffset);
    }

    /// <summary>将 JSON 记录解析为 CapturedMessage，兼容多种字段别名，缺省 ID 时用哈希生成。</summary>
    private CapturedMessage ParseRecord(JsonElement element, string filePath, int recordNumber)
    {
        var messageId = FirstString(element, "msgId", "messageId", "id", "MsgSvrID", "localId")
            ?? StableHash($"{filePath}:{recordNumber}:{FirstString(element, "content", "message", "msg", "StrContent", "strContent")}");
        var chatId = FirstString(element, "chatId", "talker", "roomId", "TalkerId", "strTalker")
            ?? FirstString(element, "chatName", "roomName", "talkerName", "sessionName")
            ?? "unknown-chat";
        var chatName = FirstString(element, "chatName", "roomName", "talkerName", "sessionName", "nickName")
            ?? chatId;
        var senderName = FirstString(element, "senderName", "sender", "senderNick", "fromUser", "displayName")
            ?? "未知发送人";
        var content = FirstString(element, "content", "message", "msg", "StrContent", "strContent")
            ?? SummarizeNonTextMessage(element);
        var sentAt = FirstDateTimeOffset(element, "sentAt", "createTime", "CreateTime", "timestamp", "time")
            ?? DateTimeOffset.Now;
        var messageType = ParseMessageType(FirstString(element, "messageType", "msgType", "type", "Type"));

        return new CapturedMessage(
            Source: _options.Source,
            SourceMessageKey: $"{_options.Source}:local:{messageId}",
            ChatId: chatId,
            ChatName: chatName,
            SenderName: senderName,
            Content: content,
            SentAt: sentAt,
            MessageType: messageType);
    }

    /// <summary>枚举目录下的 .jsonl 与 .json 文件，按文件名排序。</summary>
    private static IEnumerable<string> EnumerateExportFiles(string directoryPath)
    {
        return Directory.EnumerateFiles(directoryPath, "*.*")
            .Where(path =>
                Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>枚举 JSON 根元素下的记录：支持数组、含 messages 数组的对象、单对象三种结构。</summary>
    private static IEnumerable<JsonElement> EnumerateJsonRecords(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                yield return element;
            }
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("messages", out var messages) &&
                 messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in messages.EnumerateArray())
            {
                yield return element;
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
        }
    }

    /// <summary>异步逐行读取文件，支持取消。以共享读写模式打开，避免阻塞导出工具写入。</summary>
    private static async IAsyncEnumerable<string> ReadLinesAsync(
        string filePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
        }
    }

    /// <summary>判断记录是否应跳过：同文件且记录号≤已读行号即视为已读。</summary>
    private static bool ShouldSkipRecord(string filePath, int recordNumber, JsonlOffset offset)
    {
        return string.Equals(filePath, offset.FilePath, StringComparison.OrdinalIgnoreCase) &&
            recordNumber <= offset.LineNumber;
    }

    /// <summary>按候选字段名依次尝试读取字符串，兼容字符串/数字/布尔，自动 trim。</summary>
    private static string? FirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            var result = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(result))
            {
                return result.Trim();
            }
        }

        return null;
    }

    /// <summary>按候选字段名读取时间，兼容字符串与 Unix 时间戳（秒/毫秒）。</summary>
    private static DateTimeOffset? FirstDateTimeOffset(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return ParseUnixTime(number);
            }
        }

        return null;
    }

    /// <summary>将 Unix 时间戳转换为 DateTimeOffset，>9999999999 视为毫秒。</summary>
    private static DateTimeOffset ParseUnixTime(long value)
    {
        return value > 9_999_999_999
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
    }

    /// <summary>将消息类型字符串/数字映射为 MessageType 枚举。</summary>
    private static MessageType ParseMessageType(string? value)
    {
        if (Enum.TryParse<MessageType>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        if (int.TryParse(value, out var numericType))
        {
            return numericType switch
            {
                3 => MessageType.Image,
                34 => MessageType.File,
                43 => MessageType.File,
                49 => MessageType.Link,
                10000 => MessageType.System,
                _ => MessageType.Text
            };
        }

        return MessageType.Text;
    }

    /// <summary>对非文本消息生成摘要：文件名、链接或占位文本。</summary>
    private static string SummarizeNonTextMessage(JsonElement element)
    {
        var fileName = FirstString(element, "fileName", "filename", "title", "FileName");
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return $"[文件] {fileName}";
        }

        var url = FirstString(element, "url", "link", "href");
        if (!string.IsNullOrWhiteSpace(url))
        {
            return $"[链接] {url}";
        }

        return "[非文本消息]";
    }

    /// <summary>计算字符串的 SHA256 哈希，取前 12 字节作为十六进制 ID（小写）。</summary>
    private static string StableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }

    /// <summary>文件读取结果：本次读取的消息列表与下次偏移（可能为空）。</summary>
    private sealed record FileReadResult(IReadOnlyList<CapturedMessage> Messages, JsonlOffset? NextOffset);
}
