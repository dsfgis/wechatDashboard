# WechatDashboard（IM 消息看板）

WechatDashboard 是一个仅面向 Windows 的 WPF 桌面应用，用于在本机汇集微信、石化通等协作消息，并完成消息去重、`@我` 识别、项目归类、紧急度排序、待办管理、桌面日历和项目看板展示。业务数据保存在项目目录下的本地 SQLite 数据库中；微信读取器状态和桌面日历偏好保存在当前用户的 `%LocalAppData%\WechatDashboard`。

> [!IMPORTANT]
> **编译成功不等于能够读取微信或石化通消息。** 微信读取还需要已登录且有本地消息数据的 Windows 微信客户端、完整的 KeyProbe/`wx_key.dll` 组件，以及合法取得的当前账户数据库 Key；石化通读取需要已登录的兼容客户端进程，并且当前版本的 `LxMainNew` 已加载 `imcore.dll`。客户端升级后，内部数据库或进程布局变化也可能导致读取失效。

本项目只应用于当前用户本人或已明确授权的数据。不要用它读取无权访问的账户、设备或消息。

## 主要功能

- 从微信本地数据库读取当天或指定初始化范围内的消息。
- 从石化通本地加密数据库增量读取消息，不依赖窗口 OCR。
- 支持微信、石化通、飞书、钉钉的 JSONL/JSON 文件导入入口。
- 统一消息去重，并在“消息流”中显示明确的来源标签。
- 根据用户别名识别 `@我` 和 `@所有人`，自动生成待办；截止时间可识别会议开始时间或“本周三”等本周日期，未识别时默认发送当天日末；也可创建手工待办、自定义分组和分组颜色。
- 消息流手动“转为待办”和自动 `@我` / `@所有人` 待办共用大模型分析协议，生成中文待办说明并判断 P0–P3 优先级；模型不可用时安全回退。
- 待办支持完成、提醒、编辑和追溯原消息；创建和详情中的截止时间使用日期、小时、分钟选择控件。
- 主窗口提供“日程概览”，可在右侧月历中按日期查看待办并打开详情。
- 提供半透明桌面待办日历，支持拖动、缩放、透明度调节、四边吸附和离开后自动收起；窗口状态会自动保存。
- 按项目、时间或群名聚合消息和待办，提供柱状图、环形图和折线图展示。
- 所有业务数据落在本地 SQLite；采集进度通过增量游标持久化。

## 技术栈与项目结构

- .NET 10、C#、WPF
- SQLite / SQLCipher
- 原生 C# 微信本地数据库读取器（SQLCipher V4、WAL/SHM 快照和 zstd/XML 内容解析）
- 独立 x64 `WechatDashboard.KeyProbe` 进程，用于隔离加载授权的 `wx_key.dll`
- Python 读取器仅作为冻结的兼容性测试基准，不在生产读取路径中启动

```text
src/WechatDashboard.Domain/          领域实体和枚举
src/WechatDashboard.Application/     采集管线、分类、待办和提醒等应用逻辑
src/WechatDashboard.Infrastructure/  SQLite 仓储及微信/石化通采集适配器
src/WechatDashboard.App/             WPF 桌面界面
src/WechatDashboard.KeyProbe/        独立的微信 DB Key 探测进程
tests/WechatDashboard.Tests/         .NET 回归测试程序
tools/wechat-local-reader/           Python 兼容性基准读取器及其测试
tools/wx-key-tools/                  微信 Key 工具包装脚本、完整工具包及 SHA-256 清单
tools/result/                        本机运行结果和敏感数据；已被 Git 忽略
design/                              设计和系统测试文档
```

## 环境要求

建议在 Windows 10/11 x64 上运行。源码环境需要：

1. Git。
2. x64 .NET 10 SDK。只有 Runtime 不能完成源码编译。
3. Windows 桌面微信客户端，以及需要读取时的石化通客户端。
4. 若使用微信自动提取 Key，使用仓库中随附的 `wx_key-windows-v2.1.8` 工具包。该包包含未签名的第三方二进制，使用前应按组织安全要求复核 `tools\wx-key-tools\README.md` 和哈希清单。
5. 仅在运行 Python 兼容性基准测试时需要 Python 3.10+，以及 `cryptography`、`zstandard`；启动应用和生产读取微信不依赖 Python。

可先检查环境：

```powershell
git --version
dotnet --info
# 仅运行 Python 兼容性测试时检查
python --version
```

## 下载、编译和启动

在 PowerShell 中执行：

```powershell
git clone <repository-url>
Set-Location wechatDashboard

dotnet restore WechatDashboard.sln
dotnet build WechatDashboard.sln --configuration Release --no-restore -m:1 -v minimal
```

建议在首次启动前运行测试：

```powershell
dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --configuration Release --no-restore --no-build
```

启动 WPF 应用：

```powershell
dotnet run --project src\WechatDashboard.App\WechatDashboard.App.csproj --configuration Release --no-restore
```

应用运行和微信初始化会按需创建以下目录和文件：

```text
tools\result\data\wechat-dashboard.db
tools\result\capture-inbox\
%LocalAppData%\WechatDashboard\Reader\state.json
%LocalAppData%\WechatDashboard\Reader\protected-wechat-key.bin
%LocalAppData%\WechatDashboard\desktop-calendar.json
```

当前版本尚不是经过干净机器验收的完整安装包。**不要只把 `WechatDashboard.App.exe` 单独复制到另一台机器运行**；至少还需要应用依赖、`WechatDashboard.KeyProbe.exe` 和经过核验的 `wx_key.dll`。源码运行时，程序会在仓库构建输出和工具目录中查找这些组件。

## 新环境读取微信消息

### 1. 准备微信本地数据

1. 在新环境安装 Windows 微信客户端并登录需要读取的本人账户。
2. 确认该机器已经同步或产生了本地聊天记录；只有云端存在、但本机数据库中没有的消息无法读取。
3. 保持微信进程运行。程序识别的进程名是 `Weixin`。
4. 程序会自动寻找包含 `db_storage\message` 的账户目录，常见根目录包括：

   ```text
   %USERPROFILE%\cache\xwechat_files\<账户>\db_storage
   %USERPROFILE%\WeChat Files\<账户>\db_storage
   %USERPROFILE%\Documents\WeChat Files\<账户>\db_storage
   <其他磁盘>:\WeChat Files\<账户>\db_storage
   ```

如果“采集诊断”显示“未检测到微信数据目录”，先确认微信已登录且本机确实生成了数据库，再检查实际文件保存位置是否属于上述扫描范围。

### 2. 准备微信数据库 Key

微信本地数据库是加密的，必须使用当前账户、当前本机数据库对应的 DB Key。当前界面只提供受控的 KeyProbe 自动提取流程，不提供明文 Key 手工输入。

#### 独立 KeyProbe 自动提取

1. 确认完整工具包已经随 Git 克隆到本机：

   ```text
   tools\wx-key-tools\wx_key-windows-v2.1.8\
   tools\wx-key-tools\wx_key-windows-v2.1.8\data\flutter_assets\assets\dll\wx_key.dll
   ```

2. 在仓库根目录执行以下校验；输出应与 `tools\wx-key-tools\SHA256SUMS.txt` 一致：

   ```powershell
   Get-FileHash -Algorithm SHA256 tools\wx-key-tools\wx_key-windows-v2.1.8\data\flutter_assets\assets\dll\wx_key.dll
   ```

3. 确认之前执行的是整个解决方案构建；`WechatDashboard.KeyProbe.exe` 是独立项目，不能只编译 WPF 项目后假设它已经存在。
4. 登录并打开微信，然后启动 WechatDashboard。
5. 进入“采集源设置”或“采集诊断”，选择首次初始化的“历史范围”（7 天、30 天或全部）。
6. 点击“自动提取 Key”。按照界面提示，在 5 分钟内重新登录微信，不要提前关闭微信进程。
7. “自动提取 Key”会在同一次流程中完成数据库验证和原生读取器初始化；成功后无需再点击“初始化本地库”。后者用于重新执行初始化流程。

KeyProbe 作为独立 x64 进程运行，通过当前用户限定的随机命名管道把 Key 交给应用；WPF 本身不会直接加载 `wx_key.dll`。验证成功后，Key 使用 Windows DPAPI CurrentUser 加密保存到 `%LocalAppData%\WechatDashboard\Reader\protected-wechat-key.bin`，后续启动会先从该缓存恢复并重新验证。不要把 DB Key 写入 README、问题单、聊天记录、截图或命令行参数。

### 3. 启用并读取微信

1. 初始化成功后，打开“采集源设置”。
2. 确认“微信本地数据库”已启用，点击“保存设置”。
3. 打开“微信消息”，点击“读取当天消息”，确认可以看到群名、发送人、时间和正文。
4. 若要把消息送入统一消息流、项目看板和待办管线，点击顶部“采集一次”或“开始监听”。

应用窗口加载完成后会自动启动监听。虽然界面文字仍写作“微信监听”，实际循环会轮询**所有已启用采集源**，包括微信本地数据库和石化通本地数据库。

### 4. 验证微信初始化结果

初始化成功后，无密钥状态和受保护 Key 默认位于：

```text
%LocalAppData%\WechatDashboard\Reader\state.json
%LocalAppData%\WechatDashboard\Reader\protected-wechat-key.bin
```

读取过程中会使用以下快照和临时目录：

```text
%LocalAppData%\WechatDashboard\Reader\Snapshots\
%LocalAppData%\WechatDashboard\Reader\Temp\
```

如果切换微信账户、微信重建了本地数据库，或原 Key 已失效，应先关闭应用，清除 `%LocalAppData%\WechatDashboard\Reader\protected-wechat-key.bin`，再重新执行 Key 提取。不要把该文件复制给其他 Windows 用户或设备。

## 消息行动分析 Agent

### 在界面配置 DeepSeek

1. 在主窗口左侧打开“模型配置”，填写 DeepSeek 平台的 API Key，然后点击“保存配置”。界面固定使用 `https://api.deepseek.com/chat/completions` 和 `deepseek-flash`，无需填写接口或模型名。
2. 保存或清除后重启应用。保存成功只表示密钥已写入本机，不代表连接或鉴权成功；本次运行始终使用启动时加载的配置。
3. 密钥使用当前 Windows 用户的 DPAPI 加密，不会明文回显或写入 SQLite。密文保存在：

```text
%LocalAppData%\WechatDashboard\Agent\protected-deepseek-api-key.bin
```

未保存本机密钥时，仍可使用 OpenAI Chat Completions 兼容、支持 `response_format: json_object` 的环境变量配置。存在本机密钥时优先使用 DeepSeek；清除本机密钥并重启后回退到以下环境变量：

```powershell
$env:WECHATDASHBOARD_AGENT_ENDPOINT = 'https://your-provider.example/v1/chat/completions'
$env:WECHATDASHBOARD_AGENT_MODEL = 'your-model-name'
$env:WECHATDASHBOARD_AGENT_API_KEY = '<从安全配置读取的密钥>'
```

### 手动“转为待办”分析

在“消息流”中点击某条消息的“转为待办”。若该消息已有待办，则直接打开现有详情，不重复调用模型；否则只把当前消息（包括采集时已合并的引用正文）发送给 Agent，单次返回 `shouldCreate`、P0–P3 优先级和简洁中文说明，不发送相邻消息或澄清回答。旧“AI 建议”、采纳、忽略和澄清流程已删除。

手动点击是明确创建指令，不受“分析消息内容”自动开关或消息是否 `@我` 限制，即使模型返回 `shouldCreate=false` 也会创建待办。Agent 说明只写入“待办说明”，不改写消息流原始正文。分析成功时，Agent 优先级同时用于新待办和该消息的消息流评分；评分与待办在同一事务写入，P0/P1/P2/P3 分别映射为 90/75/50/20 分。模型不可用、超时或响应无效时仍创建待办，回退到默认说明和本地优先级，不追加 Agent 评分；空白说明同样使用默认文本，但保留有效 Agent 优先级。

消息颜色按最新有效优先级显示：P0 鲜红、P1 橙、P2 蓝、P3 深灰；缺少评分时按 P3 显示。普通消息采集、列表刷新和自动待办分析不会调用大模型重排消息流。

### `@我` / `@所有人` 自动待办分析

1. 在“模型配置”页勾选“分析消息内容”。开关保存到本机 SQLite 并立即生效，只影响之后新处理的消息；API Key 的保存或清除仍是重启后生效。
2. 开启后，新的 `@我` / `@所有人` 消息在自动创建待办前，由 Agent 单次返回 `shouldCreate`、P0–P3 优先级和简洁中文摘要。模型判定与当前用户无关、低优先级、仅通知或没有行动要求时，只保存消息，不创建待办；判定需要创建时才写入待办优先级和“待办说明”。未开启时不调用模型，仍按原规则自动创建待办。
3. Agent 只决定自动待办是否创建，并覆盖其优先级和说明，不改变标题、项目、截止时间或自动创建标记，也不改写消息流使用的本地紧急度评分。只有上述手动“转为待办”成功分析时才追加 Agent 消息流评分。
4. 模型未配置、异常、超时或响应格式错误不会被当成“无需创建”：系统仍自动创建待办，并回退到本地优先级和默认说明。空白说明使用默认文本，但保留有效模型优先级。重复消息不重复调用模型，已有消息和待办不自动批量回填或重算。

模型请求会把上述消息数据发往 DeepSeek 或环境变量所配置的服务：手动转待办和自动待办分析都只发送当前消息（包括采集时已合并的引用正文），不发送相邻消息或澄清回答。在使用真实工作消息前，请按公司的数据处理规则选择获准的模型服务。界面保存的密钥仅以 DPAPI 密文写入本机文件，环境变量密钥仍只从运行环境读取，两者都不会写入 SQLite。

## 待办日期与日程概览

自动待办的截止时间由本地规则解析：优先使用消息中明确的会议开始时间；其次识别“本周三”“这周五”等本周星期，按消息发送所在周（周一开始）定位日期，没有具体时刻时使用目标日日末；均未识别时使用消息发送当天日末。日程概览和桌面日历均按同一个待办截止时间分组。

1. 在“待办理”中点击“添加待办”或打开待办详情。截止时间使用日期选择器及小时、分钟下拉框；清空日期表示无截止时间。新选择的日期默认 `23:59`，已有截止时间会保留原时分。
2. 在主窗口左侧点击“日程概览”，右侧会显示 6×7 月历、所选日期的待办及状态摘要。使用上月、今天、下月切换月份；点击待办标题可打开详情。
3. 如需独立常驻窗口，在主窗口左侧点击“桌面日历”。日程概览和桌面日历使用同一份待办数据、月历视图和详情流程。
4. 在独立窗口中拖动标题栏可移动窗口；开启“自动吸附”后，靠近屏幕边缘会自动停靠，也可点击“停靠边缘”。
5. 停靠后鼠标离开 2 秒，日历会缩进屏幕边缘；点击保留的蓝色把手即可展开。
6. 窗口位置、大小、透明度、吸附开关和停靠边缘保存在 `%LocalAppData%\WechatDashboard\desktop-calendar.json`。关闭日历只会隐藏窗口，退出主程序时才结束它。

## 新环境读取石化通消息

石化通读取器直接以只读权限访问本机石化通进程中的数据库目录和 Key，然后复制数据库及 WAL/SHM 到临时快照后读取。它不需要 `wx_key.dll`、Python 或可见窗口 OCR。

### 操作步骤

1. 安装与当前读取器兼容的 Windows 石化通客户端。
2. 登录需要读取的本人账户，并保持客户端运行，直到 `LxMainNew` 进程已经加载 `imcore.dll`。仅看到同名进程并不足够。
3. 启动 WechatDashboard，打开“采集诊断”。
4. 确认 `Shihuatong.LocalDatabase` 显示“已就绪”。如果仍显示“等待石化通运行”，先在石化通中完成登录并进入正常工作界面，再刷新诊断。
5. 打开“采集源设置”，确认“石化通本地数据库 / `ShihuatongLocalDatabase`”已启用，然后点击“保存设置”。
6. 点击“采集一次”，或保持“开始监听”运行。
7. 打开“消息流”，按“来源”列确认消息显示为“石化通”。石化通没有单独的消息页，统一从“消息流”查看。

首次读取石化通时，当前实现从**当天 00:00**开始建立增量游标；之后只读取游标以后的新增消息。它不会因为选择了微信的“历史范围”而回读石化通历史消息。

当前消息带有 `reference_seq` 时，读取器会在同一会话中按 `cider + server_seq` 联查被引用记录，并将非空、未重复的引用正文以“引用消息：”附在当前正文后。合并内容会进入消息展示、自动待办分析、本地紧急度和截止时间解析，避免引用的会议主题、时间和链接等信息丢失；找不到引用或正文已包含引用时不会重复拼接。

### 石化通兼容限制

当前实现依赖以下客户端内部特征：

- 进程名为 `LxMainNew`；
- 目标进程已加载 `imcore.dll`；
- 数据库管理器内存偏移和元数据结构与当前实现一致；
- 本地库为代码当前支持的 SQLCipher 3 兼容格式；
- 账户目录中存在 `mdb.db` 和 `msg_1.db` 至 `msg_5.db` 中的至少一个消息库。

这些是运行时兼容条件，不是编译器可以验证的条件。石化通升级后若出现“当前版本与本地数据库读取器不兼容”“元数据格式不受支持”或“数据库解密失败”，应先按客户端版本变化处理，不能通过反复编译解决。

## 常见问题

| 现象 | 可能原因与处理 |
| --- | --- |
| 编译成功，但微信显示“原生 C# 微信读取器不可用” | 构建整个解决方案，确认 `WechatDashboard.KeyProbe.exe` 已生成且 `wx_key.dll` 位于受支持的工具目录；不要只复制 WPF EXE。 |
| 微信显示“未找到 Weixin 进程” | 先登录并打开 Windows 微信客户端；确认进程名是 `Weixin`。 |
| 微信显示“未检测到数据目录” | 本机没有生成聊天数据库，或实际存储路径不在自动扫描范围。先确认 `db_storage\message` 实际存在。 |
| “自动提取 Key”失败或提示缺少 DLL | 先确认 Git 已完整下载 `tools\wx-key-tools\wx_key-windows-v2.1.8`，再构建整个解决方案；不要只复制 WPF EXE。用 `SHA256SUMS.txt` 检查 `wx_key.dll` 是否损坏或被替换。 |
| 读取微信或石化通进程时提示“拒绝访问” | 先让看板与目标客户端以相同 Windows 用户和相同权限级别运行；仍失败时再以管理员身份启动看板。不要为绕过组织安全策略而关闭防护软件。 |
| 微信初始化失败或重启后无法恢复 | Key 未取得、受保护缓存不属于当前 Windows 用户、Key 与数据库不匹配，或数据库快照/结构不兼容。查看界面中的分阶段脱敏错误；必要时重置 Reader 后重新提取。 |
| 石化通一直显示“等待运行” | 客户端尚未完成登录，或当前 `LxMainNew` 没有加载 `imcore.dll`。也可能是客户端版本/进程名已经变化。 |
| 石化通提示数据库正在持续写入 | 程序连续三次未得到一致快照；等待片刻后再点“采集一次”。 |
| 石化通只能看到当天消息 | 这是当前首次游标的设计，不是界面筛选问题；微信“历史范围”不影响石化通。 |
| 消息未进入“消息流” | 检查“采集源设置”中对应来源是否启用并已保存，再点击“采集一次”。“微信消息”页读取与统一采集源的展示入口不同。 |

## 本地数据与安全

以下内容可能包含受保护的 DB Key、数据库快照、聊天正文、联系人或项目数据：

```text
tools\result\data\wechat-dashboard.db
tools\result\capture-inbox\
%LocalAppData%\WechatDashboard\Reader\
```

`tools\result` 已被 `.gitignore` 忽略；`protected-wechat-key.bin` 使用当前 Windows 用户的 DPAPI 保护，但其他运行数据不因此自动加密。请遵守以下要求：

- 不要提交、上传、截图或转发 `protected-wechat-key.bin`、数据库快照和真实消息文件。
- 不要把 `tools\result` 放进公共网盘或未经批准的同步目录。
- 需要迁移电脑时，按组织的数据安全规则单独处理运行数据；不要把旧 Key 当作普通配置复制。
- 不要在日志、Issue 或故障描述中粘贴 Key、完整消息正文、联系人身份或内存地址。
- 仓库随附的第三方 `wx_key.dll` 及工具包必须保留版本、来源、哈希、依赖、安全扫描和签名状态记录；不要用未知版本直接覆盖。

## 随仓库工具包的完整性与风险

仓库跟踪 `tools\wx-key-tools\wx_key-windows-v2.1.8` 的完整 2.1.8 工具包，便于新环境克隆后具备自动提取微信 DB Key 所需的 DLL 和依赖布局。当前包共 27 个文件、86,386,635 字节，单文件均低于 GitHub 100 MB 限制。

关键文件 SHA-256：

```text
wx_key.exe  05862a20389ea54b8540850f4260a769d3b6e4490686001f2fee38b1b5af2053
wx_key.dll  f946ef8cb2a59bc03ce0b6ae0e22ed905a57e4c8228ed6b1c2b07fd54ecb9a05
```

全部文件哈希见 `tools\wx-key-tools\SHA256SUMS.txt`，工具包审计说明见 `tools\wx-key-tools\README.md`。需要注意：

- 项目维护者已确认所选工具包可以随项目外部分发，但该确认不应被理解为向第三方授予新的开源许可证。
- 当前核心 EXE/DLL 没有 Authenticode 签名，Windows 无法通过发布者签名确认来源。
- 包内带有 Flutter 的压缩 `NOTICES.Z`，但没有独立、明文的根级许可证或上游来源说明。
- 本次入库时 Windows Defender 处于禁用状态，Defender 自定义扫描未能执行；上传前的安全扫描状态为“未完成”，不是“扫描通过”。应在发布或生产部署前使用组织批准且处于启用状态的安全产品重新扫描。
- 工具只能在本人或已获授权的账户和设备上使用；自动提取 Key 仍需用户在界面中主动触发。

## 文件导入备用入口

当客户端本地库暂时不兼容时，可将合法导出的 JSONL/JSON 放入：

```text
tools\result\capture-inbox\WeChat
tools\result\capture-inbox\WeChatLocalExport
tools\result\capture-inbox\Shihuatong
tools\result\capture-inbox\Feishu
tools\result\capture-inbox\DingTalk
```

通用 JSONL 每行一条消息，例如：

```json
{"id":"wx-1","platform":"WeChat","chatId":"crm","chatName":"CRM项目群","senderName":"王经理","content":"@张三 今天处理线上故障","sentAt":"2026-06-03T10:00:00+08:00","messageType":"Text"}
```

文件放入后，在“采集源设置”中启用相应 JSONL/本地导出来源并保存，再点击“采集一次”。

## 开发与验证命令

```powershell
dotnet restore WechatDashboard.sln
dotnet build WechatDashboard.sln --configuration Release --no-restore -m:1 -v minimal
dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --configuration Release --no-restore --no-build

# 可选：运行冻结的 Python 兼容性基准测试
python -m pip install --upgrade cryptography zstandard
python -m unittest discover -v -s tools\wechat-local-reader -p test_wechat_local_reader.py

dotnet run --project src\WechatDashboard.App\WechatDashboard.App.csproj --configuration Release --no-restore
```

构建和测试应串行执行，避免多个进程同时写入 `bin`/`obj` 导致 DLL 锁定。如果 WPF 正在运行，可关闭它后重新构建；开发时也可临时把独立构建输出写到 `tools\result\build-check`。

## 当前边界

- 当前仓库以源码运行和开发验证为主，尚未提供经过干净机器验收的完整安装器。
- 微信生产读取路径已切换为原生 C#；Python 读取器仅保留为冻结测试基准。离线夹具与字段级对照通过不等于真实微信版本或新机器已经验收。
- 客户端内部格式并非公开稳定接口，微信或石化通升级后需要重新做兼容验证。
- 仓库当前没有根级 `LICENSE` 文件；随附工具包是为本项目部署而提交，不代表项目代码或第三方二进制获得了不受限制的自由再分发许可。

第三方来源和许可证提示见 `tools\wechat-local-reader\THIRD-PARTY-NOTICES.md`。
