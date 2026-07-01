# WeChat 本地数据库采集系统测试步骤

更新时间：2026-06-30

## 1. 测试目标

验证当前程序是否可以在本机完成下面的完整链路：

1. 通过外部 `wx_key` 工具取得当前登录微信的 DB Key。
2. 用 DB Key 解密本机微信 `db_storage` 数据库。
3. 从 `SessionTable`、`Name2Id`、`Msg_<md5(username)>` 等 V4 表结构读取本地消息。
4. 通过 WPF 的 `自动提取Key`、`初始化本地库`、`采集一次`、`开始微信监听` 完成端到端采集。
5. 在微信窗口最小化或被遮挡时仍能读取本地数据库消息。

本测试不验证 UIA/OCR 可见窗口采集。OCR 只作为兜底能力，不是本地数据库采集主路径。

## 当前实现基线（2026-06-30）

当前工作树的已验证基线：

- Python reader 单元测试：47 条通过。
- .NET 控制台回归测试：24 条通过。
- WPF App 构建到 `tools\result\build-check\WechatDashboard.App` 成功。
- WPF `微信消息` tab 默认读取当天消息，每页 50 条，字段为 `消息内容`、`群名称`、`发消息人`。
- 非文本 XML 消息不应再显示 XML 原文，应显示 `[图片]`、`[视频]`、`[表情]`、`[文件]`、`[链接] 标题 - 描述` 或 `[位置]`。
- 所有 key、配置、解密库和临时输出应留在 `tools\result`，不要提交到 Git。
## 2. 安全边界

测试过程中不要把以下内容复制到聊天、日志或 Git：

- DB Key 原文。
- `wx-key-found.txt`。
- `all_keys.json`。
- 解密后的 SQLite 数据库。
- `capture.json` 里的消息正文。
- 微信聊天消息截图或完整聊天内容。

测试完成后必须删除 `tools/result` 下的临时敏感文件，见第 11 节。

## 3. 前置条件

### 3.1 本机环境

1. Windows 机器。
2. 微信 PC 版已登录。
3. 项目目录存在：

   ```powershell
   D:\study\code\wechatDashboard
   ```

4. Python 可用：

   ```powershell
   python --version
   ```

5. .NET SDK 可用：

   ```powershell
   dotnet --version
   ```

### 3.2 微信数据库目录

当前已验证过的本机目录是：

```powershell
D:\cache\xwechat_files\dsfgis_84f8\db_storage
```

确认目录存在：

```powershell
Test-Path D:\cache\xwechat_files\dsfgis_84f8\db_storage
```

期望输出：

```text
True
```

如果该目录不存在，先查找本机微信数据目录：

```powershell
Get-ChildItem -Directory D:\cache\xwechat_files
```

选择包含 `db_storage\message` 且有 `.db` 文件的账号目录。

### 3.3 DB Key 提取工具

当前测试采用 `ycccccccy/wx_key` 的 Windows 工具。项目内路径是：

```powershell
D:\study\code\wechatDashboard\tools\wx-key-tools\wx_key-windows-v2.1.8\wx_key.exe
D:\study\code\wechatDashboard\tools\wx-key-tools\run-wx-key-probe.ps1
```

确认工具和脚本存在：

```powershell
Test-Path D:\study\code\wechatDashboard\tools\wx-key-tools\wx_key-windows-v2.1.8\wx_key.exe
Test-Path D:\study\code\wechatDashboard\tools\wx-key-tools\run-wx-key-probe.ps1
```

两个结果都应为：

```text
True
```

## 4. 代码级测试

从仓库根目录执行：

```powershell
cd D:\study\code\wechatDashboard
```

### 4.1 Python reader 单元测试

不要使用下面这个命令：

```powershell
python -m unittest -v tools\wechat-local-reader\test_wechat_local_reader.py
```

这个命令会让 `test_wechat_local_reader.py` 无法导入同目录的 `wechat_local_reader.py`。

应使用 `discover`：

```powershell
python -m unittest discover -v -s tools\wechat-local-reader -p test_wechat_local_reader.py
```

期望结果：

```text
OK
```

也可以进入 reader 目录后运行：

```powershell
cd D:\study\code\wechatDashboard\tools\wechat-local-reader
python -m unittest -v test_wechat_local_reader.py
cd D:\study\code\wechatDashboard
```

### 4.2 .NET 控制台回归测试

当前测试项目是控制台式 runner，不是 xUnit/NUnit 发现式测试。应运行：

```powershell
dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj
```

期望结果：

```text
All 24 tests passed.
```

如果看到 `SQLitePCLRaw.lib.e_sqlite3` 的 NuGet 漏洞告警，先记录为依赖治理问题；这不影响本轮 DB Key 和本地读取链路验证。

## 5. 提取 DB Key

保持微信已登录并运行。

### 5.1 推荐方式：WPF 自动提取

启动 WPF：

```powershell
cd D:\study\code\wechatDashboard
dotnet run --project src\WechatDashboard.App\WechatDashboard.App.csproj
```

点击顶部的 `自动提取Key` 按钮。

程序会自动完成。点击后请在 5 分钟内在微信里重新登录，不要关闭微信进程：

1. 查找当前 `Weixin` 进程。
2. 优先选择 `MainWindowTitle = 微信` 的主窗口进程。
3. 如果找不到主窗口标题，则选择内存占用最大的 `Weixin` 进程。
4. 调用 PowerShell 执行：

   ```text
   D:\study\code\wechatDashboard\tools\wx-key-tools\run-wx-key-probe.ps1
   ```

5. 传入 `-TargetPid`、`-DllDir`、`-KeyPath`、`-LogPath`、`-Seconds` 参数。
6. 将 DB Key 写入项目内结果目录：

   ```powershell
   D:\study\code\wechatDashboard\tools\result\wechat-local-reader\wx-key-found.txt
   ```

7. 自动把该路径填入界面的 `Key文件` 输入框。

期望顶部提示类似：

```text
DB Key 提取成功（微信 PID 20820），Key 文件已写入 tools/result。点击"初始化本地库"继续。
```

不要打开或复制 `wx-key-found.txt` 内容。

提取成功后，可以只验证文件存在和格式，不打印 key：

```powershell
$keyPath = "D:\study\code\wechatDashboard\tools\result\wechat-local-reader\wx-key-found.txt"
$text = Get-Content -Raw -LiteralPath $keyPath
[pscustomobject]@{
  Exists = Test-Path -LiteralPath $keyPath
  Has64Hex = [bool]($text -match '[A-Fa-f0-9]{64}')
  Length = $text.Length
}
```

期望：

- `Exists = True`
- `Has64Hex = True`

### 5.2 手动 fallback：直接运行 PowerShell 探测脚本

只有当 WPF 的 `自动提取Key` 失败时，才需要手动执行下面步骤。

先找微信主进程 PID：

```powershell
Get-Process Weixin -ErrorAction SilentlyContinue |
  Sort-Object WorkingSet64 -Descending |
  Select-Object Id,ProcessName,MainWindowTitle,WorkingSet64
```

优先选择 `MainWindowTitle` 为 `微信` 的进程；如果多个进程都没有标题，选择 `WorkingSet64` 最大的 `Weixin` 进程。

然后执行非交互提取命令。不要使用 `$pid` 变量名，因为 PowerShell 内置 `$PID` 是只读变量。

```powershell
$target = Get-Process Weixin -ErrorAction SilentlyContinue |
  Where-Object { $_.MainWindowTitle -eq "微信" } |
  Sort-Object WorkingSet64 -Descending |
  Select-Object -First 1

if ($null -eq $target) {
  $target = Get-Process Weixin -ErrorAction SilentlyContinue |
    Sort-Object WorkingSet64 -Descending |
    Select-Object -First 1
}

if ($null -eq $target) {
  throw "未找到 Weixin 进程，请先登录并打开微信。"
}

$targetPid = $target.Id
$dllDir = "D:\study\code\wechatDashboard\tools\wx-key-tools\wx_key-windows-v2.1.8\data\flutter_assets\assets\dll"
$keyPath = "D:\study\code\wechatDashboard\tools\result\wechat-local-reader\wx-key-found.txt"
$logPath = "D:\study\code\wechatDashboard\tools\result\wechat-local-reader\wx-key-probe.log"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $keyPath) | Out-Null

D:\study\code\wechatDashboard\tools\wx-key-tools\run-wx-key-probe.ps1 `
  -TargetPid $targetPid `
  -DllDir $dllDir `
  -KeyPath $keyPath `
  -LogPath $logPath `
  -Seconds 180
```

手动提取成功后，将 `$keyPath` 对应路径填入 WPF 的 `Key文件` 输入框。

如果提取失败：

1. 确认微信仍处于登录状态。
2. 关闭并重新打开微信后重试。
3. 用管理员 PowerShell 或以管理员身份启动 WPF 后重试。
4. 不要改用旧的内存扫描作为结论依据。当前微信 4.1.x 上旧扫描可能找不到 key。

## 6. CLI 初始化本地数据库读取器

如果你已经通过 WPF `自动提取Key` 成功，key 文件应在：

```powershell
D:\study\code\wechatDashboard\tools\result\wechat-local-reader\wx-key-found.txt
```

创建临时测试目录：

```powershell
New-Item -ItemType Directory -Force -Path D:\study\code\wechatDashboard\tools\result\wechat-reader-system-test | Out-Null
```

初始化：

```powershell
cd D:\study\code\wechatDashboard
$keyPath = "D:\study\code\wechatDashboard\tools\result\wechat-local-reader\wx-key-found.txt"

python tools\wechat-local-reader\wechat_local_reader.py init `
  --db-dir D:\cache\xwechat_files\dsfgis_84f8\db_storage `
  --config D:\study\code\wechatDashboard\tools\result\wechat-reader-system-test\config.json `
  --key-file $keyPath `
  --bootstrap-range 30d
```

期望 JSON 结果包含：

- `status = initialized`
- `key_provider.status = key_file`
- `key_validation.status = ok`
- `extractionMode = imported_passphrase`
- `databaseCount > 0`
- `keyCount > 0`

参考基线：

上次本机验证时，`databaseCount = 18`，`keyCount = 18`。这个数字可能随微信版本和账号数据变化，不作为硬性失败条件。

如果失败：

1. `External key file did not contain a usable DB key.`
   - 说明 key 文件不存在、为空、或没有 64 位十六进制 key。
   - 回到第 5 节重新提取。

2. `No usable WeChat 4.x database keys were found`
   - 说明 key 和当前 `db_storage` 不匹配。
   - 检查是否选错账号目录。
   - 重新确认当前登录微信账号和 `D:\cache\xwechat_files\...\db_storage` 是否对应。

3. `WeChat database directory not found`
   - 说明 `--db-dir` 写错。
   - 回到第 3.2 节重新定位。

## 7. CLI 读取消息

Windows PowerShell 默认重定向可能使用 GBK 编码，遇到特殊 Unicode 字符会失败。因此先设置 UTF-8：

```powershell
$env:PYTHONIOENCODING = "utf-8"
```

执行读取：

```powershell
python tools\wechat-local-reader\wechat_local_reader.py capture `
  --config D:\study\code\wechatDashboard\tools\result\wechat-reader-system-test\config.json `
  --initial-lookback-seconds 2592000 `
  --limit 50 `
  > D:\study\code\wechatDashboard\tools\result\wechat-reader-system-test\capture.json
```

只解析统计信息，不打印消息正文：

```powershell
$json = Get-Content -Raw -LiteralPath D:\study\code\wechatDashboard\tools\result\wechat-reader-system-test\capture.json | ConvertFrom-Json
$query = $json.stages | Where-Object { $_.stage -eq "query" } | Select-Object -First 1
$decrypt = $json.stages | Where-Object { $_.stage -eq "decrypt" } | Select-Object -First 1

[pscustomobject]@{
  Status = $json.status
  MessageCount = @($json.messages).Count
  NextOffset = $json.nextOffset
  DecryptTotal = $decrypt.total
  DecryptSkipped = $decrypt.skipped
  DecryptFailed = $decrypt.failed
  Sessions = $query.sessions
  ShardsScanned = $query.shards_scanned
  MatchedTables = $query.matched_tables
  RowsRead = $query.rows_read
  QueryErrors = $query.query_errors
}
```

必须满足：

- `Status = ok`
- `DecryptFailed = 0`
- `Sessions > 0`
- `RowsRead > 0`
- `MessageCount > 0`

允许：

- `QueryErrors > 0`

`QueryErrors` 表示某些会话表或消息表在 SQLite 查询时局部异常。当前 reader 会跳过异常表并继续读取其它表。

参考基线：

上次本机验证时读取结果是：

- `MessageCount = 3317`
- `Sessions = 560`
- `MatchedTables = 123`
- `RowsRead = 3317`
- `QueryErrors = 6`

这些是参考值，不是固定断言。

## 8. WPF 初始化本地库

启动 WPF：

```powershell
cd D:\study\code\wechatDashboard
dotnet run --project src\WechatDashboard.App\WechatDashboard.App.csproj
```

界面顶部按以下方式操作：

1. `历史导入范围`：选择 `30天`。
2. `DB Key`：留空。
3. `Key工具`：留空。
4. 点击 `自动提取Key`，等待顶部提示 DB Key 提取成功。
5. 确认 `Key文件` 已自动填入：

   ```text
   D:\study\code\wechatDashboard\tools\result\wechat-local-reader\wx-key-found.txt
   ```

6. 点击 `初始化本地库`。

期望：

- 顶部提示 `本地数据库初始化成功！点击"采集一次"即可读取微信消息。`
- 诊断区 `WeChat.LocalDatabase` 变为 `已就绪`。
- 本机生成配置：

  ```powershell
  Test-Path "D:\study\code\wechatDashboard\tools\result\wechat-local-reader\config.json"
  Test-Path "D:\study\code\wechatDashboard\tools\result\wechat-local-reader\all_keys.json"
  ```

  两个结果都应为 `True`。

说明：

- WPF 从源码运行时会优先使用仓库里的 `tools\wechat-local-reader\wechat_local_reader.py`。
- `wx-key-found.txt`、`config.json`、`all_keys.json`、解密数据库和 capture JSON 都写入 `tools/result`，不要提交到 Git。

## 9. WPF 单次采集

在 WPF 点击 `采集一次`。

期望：

1. 顶部提示类似：

   ```text
   本次采集 N 条，入库 M 条，创建待办 K 条
   ```

2. `消息流` 列表出现真实微信消息。
3. `今日消息`、`@我`、`待办理` 等统计刷新。
4. 如果新增消息内容命中 `@白驹过隙`、`@戴少峰` 等默认别名，应创建待办。

如果 `采集一次` 为 0：

1. 先确认第 7 节 CLI 读取结果是否 `RowsRead > 0`。
2. 检查是否已经导入过同一批消息，系统会按 offset 和消息 ID 去重。
3. 发送一条新的微信测试消息后再点一次。

## 10. WPF 监听与最小化测试

### 10.1 监听测试

1. 点击 `开始微信监听`。
2. 期望顶部显示：

   ```text
   微信监听运行中，间隔 5 秒
   ```

3. 在微信中发送或接收一条新的测试消息。
4. 等待 5 到 10 秒。
5. 点击 `刷新`。
6. 确认消息流或统计更新。
7. 点击 `停止监听`。

### 10.2 微信最小化测试

1. 确认第 8 节初始化成功。
2. 最小化微信窗口。
3. 点击 WPF 的 `采集一次`。
4. 期望仍能读取新增或未导入消息。

通过条件：

- 微信最小化后仍能完成本地数据库采集。
- 这证明当前路径不是依赖 OCR 的可见窗口采集。

## 11. 清理敏感临时文件

测试完成后执行：

```powershell
Remove-Item D:\study\code\wechatDashboard\tools\result\wechat-reader-system-test -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "D:\study\code\wechatDashboard\tools\result\wechat-local-reader\wx-key-found.txt" -Force -ErrorAction SilentlyContinue
Remove-Item "D:\study\code\wechatDashboard\tools\result\wechat-local-reader\wx-key-probe.log" -Force -ErrorAction SilentlyContinue
```

如果 WPF 已初始化成功，下面文件也包含敏感密钥或解密数据。只在需要重新初始化时删除：

```powershell
Remove-Item "D:\study\code\wechatDashboard\tools\result\wechat-local-reader\all_keys.json" -Force -ErrorAction SilentlyContinue
Remove-Item "D:\study\code\wechatDashboard\tools\result\wechat-local-reader\config.json" -Force -ErrorAction SilentlyContinue
Remove-Item "D:\study\code\wechatDashboard\tools\result\wechat-local-reader\decrypted" -Recurse -Force -ErrorAction SilentlyContinue
```

不要删除 `wx_key.exe` 本体，后续还要用。

### 11.1 不应提交的文件检查

提交或新开工作树前执行：

```powershell
git status --short
git status --ignored --short tools\result tools\wechat-local-reader tools\wx-key-tools
```

要求：

- `tools/result/` 只能显示为 ignored，不应出现在待提交文件里。
- 不应出现 `wx-key-found.txt`、`all_keys.json`、`config.json`、`decrypted`、`capture.json` 等敏感结果文件。
- `tools/wx-key-tools/` 如果作为未跟踪目录存在，提交前必须先确认来源、版本、SHA-256、许可证和是否允许分发。
## 12. 常见错误

### 12.1 `ModuleNotFoundError: No module named 'wechat_local_reader'`

原因：

从仓库根目录用文件路径直接运行了测试：

```powershell
python -m unittest -v tools\wechat-local-reader\test_wechat_local_reader.py
```

修复：

```powershell
python -m unittest discover -v -s tools\wechat-local-reader -p test_wechat_local_reader.py
```

### 12.2 `'gbk' codec can't encode character`

原因：

PowerShell 重定向 JSON 时使用 GBK，消息里有 GBK 不支持的字符。

修复：

```powershell
$env:PYTHONIOENCODING = "utf-8"
```

然后重新执行 capture。

### 12.3 `database disk image is malformed`

原因：

某些解密后的消息表存在 SQLite 局部异常。

当前处理：

reader 会跳过单个异常查询，并把次数计入 `query_errors`。只要 `RowsRead > 0` 且 `Status = ok`，系统测试可以继续。

### 12.4 初始化成功但 WPF `WeChat.LocalDatabase` 仍未就绪

检查：

```powershell
Test-Path "D:\study\code\wechatDashboard\tools\result\wechat-local-reader\config.json"
Test-Path "D:\study\code\wechatDashboard\tools\result\wechat-local-reader\all_keys.json"
```

如果任一为 `False`：

1. 回到 WPF 点击 `初始化本地库`。
2. 或先用第 6 节 CLI 验证 key 和 db 目录。

### 12.5 WPF 找不到微信数据目录

当前自动定位会搜索：

- `%USERPROFILE%\cache\xwechat_files`
- 各磁盘根目录下的 `cache\xwechat_files`
- `WeChat Files`
- `Documents\WeChat Files`

如果你的微信数据不在这些位置，先用 CLI 指定 `--db-dir` 验证。WPF 后续需要补充手工选择 `db_storage` 的入口。

### 12.6 表格里显示 XML 原文

原因：

微信图片、视频、表情、链接等非文本消息在数据库中常以 XML 元数据保存。如果这些 XML 直接进入 UI，说明读取器没有在输出前执行摘要转换。

期望：

- 文本消息显示原始消息正文。
- 图片显示 `[图片]`。
- 视频显示 `[视频]`。
- 表情显示 `[表情]`。
- 文件显示 `[文件]`。
- 链接优先显示 `[链接] 标题 - 描述`。
- 位置显示 `[位置]` 或 `[位置] 地址`。

排查：

```powershell
rg -n "summarize_message_content|query_messages_from_shard" tools\wechat-local-reader\wechat_local_reader.py
python -m unittest discover -v -s tools\wechat-local-reader -p test_wechat_local_reader.py
```

确认 `query_messages_from_shard` 在追加消息前调用了 `summarize_message_content(text, local_type)`。
### 12.6 `自动提取Key` 失败

检查：

```powershell
Test-Path D:\study\code\wechatDashboard\tools\wx-key-tools\run-wx-key-probe.ps1
Test-Path D:\study\code\wechatDashboard\tools\wx-key-tools\wx_key-windows-v2.1.8\data\flutter_assets\assets\dll
Get-Process Weixin -ErrorAction SilentlyContinue |
  Select-Object Id,ProcessName,MainWindowTitle,WorkingSet64
```

处理：

1. 确认微信已登录并运行。
2. 关闭并重新打开微信。
3. 以管理员身份启动 WPF。
4. 再点击 `自动提取Key`。
5. 如果仍失败，按第 5.2 节手动 fallback 排查。

## 13. 通过标准

系统测试通过必须同时满足：

1. Python reader 单元测试通过。
2. .NET 控制台回归测试通过。
3. WPF `自动提取Key` 可以生成 64 位十六进制 DB Key 文件。
4. CLI init 返回 `status = initialized`，且 `key_validation.status = ok`。
5. CLI capture 返回 `status = ok`，`RowsRead > 0`，`MessageCount > 0`。
6. WPF `初始化本地库` 成功。
7. WPF `采集一次` 能把微信消息写入本地看板。
8. WPF `开始微信监听` 能在 5 到 10 秒内采集新增消息。
9. 微信最小化后，本地数据库采集仍可用。
10. 测试结束后已删除临时 DB Key、解密数据库和 capture JSON。

## 14. 测试记录模板

```text
测试时间：
微信版本：
DB 目录：
DB Key 提取工具：

Python 测试结果：
.NET 测试结果：

WPF 自动提取Key：
- 结果：
- 目标 PID：
- Key 文件路径：

CLI init:
- status:
- databaseCount:
- keyCount:
- extractionMode:

CLI capture:
- status:
- MessageCount:
- Sessions:
- MatchedTables:
- RowsRead:
- QueryErrors:

WPF:
- 初始化本地库：
- 采集一次：
- 开始微信监听：
- 最小化微信采集：

清理情况：
遗留问题：
```
