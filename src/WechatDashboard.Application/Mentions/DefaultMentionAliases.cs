namespace WechatDashboard.Application.Mentions;

/// <summary>
/// 默认 @我 别名集合（兜底用）。
/// 当数据库未配置别名时使用，包含"白驹过隙""戴少峰"。
/// 实际运行时优先使用用户在界面配置的别名。
/// </summary>
public static class DefaultMentionAliases
{
    /// <summary>默认别名列表。</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        "白驹过隙",
        "戴少峰"
    };
}
