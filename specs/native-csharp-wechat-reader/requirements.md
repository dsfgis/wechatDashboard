# 原生 C# 微信本地消息读取需求

## 1. 背景与目标

当前系统已经通过独立 x64 C# 进程 WechatDashboard.KeyProbe.exe 动态加载 wx_key.dll，并通过当前用户限定的随机命名管道向 WPF 侧交付微信数据库主密钥。wx_key.dll 并不是由 Python 调用。

当前仍依赖 Python 的部分是 tools/wechat-local-reader/wechat_local_reader.py：它负责数据库发现、密钥派生与验证、数据库解密、微信 V4 会话和消息分片读取、zstd/XML 内容解析、分页以及增量 offset 计算。

本变更的目标是用 C# 完整替代上述 Python 运行时链路，同时保留独立 KeyProbe 进程隔离第三方 DLL。迁移完成并通过验收后，正式 Release 不再包含或依赖 Python、PyInstaller 产物、Python 脚本和 PowerShell Key 探测链路。

## 2. 已确认事实、假设与待确认事项

### 2.1 已确认事实

- 项目是 Windows WPF 桌面应用，当前目标框架为 .NET 10，微信相关进程和 KeyProbe 均以 Windows x64 为目标。
- WechatDashboard.KeyProbe.exe 已使用 C# NativeLibrary API 调用 wx_key.dll 的 InitializeHook、PollKeyData、GetStatusMessage、CleanupHook 和 GetLastErrorMsg 导出。
- WPF 主进程当前不直接加载 wx_key.dll；KeyProbe 使用当前用户限定的随机命名管道传输 32 字节主密钥。
- 本规格制定时，Python reader 是微信数据库读取、解密、消息解析和分页的实际运行时实现；当前工作区已切换到原生 C# reader，但真实机和发布验收尚未完成。
- 当前 Python reader 的正式排序契约是按 sentAt、id 降序，即最新消息优先。
- 现有采集结果继续通过 IMessageCaptureAdapter 和 MessageCapturePipeline 进入去重、持久化、@我识别、Todo 创建和看板刷新流程。
- Infrastructure 项目已经引用 Microsoft.Data.Sqlite.Core 和 SQLitePCLRaw.bundle_e_sqlcipher。
- 工作区当前存在与本功能无关的未提交修改；本功能不得覆盖或清理这些修改。
- 项目记录表明当前选定的 wx_key.dll 已获准对外发布，但每个发布版本仍需保存针对实际二进制的授权与来源证据。
- 2026-08-24 对 `WechatDashboard.App.csproj` 执行默认 Release publish 后，发布目录包含 WPF 主程序和 SQLCipher 依赖，但不包含 `WechatDashboard.KeyProbe.exe` 与 `wx_key.dll`；该输出不能读取微信本地数据库。
- 当前默认 publish 是依赖框架的发布，干净机若未安装 .NET 10 Windows Desktop Runtime 将不能启动；项目尚无 WiX、Inno Setup、MSIX 或其他可复现安装工程。
- 当前微信 reader 状态已写入 `%LocalAppData%\WechatDashboard`，但消息主数据库和采集收件箱仍通过开发期 `tools\result` 路径解析；安装到只读目录时存在启动或持久化失败风险。
- 石化通 reader 已内置于主程序且不依赖 Python、KeyProbe 或 `wx_key.dll`；它仍依赖目标机已登录的 `LxMainNew` 进程加载 `imcore.dll`、兼容的客户端内存布局以及匹配的用户/权限级别。

### 2.2 假设

- 首个支持目标为当前项目已经覆盖的微信 4.1.x/V4 本地数据库结构；客户端升级导致的 schema 或密码学参数变化按可诊断的不兼容处理，不承诺自动兼容未知版本。
- 现有微信消息页的初始化、当天消息分页、采集一次、后台监听和设置入口保持用户可见行为兼容，不在本变更中重做界面。
- Python reader 及其测试在迁移期间只作为 golden oracle 使用，不进入最终 Release。
- 优先尝试通过现有 SQLCipher 依赖读取稳定快照；若兼容 fixture 证明无法可靠读取，则允许采用等价的 C# 托管页面解密实现。这是已批准的技术回退，不改变功能范围。
- 可变状态、快照和工作文件最终迁移到 %LocalAppData%\WechatDashboard；安装目录只保存只读程序与受控二进制。

### 2.3 暂时无法验证的信息

- 当前 wx_key.dll 是否仍与用户机器上的实际微信版本完全兼容，尚需经用户明确操作的真实机 Hook 验证。
- 真实数据库的完整 schema、分片数量、WAL 状态和消息类型分布在本轮文档阶段不可读取。
- wx_key.dll 的发布授权、依赖闭包、安全扫描与签名证据是否已为下一发布版本完整归档，需在发布验收时读取实际记录。

### 2.4 非阻塞开放事项

- zstd 的具体 .NET 实现需在实现阶段依据许可证、win-x64 自包含发布兼容性和 fixture 结果选择并锁定版本。
- SQLCipher 直读与托管页面解密的最终选择由离线兼容 fixture 的可重复结果决定；对上层接口和验收标准无影响。
- 安装技术尚未选定；可选择 WiX、Inno Setup、MSIX 或等价方案，但必须满足 REQ-013 的可复现构建、载荷清单、签名、升级/卸载和双来源干净机验收，不得以手工复制文件作为正式发布流程。

## 3. 功能需求

### REQ-001：保持 DLL 调用的 C# 进程隔离

系统必须继续由独立 x64 C# KeyProbe 进程加载 wx_key.dll。WPF 主进程不得直接加载该 DLL，Python 不得参与 DLL 调用或密钥传输。

验收标准：

- AC-001：给定有效的 KeyProbe、DLL 和微信 PID，当用户显式触发密钥提取时，KeyProbe 通过 C# 调用 DLL 导出并仅通过当前用户限定的随机命名管道交付固定 32 字节主密钥。
- AC-002：给定 KeyProbe 初始化失败、超时、DLL 缺失或导出缺失，当操作结束时，WPF 保持运行并得到不包含数据库密钥的阶段化错误。

### REQ-002：使用 C# 完成初始化和密钥验证

系统必须使用 C# 发现微信 db_storage、枚举数据库、为每个数据库按其 salt 派生密钥并验证必需数据库，不能将主密钥或派生密钥交给 Python。

验收标准：

- AC-003：给定有效主密钥和测试数据库，初始化必须验证 session/session.db、contact/contact.db 以及至少一个 message/message_N.db 后才报告成功。
- AC-004：给定无效密钥、缺失必需数据库或不支持的密码学参数，初始化必须失败并指出 key_validation 或 database_discovery 阶段，不得生成“已初始化”状态。
- AC-005：给定 64 字节或 80 字节 reserve 的受支持 SQLCipher V4 fixture，C# 密钥派生与首页 HMAC 校验结果必须与 Python oracle 一致。

### REQ-003：只读快照并读取加密数据库

系统必须在不修改微信源数据库的前提下读取稳定快照，并正确处理数据库主文件及存在的 WAL/SHM 伴随文件。

验收标准：

- AC-006：给定正在使用且包含 WAL 的数据库，采集必须从一致性快照读取，不直接写入或更改源数据库及其 WAL/SHM。
- AC-007：给定快照复制、打开、解密或完整性校验失败，系统必须停止使用该快照、清理不完整工作文件并返回对应阶段错误。

### REQ-004：用 C# 解析微信 V4 消息

系统必须使用 C# 读取 SessionTable、联系人显示名、Name2Id 和 Msg_<md5(username)> 消息表，并输出现有 CapturedMessage 所需字段。

验收标准：

- AC-008：给定跨多个 message_N.db 分片的 fixture，C# reader 必须解析相同的消息 ID、chatId、chatName、senderName、sentAt 和 messageType，并去除跨分片重复项。
- AC-009：给定私聊、群聊及真实 V4 字段别名，C# reader 必须按现有契约解析群成员发送者和联系人显示名。
- AC-010：给定普通文本、zstd 正文及图片、视频、表情、文件、链接和位置 XML，C# 输出摘要必须与 Python oracle 逐字段一致。

### REQ-005：保持分页、排序和增量语义

系统必须保持现有消息页面和监听采集的排序、日期边界、分页与 offset 语义。

验收标准：

- AC-011：给定相同消息集合，结果必须稳定按 sentAt、id 降序排列。
- AC-012：给定日期、页码和每页条数，C# reader 返回的总数、页内容和页边界必须与现有行为一致；页大小继续限制为 1 至 200。
- AC-013：给定上次成功提交的 offset，增量采集允许受控回看窗口但不得因重复轮询产生重复持久化消息，只有整个批次提交成功后才能推进 offset。

### REQ-006：接入现有采集与 UI 流程

C# reader 必须替代 WeChatLocalCommand/Python 外部进程链路，并继续复用现有采集管线和微信消息页。

验收标准：

- AC-014：初始化、读取当天消息、采集一次和后台监听均必须调用同一 C# 本地数据库读取核心，不再启动 python、python3、py 或 wechat-local-reader 外部进程。
- AC-015：读取结果必须继续进入 MessageCapturePipeline，并保持去重、持久化、@我 Todo 创建、关注群过滤及看板刷新行为。
- AC-016：微信窗口最小化或被其他窗口遮挡时，本地数据库采集仍可工作；该能力不得依赖 UIA/OCR。

### REQ-007：提供可诊断且不泄密的失败行为

系统必须为路径发现、KeyProbe、密钥验证、快照、数据库访问、schema、内容解码、查询和持久化提供结构化阶段结果。

验收标准：

- AC-017：任一阶段失败时，诊断必须包含稳定错误码、阶段、脱敏摘要和时间，不得把异常折叠成“成功读取 0 条”。
- AC-018：诊断、日志、stdout/stderr、UI 状态和测试失败输出中不得出现连续 64 位十六进制数据库密钥或真实消息正文。

### REQ-008：保护密钥和本地消息隐私

数据库主密钥和派生密钥只能存在于受控的短生命周期内存中，使用结束、重新初始化、取消或退出时必须清零。

验收标准：

- AC-019：正常 C# 链路不得把主密钥或派生密钥写入命令行、环境变量、config.json、all_keys.json、普通文件、应用 SQLite 数据库或日志。
- AC-020：密钥租约在成功、失败、取消和异常路径均被释放并清零；重新初始化必须先销毁旧租约。

### REQ-009：正式 Release 不依赖 Python

完成迁移验收后，正式 Release 必须移除 Python 运行时路径和相关分发产物。

验收标准：

- AC-021：win-x64 Release staging 中不得包含 .py 文件、Python/PyInstaller 可执行文件、Python 包、run-wx-key-probe.ps1、明文 Key 文件或调用 Python 的配置。
- AC-022：在未安装 Python、PATH 中没有 Python、未安装 .NET SDK 的干净 Windows x64 环境中，安装、初始化、分页读取和增量采集均可执行。

### REQ-010：以对照迁移证明行为等价

删除 Release 中的 Python 路径前，必须使用冻结 fixture 和获准的真实机聚合验证证明 C# reader 与 Python oracle 等价。

验收标准：

- AC-023：对所有离线 fixture，C# 与 Python 的数据库识别、消息字段、内容摘要、排序、分页和下一 offset 必须完全一致；明确批准的错误分类差异需记录理由。
- AC-024：真实机对照只记录数据库数、会话数、消息数、时间范围、已知测试消息 ID 的匹配结果和脱敏阶段状态，不记录真实正文；所有获准比较项必须一致。
- AC-025：现有 .NET 回归测试和迁移期 Python oracle 测试必须全部通过，且新增 C# reader 测试覆盖成功、边界、损坏输入和取消路径。

### REQ-011：支持可安装目录布局

程序文件和运行时可变状态必须分离，不能依赖解决方案目录或固定开发机路径。

验收标准：

- AC-026：KeyProbe、wx_key.dll 和只读依赖从安装目录定位；配置、快照、缓存、日志和 reader 状态写入 %LocalAppData%\WechatDashboard。
- AC-027：安装路径和用户目录包含空格或中文字符时，初始化、读取、升级和卸载仍通过；卸载不得删除用户未明确选择删除的本地消息数据。

### REQ-012：控制第三方二进制发布风险

只有能够对应到授权与安全记录的实际 wx_key.dll 才能进入 Release。

验收标准：

- AC-028：每个 Release 必须归档实际 wx_key.dll 的授权凭据、来源、版本、SHA-256、依赖闭包、安全扫描和签名结果，且清单哈希与 staging 文件一致。
- AC-029：主程序、KeyProbe、受控原生组件和安装包的签名与安全软件/EDR 验证结果必须可追溯；缺失任何强制证据时 Release 验收失败。

### REQ-013：交付可在新机器安装并读取双来源消息的安装包

正式安装包必须从可复现的 win-x64 self-contained staging 生成，在标准用户权限和只读安装目录下运行，并分别通过真实微信与真实石化通的端到端验收。不能把开发机能构建、fixture 通过或手工补文件视为新机发布完成。

验收标准：

- AC-030：给定未安装 .NET SDK、.NET Desktop Runtime 和 Python 的干净 Windows x64 机器，安装包必须能完成安装并启动主程序；构建记录能够从固定命令重建相同版本、架构和载荷类型的 staging 与安装包。
- AC-031：安装后的只读程序目录必须包含并能定位 `WechatDashboard.App.exe`、`WechatDashboard.KeyProbe.exe`、经核验的 `wx_key.dll`、SQLCipher、zstd 及全部 win-x64 托管/原生运行依赖；载荷清单、文件哈希和实际安装文件必须一致。
- AC-032：以标准用户从 `Program Files` 或等价只读目录启动时，消息主数据库、offset、采集设置、采集收件箱、reader 状态、快照、临时文件和日志必须全部写入 `%LocalAppData%\WechatDashboard`；安装目录和源码仓库不得产生可变运行文件。
- AC-033：在目标机安装并登录兼容微信、产生本地聊天数据库后，经用户显式触发 KeyProbe，必须完成真实 Hook、初始化、当天分页、已知测试消息和第二次增量零重复验收；权限不匹配、DLL/客户端不兼容或安全软件拦截必须显示明确诊断，不能表现为成功读取 0 条。
- AC-034：在目标机安装并登录兼容石化通、确认 `LxMainNew` 已加载 `imcore.dll` 后，必须在匹配的用户和权限级别下读出已知测试消息的群名、发送人、正文摘要和时间，并证明第二次增量无重复；客户端布局不兼容或进程访问被拒绝必须显示明确诊断，不能静默为空。
- AC-035：安装、同版本修复、升级、保留用户数据卸载、完全卸载和回滚到上一签名版本必须按文档执行；默认卸载不得删除用户消息数据，签名、安全软件/EDR、载荷清单和双来源验收记录必须绑定到同一安装包哈希。

## 4. 非功能需求

- NFR-001 安全：遵守最小暴露原则，第三方 DLL 只存在于隔离进程；所有错误默认脱敏。
- NFR-002 可靠性：数据库源文件只读；快照失败不污染上次可用状态；取消和进程退出可清理临时资源。
- NFR-003 性能：单次增量采集不得无条件重新处理未变化的全部数据库；允许基于文件变化、分片高水位和受控回看减少工作量。
- NFR-004 可维护性：密码学、快照、schema、内容解析、查询、状态和 UI 编排职责分离，可使用 fixture 独立测试。
- NFR-005 兼容性：支持 Windows x64 与当前目标微信 V4 契约；未知 schema 必须显式报不兼容，不得静默返回空数据。
- NFR-006 可观测性：记录阶段、耗时、数量和错误码，但不记录主密钥、派生密钥和真实消息正文。
- NFR-007 可部署性：安装包构建必须可重复、载荷可审计、无需预装 .NET/Python/SDK，并在标准用户和只读安装目录下工作。

## 5. 范围

### 5.1 范围内

- 保留并加固 C# KeyProbe 与 wx_key.dll 隔离调用。
- C# 数据库发现、快照、密钥派生/校验、SQLCipher V4 访问。
- C# 微信 V4 会话、联系人、Name2Id、分片消息、zstd/XML 内容解析。
- 当天消息分页、历史引导范围、增量 offset 和采集管线接入。
- 阶段化诊断、密钥零化、LocalAppData 状态目录和无 Python Release。
- 离线 fixture、Python oracle 对照、真实机脱敏验证和干净机验收。
- win-x64 self-contained staging、正式安装工程、载荷清单、签名以及微信/石化通双来源新机验收。

### 5.2 范围外

- 在 WPF 主进程内直接加载 wx_key.dll。
- 绕过用户显式操作的隐藏 Hook、远程采集或上传微信数据库/消息。
- 支持 macOS、Linux、32 位 Windows 或未知微信数据库版本。
- 重做微信消息页 UI、改变 Todo/分类/紧急度业务规则。
- 用 UIA/OCR 替代本地数据库读取。
- 重新授权第三方二进制；本变更只验证和归档已有授权证据。
- 承诺兼容所有历史或未来微信、石化通客户端版本；未知版本必须安全失败并通过后续兼容发布处理。

## 6. 约束与依赖

- KeyProbe 和 wx_key.dll 必须为架构兼容的 Windows x64 组件。
- Hook 必须由用户显式触发，可能需要与微信进程相匹配的权限级别。
- 微信数据库属于未公开的内部格式，客户端升级可能导致 schema 或密码学参数变化。
- 直接 SQLCipher 访问依赖现有 SQLitePCLRaw.bundle_e_sqlcipher；任何新增 zstd 依赖必须通过许可证与自包含发布审查。
- Python oracle 仅用于迁移验证，不能成为新 C# 生产路径的运行时依赖。
- 实现期间必须保留当前工作区已有的无关未提交修改。
- 新机验收依赖测试机分别安装并登录获准测试的微信和石化通客户端，且允许在组织安全策略范围内执行只读进程访问、KeyProbe Hook 和安装包安全扫描。

## 7. 用户故事

- US-001：作为应用用户，我希望无需安装 Python 就能读取本机微信消息，以便简化部署和运行环境。
- US-002：作为应用用户，我希望 DLL 异常不会导致整个 WPF 应用崩溃，以便可以查看诊断并重试。
- US-003：作为隐私敏感用户，我希望数据库密钥不落盘、不进日志，以减少本地敏感信息泄露风险。
- US-004：作为消息看板用户，我希望迁移后分页、监听、去重和 Todo 行为不变，以免已有工作流出现回归。
- US-005：作为维护者，我希望每个读取阶段可独立测试和诊断，以便微信升级后能快速定位不兼容环节。
- US-006：作为发布负责人，我希望安装包不包含 Python 且第三方 DLL 有完整证据链，以便完成可审计发布。
- US-007：作为新机器用户，我希望安装后无需开发仓库、SDK、Python 或额外手工补文件即可读取本人微信和石化通消息，并在不兼容时得到明确诊断。

## 8. 成功定义

只有 AC-001 至 AC-035 全部获得证据，且四份 SDD 文档已重新审批、实现检查清单已完成，才可宣称“安装包可在新机器读取微信和石化通消息”。仅完成 KeyProbe、仅通过编译/fixture、仅在开发机读取成功、仅生成 publish 目录或仅手工补齐文件都不构成正式发布完成。
