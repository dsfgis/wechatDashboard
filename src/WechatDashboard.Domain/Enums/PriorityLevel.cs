namespace WechatDashboard.Domain.Enums;

/// <summary>
/// 优先级枚举，P0 最高、P3 最低。
/// </summary>
public enum PriorityLevel
{
    /// <summary>P0：最高优先级，需立即处理（线上故障等）。</summary>
    P0,
    /// <summary>P1：高优先级，需尽快处理。</summary>
    P1,
    /// <summary>P2：中优先级，按计划处理。</summary>
    P2,
    /// <summary>P3：低优先级，有空再处理。</summary>
    P3
}
