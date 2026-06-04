using System.Globalization;
using System.IO;
using System.Text.Json;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Capture;

public sealed class JsonlDirectoryCaptureAdapter : IMessageCaptureAdapter
{
    private readonly string _platform;
    private readonly string _directoryPath;

    public JsonlDirectoryCaptureAdapter(string platform, string directoryPath)
    {
        _platform = string.IsNullOrWhiteSpace(platform) ? throw new ArgumentException("Platform is required.", nameof(platform)) : platform.Trim();
        _directoryPath = string.IsNullOrWhiteSpace(directoryPath) ? throw new ArgumentException("Directory path is required.", nameof(directoryPath)) : directoryPath;
    }

    public string Name => $"{_platform}.JsonlDirectory";

    public async Task<CaptureBatch> CaptureAsync(CaptureContext context, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directoryPath);

        var offset = JsonlOffset.Parse(context.GetOffset(Name));
        var messages = new List<CapturedMessage>();
        var nextOffset = offset;

        foreach (var filePath in Directory.EnumerateFiles(_directoryPath, "*.jsonl").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var lastWriteTicks = File.GetLastWriteTimeUtc(filePath).Ticks;
            if (lastWriteTicks < offset.LastWriteUtcTicks)
            {
                continue;
            }

            var lineNumber = 0;
            await foreach (var line in ReadLinesAsync(filePath, cancellationToken))
            {
                lineNumber++;
                if (string.Equals(filePath, offset.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    lineNumber <= offset.LineNumber)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                messages.Add(ParseLine(line, filePath, lineNumber));
                nextOffset = new JsonlOffset(lastWriteTicks, filePath, lineNumber);
            }
        }

        return new CaptureBatch(Name, messages, nextOffset.ToString());
    }

    private CapturedMessage ParseLine(string line, string filePath, int lineNumber)
    {
        try
        {
            var record = JsonSerializer.Deserialize<JsonlMessageRecord>(line, JsonOptions)
                ?? throw new InvalidOperationException("JSON record is empty.");
            var platform = string.IsNullOrWhiteSpace(record.Platform) ? _platform : record.Platform.Trim();
            var id = string.IsNullOrWhiteSpace(record.Id) ? $"{Path.GetFileName(filePath)}:{lineNumber}" : record.Id.Trim();
            var chatId = string.IsNullOrWhiteSpace(record.ChatId) ? record.ChatName ?? "unknown-chat" : record.ChatId.Trim();
            var chatName = string.IsNullOrWhiteSpace(record.ChatName) ? chatId : record.ChatName.Trim();
            var senderName = string.IsNullOrWhiteSpace(record.SenderName) ? "未知发送人" : record.SenderName.Trim();
            var content = record.Content?.Trim() ?? "";
            var sentAt = ParseSentAt(record.SentAt);
            var messageType = ParseMessageType(record.MessageType);

            return new CapturedMessage(
                Source: platform,
                SourceMessageKey: $"{platform}:{id}",
                ChatId: chatId,
                ChatName: chatName,
                SenderName: senderName,
                Content: content,
                SentAt: sentAt,
                MessageType: messageType);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse '{filePath}' line {lineNumber}: {ex.Message}", ex);
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

    private static DateTimeOffset ParseSentAt(string? value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.Now;
    }

    private static MessageType ParseMessageType(string? value)
    {
        if (Enum.TryParse<MessageType>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return MessageType.Text;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record JsonlMessageRecord(
        string? Id,
        string? Platform,
        string? ChatId,
        string? ChatName,
        string? SenderName,
        string? Content,
        string? SentAt,
        string? MessageType);
}
