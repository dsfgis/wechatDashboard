namespace WechatDashboard.Domain.Enums;

/// <summary>
/// 项目分类规则类型枚举。
/// 决定 <see cref="ProjectRule.Pattern"/> 如何与消息匹配。
/// </summary>
public enum ProjectRuleType
{
    /// <summary>按群聊名称匹配。</summary>
    ChatName,
    /// <summary>按消息内容关键词匹配。</summary>
    Keyword,
    /// <summary>按发送人名称匹配。</summary>
    Sender
}
