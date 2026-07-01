using System.Globalization;
using System.IO;
using System.Text.Json;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Capture;

public sealed class WeChatLocalCommandCaptureAdapter : IMessageCaptureAdapter
{
    private const int MaxDiagnosticErrorLength = 500;

    private readonly WeChatLocalCommandOptions _options;
    private readonly IExternalCommandRunner _commandRunner;

    private CaptureStageDiagnostics? _lastStages;

    public WeChatLocalCommandCaptureAdapter(
        WeChatLocalCommandOptions options,
        IExternalCommandRunner commandRunner)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));

        if (string.IsNullOrWhiteSpace(_options.ExecutablePath))
        {
            throw new ArgumentException("Executable path is required.", nameof(options));
        }
    }

    public string Name => "WeChat.LocalDatabase";

    public CaptureStageDiagnostics? LastStages => _lastStages;

    public async Task<CaptureBatch> CaptureAsync(CaptureContext context, CancellationToken cancellationToken)
    {
        var currentOffset = context.GetOffset(Name) ?? "";
        var environment = new Dictionary<string, string>
        {
            ["WECHAT_DASHBOARD_OFFSET"] = currentOffset,
            ["PYTHONUTF8"] = "1",
            ["PYTHONIOENCODING"] = "utf-8:backslashreplace"
        };

        if (!string.IsNullOrWhiteSpace(_options.TemporaryDirectory))
        {
            Directory.CreateDirectory(_options.TemporaryDirectory);
            environment["TEMP"] = _options.TemporaryDirectory;
            environment["TMP"] = _options.TemporaryDirectory;
        }

        var result = await _commandRunner.RunAsync(
            _options.ExecutablePath,
            _options.Arguments,
            _options.WorkingDirectory,
            environment,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"exit code {result.ExitCode}"
                : Truncate(result.StandardError.Trim(), MaxDiagnosticErrorLength);
            _lastStages = new CaptureStageDiagnostics(
                Status: "failed",
                Stages: Array.Empty<CaptureStage>(),
                Error: detail);
            throw new InvalidOperationException($"WeChat local database reader failed: {detail}");
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var messages = ReadMessages(root);
            var nextOffset = ReadString(root, "nextOffset", "next_offset") ?? currentOffset;
            _lastStages = ReadStageDiagnostics(root);
            return new CaptureBatch(Name, messages, nextOffset);
        }
        catch (JsonException ex)
        {
            _lastStages = new CaptureStageDiagnostics(
                Status: "invalid_json",
                Stages: Array.Empty<CaptureStage>(),
                Error: Truncate(ex.Message, MaxDiagnosticErrorLength));
            throw new InvalidOperationException("WeChat local database reader returned invalid JSON.", ex);
        }
    }

    private static IReadOnlyList<CapturedMessage> ReadMessages(JsonElement root)
    {
        if (!TryGetProperty(root, out var messagesElement, "messages") ||
            messagesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CapturedMessage>();
        }

        var messages = new List<CapturedMessage>();
        foreach (var element in messagesElement.EnumerateArray())
        {
            var id = ReadString(element, "id", "msgId", "messageId", "localId")
                ?? throw new InvalidOperationException("WeChat local database message is missing an id.");
            var chatId = ReadString(element, "chatId", "username", "talker") ?? "unknown-chat";
            var chatName = ReadString(element, "chatName", "chat", "roomName") ?? chatId;
            var senderName = ReadString(element, "senderName", "sender") ?? "未知发送人";
            var content = ReadString(element, "content", "message", "last_message") ?? "";
            var sentAt = ReadDateTimeOffset(element, "sentAt", "timestamp", "createTime") ?? DateTimeOffset.Now;
            var messageType = ParseMessageType(ReadString(element, "messageType", "msgType", "type"));

            messages.Add(new CapturedMessage(
                Source: "WeChat",
                SourceMessageKey: $"WeChat:local:{id}",
                ChatId: chatId,
                ChatName: chatName,
                SenderName: senderName,
                Content: content,
                SentAt: sentAt,
                MessageType: messageType));
        }

        return messages;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, out var value, name))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unixTime))
            {
                return unixTime > 9_999_999_999
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unixTime)
                    : DateTimeOffset.FromUnixTimeSeconds(unixTime);
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, out var value, name))
            {
                continue;
            }

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static MessageType ParseMessageType(string? value)
    {
        if (Enum.TryParse<MessageType>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return value switch
        {
            "图片" => MessageType.Image,
            "链接/文件" or "文件" or "语音" or "视频" => MessageType.File,
            "链接" => MessageType.Link,
            "系统" or "撤回" => MessageType.System,
            _ => MessageType.Text
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static CaptureStageDiagnostics ReadStageDiagnostics(JsonElement root)
    {
        var status = ReadString(root, "status") ?? "ok";
        var stages = new List<CaptureStage>();

        if (root.TryGetProperty("stages", out var stagesElement) &&
            stagesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var stageElement in stagesElement.EnumerateArray())
            {
                var stageName = ReadString(stageElement, "stage") ?? "unknown";
                var stageStatus = ReadString(stageElement, "status") ?? "ok";
                var detail = stageElement.ValueKind == JsonValueKind.Object
                    ? string.Join(", ", stageElement.EnumerateObject()
                        .Where(p => p.Name is not "stage" and not "status")
                        .Select(p => $"{p.Name}={FormatStageValue(p.Value)}"))
                    : "";
                stages.Add(new CaptureStage(stageName, stageStatus, detail));
            }
        }

        return new CaptureStageDiagnostics(status, stages, Error: null);
    }

    private static string FormatStageValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => value.GetRawText()
        };
    }
}

public sealed record CaptureStageDiagnostics(
    string Status,
    IReadOnlyList<CaptureStage> Stages,
    string? Error);

public sealed record CaptureStage(string Name, string Status, string Detail);
