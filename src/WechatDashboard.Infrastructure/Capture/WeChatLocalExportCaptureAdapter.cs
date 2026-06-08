using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Capture;

public sealed class WeChatLocalExportCaptureAdapter : IMessageCaptureAdapter
{
    private readonly WeChatLocalExportOptions _options;

    public WeChatLocalExportCaptureAdapter(WeChatLocalExportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.DirectoryPath))
        {
            throw new ArgumentException("Directory path is required.", nameof(options));
        }
    }

    public string Name => $"{_options.Source}.LocalExport";

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

    private static IEnumerable<string> EnumerateExportFiles(string directoryPath)
    {
        return Directory.EnumerateFiles(directoryPath, "*.*")
            .Where(path =>
                Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

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

    private static bool ShouldSkipRecord(string filePath, int recordNumber, JsonlOffset offset)
    {
        return string.Equals(filePath, offset.FilePath, StringComparison.OrdinalIgnoreCase) &&
            recordNumber <= offset.LineNumber;
    }

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

    private static DateTimeOffset ParseUnixTime(long value)
    {
        return value > 9_999_999_999
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
    }

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

    private static string StableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }

    private sealed record FileReadResult(IReadOnlyList<CapturedMessage> Messages, JsonlOffset? NextOffset);
}
