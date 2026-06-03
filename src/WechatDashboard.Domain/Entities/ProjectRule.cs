using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Domain.Entities;

public sealed record ProjectRule(
    long ProjectId,
    string ProjectName,
    ProjectRuleType RuleType,
    string Pattern,
    int Weight);
