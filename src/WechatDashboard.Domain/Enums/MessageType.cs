namespace WechatDashboard.Domain.Enums;

/// <summary>
/// 消息类型枚举。
/// </summary>
public enum MessageType
{
    /// <summary>文本消息。</summary>
    Text,
    /// <summary>图片消息。</summary>
    Image,
    /// <summary>文件消息。</summary>
    File,
    /// <summary>链接消息。</summary>
    Link,
    /// <summary>系统消息（如撤回、入群等）。</summary>
    System
}
