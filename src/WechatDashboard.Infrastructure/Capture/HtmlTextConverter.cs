using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 极简 HTML 转纯文本：保留段落与换行语义、丢弃脚本与样式、解码常见实体。
/// 目标只是让邮件正文可读、可送入模型，不追求浏览器级渲染等价。
/// </summary>
internal static partial class HtmlTextConverter
{
    private const int MaxLength = 20_000;

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = html;
        // 脚本与样式内容对正文无语义，先整段移除。
        text = ScriptOrStyleRegex().Replace(text, string.Empty);
        text = CommentRegex().Replace(text, string.Empty);
        // 块级边界转换为换行，行内标签直接剔除，避免把相邻单词粘连。
        text = BlockBreakRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);

        var builder = new StringBuilder(text.Length);
        var blankLine = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Replace('\u00A0', ' ').Trim();
            if (line.Length == 0)
            {
                if (blankLine || builder.Length == 0)
                {
                    continue;
                }

                blankLine = true;
                builder.Append('\n');
                continue;
            }

            blankLine = false;
            builder.Append(line).Append('\n');
        }

        var result = builder.ToString().TrimEnd();
        return result.Length <= MaxLength ? result : result[..MaxLength] + "\n…（HTML 正文已截断）";
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyleRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"<\s*(br|/p|/div|/tr|/li|/h[1-6]|p|div|tr|li|h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();
}
