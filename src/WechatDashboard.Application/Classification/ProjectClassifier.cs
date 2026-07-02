using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Classification;

/// <summary>
/// 项目分类器：基于规则将消息归入具体项目，并推断消息类别。
/// 规则按权重排序，得分最高者胜出；类别通过关键词启发式判断。
/// </summary>
public sealed class ProjectClassifier
{
    // 已加载的项目分类规则（按权重计算匹配分）
    private readonly ProjectRule[] _rules;

    /// <param name="rules">项目分类规则集合。</param>
    public ProjectClassifier(IEnumerable<ProjectRule> rules)
    {
        _rules = rules.ToArray();
    }

    /// <summary>
    /// 对单条消息进行分类。
    /// </summary>
    /// <param name="message">待分类的消息。</param>
    /// <returns>分类结果（命中项目、类别、置信度、理由）。</returns>
    public ClassificationResult Classify(Message message)
    {
        // 计算每条规则的匹配分，取最高者
        var winner = _rules
            .Select(rule => new { Rule = rule, Score = MatchScore(rule, message) })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Rule.ProjectName, StringComparer.Ordinal)
            .FirstOrDefault();

        // 同时推断消息类别（需求/故障/会议等）
        var category = DetectCategory(message.Content);

        // 未命中任何项目规则
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

        // 置信度 = 0.5 基础分 + 权重/200，上限 0.99
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

    /// <summary>
    /// 计算单条规则对消息的匹配分（命中返回权重，否则 0）。
    /// </summary>
    private static int MatchScore(ProjectRule rule, Message message)
    {
        if (string.IsNullOrWhiteSpace(rule.Pattern))
        {
            return 0;
        }

        // 按规则类型在不同字段上做包含匹配
        return rule.RuleType switch
        {
            ProjectRuleType.ChatName when message.ChatName.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase) => rule.Weight,
            ProjectRuleType.Keyword when message.Content.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase) => rule.Weight,
            ProjectRuleType.Sender when message.SenderName.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase) => rule.Weight,
            _ => 0
        };
    }

    /// <summary>
    /// 启发式推断消息类别，按关键词优先级返回。
    /// </summary>
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

    /// <summary>判断 value 是否包含候选词中的任意一个。</summary>
    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }
}
