# 原生 C# 微信读取器实施证据

## 当前结论

- 已完成生产编排切换：`WeChatLocalReaderService`、当天分页、采集一次和后台监听共同使用 `IWeChatLocalDatabaseReader`；生产 factory 只注册 `WeChatLocalDatabaseCaptureAdapter`。
- 已删除会进入 Release 的 Python 命令适配器、Python 解释器发现、外部 JSON 解析、PowerShell Key probe、密钥环境变量和 `all_keys.json` 就绪判断。
- Python reader 仅保留在 `tools/wechat-local-reader` 作为迁移 oracle，不参与产品构建或运行。
- 尚未执行真实微信 Hook、真实消息对照、发布签名/EDR、self-contained staging 和无 Python 干净机验收；这些步骤按文档要求需要单独授权或外部发布环境，不能由离线测试代替。

## 组件与测试证据

| 范围 | 当前证据 | 状态 |
|---|---|---|
| C# DLL 调用隔离 | KeyProbe 使用 `NativeLibrary.Load/GetExport` 调用 `wx_key.dll`，主进程通过 CurrentUserOnly 随机命名管道接收 32 字节租约 | 离线完成，真实 Hook 待验收 |
| SQLCipher V4 | PBKDF2-HMAC-SHA512 256000、首页 HMAC、reserve 64/80、错误 Key、真实 SQLCipher 加密 fixture | 完成 |
| 快照只读 | DB/WAL/SHM 成组复制；源文件大小和时间戳不变；读取后删除稳定快照 | 完成基本路径，故障注入矩阵待补 |
| Schema/内容 | SessionTable、contact、Name2Id、Msg_md5、群发送者、zstd、图片 XML、安全 DTD/畸形 XML、损坏 zstd | 离线 C#/Python 逐字段 fixture 对照完成；真实 V4 对照待补 |
| 查询语义 | 日期边界、总数、分页 offset/limit、稳定倒序、增量 high-water/lookback、管线去重 | 完成基本路径，完整边界矩阵待补 |
| 设置迁移 | `WeChatLocalCommand` 到 `WeChatLocalDatabase` 幂等迁移，保留启用状态，factory 使用共享内存 reader | 完成 |
| 密钥安全 | 不转字符串、不落盘、不放环境变量；租约、派生密钥和临时缓冲区 Dispose/Finally 清零；诊断正则脱敏 | 完成离线验证，进程内存审计待真实机 |

## 可复核命令

~~~powershell
python -m unittest discover -v -s tools\wechat-local-reader -p test_wechat_local_reader.py
dotnet build WechatDashboard.sln --configuration Release --no-restore -m:1
dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --configuration Release --no-build
git diff --check
rg -n --pcre2 "(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])" src tests --glob '!**/bin/**' --glob '!**/obj/**'
~~~

结果：Python 47/47；.NET 45/45；Release 0 警告、0 错误；diff 检查通过；敏感串零匹配。双跑 harness 对消息七个字段、next offset 和五个诊断计数逐项比较，零差异。

## 外部验收门槛

下一步是 TASK-014。执行真实微信 Hook 前必须再次获得用户明确授权，证据只能记录数据库数、会话数、消息数、时间范围、已知测试消息 ID 和阶段状态，不记录真实正文、密钥或未脱敏路径。TASK-015 至 TASK-017 还依赖签名材料、wx_key.dll 授权/哈希、EDR 和干净 Windows 环境。
