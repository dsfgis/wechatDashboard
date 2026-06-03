using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Domain.Entities;

public sealed record ClassificationResult(
    long MessageId,
    long? ProjectId,
    string ProjectName,
    MessageCategory Category,
    double Confidence,
    string Reason,
    string Classifier);
