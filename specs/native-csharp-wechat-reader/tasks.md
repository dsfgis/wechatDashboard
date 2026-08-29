# 原生 C# 微信本地消息读取实施任务

## 1. 执行规则

- 本文件在 requirements.md、design.md、tasks.md 和 check_list.md 四份文档被用户整体确认后才可执行。
- 实施时必须保留工作区已有的无关未提交修改；不得用 reset、checkout 或批量覆盖清理它们。
- 任务按依赖顺序执行。只有标记“可并行”的任务才能并行，且并行任务不得编辑同一文件。
- 每个任务完成后必须运行其验证方法并记录证据。没有证据不得标记完成。
- Python 只允许作为迁移期 golden oracle；生产 C# 链路和最终 Release 禁止调用 Python。
- 若实现发现密码学参数、schema 或 DLL 接口与本文实质不符，必须停止实施、修订四份文档并重新取得确认。

## 2. 任务列表

### TASK-001：冻结迁移 oracle 与离线 fixture

- 交付物：整理现有 Python reader 的正式行为契约；补齐不含真实消息/密钥的 SQLCipher V4、WAL、SessionTable、联系人、Name2Id、跨分片、zstd/XML、排序、分页和 offset fixtures；记录 oracle 版本与哈希。
- 需求/设计引用：REQ-002、REQ-003、REQ-004、REQ-005、REQ-010；D-005 至 D-008、D-012。
- 覆盖验收：AC-005、AC-006、AC-008 至 AC-013、AC-023、AC-025。
- 依赖：四份文档已确认。
- 验证方法：运行 python -m unittest discover -v -s tools\wechat-local-reader -p test_wechat_local_reader.py；计算 fixture 清单哈希；运行敏感串扫描。
- 预期结果：全部 Python oracle 测试通过；fixture 可重复生成或校验；不含真实正文和 64 位十六进制密钥。
- 并行：否，必须先冻结基线。

### TASK-002：定义 C# reader、阶段诊断与错误契约

- 交付物：新增 IWeChatLocalDatabaseReader、初始化/读取请求与结果、阶段枚举、稳定错误码、WeChatReaderException、脱敏器及诊断模型；保持 WeChatLocalMessagePage 外部契约兼容。
- 需求/设计引用：REQ-005、REQ-006、REQ-007；D-002、D-008、D-010，设计第 5 节。
- 覆盖验收：AC-011 至 AC-018。
- 依赖：TASK-001。
- 验证方法：在 tests\WechatDashboard.Tests 中增加契约、错误码、脱敏和 JSON/显示边界测试；运行 dotnet build WechatDashboard.sln --configuration Release；运行 dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --configuration Release --no-build。
- 预期结果：Release 构建 0 错误；全部 .NET 测试通过；错误输出不包含测试密钥或正文。
- 并行：否，为后续组件提供共同契约。

### TASK-003：改造二进制密钥租约和 KeyProbe 生命周期

- 交付物：让 WeChatDatabaseKeyLease 以受限字节访问支持 C# reader；新增 WeChatReaderSession；删除生产初始化链路的 ToHexString、ImportedDatabaseKey 和密钥环境变量传递；覆盖重初始化、取消、异常、退出清零；加固 KeyProbe 导出缺失、超时和 CleanupHook 处理。
- 需求/设计引用：REQ-001、REQ-002、REQ-007、REQ-008；D-001、D-003。
- 覆盖验收：AC-001 至 AC-004、AC-017 至 AC-020。
- 依赖：TASK-002。
- 验证方法：增加 KeyProbe 参数/缺失 DLL/缺失导出/超时/管道断开、租约零化与重复初始化测试；运行 Release 构建和 .NET 测试；对构建/测试输出运行 64 位十六进制敏感串扫描。
- 预期结果：KeyProbe 故障不导致测试宿主崩溃；成功和所有失败路径均释放租约；生产代码不再把 Key 转为字符串或环境变量。
- 并行：可与 TASK-004 并行，前提是不编辑相同路径/状态文件。

### TASK-004：拆分安装根目录和全部用户可变数据目录

- 交付物：重构 ProjectToolPaths，定义 InstallRoot 与 `%LocalAppData%\WechatDashboard` UserDataRoot；把消息主数据库、offset、采集设置、采集收件箱、reader 状态、快照、临时文件和日志全部迁入 UserDataRoot；新增无密钥 WeChatReaderStateStore、readerVersion 迁移和安全临时目录；兼容开发源码布局但不依赖固定仓库路径。
- 需求/设计引用：REQ-008、REQ-011、REQ-013；D-004、D-011、D-012，设计第 6 节。
- 覆盖验收：AC-019、AC-026、AC-027、AC-032。
- 依赖：TASK-002。
- 验证方法：用包含空格、中文和长路径边界的临时根目录运行路径/状态测试；覆盖源码目录缺失、`Program Files` 只读安装、目录缺失、权限拒绝、升级迁移和卸载保留数据；扫描安装目录与 state/config/log/temp 文件内容，确认安装目录无运行写入且无 64 位十六进制密钥。
- 预期结果：安装文件只读定位正确，全部可变文件进入用户目录；标准用户可从只读安装目录运行；路径测试全通过；卸载数据策略可由安装层识别。
- 并行：可与 TASK-003 并行。

### TASK-005：实现数据库发现和一致性快照

- 交付物：新增 WeChatDatabaseDiscovery、数据库描述模型和 WeChatDatabaseSnapshotService；发现 session/contact/message_N 数据库，成组复制 DB/WAL/SHM，使用完成标记并清理失败快照。
- 需求/设计引用：REQ-002、REQ-003、REQ-011；D-004、设计第 6、7 节。
- 覆盖验收：AC-003、AC-004、AC-006、AC-007、AC-026、AC-027。
- 依赖：TASK-002、TASK-004。
- 验证方法：使用缺失必需库、含 WAL/SHM、复制中变化、取消、磁盘/权限失败的临时 fixture；比较源文件哈希和时间戳；运行 Release 构建与 .NET 测试。
- 预期结果：有效组生成稳定快照；失败组不被标记完成；源文件未变化；错误阶段准确。
- 并行：可与 TASK-006 并行。

### TASK-006：实现逐库密钥派生和首页验证

- 交付物：新增 WeChatDatabaseKeyDeriver 与 WeChatDatabaseKeyValidator；实现已冻结的 PBKDF2-HMAC-SHA512 参数、V4 首页 HMAC、reserve 64/80、必需数据库验证和派生密钥零化。
- 需求/设计引用：REQ-002、REQ-008、REQ-010；D-003、D-006。
- 覆盖验收：AC-003 至 AC-005、AC-019、AC-020、AC-023、AC-025。
- 依赖：TASK-001、TASK-002、TASK-003。
- 验证方法：逐一运行有效 Key、无效 Key、不同 salt、reserve 64/80、截断页和取消 fixture；与 Python oracle 输出对照；运行 Release 构建与 .NET 测试。
- 预期结果：每个 fixture 的派生/验证结果与 oracle 一致；无效输入稳定失败；缓冲区清零测试通过。
- 并行：可与 TASK-005 并行。

### TASK-007：实现加密数据库访问层并通过兼容性门槛

- 交付物：定义 IWeChatEncryptedDatabaseAccessor；先实现 SqlCipherDatabaseAccessor 并固定经过 fixture 验证的参数。若门槛失败，记录证据并实现 ManagedPageDecryptingAccessor；托管实现的临时明文快照必须受限并及时删除。
- 需求/设计引用：REQ-002、REQ-003、REQ-008、REQ-010；D-004 至 D-006。
- 覆盖验收：AC-005 至 AC-007、AC-019、AC-020、AC-023、AC-025。
- 依赖：TASK-005、TASK-006。
- 验证方法：对相同 V4/WAL/reserve fixtures 执行 SQLCipher 直读；记录每项门槛结果；如启用托管回退，逐页与 oracle 比较并验证取消/异常后临时明文删除；运行 Release 构建和 .NET 测试。
- 预期结果：至少一个受支持 accessor 对全部冻结 fixture 可重复打开并查询；失败分类稳定；源库未修改；无残留密钥或不完整明文文件。
- 并行：否，后续 schema reader 依赖此访问层。

### TASK-008：实现微信 V4 schema 与跨分片读取

- 交付物：新增 WeChatV4SchemaReader；支持 SessionTable、联系人映射、Name2Id、Msg_<md5(username)>、字段别名、跨 message_N.db 聚合、发送者解析和稳定源消息 ID。
- 需求/设计引用：REQ-004、REQ-007、REQ-010；D-007、D-010。
- 覆盖验收：AC-008、AC-009、AC-017、AC-023、AC-025。
- 依赖：TASK-007。
- 验证方法：运行缺表、缺列、真实 V4 别名、多会话、多分片、重复行和损坏分片 fixtures；逐字段对比 Python oracle；运行 Release 构建和 .NET 测试。
- 预期结果：受支持 fixture 输出一致；缺失必需 schema 返回 SchemaUnsupported 而非空成功；可隔离损坏分片计数准确。
- 并行：可与 TASK-009 并行，双方只依赖共同 accessor/契约。

### TASK-009：实现 zstd、XML 与消息类型解码

- 交付物：选定并锁定经许可证审查的 zstd .NET 依赖；新增 WeChatMessageContentDecoder，支持普通文本、群聊前缀、zstd、图片/视频/表情/文件/链接/位置/系统 XML 摘要、安全 XML 设置与解压大小限制。
- 需求/设计引用：REQ-004、REQ-007、REQ-008、REQ-012；D-007，设计第 9 节。
- 覆盖验收：AC-009、AC-010、AC-017 至 AC-019、AC-025、AC-029。
- 依赖：TASK-001、TASK-002、TASK-007。
- 验证方法：逐个内容 fixture 与 Python oracle 比较；测试损坏 zstd、超大解压、DTD/外部实体和畸形 XML；生成依赖/许可证记录；运行 Release 构建和 .NET 测试。
- 预期结果：受支持摘要逐字段一致；恶意/损坏输入不访问外部资源、不无限分配、不泄露原始敏感 blob；依赖记录完整。
- 并行：可与 TASK-008 并行。

### TASK-010：实现统一查询、排序、分页和增量语义

- 交付物：新增 WeChatMessageQueryService；合并 schema/content 结果，按 ID 去重，按 sentAt、id 降序排序；实现日期边界、总数、分页、受控 lookback 和 ProposedNextOffset。
- 需求/设计引用：REQ-005、REQ-010；D-008，设计第 7 节。
- 覆盖验收：AC-011 至 AC-013、AC-023、AC-025。
- 依赖：TASK-008、TASK-009。
- 验证方法：运行同时间戳不同 ID、跨日/夏令时边界、空页、末页、页大小 1/200/越界、重复轮询和批次失败重放测试；与 Python oracle 比较。
- 预期结果：排序、总数、页内容和 next offset 一致；失败批次不推进已提交 offset；全部 .NET 测试通过。
- 并行：否。

### TASK-011：将 WeChatLocalReaderService 切换为 C# 编排

- 交付物：用 TASK-003 至 TASK-010 的组件重构 WeChatLocalReaderService；移除 Python 路径发现、Python 命令构造、外部 JSON 解析、all_keys.json 就绪判断和密钥环境变量；保留 UI 所需公共行为和阶段诊断。
- 需求/设计引用：REQ-002、REQ-005、REQ-006、REQ-007、REQ-008；D-002、D-003、D-010。
- 覆盖验收：AC-003、AC-011 至 AC-020。
- 依赖：TASK-003 至 TASK-010。
- 验证方法：改写现有 reader service 测试为 C# fake/fixture；监控测试进程树确保不启动 python/python3/py；测试初始化、分页、取消、重初始化和错误展示；运行 Release 构建与 .NET 测试。
- 预期结果：服务行为兼容，生产执行路径无 Python 子进程和密钥文本，阶段错误可观察。
- 并行：否。

### TASK-012：替换采集适配器并迁移保存设置

- 交付物：新增 WeChatLocalDatabaseCaptureAdapter；CaptureAdapterFactory 默认创建原生适配器；将旧 WeChatLocalCommand 设置幂等迁移到 WeChatLocalDatabase；保留消息页、采集一次和监听的共同核心；最终生产注册移除命令适配器。
- 需求/设计引用：REQ-005、REQ-006、REQ-009、REQ-011；D-008、D-009、D-011。
- 覆盖验收：AC-013 至 AC-016、AC-021、AC-026、AC-027。
- 依赖：TASK-011。
- 验证方法：运行设置迁移、适配器 offset、管线提交/回滚、@我 Todo、关注群和 UI ViewModel/服务测试；检查进程树和 factory 注册；运行 Release 构建与完整 .NET 测试。
- 预期结果：所有入口使用 C# reader；旧设置无损迁移；现有业务管线行为不回归；生产 factory 无 Python adapter。
- 并行：否。

### TASK-013：完成离线逐字段对照和全量回归

- 交付物：测试构建专用双跑 harness、逐字段差异报告、fixture 覆盖清单、性能与资源上限记录；不得进入 Release。
- 需求/设计引用：REQ-004、REQ-005、REQ-007、REQ-008、REQ-010；D-007、D-008、D-010、D-012。
- 覆盖验收：AC-008 至 AC-013、AC-017 至 AC-020、AC-023、AC-025。
- 依赖：TASK-012。
- 验证方法：依次运行 Python oracle、dotnet build WechatDashboard.sln --configuration Release、dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --configuration Release --no-build、git diff --check 和敏感串扫描。
- 预期结果：oracle 与 C# 对照无未批准差异；Release 构建 0 错误；全部 .NET/Python 测试通过；无 diff whitespace 或敏感输出问题。
- 并行：否。

### TASK-014：执行用户授权的真实微信脱敏验收

- 交付物：真实机 Hook、初始化、已知测试消息、分页、最小化微信、增量采集、去重、Todo 和诊断证据；只记录允许的聚合数据。
- 需求/设计引用：REQ-001 至 REQ-008、REQ-010；D-001、D-004 至 D-010，设计第 7 至 9 节。
- 覆盖验收：AC-001 至 AC-004、AC-006 至 AC-020、AC-024、AC-025。
- 依赖：TASK-013、TASK-018 至 TASK-020；每次 Hook 与真实数据验证前取得用户显式授权。
- 验证方法：按 design\wechat-local-database-system-test.md 的更新版步骤执行；用测试群发送一条已知 @我 消息；最小化微信后轮询；人为制造 KeyProbe 超时/DLL 不匹配；检查日志和应用数据库不含密钥。
- 预期结果：获准聚合项与 Python oracle 一致；已知测试消息进入管线且只生成一个 Todo；最小化不影响读取；故障不导致 WPF 崩溃或泄密。
- 并行：否，真实机串行执行。

### TASK-015：生成无 Python staging、安装工程并完成发布证据链

- 交付物：可复现的 self-contained win-x64 staging 和正式安装工程；明确包含主程序、KeyProbe、`wx_key.dll`、SQLCipher、zstd 及完整 win-x64 依赖；生成允许/拒绝清单、载荷 SHA-256 清单、实际 wx_key.dll 授权/来源/版本/依赖/扫描/签名记录，以及主程序、KeyProbe、安装包签名和 EDR 结果。
- 需求/设计引用：REQ-009、REQ-011 至 REQ-013；D-011 至 D-013，设计第 10 节。
- 覆盖验收：AC-021、AC-026 至 AC-032、AC-035。
- 依赖：TASK-013；发布打包前 TASK-014 必须通过。
- 验证方法：用固定命令连续生成两次 staging 和安装包并比较规范化载荷；扫描文件名、内容、运行时模式和依赖图；确认 `WechatDashboard.KeyProbe.exe` 与 `wx_key.dll` 在约定安装路径；计算实际 DLL 和安装包 SHA-256 并与记录比对；验证签名；执行安全软件/EDR 检查。
- 预期结果：staging 为 win-x64 self-contained，不要求预装 .NET；不含 Python、.py、PyInstaller、PowerShell Key 探测或明文 Key 文件；必需载荷完整；安装包可复现；所有二进制和证据哈希一致；签名有效。
- 并行：证据整理可与 TASK-014 的非冲突准备并行，但最终 staging 只能在真实机通过后生成。

### TASK-016：执行双来源新机、升级、卸载和回滚验收

- 交付物：干净 Windows x64 测试记录、无 Python/PATH/.NET SDK/.NET Desktop Runtime 证明、标准用户安装与启动记录、微信和石化通各自的真实消息/增量/故障诊断证据、路径兼容、升级/卸载数据保留和上一已签名版本回滚记录。
- 需求/设计引用：REQ-001、REQ-007、REQ-009 至 REQ-013；D-011 至 D-014，设计第 10 节。
- 覆盖验收：AC-022、AC-026 至 AC-035。
- 依赖：TASK-015。
- 验证方法：在隔离干净机以标准用户安装到 `Program Files` 或等价只读目录；确认没有 .NET/Python/SDK 仍能启动；分别执行 D-014 的微信门和石化通门；制造权限、版本或安全软件阻断并核对明确诊断；检查安装目录零运行写入；在空格和中文用户路径测试；升级后重建 reader 缓存；卸载时默认保留数据；安装上一签名版本完成回滚。
- 预期结果：同一安装包哈希在无预装运行时环境下启动；微信和石化通真实读取及第二次增量零重复均通过；不兼容不伪装成零消息；路径、升级、卸载和回滚符合设计；证据归档完整。
- 并行：否。

### TASK-017：清理生产兼容路径并更新文档

- 交付物：从生产代码和 Release 配置移除 WeChatLocalCommand/Python 查找、PowerShell probe、all_keys.json 和 plaintext Key 路径；保留且仅允许 DPAPI CurrentUser 密文缓存；更新 README、Agent.md、系统测试文档和发布说明，明确 Python 仅为源码测试 oracle，并写明安装前置条件、微信/石化通兼容边界、受保护 Key 缓存残余风险、数据目录、诊断、升级/卸载和回滚步骤。
- 需求/设计引用：REQ-006、REQ-008 至 REQ-015；D-002、D-009、D-012 至 D-016。
- 覆盖验收：AC-014、AC-019 至 AC-045。
- 依赖：TASK-016。
- 验证方法：rg -n -i 扫描生产源码和 staging 中的 python、wechat_local_reader.py、WECHAT_DASHBOARD_DB_KEY、all_keys.json、run-wx-key-probe.ps1、明文 Key 文件模式；确认仅受保护缓存路径获准；重新运行 TASK-013 和 TASK-020 的全量回归与 staging 扫描。
- 预期结果：生产与 Release 路径不存在 Python/明文 Key 兼容调用；唯一持久 Key 载体是 DPAPI CurrentUser 密文；文档与实际代码一致；测试 oracle 不会被打包。
- 并行：否。

### TASK-018：实现 DPAPI CurrentUser Key 缓存和启动恢复

- 交付物：新增 `IWeChatDatabaseKeyStore`、可替换的数据保护边界和 `ProtectedWeChatDatabaseKeyStore`；实现版本化二进制格式、大小限制、DPAPI CurrentUser 保护/解密、原子保存、升级保留、显式重置/卸载清理；把缓存恢复接入 `WeChatLocalReaderService`，成功验证的 KeyProbe 初始化才更新缓存。
- 需求/设计引用：REQ-008、REQ-011、REQ-014；D-003、D-011、D-015，设计第 6、7、9、10 节。
- 覆盖验收：AC-019、AC-020、AC-026、AC-027、AC-032、AC-036 至 AC-040。
- 依赖：本轮四份文档已确认；TASK-003、TASK-004、TASK-006、TASK-011 已具备实现基础。
- 验证方法：使用可注入 protector 测试有效、损坏、错误版本、超大文件、保护/解密失败、验证失败、取消和原子替换；运行同一 Windows 用户真实 DPAPI 保存/进程重启/恢复 smoke test；扫描安装目录、应用 SQLite、state.json、日志、临时目录和测试输出；检查所有明文缓冲区清零。
- 预期结果：同用户重启无需 KeyProbe 即 Ready；无效缓存安全失败并提示重新提取；失败提取不覆盖有效缓存；除 DPAPI 密文外无 Key 落盘或输出。
- 并行：否，先建立新的密钥生命周期边界。

### TASK-019：实现逐采集源结果和监听健康状态

- 交付物：新增 `CaptureSourceRunStatus`、`CaptureSourceRunResult` 并扩展 `CaptureRunResult`；让管线记录每个适配器的成功、合法空结果、跳过和脱敏失败；WPF 将结果写入采集诊断并显示正常/部分失败/全部失败；开始监听前触发一次受保护缓存恢复，微信未初始化时给出“请提取 Key”动作提示，同时允许健康来源继续。
- 需求/设计引用：REQ-007、REQ-015；D-010、D-016，设计第 7.3、8、9.1 节。
- 覆盖验收：AC-017、AC-018、AC-041 至 AC-045。
- 依赖：TASK-002、TASK-012、TASK-018。
- 验证方法：用 fake adapters 覆盖全成功零消息、微信失败/石化通成功、微信恢复、全部失败、持久化失败、取消和错误中含敏感串；验证各来源 offset、汇总计数、UI 状态文案、诊断行和错误脱敏。
- 预期结果：单来源失败可见但不阻断健康来源；失败来源不推进 offset；合法 0 条与失败不再混淆；微信恢复后后续轮询自动转为成功。
- 并行：否，与 TASK-018 串行完成启动状态衔接。

### TASK-020：验证重启复用、失效回退和发布生命周期

- 交付物：DPAPI/监听状态专项回归证据、真实 Debug 运行重启验收记录、升级/卸载缓存生命周期记录、敏感文件与输出扫描结果，以及更新后的系统测试说明。
- 需求/设计引用：REQ-008、REQ-010、REQ-011、REQ-013 至 REQ-015；D-003、D-010 至 D-016。
- 覆盖验收：AC-019、AC-020、AC-025 至 AC-027、AC-032、AC-033、AC-035 至 AC-045。
- 依赖：TASK-018、TASK-019。
- 验证方法：按仓库标准串行 Release 构建和自定义 .NET runner；运行 `git diff --check` 与敏感串扫描；在当前 Windows 用户完成一次显式提取后关闭/重启实际 Debug 应用，验证不启动 KeyProbe仍能读取；用损坏副本或 fake protector 验证失效提示；验证升级保留、显式重置和卸载删除缓存。
- 预期结果：构建和回归全绿；真实应用重启自动恢复；失效只影响微信并显示部分失败；无明文 Key；缓存生命周期与设计一致。
- 并行：否。

## 3. 增量执行顺序

~~~text
TASK-001 -> TASK-002 -> +-> TASK-003 -+
                         +-> TASK-004 -+-> TASK-005 -+
                                       +-> TASK-006 -+-> TASK-007
                                                          |
                                                +---------+---------+
                                                v                   v
                                             TASK-008            TASK-009
                                                +---------+---------+
                                                          v
                                                       TASK-010
                                                          v
                                                       TASK-011
                                                          v
                                                       TASK-012
                                                          v
                                                       TASK-013
                                                          v
                                                       TASK-018
                                                          v
                                                       TASK-019
                                                          v
                                                       TASK-020
                                                          v
                                                       TASK-014
                                                          v
                                                       TASK-015
                                                          v
                                                       TASK-016
                                                          v
                                                       TASK-017
~~~

## 4. 追踪检查

| 需求 | 任务 |
|---|---|
| REQ-001 | TASK-003、TASK-014 |
| REQ-002 | TASK-001、TASK-003、TASK-005 至 TASK-007、TASK-011、TASK-014 |
| REQ-003 | TASK-001、TASK-005、TASK-007、TASK-014 |
| REQ-004 | TASK-001、TASK-008、TASK-009、TASK-013、TASK-014 |
| REQ-005 | TASK-001、TASK-002、TASK-010 至 TASK-013 |
| REQ-006 | TASK-002、TASK-011、TASK-012、TASK-014、TASK-017 |
| REQ-007 | TASK-002、TASK-003、TASK-008、TASK-009、TASK-011、TASK-013、TASK-014 |
| REQ-008 | TASK-003、TASK-004、TASK-006、TASK-007、TASK-009、TASK-011、TASK-013、TASK-017 |
| REQ-009 | TASK-012、TASK-015 至 TASK-017 |
| REQ-010 | TASK-001、TASK-006 至 TASK-010、TASK-013、TASK-014、TASK-017 |
| REQ-011 | TASK-004、TASK-005、TASK-012、TASK-015、TASK-016 |
| REQ-012 | TASK-009、TASK-015、TASK-016 |
| REQ-013 | TASK-004、TASK-014 至 TASK-017 |
| REQ-014 | TASK-003、TASK-004、TASK-006、TASK-011、TASK-014、TASK-017、TASK-018、TASK-020 |
| REQ-015 | TASK-002、TASK-012、TASK-014、TASK-017、TASK-019、TASK-020 |

### 4.1 验收标准到任务的逐项映射

| 验收标准 | 任务 |
|---|---|
| AC-001 | TASK-003、TASK-014 |
| AC-002 | TASK-003、TASK-014 |
| AC-003 | TASK-003、TASK-005、TASK-006、TASK-011、TASK-014 |
| AC-004 | TASK-003、TASK-005、TASK-006、TASK-014 |
| AC-005 | TASK-001、TASK-006、TASK-007 |
| AC-006 | TASK-001、TASK-005、TASK-007、TASK-014 |
| AC-007 | TASK-005、TASK-007、TASK-014 |
| AC-008 | TASK-001、TASK-008、TASK-013、TASK-014 |
| AC-009 | TASK-001、TASK-008、TASK-009、TASK-013、TASK-014 |
| AC-010 | TASK-001、TASK-009、TASK-013、TASK-014 |
| AC-011 | TASK-001、TASK-002、TASK-010、TASK-011、TASK-013、TASK-014 |
| AC-012 | TASK-001、TASK-002、TASK-010、TASK-011、TASK-013、TASK-014 |
| AC-013 | TASK-001、TASK-002、TASK-010、TASK-012、TASK-013、TASK-014 |
| AC-014 | TASK-002、TASK-011、TASK-012、TASK-014、TASK-017 |
| AC-015 | TASK-002、TASK-011、TASK-012、TASK-014 |
| AC-016 | TASK-002、TASK-012、TASK-014 |
| AC-017 | TASK-002、TASK-003、TASK-008、TASK-009、TASK-011、TASK-013、TASK-014 |
| AC-018 | TASK-002、TASK-003、TASK-009、TASK-011、TASK-013、TASK-014 |
| AC-019 | TASK-003、TASK-004、TASK-006、TASK-007、TASK-009、TASK-011、TASK-013、TASK-017 |
| AC-020 | TASK-003、TASK-006、TASK-007、TASK-011、TASK-013、TASK-014、TASK-017 |
| AC-021 | TASK-012、TASK-015、TASK-017 |
| AC-022 | TASK-015、TASK-016、TASK-017 |
| AC-023 | TASK-001、TASK-006 至 TASK-010、TASK-013、TASK-017 |
| AC-024 | TASK-014、TASK-017 |
| AC-025 | TASK-001、TASK-006、TASK-008 至 TASK-010、TASK-013、TASK-014、TASK-017 |
| AC-026 | TASK-004、TASK-005、TASK-012、TASK-015、TASK-016 |
| AC-027 | TASK-004、TASK-005、TASK-012、TASK-015、TASK-016 |
| AC-028 | TASK-015、TASK-016 |
| AC-029 | TASK-009、TASK-015、TASK-016 |
| AC-030 | TASK-015、TASK-016 |
| AC-031 | TASK-015、TASK-016 |
| AC-032 | TASK-004、TASK-015、TASK-016 |
| AC-033 | TASK-014、TASK-016 |
| AC-034 | TASK-016 |
| AC-035 | TASK-015 至 TASK-017 |
| AC-036 | TASK-018、TASK-020 |
| AC-037 | TASK-018、TASK-020 |
| AC-038 | TASK-018 至 TASK-020 |
| AC-039 | TASK-018、TASK-020 |
| AC-040 | TASK-017、TASK-018、TASK-020 |
| AC-041 | TASK-019、TASK-020 |
| AC-042 | TASK-019、TASK-020 |
| AC-043 | TASK-019、TASK-020 |
| AC-044 | TASK-019、TASK-020 |
| AC-045 | TASK-017、TASK-019、TASK-020 |

所有 REQ-001 至 REQ-015 和 AC-001 至 AC-045 均由至少一个任务覆盖。实际完成状态以 check_list.md 中的证据为准。
