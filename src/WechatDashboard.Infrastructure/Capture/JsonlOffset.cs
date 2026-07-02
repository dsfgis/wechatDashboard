namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// JSONL 采集偏移量：记录上次读取到的文件、最后修改时间戳与行号，用于增量续读。
/// 序列化格式为 "ticks|filePath|lineNumber"。
/// </summary>
internal sealed record JsonlOffset(long LastWriteUtcTicks, string FilePath, int LineNumber)
{
    /// <summary>空偏移量（从头开始）。</summary>
    public static JsonlOffset Empty { get; } = new(0, "", 0);

    /// <summary>解析字符串形式的偏移量，格式非法时返回 Empty。</summary>
    public static JsonlOffset Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Empty;
        }

        var parts = value.Split('|', 3);
        if (parts.Length != 3 ||
            !long.TryParse(parts[0], out var ticks) ||
            !int.TryParse(parts[2], out var lineNumber))
        {
            return Empty;
        }

        return new JsonlOffset(ticks, parts[1], lineNumber);
    }

    /// <summary>序列化为字符串。</summary>
    public override string ToString()
    {
        return $"{LastWriteUtcTicks}|{FilePath}|{LineNumber}";
    }
}
