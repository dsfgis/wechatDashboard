namespace WechatDashboard.Infrastructure.Capture;

internal sealed record JsonlOffset(long LastWriteUtcTicks, string FilePath, int LineNumber)
{
    public static JsonlOffset Empty { get; } = new(0, "", 0);

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

    public override string ToString()
    {
        return $"{LastWriteUtcTicks}|{FilePath}|{LineNumber}";
    }
}
