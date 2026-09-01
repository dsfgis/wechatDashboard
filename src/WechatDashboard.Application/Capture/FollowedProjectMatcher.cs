using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

/// <summary>复用现有项目关键字规则，为消息返回唯一关注项目。</summary>
public static class FollowedProjectMatcher
{
    public static FollowedProjectKeyword? Match(
        Message message,
        IEnumerable<FollowedProjectKeyword> keywords)
    {
        return keywords
            .Where(keyword => message.ChatName.Contains(keyword.Keyword, StringComparison.OrdinalIgnoreCase)
                || message.Content.Contains(keyword.Keyword, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(keyword => keyword.Keyword.Length)
            .ThenBy(keyword => keyword.ProjectName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
