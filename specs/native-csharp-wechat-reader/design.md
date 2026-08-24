# 原生 C# 微信本地消息读取设计

## 1. 设计目标与非目标

### 1.1 设计目标

- G-001：保持 wx_key.dll 位于独立 x64 C# KeyProbe 进程内，避免第三方 Hook/DLL 故障污染 WPF 主进程。（REQ-001、REQ-007）
- G-002：以可测试的 C# 组件替代 Python 的数据库发现、密钥验证、快照、数据库访问、schema 读取和内容解析。（REQ-002 至 REQ-005）
- G-003：让微信消息页和 MessageCapturePipeline 共用同一个 C# 读取核心。（REQ-006）
- G-004：使主密钥和派生密钥不落盘、不进入文本接口，并在所有生命周期出口清零。（REQ-008）
- G-005：通过 fixture、Python oracle、真实机脱敏对照和干净机安装验收后，从 Release 删除 Python。（REQ-009、REQ-010、REQ-011）
- G-006：为每个读取阶段提供稳定、脱敏、可回归的诊断。（REQ-007）

### 1.2 非目标

- 不把 wx_key.dll 加载进 WPF 主进程。
- 不重新设计消息、Todo、分类、紧急度或看板业务规则。
- 不把 UIA/OCR 改成主要读取链路。
- 不承诺兼容尚未验证的未来微信 schema。
- 不把 Python 打包成隐藏的生产回退。

## 2. 当前系统上下文

### 2.1 当前链路

1. 用户在 WPF 中触发“自动提取 Key”。
2. NativeHookKeyProvider 启动 WechatDashboard.KeyProbe.exe。
3. KeyProbe 使用 C# NativeLibrary 加载 wx_key.dll，并通过命名管道返回 32 字节主密钥。
4. WeChatLocalReaderService 把密钥转成十六进制字符串并通过环境变量交给 Python init。
5. Python 写入 config.json 和 all_keys.json，解密数据库并执行 V4 schema/内容读取。
6. WPF 解析 Python JSON；监听路径由 WeChatLocalCommandCaptureAdapter 启动外部命令并接入 MessageCapturePipeline。

步骤 1 至 3 已是 C#；本设计替换步骤 4 至 6，并消除明文密钥字符串、all_keys.json 和 Python 进程。

### 2.2 目标架构

~~~text
WPF / MessageCapturePipeline
        |
        v
WeChatLocalReaderService  ----  WeChatReaderStateStore
        |
        +---- explicit key action ----> NativeHookKeyProvider
        |                                  |
        |                                  v
        |                         WechatDashboard.KeyProbe.exe
        |                                  |
        |                             wx_key.dll
        |                                  |
        +<----- current-user named pipe ---+
        |
        +----> WeChatDatabaseDiscovery
        +----> WeChatDatabaseSnapshotService
        +----> WeChatDatabaseKeyDeriver / Validator
        +----> IWeChatEncryptedDatabaseAccessor
        +----> WeChatV4SchemaReader
        +----> WeChatMessageContentDecoder
        +----> WeChatMessageQueryService
        |
        v
CapturedMessage / WeChatLocalMessagePage / stage diagnostics
~~~

KeyProbe 是唯一允许加载 wx_key.dll 的进程。其余读取逻辑位于 Infrastructure 的 C# 组件内，但通过 Application 层已有契约向上提供消息。（REQ-001、REQ-006）

## 3. 核心设计决策

### D-001：保留独立 KeyProbe 进程边界

KeyProbe 继续动态解析 DLL 导出，并在 finally/Dispose 路径调用 CleanupHook。WPF 只向 KeyProbe 传递 PID、随机管道名、DLL 路径和有界超时；密钥只通过 PipeOptions.CurrentUserOnly 的命名管道传输。KeyProbe stdout/stderr 只能输出脱敏状态。（REQ-001、REQ-007、REQ-008）

原因：第三方 DLL 可能崩溃、挂起或改变进程状态。独立进程提供故障、架构和权限隔离。直接在 WPF 中 P/Invoke 虽减少一次进程启动，但会扩大崩溃和敏感内存边界，因此拒绝。

### D-002：WeChatLocalReaderService 作为编排器，不承载密码学和 schema 细节

WeChatLocalReaderService 保留 UI 已依赖的初始化、可用性、最近错误和分页入口，但内部改为组合 C# 服务。它不再定位 Python、构造 init/capture 命令或解析外部 JSON。（REQ-006、NFR-004）

服务同时为消息页和采集适配器提供统一的 InitializeAsync、ReadPageAsync 与 CaptureAsync 核心，避免两条读取路径漂移。

### D-003：密钥使用二进制租约，不转换为字符串

扩展 WeChatDatabaseKeyLease，使受控组件能在不复制为字符串的情况下访问只读字节视图。主密钥租约由 reader session 持有，重初始化、账户切换、取消、应用退出或 Dispose 时清零。每个数据库的派生密钥使用独立短生命周期缓冲区，并在数据库连接建立或解密完成后清零。（REQ-002、REQ-008）

不再使用 ImportedDatabaseKey 字符串、WECHAT_DASHBOARD_DB_KEY 环境变量或 all_keys.json。受控的手动导入恢复入口若保留，也必须直接创建内存租约，不能写普通文件。

### D-004：源数据库只读，所有读取针对稳定快照

WeChatDatabaseSnapshotService 发现 session、contact、message 分片及相应 WAL/SHM，将同一数据库组复制到 %LocalAppData%\WechatDashboard\Reader\Snapshots 下的随机临时目录。复制完成并通过文件存在性、大小和可打开性检查后原子切换为本次工作快照。（REQ-003、REQ-011）

失败或取消时删除未完成快照；上一次已完成快照只用于诊断/受控恢复，不能被误报为最新数据。源文件以共享读方式打开，绝不写入、执行 checkpoint 或修改时间戳。

### D-005：加密数据库访问使用可替换边界

定义 IWeChatEncryptedDatabaseAccessor：输入稳定快照描述和短生命周期派生密钥，输出只读 Microsoft.Data.Sqlite 连接或等价的查询会话。（REQ-002、REQ-003）

首选实现 SqlCipherDatabaseAccessor，使用项目已引用的 SQLitePCLRaw.bundle_e_sqlcipher，并通过离线 fixture 固定微信 V4 所需参数。若 direct SQLCipher 对 64/80 reserve、WAL 合并或特定 fixture 无法稳定兼容，则启用 ManagedPageDecryptingAccessor：使用 .NET PBKDF2-HMAC-SHA512、HMAC-SHA512 和 AES-256-CBC 生成仅位于 LocalAppData 临时工作区的明文快照，查询结束后删除。

选择门槛是自动化 fixture，而不是开发者主观判断。两个实现均遵守同一接口、阶段错误和上层数据契约，因此切换不改变产品需求。（REQ-002、REQ-003、REQ-010）

### D-006：按数据库 salt 独立派生并验证密钥

WeChatDatabaseKeyDeriver 对每个数据库读取 4096 字节首页，使用 32 字节主密钥、16 字节 salt、PBKDF2-HMAC-SHA512 和当前 Python oracle 已冻结的迭代参数派生数据库密钥。WeChatDatabaseKeyValidator 支持已验证的 reserve 64/80 首页 HMAC 形式。（REQ-002）

初始化只有在 session/session.db、contact/contact.db 和至少一个 message/message_N.db 均验证成功后才进入 Ready。其他数据库验证失败记录为部分能力诊断，但必需数据库失败是硬失败。

### D-007：V4 schema 读取与内容解码解耦

WeChatV4SchemaReader 负责：

- 从 SessionTable 读取 username、display name、时间和 DBInfo/Timestamp 兼容字段。
- 从联系人库建立 wxid/user_name 到显示名映射。
- 从每个消息分片读取 Name2Id。
- 对每个会话计算 md5(username) 并定位 Msg_<hash> 表。
- 适配已在 Python fixture 覆盖的字段别名，未知必需 schema 返回 SchemaUnsupported，不静默返回空集。

WeChatMessageContentDecoder 负责：

- 普通 UTF-8 文本。
- zstd 压缩 blob/string 的受限解压和无效数据错误。
- 群聊 sender 前缀与 Name2Id 解析。
- 图片、视频、表情、文件、链接、位置、系统/撤回 XML 摘要。
- 输出 Domain MessageType 和现有摘要文字契约。

XML 解析禁用外部实体和 DTD，zstd 解压设置输出大小上限，避免恶意或损坏本地数据导致资源耗尽。（REQ-004、REQ-007、REQ-008）

### D-008：查询、排序、分页和增量使用单一查询服务

WeChatMessageQueryService 从所有已验证分片查询指定时间窗口，生成稳定 SourceMessageKey，并以消息 ID 去重。最终集合按 sentAt、id 降序排序。（REQ-005）

- 分页模式：先得到同一日期窗口内的稳定总数与排序结果，再按 offset/limit 切片；页大小限制 1 至 200。
- 增量模式：以已提交高水位减去受控 lookback 为查询起点；结果仍依赖 SourceMessageKey 去重。
- offset 只由 MessageCapturePipeline 在整个批次持久化成功后提交。reader 返回 proposed next offset，不直接修改持久状态。

### D-009：用原生适配器替换命令适配器

新增 WeChatLocalDatabaseCaptureAdapter，直接调用 C# reader session，不创建外部 Python 进程。CaptureSourceKind 新增或迁移为 WeChatLocalDatabase；读取旧 WeChatLocalCommand 设置时执行一次兼容映射，不要求用户重建设置。（REQ-006、REQ-009）

WeChatLocalCommandCaptureAdapter 可在迁移对照测试中保留，但不得注册到最终 Release 的默认或已保存生产配置中。迁移完成后可从生产代码删除。

### D-010：阶段化错误是一等数据

所有组件返回或抛出包含以下字段的 WeChatReaderException / WeChatReaderStageResult：（REQ-007）

- Stage：PathDiscovery、KeyProbe、KeyValidation、Snapshot、DatabaseOpen、Schema、ContentDecode、Query、PipelineCommit。
- Code：稳定枚举，例如 DllMissing、ExportMissing、KeyTimeout、KeyInvalid、RequiredDatabaseMissing、SnapshotFailed、CipherUnsupported、SchemaUnsupported、ContentCorrupt、Cancelled。
- SanitizedMessage：最多 500 字符，经过 64 位十六进制串与已知敏感路径脱敏。
- StartedAt、Duration、Counts：仅记录数量、耗时和兼容版本，不记录正文。

“成功读取 0 条”只有在所有必需阶段成功且查询范围确实无匹配行时才允许。

### D-011：状态目录与安装目录分离

ProjectToolPaths 被拆分为只读 InstallRoot 和可变 UserDataRoot。默认路径：（REQ-011）

~~~text
<InstallRoot>\
  WechatDashboard.App.exe
  KeyProbe\WechatDashboard.KeyProbe.exe
  KeyProbe\wx_key.dll
  runtimes\...

%LocalAppData%\WechatDashboard\
  Reader\state.json
  Reader\Snapshots\
  Reader\Temp\
  Logs\
  Data\wechat-dashboard.db
  CaptureInbox\
~~~

state.json 只保存数据库根目录、bootstrap 范围、schema/reader 版本、上次成功阶段和非敏感高水位，不保存任何密钥。消息主数据库、offset、采集设置、收件箱、reader 状态、快照、临时文件和日志都必须以 UserDataRoot 为根；不得再通过 `ProjectRoot\tools\result` 解析生产写入路径。路径解析必须使用 Path API 和 ArgumentList，不拼接 shell 字符串。

### D-012：迁移与回滚以发布边界为准

开发和验证构建允许显式启用 Python oracle 双跑，默认关闭且不得记录正文。Release staging 生成前执行清单扫描，拒绝 Python、.py、PyInstaller、PowerShell Key 探测和普通 Key 文件。（REQ-009、REQ-010）

迁移完成后的回滚不是在新版本中调用 Python，而是安装上一版已签名、已知可用版本并保留用户数据。state.json 带 readerVersion；升级迁移应可重建缓存和快照，不修改消息主数据库。

### D-013：以可复现 self-contained staging 驱动安装工程

安装工程只消费固定的 `win-x64` self-contained staging，不直接从各项目 `bin` 目录或开发仓库拼装文件。staging 构建必须显式发布 WPF 主程序和 x64 KeyProbe，并把 `wx_key.dll` 放入约定的 `KeyProbe` 只读目录；SQLCipher、zstd 和其他托管/原生依赖由发布清单闭包验证。安装技术可以后选，但必须提供非交互构建命令、版本输入、载荷清单、SHA-256 清单和签名步骤。（REQ-009、REQ-011 至 REQ-013）

发布校验器在安装包生成前后分别验证：必需文件全部存在；禁止文件全部缺失；架构和运行时模式正确；staging、安装包清单及安装后文件哈希一致。任一缺失、额外未审计二进制或哈希漂移都阻止发布。（REQ-012、REQ-013）

### D-014：微信与石化通采用独立的新机验收门

干净机验收不以“应用启动”作为成功，而是对两个来源分别记录前置条件、读取结果和失败诊断：（REQ-007、REQ-010、REQ-013）

- 微信门：目标机已安装并登录受支持的微信，存在本地 `db_storage`；用户显式触发 Hook；验证 KeyProbe、初始化、分页、已知测试消息、最小化采集和第二次增量零重复。
- 石化通门：目标机已安装并登录受支持的石化通，`LxMainNew` 已加载 `imcore.dll`；验证只读进程访问、群名/发送人/正文摘要/时间和第二次增量零重复。
- 两个来源都必须在标准用户下先测；只有目标客户端以提升权限运行而确有需要时，才测试匹配权限，并记录原因。版本/架构/权限/EDR 不兼容必须映射为明确诊断，不能折叠成合法零消息。
- 验收证据只保存版本、文件哈希、计数、时间范围、已知测试消息 ID 和脱敏状态，不保存密钥或真实正文。

## 4. 组件职责

| 组件 | 层 | 职责 | 需求 |
|---|---|---|---|
| NativeHookKeyProvider | Infrastructure | 启动 KeyProbe、建立受限命名管道、超时/取消和错误映射 | REQ-001、REQ-008 |
| WechatDashboard.KeyProbe | 独立进程 | 唯一 DLL 加载边界、Hook 生命周期、密钥管道交付 | REQ-001、REQ-012 |
| WeChatReaderSession | Infrastructure | 持有主密钥租约与初始化状态，统一释放 | REQ-002、REQ-008 |
| WeChatDatabaseDiscovery | Infrastructure | 规范化 db_storage 并枚举必需数据库/分片 | REQ-002 |
| WeChatDatabaseSnapshotService | Infrastructure | 只读复制 DB/WAL/SHM、完成标记、失败清理 | REQ-003、REQ-011 |
| WeChatDatabaseKeyDeriver/Validator | Infrastructure | 按库派生密钥和首页 HMAC 验证 | REQ-002 |
| IWeChatEncryptedDatabaseAccessor | Infrastructure | 为已验证快照提供只读查询会话 | REQ-003 |
| WeChatV4SchemaReader | Infrastructure | 会话、联系人、Name2Id、Msg 分片读取 | REQ-004 |
| WeChatMessageContentDecoder | Infrastructure | zstd、XML、群聊发送者和类型摘要 | REQ-004 |
| WeChatMessageQueryService | Infrastructure | 跨分片查询、去重、排序、分页、next offset | REQ-005 |
| WeChatLocalReaderService | Infrastructure facade | 初始化、分页、采集和诊断编排 | REQ-006、REQ-007 |
| WeChatLocalDatabaseCaptureAdapter | Infrastructure adapter | 将 C# 读取结果送入现有采集管线 | REQ-006 |
| WeChatReaderStateStore | Infrastructure | 保存无密钥状态和 reader 版本 | REQ-008、REQ-011 |
| Release staging builder / installer | 发布层 | 生成 self-contained win-x64 载荷、安装包、清单、哈希和签名输入 | REQ-009、REQ-011 至 REQ-013 |
| Clean-machine acceptance harness | 验收层 | 在同一安装包上分别验证微信、石化通、安装生命周期和脱敏证据 | REQ-010、REQ-013 |

## 5. 接口与内部契约

建议新增或调整以下契约，确切命名可在实现时保持项目命名一致，但字段语义不得改变。

~~~csharp
public interface IWeChatLocalDatabaseReader : IAsyncDisposable
{
    bool IsAvailable { get; }
    bool IsInitialized { get; }
    WeChatReaderDiagnostics Diagnostics { get; }

    Task<WeChatInitializationResult> InitializeAsync(
        WeChatInitializationRequest request,
        CancellationToken cancellationToken);

    Task<WeChatReadResult> ReadAsync(
        WeChatReadRequest request,
        CancellationToken cancellationToken);
}

public sealed record WeChatReadRequest(
    DateTimeOffset? Start,
    DateTimeOffset? End,
    int Offset,
    int Limit,
    string? CommittedHighWatermark,
    TimeSpan IncrementalLookback);

public sealed record WeChatReadResult(
    IReadOnlyList<CapturedMessage> Messages,
    int TotalCount,
    string ProposedNextOffset,
    IReadOnlyList<WeChatReaderStageResult> Stages);
~~~

IWeChatDatabaseKeyProvider 继续返回 WeChatDatabaseKeyLease，但租约新增受限的 ReadOnlySpan<byte> 使用方式；禁止 ToHexString 用于生产初始化链路。（REQ-008）

WeChatLocalMessagePage 的 Messages、TotalCount、PageNumber、PageSize 保持现有 UI 契约。（REQ-005、REQ-006）

## 6. 数据模型、验证与生命周期

### 6.1 非敏感状态

state.json 建议字段：

| 字段 | 类型 | 说明 |
|---|---|---|
| readerVersion | string | C# reader 状态版本 |
| databaseRoot | string | 用户本机 db_storage 绝对路径，可在诊断显示时缩略 |
| bootstrapRange | 7d/30d/all | 首次读取范围 |
| schemaFingerprint | string | 仅由表/列名组成的哈希，不含数据 |
| lastSuccessfulStage | enum | 最近成功阶段 |
| lastValidatedAt | datetime | 最近密钥/数据库验证时间 |
| shardHighWatermarks | map | 每分片非敏感时间/ID 高水位 |

禁止字段：主密钥、派生密钥、密钥 salt+key 组合、真实消息正文、用户聊天列表。

### 6.2 Reader 生命周期

1. Uninitialized：没有有效内存租约。
2. AcquiringKey：用户显式触发 KeyProbe。
3. Validating：发现数据库并派生/校验各库密钥。
4. Ready：主密钥租约存活，可按需为当前快照派生短期数据库密钥。
5. Reading：快照、打开、schema 和查询。
6. Faulted：保留脱敏诊断，不保留无效密钥。
7. Disposed：所有租约、连接和临时文件已释放。

账户切换、数据库 salt 变化、schemaFingerprint 变化、显式重新初始化或 KeyInvalid 都会从 Ready 回到 Uninitialized，并清零旧密钥。

## 7. 控制流与数据流

### 7.1 初始化

1. 用户显式点击提取/初始化。
2. reader 定位 Weixin.exe 和 db_storage，记录 PathDiscovery 数量信息。
3. NativeHookKeyProvider 启动 KeyProbe，通过命名管道取得主密钥租约。
4. Discovery 枚举数据库；SnapshotService 创建一致性快照。
5. KeyDeriver/Validator 对必需数据库逐一验证。
6. accessor 打开 fixture/快照并执行最小 schema 探测。
7. 成功后写入无密钥 state.json，session 进入 Ready；失败则清零并返回稳定阶段错误。

### 7.2 分页读取

1. UI 将本地日期转换为明确的本地时区日界。
2. reader 创建最新稳定快照并复验发生变化的数据库。
3. schema reader 建立会话、联系人和分片映射。
4. query service 查询完整日期范围，解码、去重并稳定排序。
5. 按 offset/limit 切页并返回总数。
6. UI 继续把本页消息送入 MessageCapturePipeline；现有去重保证不会重复写入。

### 7.3 增量采集

1. adapter 从 CaptureContext 读取已提交 offset。
2. reader 以 offset-lookback 为窗口读取并返回 ProposedNextOffset。
3. MessageCapturePipeline 完成全部持久化和 Todo 操作。
4. 只有提交成功后，外层保存 ProposedNextOffset；失败保持旧 offset 以便重放。

## 8. 错误、恢复与用户可见行为

| 阶段 | 示例错误 | 恢复 | 用户可见行为 |
|---|---|---|---|
| PathDiscovery | 未找到 Weixin/db_storage | 登录微信后重试或手动选择目录 | 显示路径阶段失败 |
| KeyProbe | DLL 缺失、导出缺失、超时、权限不匹配 | 修复安装、重启微信、以匹配权限重试 | WPF 不崩溃，显示脱敏错误 |
| KeyValidation | Key 与当前账户数据库不匹配 | 清零旧 Key，重新提取 | 不进入已初始化状态 |
| Snapshot | 文件变化、复制失败、空间不足 | 清理未完成快照后重试 | 保留旧状态但不误报新数据 |
| DatabaseOpen | cipher 参数不支持、损坏 | 报兼容性错误；fixture 门控决定 accessor | 不返回 0 条伪成功 |
| Schema | 必需表/列缺失 | 标记客户端版本不兼容 | 显示 schema 阶段失败 |
| ContentDecode | 单条损坏/zstd/XML 异常 | 对可隔离行产生安全占位摘要并计数；结构性失败中止 | 不显示密钥或原始损坏 blob |
| Query | 分片失败、取消 | 可安全重试，不推进 offset | 保留旧消息与旧 offset |
| PipelineCommit | SQLite/Todo 事务失败 | 整批重放 | 不推进 offset |

## 9. 安全、隐私、性能与兼容性

### 9.1 安全与隐私

- KeyProbe 参数白名单和长度边界保持不变；管道名不可预测且 CurrentUserOnly。
- 所有 byte[] 密钥使用 CryptographicOperations.ZeroMemory 清理。
- 诊断统一经过敏感串过滤，不允许组件自行拼接原始异常到 UI。
- XML 使用安全解析设置；zstd 限制最大输出；SQLite 标识符必须白名单验证并正确引用。
- 真实机对照默认只处理计数、时间范围、schema 名和已知测试消息 ID。

### 9.2 性能

- 数据库派生密钥可在当前 reader session 内以内存方式缓存，但必须与数据库 salt 绑定并在 session 结束时清零。
- 快照和解密以文件大小、最后写入时间以及可用的 WAL 状态判断变化，未变化分片可复用本次安全缓存。
- 增量读取使用时间/ID 高水位，避免每轮加载全部历史；分页首先保证正确性，再通过索引/分片并行优化。
- 并行派生/读取必须有上限，默认不超过 min(8, processor count)，并接受取消。

### 9.3 兼容性

- 当前目标 Windows x64、.NET 10、微信 V4。
- schema 别名以冻结 fixture 为准；新增别名需要测试，不使用猜测字段。
- SQLite/zstd 包必须锁定版本并进入依赖、许可证和自包含发布检查。
- 安装路径和用户数据路径用系统 Path API，必须覆盖空格、中文和长路径边界。
- 石化通兼容性以 `LxMainNew`、`imcore.dll`、进程架构和已验证内存布局为边界；客户端变化必须报告不兼容，不能把零消息当作兼容证明。

## 10. 迁移、发布与回滚

### 10.1 迁移阶段

1. 冻结 Python oracle 和 fixture。
2. 完成密钥派生、数据库访问和 schema/content 单元层。
3. 完成 C# reader 并在测试构建中双跑逐字段对照。
4. 将 UI/采集默认切换到 C#，Python 仅保留显式测试开关。
5. 完成真实机脱敏验证与最小化微信测试。
6. 将所有生产可变数据迁移到 `%LocalAppData%\WechatDashboard`，并在只读安装目录验证标准用户启动。
7. 生成无 Python self-contained win-x64 staging，显式包含 KeyProbe、`wx_key.dll` 和全部运行依赖。
8. 从固定 staging 生成签名安装包，归档载荷/哈希清单。
9. 在同一干净机安装包上完成微信和石化通独立端到端验收，以及升级、卸载和回滚验收。
10. 删除 Release Python、PowerShell 和明文 Key 产物；开发 oracle 可留在源码树但不得复制到 staging。

### 10.2 发布门槛

- AC-001 至 AC-035 全部有证据。
- 实际 wx_key.dll 哈希与授权/来源/安全/签名记录一致。
- staging 扫描和干净机测试证明无 Python。
- 主程序、KeyProbe、DLL/依赖和安装包完成签名与安全软件检查。
- 标准用户从只读安装目录启动，所有可变数据只写入 LocalAppData。
- 同一安装包哈希已分别通过真实微信和真实石化通端到端验收；任一来源未通过时，不得宣称安装包支持双来源读取。

### 10.3 回滚

- 应用升级前保留 state.json 备份，但不备份密钥。
- reader 缓存和快照可删除重建；消息主数据库保持迁移兼容。
- 新版失败时回滚到上一版已签名安装包，不在新 Release 中启用 Python 隐藏回退。
- 若 schema 不兼容，禁用微信本地数据库采集并保留其他采集源，直到发布兼容更新。

## 11. 备选方案与权衡

| 方案 | 优点 | 缺点 | 结论 |
|---|---|---|---|
| WPF 直接 P/Invoke wx_key.dll | 进程少、调用直接 | DLL/Hook 崩溃拖垮 UI，扩大密钥与权限边界 | 拒绝 |
| 保留 Python 生产 reader | 现有行为成熟、开发量小 | 部署复杂，密钥经过字符串/环境变量，无法满足无 Python 目标 | 仅迁移 oracle |
| PyInstaller 打包 Python | 用户表面无需安装 Python | 仍是 Python 运行时，包大、审计与更新复杂 | 拒绝 |
| 独立 C# reader 进程 | 故障隔离更强 | 与 WPF 之间需再设计消息 IPC，复杂度高 | 当前不采用；数据库访问保持托管组件，DLL 仍由 KeyProbe 隔离 |
| SQLCipher 直接访问 | 少写密码学代码，可直接查询 | 微信参数/WAL 兼容需 fixture 证明 | 首选 |
| C# 托管页面解密 | 与 Python 算法可精确对照 | 密码学和文件格式维护成本高，产生临时明文快照 | fixture 证明必要时启用 |

## 12. 需求到设计追踪

| 需求 | 设计决策/章节 |
|---|---|
| REQ-001 | D-001、3.1、7.1 |
| REQ-002 | D-003、D-005、D-006、6.2 |
| REQ-003 | D-004、D-005、7.1 至 7.3 |
| REQ-004 | D-007、4、5 |
| REQ-005 | D-008、7.2、7.3 |
| REQ-006 | D-002、D-009、7.2、7.3 |
| REQ-007 | D-001、D-007、D-010、8 |
| REQ-008 | D-003、6、9.1 |
| REQ-009 | D-009、D-012、10 |
| REQ-010 | D-005、D-012、10.1 |
| REQ-011 | D-004、D-011、9.3 |
| REQ-012 | D-001、10.2 |
| REQ-013 | D-011、D-013、D-014、9.3、10 |
