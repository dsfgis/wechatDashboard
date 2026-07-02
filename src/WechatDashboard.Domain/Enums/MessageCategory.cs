namespace WechatDashboard.Domain.Enums;

/// <summary>
/// 消息类别枚举，用于对消息内容进行语义分类。
/// </summary>
public enum MessageCategory
{
    /// <summary>需求：提出新功能或变更。</summary>
    Requirement,
    /// <summary>故障：线上问题或异常。</summary>
    Incident,
    /// <summary>会议：会议通知或纪要。</summary>
    Meeting,
    /// <summary>交付：交付物或进度同步。</summary>
    Delivery,
    /// <summary>提问：需要回复的问题。</summary>
    Question,
    /// <summary>周知：仅供知会，无需操作。</summary>
    FYI,
    /// <summary>未分类：未能命中任何类别。</summary>
    Unclassified
}
