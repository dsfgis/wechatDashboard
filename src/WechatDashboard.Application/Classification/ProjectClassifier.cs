using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Classification;

public sealed class ProjectClassifier
{
    private readonly ProjectRule[] _rules;

    public ProjectClassifier(IEnumerable<ProjectRule> rules)
    {
        _rules = rules.ToArray();
    }

    public ClassificationResult Classify(Message message)
    {
        var winner = _rules
            .Select(rule => new { Rule = rule, Score = MatchScore(rule, message) })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Rule.ProjectName, StringComparer.Ordinal)
            .FirstOrDefault();

        var category = DetectCategory(message.Content);

        if (winner is null)
        {
            return new ClassificationResult(
                message.Id,
                null,
                "未分类",
                category,
                0,
                "No project rule matched.",
                "Rules");
        }

        var confidence = Math.Min(0.99, 0.50 + winner.Score / 200.0);
        return new ClassificationResult(
            message.Id,
            winner.Rule.ProjectId,
            winner.Rule.ProjectName,
            category,
            confidence,
            $"{winner.Rule.RuleType} matched '{winner.Rule.Pattern}'.",
            "Rules");
    }

    private static int MatchScore(ProjectRule rule, Message message)
    {
        if (string.IsNullOrWhiteSpace(rule.Pattern))
        {
            return 0;
        }

        return rule.RuleType switch
        {
            ProjectRuleType.ChatName when message.ChatName.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase) => rule.Weight,
            ProjectRuleType.Keyword when message.Content.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase) => rule.Weight,
            ProjectRuleType.Sender when message.SenderName.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase) => rule.Weight,
            _ => 0
        };
    }

    private static MessageCategory DetectCategory(string content)
    {
        if (ContainsAny(content, "线上", "故障", "阻塞", "宕机", "事故"))
        {
            return MessageCategory.Incident;
        }

        if (ContainsAny(content, "需求", "变更", "改造"))
        {
            return MessageCategory.Requirement;
        }

        if (ContainsAny(content, "会议", "评审", "同步会"))
        {
            return MessageCategory.Meeting;
        }

        if (ContainsAny(content, "上线", "交付", "验收"))
        {
            return MessageCategory.Delivery;
        }

        if (ContainsAny(content, "问题", "确认", "反馈", "请"))
        {
            return MessageCategory.Question;
        }

        if (ContainsAny(content, "通知", "知悉", "同步"))
        {
            return MessageCategory.FYI;
        }

        return MessageCategory.Unclassified;
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }
}
