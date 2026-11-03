namespace WechatDashboard.Domain.Entities;

/// <summary>
/// 项目成员：在“项目设置”中维护，用于按发送人归集每周工作内容。
/// 别名用于匹配不同来源的昵称（微信昵称、石化通显示名），邮箱仅用于匹配成员直接发来的石化邮，不用于自动发信。
/// </summary>
/// <param name="Id">自增主键。</param>
/// <param name="ProjectId">所属关注项目 Id。</param>
/// <param name="DisplayName">成员姓名，项目内唯一。</param>
/// <param name="Alias">昵称或别名，可为空；多条时用逗号分隔。</param>
/// <param name="Email">邮箱地址，可为空；仅用于匹配成员直接发来的石化邮。</param>
/// <param name="IsReporting">是否参与每周周报。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="WeChatUserId">微信稳定用户 ID（wxid），仅用于精确匹配私聊发送人。</param>
public sealed record ProjectMember(
    long Id,
    long ProjectId,
    string DisplayName,
    string? Alias,
    string? Email,
    bool IsReporting,
    DateTimeOffset CreatedAt,
    string? WeChatUserId = null);
