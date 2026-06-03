using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Urgency;

public sealed class UrgencyRanker
{
    private readonly HashSet<string> _priorityContacts;
    private readonly HashSet<long> _priorityProjectIds;

    public UrgencyRanker(IEnumerable<string>? priorityContacts = null, IEnumerable<long>? priorityProjectIds = null)
    {
        _priorityContacts = new HashSet<string>(priorityContacts ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _priorityProjectIds = new HashSet<long>(priorityProjectIds ?? Array.Empty<long>());
    }

    public UrgencyScore Calculate(Message message, bool isMentionMe, ClassificationResult classification)
    {
        var score = 0;
        var reasons = new List<string>();

        if (isMentionMe)
        {
            score += 30;
            reasons.Add("@我");
        }

        if (ContainsAny(message.Content, "紧急", "马上", "立即", "尽快"))
        {
            score += 25;
            reasons.Add("紧急词");
        }

        if (ContainsAny(message.Content, "今天", "下班前", "明早", "上午", "下午"))
        {
            score += 15;
            reasons.Add("明确时间");
        }

        if (classification.Category == MessageCategory.Incident)
        {
            score += 20;
            reasons.Add("线上故障");
        }

        if (_priorityContacts.Contains(message.SenderName))
        {
            score += 10;
            reasons.Add("重点联系人");
        }

        if (classification.ProjectId.HasValue && _priorityProjectIds.Contains(classification.ProjectId.Value))
        {
            score += 10;
            reasons.Add("重点项目");
        }

        var boundedScore = Math.Clamp(score, 0, 100);
        return new UrgencyScore(message.Id, boundedScore, ToPriority(boundedScore), string.Join("; ", reasons));
    }

    private static PriorityLevel ToPriority(int score)
    {
        if (score >= 85)
        {
            return PriorityLevel.P0;
        }

        if (score >= 65)
        {
            return PriorityLevel.P1;
        }

        if (score >= 40)
        {
            return PriorityLevel.P2;
        }

        return PriorityLevel.P3;
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }
}
