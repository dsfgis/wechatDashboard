namespace WechatDashboard.Domain.Enums;

/// <summary>项目记忆事项类别，与简报中的进展、风险和行动条目对应。</summary>
public enum ProjectMemoryCategory
{
    /// <summary>已经发生的进展、决定或交付。</summary>
    Progress = 0,

    /// <summary>故障、延期、争议或其他风险。</summary>
    Risk = 1,

    /// <summary>仍需跟进、确认或执行的行动。</summary>
    Action = 2
}

/// <summary>项目记忆事项状态，独立于待办状态生命周期。</summary>
public enum ProjectMemoryStatus
{
    /// <summary>已识别但尚无明确推进证据。</summary>
    Open = 0,

    /// <summary>正在推进。</summary>
    InProgress = 1,

    /// <summary>被问题或外部条件阻断。</summary>
    Blocked = 2,

    /// <summary>主动暂缓，后续仍可能恢复。</summary>
    Shelved = 3,

    /// <summary>已有交付或验收证据。</summary>
    Completed = 4,

    /// <summary>已取消或不再需要，不能与完成混用。</summary>
    Discarded = 5
}

/// <summary>一次状态变化由哪类操作产生。</summary>
public enum ProjectMemoryChangeSource
{
    /// <summary>由用户确认的简报建议产生。</summary>
    Briefing = 0,

    /// <summary>由用户人工修正产生。</summary>
    Manual = 1
}
