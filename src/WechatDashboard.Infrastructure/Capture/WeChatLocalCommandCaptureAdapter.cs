using System.Globalization;
using System.IO;
using System.Text.Json;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 微信本地数据库命令采集适配器：通过外部命令行工具（Python 脚本/exe）
/// 调用微信本地数据库读取器，解析其 JSON 输出为消息批次。
/// 维护上次执行的分阶段诊断信息（stages），便于在 UI 中展示采集流程状态与错误。
/// </summary>
public sealed class WeChatLocalCommandCaptureAdapter : IMessageCaptureAdapter
{
    // 诊断错误信息的最大长度，超出则截断
    private const int MaxDiagnosticErrorLength = 500;

    // 命令行执行选项：可执行文件路径、参数、工作目录等
    private readonly WeChatLocalCommandOptions _options;
    // 外部命令执行器抽象，封装进程启动与输出捕获
    private readonly IExternalCommandRunner _commandRunner;

    // 上次采集的分阶段诊断信息
    private CaptureStageDiagnostics? _lastStages;

    /// <summary>构造适配器：校验可执行文件路径非空。</summary>
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

    /// <summary>适配器名称，固定为 WeChat.LocalDatabase。</summary>
    public string Name => "WeChat.LocalDatabase";

    /// <summary>上次采集的阶段诊断，可能为空（首次调用前或失败后）。</summary>
    public CaptureStageDiagnostics? LastStages => _lastStages;

    /// <summary>
    /// 执行一次采集：构造环境变量（含上次偏移）、运行外部命令、解析 JSON 输出。
    /// 失败时（非零退出码或 JSON 解析异常）记录诊断并抛出异常。
    /// 成功时返回消息批次与新的偏移，并更新阶段诊断。
    /// </summary>
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

    /// <summary>从 JSON 根元素读取 messages 数组并解析为 CapturedMessage 列表。</summary>
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

    /// <summary>按候选字段名读取时间，兼容字符串与 Unix 时间戳（秒/毫秒）。</summary>
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

    /// <summary>按候选字段名读取字符串，兼容字符串/数字。</summary>
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

    /// <summary>按候选字段名查找属性（忽略大小写），找到则输出值并返回 true。</summary>
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

    /// <summary>将消息类型字符串映射为 MessageType 枚举（支持中文名称与枚举名）。</summary>
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

    /// <summary>截断字符串到指定长度。</summary>
    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    /// <summary>从 JSON 根元素读取分阶段诊断：状态、阶段列表（名称/状态/详情）。</summary>
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

    /// <summary>将 JSON 值格式化为阶段详情字符串。</summary>
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

/// <summary>采集分阶段诊断：总体状态、各阶段明细与错误信息。</summary>
public sealed record CaptureStageDiagnostics(
    string Status,
    IReadOnlyList<CaptureStage> Stages,
    string? Error);

/// <summary>单个采集阶段：名称、状态与详情字符串。</summary>
public sealed record CaptureStage(string Name, string Status, string Detail);
