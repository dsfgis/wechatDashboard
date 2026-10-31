namespace WechatDashboard.Application.Common;

/// <summary>
/// 邮件类采集源的统一约定：来源标识、主题清洗与“是否由 Agent 决定建待办”。
/// 邮件与聊天消息的语义差异集中在这里，避免这些判断散落到管线、工厂和界面三处。
/// </summary>
public static class MailSourcePolicy
{
    /// <summary>石化邮采集源标识，与 <c>CaptureSourceDefinition.Source</c> 保存的值一致。</summary>
    public const string SinopecMail = "SinopecMail";

    /// <summary>邮件主题进入标题/会话名之前允许保留的最大长度。</summary>
    public const int SubjectMaxLength = 60;

    /// <summary>判断某个来源是否属于邮件类采集源。</summary>
    public static bool IsMailSource(string? source)
        => string.Equals(source, SinopecMail, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 清理邮件主题：剥离可重复的 Re/Fw/Fwd/答复/转发 前缀与首尾空白，折叠内部空白，
    /// 并按 <see cref="SubjectMaxLength"/> 截断。空主题返回空字符串，由调用方回退到正文。
    /// </summary>
    public static string CleanSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return string.Empty;
        }

        var cleaned = string.Join(" ", subject.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // 前缀可能层层叠加（"Re: Fw: 答复: ..."），循环剥离直到稳定。
        while (true)
        {
            var stripped = StripLeadingPrefix(cleaned);
            if (stripped.Length == cleaned.Length)
            {
                break;
            }

            cleaned = stripped;
        }

        cleaned = cleaned.Trim();
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        return cleaned.Length <= SubjectMaxLength ? cleaned : cleaned[..(SubjectMaxLength - 1)] + "…";
    }

    private static string StripLeadingPrefix(string value)
    {
        ReadOnlySpan<string> prefixes =
        [
            "Re:", "RE:", "re:", "Rr:", "答复:", "回复:",
            "Fw:", "FW:", "Fwd:", "FWD:", "转发:", "转发：",
            "Re：", "RE：", "Fw：", "FW：", "Fwd：", "答复：", "回复："
        ];

        foreach (var prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return value[prefix.Length..].TrimStart();
            }
        }

        return value;
    }
}
