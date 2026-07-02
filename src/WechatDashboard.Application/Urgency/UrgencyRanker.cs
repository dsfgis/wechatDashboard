using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Urgency;

/// <summary>
/// 紧急度评分器：综合 @我、紧急词、截止时间、线上故障、重点联系人、重点项目等因素，
/// 计算消息的紧急度分数（0~100）并映射为优先级 P0~P3。
/// </summary>
public sealed class UrgencyRanker
{
    // 重点联系人集合（命中加分）
    private readonly HashSet<string> _priorityContacts;
    // 重点项目 ID 集合（命中加分）
    private readonly HashSet<long> _priorityProjectIds;

    /// <param name="priorityContacts">重点联系人名单。</param>
    /// <param name="priorityProjectIds">重点项目 ID 列表。</param>
    public UrgencyRanker(IEnumerable<string>? priorityContacts = null, IEnumerable<long>? priorityProjectIds = null)
    {
        _priorityContacts = new HashSet<string>(priorityContacts ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _priorityProjectIds = new HashSet<long>(priorityProjectIds ?? Array.Empty<long>());
    }

    /// <summary>
    /// 计算单条消息的紧急度。
    /// </summary>
    /// <param name="message">消息实体。</param>
    /// <param name="isMentionMe">是否 @ 到自己。</param>
    /// <param name="classification">该消息的分类结果。</param>
    /// <returns>紧急度评分结果。</returns>
    public UrgencyScore Calculate(Message message, bool isMentionMe, ClassificationResult classification)
    {
        var score = 0;
        var reasons = new List<string>();

        // @我 加 30 分（最高权重）
        if (isMentionMe)
        {
            score += 30;
            reasons.Add("@我");
        }

        // 紧急词加 25 分
        if (ContainsAny(message.Content, "紧急", "马上", "立即", "尽快"))
        {
            score += 25;
            reasons.Add("紧急词");
        }

        // 明确时间约束加 15 分
        if (ContainsAny(message.Content, "今天", "下班前", "明早", "上午", "下午"))
        {
            score += 15;
            reasons.Add("明确时间");
        }

        // 线上故障类加 20 分
        if (classification.Category == MessageCategory.Incident)
        {
            score += 20;
            reasons.Add("线上故障");
        }

        // 重点联系人加 10 分
        if (_priorityContacts.Contains(message.SenderName))
        {
            score += 10;
            reasons.Add("重点联系人");
        }

        // 重点项目加 10 分
        if (classification.ProjectId.HasValue && _priorityProjectIds.Contains(classification.ProjectId.Value))
        {
            score += 10;
            reasons.Add("重点项目");
        }

        // 限定到 0~100 区间并映射优先级
        var boundedScore = Math.Clamp(score, 0, 100);
        return new UrgencyScore(message.Id, boundedScore, ToPriority(boundedScore), string.Join("; ", reasons));
    }

    /// <summary>
    /// 将分数映射为优先级：≥85 P0，≥65 P1，≥40 P2，其余 P3。
    /// </summary>
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

    /// <summary>判断 value 是否包含候选词中的任意一个。</summary>
    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }
}
