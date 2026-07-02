using System.Text;
using System.Text.RegularExpressions;

namespace WechatDashboard.Application.Mentions;

/// <summary>
/// 消息内容高亮处理工具：将消息文本中的 @别名 片段标记为高亮，同时识别链接。
/// 用于界面渲染时突出显示关注的 @ 提及并展示可点击的链接。
/// </summary>
public static class MessageHighlighter
{
    private static readonly Regex UrlRegex = new(
        @"https?://[A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 将消息文本转换为带高亮标记的富文本片段序列。
    /// 每个片段包含文本内容、类型标记。
    /// </summary>
    /// <param name="content">原始消息文本。</param>
    /// <param name="aliases">需要高亮的别名列表（不含前导 @）。</param>
    /// <returns>文本片段序列，每个片段标记类型。</returns>
    public static IEnumerable<TextSegment> HighlightMentions(string content, IReadOnlyList<string> aliases)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        var segments = new List<(int Start, int Length, SegmentType Type)>();

        // 1. 先匹配链接
        foreach (Match urlMatch in UrlRegex.Matches(content))
        {
            segments.Add((urlMatch.Index, urlMatch.Length, SegmentType.Link));
        }

        // 2. 再匹配 @别名
        if (aliases != null && aliases.Count > 0)
        {
            var aliasPatterns = aliases
                .Select(Regex.Escape)
                .ToArray();
            var pattern = @"@(" + string.Join("|", aliasPatterns) + @")(?=\s|$|，|。|！|？|,|\.|\uD83D|\uD83C|【|】|（|）|\(|\))";
            var aliasRegex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

            foreach (Match aliasMatch in aliasRegex.Matches(content))
            {
                // 检查是否与已有片段重叠
                var overlaps = segments.Any(s =>
                    aliasMatch.Index < s.Start + s.Length &&
                    aliasMatch.Index + aliasMatch.Length > s.Start);

                if (!overlaps)
                {
                    segments.Add((aliasMatch.Index, aliasMatch.Length, SegmentType.Mention));
                }
            }
        }

        // 3. 按起始位置排序
        segments = segments.OrderBy(s => s.Start).ToList();

        // 4. 生成片段
        var lastIndex = 0;
        foreach (var (start, length, type) in segments)
        {
            // 普通文本
            if (start > lastIndex)
            {
                yield return new TextSegment(content.Substring(lastIndex, start - lastIndex), SegmentType.Normal, null);
            }

            var text = content.Substring(start, length);
            yield return new TextSegment(
                text,
                type,
                type == SegmentType.Link ? text : null);
            lastIndex = start + length;
        }

        // 剩余的普通文本
        if (lastIndex < content.Length)
        {
            yield return new TextSegment(content.Substring(lastIndex), SegmentType.Normal, null);
        }
    }

    /// <summary>
    /// 文本片段类型。
    /// </summary>
    public enum SegmentType
    {
        Normal,
        Mention,
        Link
    }

    /// <summary>
    /// 文本片段：包含文本内容、类型和链接地址。
    /// </summary>
    public sealed record TextSegment(
        /// <summary>文本内容。</summary>
        string Text,
        /// <summary>片段类型。</summary>
        SegmentType Type,
        /// <summary>链接地址（仅 Link 类型有值）。</summary>
        string? LinkUrl);
}
