using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Domain.Entities;

/// <summary>
/// 消息分类结果，由 <see cref="ProjectClassifier"/> 产出。
/// 描述一条消息被归入哪个项目、属于什么类别、置信度如何。
/// </summary>
/// <param name="MessageId">被分类的消息 ID。</param>
/// <param name="ProjectId">命中的项目 ID，未命中则为空。</param>
/// <param name="ProjectName">命中的项目名称。</param>
/// <param name="Category">消息类别（需求/故障/会议/交付/提问/周知/未分类）。</param>
/// <param name="Confidence">分类置信度，0~1。</param>
/// <param name="Reason">分类理由说明。</param>
/// <param name="Classifier">分类器标识。</param>
public sealed record ClassificationResult(
    long MessageId,
    long? ProjectId,
    string ProjectName,
    MessageCategory Category,
    double Confidence,
    string Reason,
    string Classifier);
