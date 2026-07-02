using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Domain.Entities;

/// <summary>
/// 紧急度评分结果，由 <see cref="UrgencyRanker"/> 计算得出。
/// 综合考虑是否 @我、紧急词、截止时间、重点联系人、重点项目等因素。
/// </summary>
/// <param name="MessageId">关联的消息 ID。</param>
/// <param name="Score">紧急度分数，越高越紧急。</param>
/// <param name="Priority">由分数映射出的优先级（P0~P3）。</param>
/// <param name="Reason">评分理由说明，便于回溯。</param>
public sealed record UrgencyScore(
    long MessageId,
    int Score,
    PriorityLevel Priority,
    string Reason);
