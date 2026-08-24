# 原生 C# 微信本地消息读取验证清单

## 1. 使用规则

- 所有检查初始均为未完成。
- 只有实际执行验证并填写可复核证据后，才能把复选框改为完成。
- 编译成功不能替代真实 DLL、真实微信、无 Python 干净机或发布证据检查。
- Python 结果仅用于迁移期 oracle，不得作为“生产链路已无 Python”的单独证据。
- 任何检查发现需求或设计实质不符时，应停止实施，修订四份文档并重新取得确认。
- 证据不得包含数据库密钥、真实消息正文、未脱敏数据库路径或其他用户隐私。

## 2. 文档就绪检查

### CHECK-001：四份文档顺序和审批边界

- [ ] 状态：待重新确认
- 引用：SDD 流程；TASK-001 前置条件。
- 验证方法：确认 requirements.md、design.md、tasks.md、check_list.md 均存在，按顺序审阅，且没有产品代码/测试/依赖随本轮 Spec 一同修改。
- 预期结果：四份文档齐全；当前变更仅位于 specs/native-csharp-wechat-reader；实施仍未开始。
- 证据：2026-08-22 用户依次确认四份文档后发出“按文档开始实施”；该审批只适用于当时的 REQ-001 至 REQ-012。2026-08-24 新增 REQ-013、AC-030 至 AC-035、D-013 至 D-014 及 CHECK-026 至 CHECK-030，属于实质发布范围修订，旧审批已失效；本轮仅修改四份 SDD 文档，待用户重新确认后才能实施产品代码或部署配置。

### CHECK-002：需求、设计、任务和检查追踪完整性

- [x] 状态：完成
- 引用：REQ-001 至 REQ-013；AC-001 至 AC-035；设计第 12 节；tasks.md 第 4 节。
- 验证方法：运行 ID 唯一性和引用扫描；逐项确认每个 REQ 至少有一个 AC、设计、任务和检查，每个 AC 至少有一个任务和检查。
- 预期结果：无重复/缺失 ID；所有引用目标存在；无未批准范围。
- 证据：实施前 ID 扫描结果为 REQ=12、AC=29、TASK=17、CHECK=25，唯一性与引用检查结果 PASS。2026-08-24 新机发布条件修订后重新扫描得到 REQ=13、AC=35、TASK=17、CHECK=30，任务/检查标题无重复，所有 REQ 均有设计、任务和检查引用，所有 AC 均有任务和检查引用；结果 PASS。

### CHECK-003：事实、假设和未验证项分离

- [x] 状态：完成
- 引用：requirements.md 第 2 节；design.md 第 2、3 节。
- 验证方法：对照当前源码、README、Agent.md 和既有设计，确认“DLL 已由 C# KeyProbe 调用”和“Python 负责数据库 reader”有源代码证据；确认真实机兼容与发布证据仍标为待验证。
- 预期结果：未把计划当成已实现，未把真实机/授权记录当成当前已验证事实。
- 证据：实施前源码确认 `WechatDashboard.KeyProbe/WxKeyNativeLibrary.cs` 已由 C# 动态加载 DLL，旧 `WeChatLocalReaderService` 与命令适配器仍调用 Python；requirements.md 和 design.md 将真实机、发布授权、干净机及签名明确列为待验证。实施后生产 reader 已切到 C#，但这些外部验证仍保持未完成。

### CHECK-004：无关工作区改动保护

- [x] 状态：完成
- 引用：requirements.md 2.1、6；tasks.md 第 1 节。
- 验证方法：实施前记录 git status --short；实施后比较无关文件，逐项确认没有被覆盖、丢弃或格式化。
- 预期结果：用户已有的未提交改动全部保留；本功能改动范围可区分。
- 证据：实施前 `git status --short` 已记录 13 个修改项和 `Views/Reminders` 新目录；实施后复核确认托盘、提醒、Todo 状态、消息页及 `IM消息看板` 等原有修改仍存在，未执行 reset/checkout/批量格式化。与本功能重叠的 MainWindow 和 factory 仅作定点补丁。

## 3. 实现验证检查

### CHECK-005：独立 C# KeyProbe 正常与故障隔离

- [ ] 状态：未完成
- 引用：REQ-001；AC-001、AC-002；D-001；TASK-003、TASK-014。
- 验证方法：运行 KeyProbe 参数、缺失 DLL、缺失导出、非零退出、超时、管道断开测试；经用户授权后运行一次真实微信 Hook，并人为终止 KeyProbe。
- 预期结果：成功时仅命名管道交付 32 字节；失败时 WPF 不崩溃，CleanupHook/进程清理执行，错误脱敏且无 Key。
- 证据：部分完成：KeyProbe 参数拒绝敏感参数、缺失二进制、超时错误映射和 64 位十六进制诊断脱敏测试通过；真实 Hook、缺失导出、主动终止与进程退出码仍待授权验证。

### CHECK-006：逐库密钥派生与必需数据库验证

- [ ] 状态：未完成
- 引用：REQ-002；AC-003、AC-004、AC-005；D-003、D-006；TASK-006。
- 验证方法：运行 session、contact、message 分片的有效/无效主密钥、不同 salt、reserve 64/80、截断页和缺失必需库 fixtures；对照 Python oracle。
- 预期结果：只有三个必需类别均验证成功才 Ready；无效输入报告正确阶段；派生和 HMAC 结果与 oracle 一致。
- 证据：部分完成：真实 SQLCipher 加密的 session/contact/message fixture、错误主密钥、reserve 64/80 HMAC、修改页拒绝均通过；截断页、缺失类别和逐项 Python 双跑尚未形成完整矩阵。

### CHECK-007：源数据库只读与稳定快照

- [ ] 状态：未完成
- 引用：REQ-003；AC-006、AC-007；D-004、D-005；TASK-005、TASK-007。
- 验证方法：对包含 DB/WAL/SHM 的 fixture 创建快照；模拟复制中变化、取消、权限/空间失败；比较源文件哈希、大小和最后写入时间；检查失败临时目录。
- 预期结果：有效快照一致可读；源文件无变化；失败快照未标记完成且不残留不完整工作文件。
- 证据：部分完成：测试创建未 checkpoint 的加密 WAL，快照复制 DB/WAL 并可只读查询 WAL 行；源 DB 大小/时间戳不变；初始化和查询完成后快照目录为空。复制中变化、取消和权限/空间故障仍待补。

### CHECK-008：V4 schema、联系人和跨分片消息读取

- [ ] 状态：未完成
- 引用：REQ-004；AC-008、AC-009；D-007；TASK-008。
- 验证方法：运行 SessionTable、联系人、Name2Id、真实 V4 字段别名、多会话、多 message_N 分片、重复行、缺表/缺列和损坏分片 fixtures。
- 预期结果：消息 ID、chatId、chatName、senderName、sentAt、messageType 与 oracle 一致；重复项去除；未知必需 schema 返回 SchemaUnsupported。
- 证据：部分完成：C# fixture 覆盖 SessionTable、contact、Name2Id、Msg_md5、发送者解析、稳定消息键、zstd 和排序；多分片、真实字段别名、损坏分片和完整逐字段双跑报告仍待补。

### CHECK-009：zstd、XML 和消息摘要兼容性

- [ ] 状态：未完成
- 引用：REQ-004、REQ-007、REQ-008；AC-010、AC-017 至 AC-019；D-007；TASK-009。
- 验证方法：运行普通文本、zstd、图片、视频、表情、文件、链接、位置、系统/撤回 XML fixtures；测试损坏 zstd、超大输出、DTD、外部实体和畸形 XML。
- 预期结果：受支持内容摘要与 oracle 一致；危险输入不访问外部资源、不无限分配、不泄露原始 blob，且产生稳定脱敏诊断。
- 证据：部分完成：普通文本、群发送者、zstd、图片 XML、DTD/外部实体、畸形 XML和损坏 zstd 测试通过；依赖锁定 `ZstdSharp.Port 0.8.8`。视频、表情、文件、链接、位置、系统以及超大解压矩阵仍待补。

### CHECK-010：稳定排序与分页边界

- [ ] 状态：未完成
- 引用：REQ-005；AC-011、AC-012；D-008；TASK-010。
- 验证方法：运行同时间戳不同 ID、乱序分片、跨日/本地时区、空结果、首页、末页、越界页、页大小 1、200 和非法页大小测试；与 oracle 比较。
- 预期结果：最终排序固定为 sentAt、id 降序；总数和切页稳定；页大小被限制到 1 至 200；不存在跨页漂移或重复。
- 证据：部分完成：当天本地时区边界、第二页 offset=50/limit=50、总数 75、sentAt/source key 稳定倒序和限制 1..200 的实现路径已测试；完整首页/末页/越界/同时间戳矩阵仍待补。

### CHECK-011：增量 offset 与事务提交语义

- [ ] 状态：未完成
- 引用：REQ-005、REQ-006；AC-013、AC-015；D-008、D-009；TASK-010、TASK-012。
- 验证方法：运行首次采集、重复轮询、lookback 重叠、新消息追加、Message/Todo 持久化失败和重放测试；读取保存的 adapter offset。
- 预期结果：重复轮询不重复持久化；成功提交后推进 offset；任何批次提交失败均保留旧 offset 并可安全重放。
- 证据：部分完成：原生 adapter high-water、lookback 和通用管线重复采集/事务回滚测试通过；原生批次持久化故障后的 offset 前后值专项测试仍待补。

### CHECK-012：所有产品入口使用同一 C# reader

- [ ] 状态：未完成
- 引用：REQ-006；AC-014、AC-015、AC-016；D-002、D-009；TASK-011、TASK-012、TASK-014。
- 验证方法：分别执行初始化、读取当天消息、采集一次和后台监听；监控子进程；最小化/遮挡微信；验证 MessageCapturePipeline、@我 Todo、关注群和看板刷新。
- 预期结果：四个入口均使用 C# 核心且不启动 python/python3/py；最小化不影响数据库读取；现有管线行为保持。
- 证据：部分完成：代码审计确认当天分页、采集一次和监听均共享 `_readerService.NativeReader`，factory 只创建 `WeChatLocalDatabaseCaptureAdapter`，Release 二进制无 Python 匹配；实际 UI、进程树、最小化和 Todo 结果待真实机授权。

### CHECK-013：阶段诊断区分零消息与失败

- [ ] 状态：未完成
- 引用：REQ-007；AC-017、AC-018；D-010；TASK-002、TASK-011、TASK-014。
- 验证方法：分别制造路径、KeyProbe、密钥验证、快照、数据库打开、schema、内容、查询和持久化失败；再运行合法零消息窗口。
- 预期结果：每个失败都有稳定阶段、错误码、耗时和脱敏摘要；合法零消息仅在所有必需阶段成功时出现；输出不含密钥或真实正文。
- 证据：部分完成：KeyProbe 超时稳定输出 `KeyProbe/KeyTimeout`，64 位十六进制文本被替换，读取服务保留 stage/error code；完整九阶段故障矩阵和合法零消息专项测试仍待补。

### CHECK-014：密钥不落盘与内存零化

- [ ] 状态：未完成
- 引用：REQ-008；AC-019、AC-020；D-003、D-011；TASK-003、TASK-004、TASK-006、TASK-011。
- 验证方法：覆盖成功、失败、取消、重新初始化和退出；检查参数、环境变量、state.json、config、应用 SQLite、日志、临时目录和进程输出；用可测试租约断言 Dispose 后缓冲区清零。
- 预期结果：不存在主/派生密钥文本、all_keys.json 或明文 Key 文件；所有生命周期路径清零；重新初始化前旧租约已销毁。
- 证据：部分完成：产品源码已移除 ToHexString、密钥环境变量、明文 Key 文件和 all_keys 状态；state.json 敏感串检查、租约/派生密钥/临时缓冲区清零路径测试通过；真实进程环境和内存审计待验收。

### CHECK-015：Release staging 不含 Python 或旧 Key 链路

- [ ] 状态：未完成
- 引用：REQ-009；AC-021；D-009、D-012；TASK-015、TASK-017。
- 验证方法：递归扫描 staging 文件名、文本、依赖和启动配置，查找 .py、python、PyInstaller、wechat_local_reader、run-wx-key-probe.ps1、all_keys.json、wx-key-found.txt 和 WECHAT_DASHBOARD_DB_KEY。
- 预期结果：无匹配的运行时/生产调用；如发布说明中出现“Python”仅用于声明“不依赖”，需人工确认不是组件引用。
- 证据：部分完成：Release App/Infrastructure/KeyProbe 输出对 `python|wechat_local_reader|all_keys.json|WECHAT_DASHBOARD_DB_KEY|run-wx-key-probe.ps1|wx-key-found.txt|PYTHONUTF8` 扫描零匹配，文件名拒绝清单零匹配；尚未生成正式 self-contained staging。

### CHECK-016：无预装运行时的干净机启动

- [ ] 状态：未完成
- 引用：REQ-009、REQ-013；AC-022、AC-030；D-011 至 D-013；TASK-015、TASK-016。
- 验证方法：在 Windows x64 干净机确认未安装 Python、PATH 无 Python、未安装 .NET SDK 和 .NET Desktop Runtime；安装正式 self-contained 安装包并以标准用户启动。
- 预期结果：安装与主程序启动成功；进程/模块列表无 Python；应用不依赖开发仓库、SDK 或预装 .NET Runtime。
- 证据：待填写环境证明、安装日志、进程列表和功能记录。

### CHECK-017：离线 C# 与 Python oracle 完全对照

- [x] 状态：完成
- 引用：REQ-010；AC-023；D-005、D-007、D-008、D-012；TASK-001、TASK-013。
- 验证方法：在同一冻结 fixture 上双跑，比较数据库识别、会话/分片数、消息 ID、时间、群名、发送人、内容摘要、类型、排序、总数、分页和 next offset。
- 预期结果：所有字段完全一致；若有批准的错误分类差异，文档中有明确理由且不影响数据结果。
- 证据：`Native WeChat reader matches the frozen Python oracle field by field` 使用同一生成器的加密 C# fixture 和语义相同的明文 oracle mirror，逐项比较 message ID、chatId、chatName、senderName、content、sentAt、messageType、next offset，以及 sessions、matched_tables、shards_scanned、rows_read、query_errors，零差异。oracle 文件哈希见 CHECK-019。

### CHECK-018：真实机隐私安全对照

- [ ] 状态：未完成
- 引用：REQ-010；AC-024；D-012；TASK-014。
- 验证方法：取得用户显式授权后，只比较数据库数、会话数、消息数、时间范围、已知测试消息 ID 和阶段状态；审查采集记录。
- 预期结果：所有获准聚合项一致；证据不含真实消息正文、密钥或未脱敏路径。
- 证据：待填写授权记录标识、聚合对照和隐私审查结果。

### CHECK-019：完整构建、测试和差异卫生

- [x] 状态：完成
- 引用：REQ-010；AC-025；TASK-013、TASK-017。
- 验证方法：依次运行：

  1. python -m unittest discover -v -s tools\wechat-local-reader -p test_wechat_local_reader.py
  2. dotnet build WechatDashboard.sln --configuration Release
  3. dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --configuration Release --no-build
  4. git diff --check
  5. 测试输出敏感串扫描

- 预期结果：迁移期 oracle 全绿；Release 构建 0 错误；全部 .NET 测试通过；无 whitespace 错误和敏感输出。
- 证据：2026-08-22 依次执行 Python oracle，47/47 通过；`dotnet build WechatDashboard.sln --configuration Release --no-restore -m:1`，0 警告、0 错误；自定义 runner 45/45 通过；`git diff --check` 退出码 0（仅 Git 的 CRLF 提示）；64 位十六进制敏感串扫描在 `src tests` 中零匹配。`specs` 中仅有下述两项明确标注的非密钥 oracle 文件 SHA-256：reader `760C418C2CAAE70765D21FC22494FD414237099C3344DC2D325063F97C7BC3AC`，tests `471E258D0DFAA22986A07A92F79BFC1116F2E8E1EDD9EDA90FF4E014F295498B`。

### CHECK-020：安装/用户数据目录和路径兼容

- [ ] 状态：未完成
- 引用：REQ-011、REQ-013；AC-026、AC-027、AC-032；D-004、D-011；TASK-004、TASK-015、TASK-016。
- 验证方法：以标准用户从 `Program Files` 或等价只读目录启动；检查安装目录与 `%LocalAppData%\WechatDashboard` 文件清单；确认主数据库、offset、采集设置、收件箱、reader 状态、快照、临时文件和日志只在用户目录变化；在包含空格/中文的用户路径执行初始化与读取；执行升级、卸载保留数据和缓存重建。
- 预期结果：二进制只在安装目录且安装目录零运行写入；全部可变状态只在用户目录；路径测试通过；卸载不会在未获选择时删除用户消息数据。
- 证据：待填写目录树、路径用例、升级/卸载记录。

### CHECK-021：wx_key.dll 发布证据和二进制一致性

- [ ] 状态：未完成
- 引用：REQ-012；AC-028；D-001、D-012；TASK-015。
- 验证方法：计算 staging 内实际 wx_key.dll SHA-256；核对授权凭据、来源、版本、依赖闭包、安全扫描和签名记录；确认所有记录指向同一哈希。
- 预期结果：证据齐全且哈希完全一致；任何缺失或不一致均阻止 Release。
- 证据：待填写证据索引、文件哈希和复核人。

### CHECK-022：签名与安全软件/EDR 验证

- [ ] 状态：未完成
- 引用：REQ-012；AC-029；TASK-009、TASK-015、TASK-016。
- 验证方法：验证主程序、KeyProbe、受控原生组件和安装包签名；对实际 staging 执行批准的安全扫描/EDR 测试；核对 zstd/SQLite 依赖版本和许可证。
- 预期结果：签名有效且可追溯；安全扫描结果已记录并处置；依赖许可证允许目标分发。
- 证据：待填写签名验证、扫描报告和依赖清单。

### CHECK-023：性能与资源边界

- [ ] 状态：未完成
- 引用：NFR-003、NFR-004；D-004、D-006、D-008；TASK-005、TASK-010、TASK-013。
- 验证方法：在大分片 fixture 上测量首次初始化、未变化增量、单分片变化、取消和连续轮询；记录耗时、峰值内存、快照字节和处理分片数。
- 预期结果：未变化轮询不会无条件重解密全部数据库；并行度有上限；取消能及时终止并释放资源；结果正确性不受优化影响。
- 证据：待填写机器配置、数据规模和性能结果。

### CHECK-024：不支持版本和损坏数据的安全失败

- [ ] 状态：未完成
- 引用：REQ-002、REQ-003、REQ-004、REQ-007；AC-004、AC-007、AC-017、AC-018；D-007、D-010；TASK-007 至 TASK-009。
- 验证方法：输入未知 schema、错误 cipher 参数、损坏页、损坏分片、畸形 XML 和压缩炸弹 fixture。
- 预期结果：返回明确阶段/错误码，不崩溃、不伪装成 0 条成功、不访问网络、不泄露正文或密钥。
- 证据：部分完成：错误主密钥、修改页、DTD/外部实体、畸形 XML、损坏 zstd 和 KeyProbe 超时安全失败均通过；未知 schema、全部损坏分片、错误 cipher 参数和压缩炸弹上限仍待完整矩阵。

### CHECK-025：升级和回滚不依赖隐藏 Python

- [ ] 状态：未完成
- 引用：REQ-009、REQ-011；AC-021、AC-022、AC-027；D-012；TASK-016、TASK-017。
- 验证方法：从上一签名版本升级到 C# reader 版本，验证 state 迁移与缓存重建；随后回滚上一签名版本；监控文件和进程，确认新版不启动隐藏 Python。
- 预期结果：用户消息数据库保留；新 reader 缓存可重建；回滚使用上一安装包而不是 Python fallback；无密钥残留。
- 证据：待填写版本号、迁移/回滚步骤和文件/进程记录。

### CHECK-026：self-contained staging 和安装载荷完整

- [ ] 状态：未完成
- 引用：REQ-009、REQ-011 至 REQ-013；AC-030、AC-031；D-011、D-013；TASK-015、TASK-016。
- 验证方法：用固定命令生成 win-x64 self-contained staging 和安装包；检查 runtimeconfig/发布属性；递归核对主程序、KeyProbe、`wx_key.dll`、SQLCipher、zstd 及全部 win-x64 依赖；安装后再次计算文件哈希并与载荷清单比较。
- 预期结果：不要求预装 .NET；必需文件完整且位于约定目录；staging、安装包清单和安装后文件哈希一致；缺失任何必需组件时构建失败。
- 证据：待填写发布命令、RID/self-contained 证明、目录树、清单路径和哈希比对结果。2026-08-24 默认 App publish 审计已确认缺少 `WechatDashboard.KeyProbe.exe` 与 `wx_key.dll`，因此当前状态不得勾选。

### CHECK-027：安装工程可复现并通过签名/安全门

- [ ] 状态：未完成
- 引用：REQ-012、REQ-013；AC-030、AC-031、AC-035；D-013；TASK-015、TASK-016。
- 验证方法：在固定源码提交、SDK、依赖锁和版本输入下连续构建两次；比较规范化载荷与安装包元数据；验证主程序、KeyProbe、原生组件和安装包签名；执行组织批准的安全软件/EDR 检查。
- 预期结果：构建步骤无需手工复制文件；规范化载荷一致；所有签名和安全记录绑定同一安装包 SHA-256；任何未审计差异阻止发布。
- 证据：待填写安装技术、构建命令、工具版本、两次清单差异、签名记录、EDR 记录和安装包 SHA-256。

### CHECK-028：新机真实微信端到端读取

- [ ] 状态：未完成
- 引用：REQ-001 至 REQ-010、REQ-013；AC-033；D-001、D-014；TASK-014、TASK-016。
- 验证方法：在干净机安装并登录获准测试的兼容微信，确认本地数据库存在；用户显式触发 KeyProbe；执行初始化、当天分页、已知测试消息、最小化采集和第二次增量；分别制造权限不匹配、DLL/客户端不兼容或安全软件阻断。
- 预期结果：已知消息字段进入消息流且无重复；最小化不影响读取；第二次增量为零；失败得到明确脱敏诊断，不伪装成合法零消息。
- 证据：待填写授权记录、微信/Windows 版本、安装包哈希、数据库/会话/消息计数、时间范围、已知测试消息 ID、增量结果和故障诊断，不记录正文、密钥或未脱敏路径。

### CHECK-029：新机真实石化通端到端读取

- [ ] 状态：未完成
- 引用：REQ-007、REQ-013；AC-034；D-014；TASK-016。
- 验证方法：在同一干净机安装并登录获准测试的兼容石化通，确认 `LxMainNew` 已加载 `imcore.dll`；以标准用户和匹配权限执行采集，核对已知测试消息的群名、发送人、正文摘要和时间，再执行第二次增量；分别制造版本布局不兼容和进程访问被拒绝。
- 预期结果：已知消息字段完整进入消息流；第二次增量无重复；版本或权限不兼容得到明确脱敏诊断，不静默返回成功空批次。
- 证据：待填写石化通/Windows 版本、进程架构、安装包哈希、消息字段完整数量、时间范围、已知测试消息 ID、增量结果和故障诊断，不记录正文、密钥或未脱敏路径。

### CHECK-030：安装、升级、卸载和回滚生命周期

- [ ] 状态：未完成
- 引用：REQ-011 至 REQ-013；AC-027、AC-035；D-012、D-013；TASK-015 至 TASK-017。
- 验证方法：执行首次安装、同版本修复、从上一签名版本升级、默认保留用户数据卸载、用户显式选择的完全卸载和回滚；每步核对安装文件、LocalAppData 数据、缓存重建、快捷方式、版本号和签名。
- 预期结果：默认卸载与升级/回滚保留消息主数据库和用户设置；缓存可安全重建；完全卸载只在用户明确选择后删除用户数据；回滚不启用 Python 隐藏路径。
- 证据：待填写版本矩阵、各阶段目录/数据库哈希、卸载选择、修复/升级/回滚日志和最终状态。

## 4. 完成判定

只有 CHECK-001 至 CHECK-030 全部勾选且每项证据可复核，才可以宣称安装包可在新机器读取微信和石化通消息。以下任何单项均不足以判定完成：

- KeyProbe 已能调用 wx_key.dll。
- C# 项目编译成功。
- Python oracle 测试通过。
- 在开发机上读到一条微信消息。
- staging 中看不到 python.exe，但仍包含 .py、PyInstaller 或命令配置。
- 未执行真实机、干净机、签名、安全扫描或授权哈希核对。
- 只把 WPF publish 目录封装成安装包，但没有 KeyProbe、`wx_key.dll`、self-contained 运行时闭包或载荷哈希清单。
- 微信通过但石化通未通过，或石化通通过但微信未通过。
