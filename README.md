# WechatDashboard（微信项目消息看板）

WechatDashboard 是一个仅面向 Windows 的 WPF 桌面应用，用于在本机汇集微信、石化通等协作消息，并完成消息去重、`@我` 识别、项目归类、紧急度排序、待办创建和项目看板展示。应用数据保存在项目目录下的本地 SQLite 数据库中。

> [!IMPORTANT]
> **编译成功不等于能够读取微信或石化通消息。** 微信读取还需要已登录且有本地消息数据的 Windows 微信客户端、Python 读取依赖，以及合法取得的当前账户数据库 Key；石化通读取需要已登录的兼容客户端进程，并且当前版本的 `LxMainNew` 已加载 `imcore.dll`。客户端升级后，内部数据库或进程布局变化也可能导致读取失效。

本项目只应用于当前用户本人或已明确授权的数据。不要用它读取无权访问的账户、设备或消息。

## 主要功能

- 从微信本地数据库读取当天或指定初始化范围内的消息。
- 从石化通本地加密数据库增量读取消息，不依赖窗口 OCR。
- 支持微信、石化通、飞书、钉钉的 JSONL/JSON 文件导入入口。
- 统一消息去重，并在“消息流”中显示明确的来源标签。
- 根据用户别名识别 `@我`，自动生成待办并支持完成、提醒和追溯原消息。
- 按项目、时间或群名聚合消息和待办，提供柱状图、环形图和折线图展示。
- 所有业务数据落在本地 SQLite；采集进度通过增量游标持久化。

## 技术栈与项目结构

- .NET 10、C#、WPF
- SQLite / SQLCipher
- Python 本地微信数据库兼容读取器
- 独立 x64 `WechatDashboard.KeyProbe` 进程，用于隔离加载授权的 `wx_key.dll`

```text
src/WechatDashboard.Domain/          领域实体和枚举
src/WechatDashboard.Application/     采集管线、分类、待办和提醒等应用逻辑
src/WechatDashboard.Infrastructure/  SQLite 仓储及微信/石化通采集适配器
src/WechatDashboard.App/             WPF 桌面界面
src/WechatDashboard.KeyProbe/        独立的微信 DB Key 探测进程
tests/WechatDashboard.Tests/         .NET 回归测试程序
tools/wechat-local-reader/           Python 微信本地库读取器及其测试
tools/wx-key-tools/                  微信 Key 工具包装脚本、完整工具包及 SHA-256 清单
tools/result/                        本机运行结果和敏感数据；已被 Git 忽略
design/                              设计和系统测试文档
```

## 环境要求

建议在 Windows 10/11 x64 上运行。源码环境需要：

1. Git。
2. x64 .NET 10 SDK。只有 Runtime 不能完成源码编译。
3. Python 3.10 或更高版本。当前源码优先调用 `tools\wechat-local-reader\wechat_local_reader.py`。
4. Python 加密与压缩依赖：AES 后端使用 `cryptography` 或 `pycryptodome`，zstd 后端使用 `zstandard` 或 `pyzstd`。下文采用 `cryptography + zstandard`。
5. Windows 桌面微信客户端，以及需要读取时的石化通客户端。
6. 若使用微信自动提取 Key，使用仓库中随附的 `wx_key-windows-v2.1.8` 工具包。该包包含未签名的第三方二进制，使用前应按组织安全要求复核 `tools\wx-key-tools\README.md` 和哈希清单。

可先检查环境：

```powershell
git --version
dotnet --info
python --version
```

## 下载、编译和启动

在 PowerShell 中执行：

```powershell
git clone <repository-url>
Set-Location wechatDashboard

python -m pip install --upgrade cryptography zstandard
dotnet restore WechatDashboard.sln
dotnet build WechatDashboard.sln --no-restore -v minimal
```

建议在首次启动前运行测试：

```powershell
dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --no-restore
python -m unittest discover -v -s tools\wechat-local-reader -p test_wechat_local_reader.py
```

启动 WPF 应用：

```powershell
dotnet run --project src\WechatDashboard.App\WechatDashboard.App.csproj --no-restore
```

首次启动会创建以下目录和文件：

```text
tools\result\data\wechat-dashboard.db
tools\result\capture-inbox\
tools\result\wechat-local-reader\
```

当前版本按源码目录布局查找 Python 读取器、KeyProbe 和生成数据，尚不是完整的安装包形态。**不要只把 `WechatDashboard.App.exe` 单独复制到另一台机器运行**；新环境应保留仓库目录结构，或先完成包含所有工具及运行时状态目录的正式发布打包。

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

微信本地数据库是加密的，必须使用当前账户、当前本机数据库对应的 64 位十六进制 DB Key。支持两种方式。

#### 方式 A：独立 KeyProbe 自动提取（推荐）

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
7. 界面提示 Key 提取成功后，立即点击“初始化本地库”。初始化会验证 Key，并在 `tools\result\wechat-local-reader` 下生成配置、Key 映射和解密结果。

KeyProbe 作为独立 x64 进程运行，优先通过当前用户的随机命名管道把 Key 交给应用；WPF 本身不会直接加载 `wx_key.dll`。源码中的 PowerShell/Key 文件链路仅保留为兼容回退。

#### 方式 B：手工导入已有 DB Key

如果已通过合法方式取得当前数据库的 Key，可在“采集源设置”或“采集诊断”顶部的“DB Key”密码框中输入 **64 个十六进制字符**，选择历史范围，然后直接点击“初始化本地库”。

不要把 DB Key 写入 README、问题单、聊天记录、截图或命令行参数。Key 必须与当前账户和当前本地数据库匹配；旧账户或客户端重建数据库前的 Key 通常不能继续使用。

### 3. 启用并读取微信

1. 初始化成功后，打开“采集源设置”。
2. 确认“微信本地数据库 / `WeChatLocalCommand`”已启用，点击“保存设置”。
3. 打开“微信消息”，点击“读取当天消息”，确认可以看到群名、发送人、时间和正文。
4. 若要把消息送入统一消息流、项目看板和待办管线，点击顶部“采集一次”或“开始监听”。

应用窗口加载完成后会自动启动监听。虽然界面文字仍写作“微信监听”，实际循环会轮询**所有已启用采集源**，包括微信本地数据库和石化通本地数据库。

### 4. 验证微信初始化结果

初始化就绪至少要求以下两个文件存在：

```text
tools\result\wechat-local-reader\config.json
tools\result\wechat-local-reader\all_keys.json
```

读取和解密过程中还可能生成：

```text
tools\result\wechat-local-reader\decrypted\
tools\result\wechat-local-reader\wx-key-found.txt   # 仅兼容回退路径可能生成
tools\result\wechat-local-reader\wx-key-probe.log   # 仅兼容回退路径可能生成
```

如果切换微信账户、微信重建了本地数据库，或原 Key 已失效，应先关闭应用，把 `tools\result\wechat-local-reader` 改名备份到安全位置，再重新执行 Key 提取和初始化。不要直接复用旧的 `config.json` 和 `all_keys.json`。

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
| 编译成功，但微信显示“本地读取器未安装” | 应从仓库根目录运行，并确认 `tools\wechat-local-reader\wechat_local_reader.py` 存在且 `python --version` 可执行。不要只复制 WPF EXE。 |
| 微信显示“未找到 Weixin 进程” | 先登录并打开 Windows 微信客户端；确认进程名是 `Weixin`。 |
| 微信显示“未检测到数据目录” | 本机没有生成聊天数据库，或实际存储路径不在自动扫描范围。先确认 `db_storage\message` 实际存在。 |
| “自动提取 Key”失败或提示缺少 DLL | 先确认 Git 已完整下载 `tools\wx-key-tools\wx_key-windows-v2.1.8`，再构建整个解决方案；不要只复制 WPF EXE。用 `SHA256SUMS.txt` 检查 `wx_key.dll` 是否损坏或被替换。 |
| 初始化提示缺少 AES/zstd 后端 | 执行 `python -m pip install --upgrade cryptography zstandard`。 |
| 读取微信或石化通进程时提示“拒绝访问” | 先让看板与目标客户端以相同 Windows 用户和相同权限级别运行；仍失败时再以管理员身份启动看板。不要为绕过组织安全策略而关闭防护软件。 |
| 微信初始化结束但没有 `config.json`/`all_keys.json` | Key 未取得、Key 与数据库不匹配、Python 依赖缺失或数据库正在被占用。查看界面摘要中的已脱敏错误后重新初始化。 |
| 石化通一直显示“等待运行” | 客户端尚未完成登录，或当前 `LxMainNew` 没有加载 `imcore.dll`。也可能是客户端版本/进程名已经变化。 |
| 石化通提示数据库正在持续写入 | 程序连续三次未得到一致快照；等待片刻后再点“采集一次”。 |
| 石化通只能看到当天消息 | 这是当前首次游标的设计，不是界面筛选问题；微信“历史范围”不影响石化通。 |
| 消息未进入“消息流” | 检查“采集源设置”中对应来源是否启用并已保存，再点击“采集一次”。“微信消息”页读取与统一采集源的展示入口不同。 |

## 本地数据与安全

以下内容可能包含 DB Key、已解密数据库、聊天正文、联系人或项目数据：

```text
tools\result\wechat-local-reader\
tools\result\data\wechat-dashboard.db
tools\result\capture-inbox\
```

`tools\result` 已被 `.gitignore` 忽略，但“被 Git 忽略”不代表“磁盘上已加密”。请遵守以下要求：

- 不要提交、上传、截图或转发 `wx-key-found.txt`、`all_keys.json`、`config.json`、解密数据库和真实消息文件。
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
dotnet build WechatDashboard.sln --no-restore -v minimal
dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --no-restore
python -m unittest discover -v -s tools\wechat-local-reader -p test_wechat_local_reader.py
dotnet run --project src\WechatDashboard.App\WechatDashboard.App.csproj --no-restore
```

构建和测试应串行执行，避免多个进程同时写入 `bin`/`obj` 导致 DLL 锁定。如果 WPF 正在运行，可关闭它后重新构建；开发时也可临时把独立构建输出写到 `tools\result\build-check`。

## 当前边界

- 当前仓库以源码运行和开发验证为主，尚未提供经过干净机器验收的完整安装器。
- 微信仍使用 Python 兼容读取器完成数据库解密和消息解析；原生 C# 读取链路尚未完全替代它。
- 客户端内部格式并非公开稳定接口，微信或石化通升级后需要重新做兼容验证。
- 仓库当前没有根级 `LICENSE` 文件；随附工具包是为本项目部署而提交，不代表项目代码或第三方二进制获得了不受限制的自由再分发许可。

第三方来源和许可证提示见 `tools\wechat-local-reader\THIRD-PARTY-NOTICES.md`。
