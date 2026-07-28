namespace WechatDashboard.Application.Capture;

/// <summary>
/// 采集源类型枚举，对应不同适配器实现。
/// </summary>
public enum CaptureSourceKind
{
    /// <summary>JSONL 目录采集（通用导入入口）。</summary>
    JsonlDirectory,
    /// <summary>微信本地导出文件采集。</summary>
    WeChatLocalExport,
    /// <summary>微信本地命令采集（调用 wechat-local-reader）。</summary>
    WeChatLocalCommand,
    /// <summary>石化通本地加密数据库采集（只读进程密钥与数据库快照）。</summary>
    ShihuatongLocalDatabase,
    /// <summary>微信可见窗口文本采集（UIA + OCR）。</summary>
    WindowText,
    /// <summary>Windows 通知监听（预留）。</summary>
    WindowsNotification,
    /// <summary>HTTP API 采集（预留）。</summary>
    Api
}
