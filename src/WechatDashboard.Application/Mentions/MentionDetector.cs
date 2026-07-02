namespace WechatDashboard.Application.Mentions;

/// <summary>
/// @我 检测器：判断一条消息是否 @ 到了当前用户。
/// 通过预先配置的别名列表（如"白驹过隙""戴少峰"）匹配 "@别名" 形式。
/// </summary>
public sealed class MentionDetector
{
    // 去重、去空白后的别名集合（忽略大小写）
    private readonly string[] _aliases;

    /// <summary>
    /// 构造检测器。
    /// </summary>
    /// <param name="aliases">当前用户的所有别名，可为空。</param>
    public MentionDetector(IEnumerable<string> aliases)
    {
        _aliases = aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 判断消息内容是否 @ 到当前用户。
    /// </summary>
    /// <param name="content">消息正文。</param>
    /// <param name="hasWechatMentionHint">是否已携带微信原生的 @ 提示（为 true 时直接命中）。</param>
    /// <returns>是否 @ 到自己。</returns>
    public bool IsMentioned(string content, bool hasWechatMentionHint = false)
    {
        // 优先使用微信原生 @ 提示，避免漏判
        if (hasWechatMentionHint)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        // 兼容 "@我" / "@你" 这类泛指形式
        if (content.Contains("@我", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("@你", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 按配置的别名逐一匹配 "@别名"
        return _aliases.Any(alias => content.Contains("@" + alias, StringComparison.OrdinalIgnoreCase));
    }
}
