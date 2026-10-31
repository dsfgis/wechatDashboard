namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 邮件采集游标的编解码。格式为 <c>uidValidity:nextUid</c>：
/// <list type="bullet">
/// <item>uidValidity 由服务器分配，变化即表示 UID 空间重建，旧 UID 全部作废；</item>
/// <item>nextUid 是下一次读取的起始 UID（本次最大 UID + 1），IMAP UID 在同一 UIDVALIDITY 下单调递增。</item>
/// </list>
/// 任何解析失败都退化为“从头读取”，保证游标损坏不会导致永久漏读。
/// </summary>
internal static class MailCursor
{
    public static (long? UidValidity, uint? NextUid) Parse(string? offset)
    {
        if (string.IsNullOrWhiteSpace(offset))
        {
            return (null, null);
        }

        var parts = offset.Split(':', 2);
        if (parts.Length != 2)
        {
            return (null, null);
        }

        long? uidValidity = long.TryParse(parts[0], out var validity) && validity > 0 ? validity : null;
        uint? nextUid = uint.TryParse(parts[1], out var uid) && uid > 0 ? uid : null;
        return (uidValidity, nextUid);
    }

    public static string Format(long uidValidity, uint nextUid)
        => $"{uidValidity}:{nextUid}";
}
