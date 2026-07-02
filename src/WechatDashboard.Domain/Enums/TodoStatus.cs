namespace WechatDashboard.Domain.Enums;

/// <summary>
/// 待办状态枚举，描述待办事项的生命周期。
/// </summary>
public enum TodoStatus
{
    /// <summary>待办理：尚未开始处理。</summary>
    Pending,
    /// <summary>进行中：正在处理。</summary>
    InProgress,
    /// <summary>等待：阻塞在外部依赖上。</summary>
    Waiting,
    /// <summary>完成：已处理完毕。</summary>
    Done,
    /// <summary>忽略：判定无需处理。</summary>
    Ignored
}
