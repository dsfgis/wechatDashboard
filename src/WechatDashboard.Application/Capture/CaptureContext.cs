namespace WechatDashboard.Application.Capture;

public sealed record CaptureContext(IReadOnlyDictionary<string, string> Offsets)
{
    public string? GetOffset(string adapterName)
    {
        return Offsets.TryGetValue(adapterName, out var offset) ? offset : null;
    }
}
