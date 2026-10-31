using WechatDashboard.Application.Common;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 石化邮 IMAP 采集配置。服务器与端口有内网默认值，账号由用户在设置中填写；
/// 密码不进入该对象，避免配置对象被序列化或打印时泄露凭证。
/// </summary>
public sealed record SinopecMailOptions
{
    /// <summary>采集源标识。</summary>
    public string Source { get; init; } = MailSourcePolicy.SinopecMail;

    /// <summary>
    /// IMAP 服务器地址。以公司邮件客户端中的实际配置为准：<c>amail.sinopec.com</c>，
    /// 而 <c>imap.sinopec.com</c> 是另一台实例，账号域也不同（@pcitc.com）。
    /// </summary>
    public string Host { get; init; } = "amail.sinopec.com";

    /// <summary>IMAPS 端口。服务器只开放 993 隐式 TLS，143 明文端口已被拒绝。</summary>
    public int Port { get; init; } = 993;

    /// <summary>登录账号，为完整邮箱地址，如 shaofeng.dai@pcitc.com。</summary>
    public string Account { get; init; } = string.Empty;

    /// <summary>要读取的邮箱文件夹。</summary>
    public string Folder { get; init; } = "INBOX";

    /// <summary>单轮最多读取的邮件数，限制单次耗时与模型调用量。</summary>
    public int MaxMessagesPerRun { get; init; } = 20;

    /// <summary>是否只读取未读邮件。</summary>
    public bool UnreadOnly { get; init; }

    /// <summary>正文保留的最大字符数，超出部分截断并标记，避免模型请求超限。</summary>
    public int MaxBodyLength { get; init; } = 4000;

    /// <summary>是否具备执行采集所需的最小配置。账号密码由凭据存储单独提供，缺省时读取器会给出明确提示。</summary>
    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(Host) && Port > 0;
}
