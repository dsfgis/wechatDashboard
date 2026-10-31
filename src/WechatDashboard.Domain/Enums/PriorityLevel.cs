namespace WechatDashboard.Domain.Enums;

/// <summary>
/// 优先级枚举，P0 最高、值越大优先级越低。
/// 实际可用层数由“优先级设置”决定（默认 4 级 P0–P3，可配置 2–6 级），
/// 枚举保留 P0–P5 的完整取值，便于用户随时增减层数而不丢失既有数据。
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
    P3,
    /// <summary>P4：自定义的第 5 级，仅在层数配置为 5 级及以上时可用。</summary>
    P4,
    /// <summary>P5：自定义的第 6 级，仅在层数配置为 6 级时可用。</summary>
    P5
}
