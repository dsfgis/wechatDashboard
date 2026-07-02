using System.Globalization;
using System.IO;
using System.Text.Json;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// JSONL 目录采集适配器：从指定目录下所有 *.jsonl 文件增量读取消息。
/// 增量进度通过 JsonlOffset（文件最后修改时间+行号）记录，避免重复读取。
/// </summary>
public sealed class JsonlDirectoryCaptureAdapter : IMessageCaptureAdapter
{
    private readonly string _platform;       // 平台标识，如 WeChat
    private readonly string _directoryPath;  // JSONL 文件所在目录

    public JsonlDirectoryCaptureAdapter(string platform, string directoryPath)
    {
        _platform = string.IsNullOrWhiteSpace(platform) ? throw new ArgumentException("Platform is required.", nameof(platform)) : platform.Trim();
        _directoryPath = string.IsNullOrWhiteSpace(directoryPath) ? throw new ArgumentException("Directory path is required.", nameof(directoryPath)) : directoryPath;
    }

    /// <summary>适配器名称，格式为 {平台}.JsonlDirectory。</summary>
    public string Name => $"{_platform}.JsonlDirectory";

    /// <summary>执行一次增量采集。</summary>
    public async Task<CaptureBatch> CaptureAsync(CaptureContext context, CancellationToken cancellationToken)
    {
        // 确保目录存在
        Directory.CreateDirectory(_directoryPath);

        // 解析上次偏移量
        var offset = JsonlOffset.Parse(context.GetOffset(Name));
        var messages = new List<CapturedMessage>();
        var nextOffset = offset;

        // 按文件名排序逐个处理，保证顺序稳定
        foreach (var filePath in Directory.EnumerateFiles(_directoryPath, "*.jsonl").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var lastWriteTicks = File.GetLastWriteTimeUtc(filePath).Ticks;
            // 文件未修改过则跳过
            if (lastWriteTicks < offset.LastWriteUtcTicks)
            {
                continue;
            }

            var lineNumber = 0;
            await foreach (var line in ReadLinesAsync(filePath, cancellationToken))
            {
                lineNumber++;
                // 同一文件内跳过已读行
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
                // 更新偏移量到当前文件当前行
                nextOffset = new JsonlOffset(lastWriteTicks, filePath, lineNumber);
            }
        }

        return new CaptureBatch(Name, messages, nextOffset.ToString());
    }

    /// <summary>解析单行 JSON 为 CapturedMessage，解析失败抛出带行号的异常。</summary>
    private CapturedMessage ParseLine(string line, string filePath, int lineNumber)
    {
        try
        {
            var record = JsonSerializer.Deserialize<JsonlMessageRecord>(line, JsonOptions)
                ?? throw new InvalidOperationException("JSON record is empty.");
            // 字段缺失时给出合理默认值
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

    /// <summary>以共享读方式逐行读取文件，避免占用写端。</summary>
    private static async IAsyncEnumerable<string> ReadLinesAsync(
        string filePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // FileShare.ReadWrite 允许其他进程同时写
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
        }
    }

    /// <summary>解析发送时间，失败回退为当前时间。</summary>
    private static DateTimeOffset ParseSentAt(string? value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.Now;
    }

    /// <summary>解析消息类型，失败回退为 Text。</summary>
    private static MessageType ParseMessageType(string? value)
    {
        if (Enum.TryParse<MessageType>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return MessageType.Text;
    }

    // JSON 反序列化选项：属性名忽略大小写
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>JSONL 单条记录的弱类型映射。</summary>
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
