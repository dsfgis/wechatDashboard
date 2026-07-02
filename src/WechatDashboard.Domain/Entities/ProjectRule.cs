using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Domain.Entities;

/// <summary>
/// 项目分类规则，用于将消息归类到具体项目。
/// 例如：群名包含"CRM项目群" => 归入"CRM升级"项目。
/// </summary>
/// <param name="ProjectId">规则归属的项目 ID。</param>
/// <param name="ProjectName">项目名称（显示用）。</param>
/// <param name="RuleType">规则类型（按群名/按关键词/按发送人）。</param>
/// <param name="Pattern">匹配模式字符串。</param>
/// <param name="Weight">规则权重，权重越高优先级越高。</param>
public sealed record ProjectRule(
    long ProjectId,
    string ProjectName,
    ProjectRuleType RuleType,
    string Pattern,
    int Weight);
