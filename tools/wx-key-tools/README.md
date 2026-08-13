# wx_key 工具包说明

本目录包含 WechatDashboard 自动提取 Windows 微信本地数据库 Key 所需的包装脚本和完整第三方工具包：

```text
run-wx-key-probe.ps1
wx_key-windows-v2.1.8\
```

应用的默认原生路径通过独立 x64 `WechatDashboard.KeyProbe.exe` 加载：

```text
wx_key-windows-v2.1.8\data\flutter_assets\assets\dll\wx_key.dll
```

WPF 主进程不直接加载该 DLL。KeyProbe 仅在用户点击“自动提取 Key”后运行，并通过当前用户的随机命名管道返回 Key。

## 分发与许可证状态

- 版本目录：`wx_key-windows-v2.1.8`
- 项目维护者确认日期：2026-08-11；维护者确认所选 `wx_key.dll` 可随本项目外部分发。
- 本次完整工具包入库要求：2026-08-13。
- 包内许可证材料：`data\flutter_assets\NOTICES.Z`，为 Flutter 运行时生成的压缩第三方 notices。
- 缺失材料：工具包没有独立的明文根级许可证、上游下载地址或源代码归档。

维护者的分发确认不等同于开放源代码许可证，也不自动授予项目使用者超出原授权范围的权利。后续若替换版本，必须重新确认来源和分发权利。

## 完整性

- 文件数：27
- 总大小：86,386,635 字节
- 全量 SHA-256：见 `SHA256SUMS.txt`
- `wx_key.exe` SHA-256：`05862a20389ea54b8540850f4260a769d3b6e4490686001f2fee38b1b5af2053`
- `wx_key.dll` SHA-256：`f946ef8cb2a59bc03ce0b6ae0e22ed905a57e4c8228ed6b1c2b07fd54ecb9a05`

克隆后可校验核心 DLL：

```powershell
Get-FileHash -Algorithm SHA256 tools\wx-key-tools\wx_key-windows-v2.1.8\data\flutter_assets\assets\dll\wx_key.dll
```

## 签名与安全扫描状态

- `wx_key.exe`：未签署 Authenticode。
- `wx_key.dll`：未签署 Authenticode。
- `flutter_windows.dll`：未签署 Authenticode。
- 2026-08-13 尝试使用 Windows Defender 自定义扫描时，本机 Defender 防病毒和实时保护均处于禁用状态，扫描命令失败。因此本次状态是**未完成安全扫描**，不能表述为“未发现威胁”。
- 系统注册了其他企业防病毒产品，但本次未找到经过确认的命令行扫描接口，未以猜测参数启动扫描。

正式发布前，应使用组织批准且处于启用状态的安全产品重新扫描，记录扫描产品、规则版本、时间和结果，并对最终发布二进制完成签名或明确接受未签名风险。

## 禁止入库的运行数据

工具包本身可以被 Git 跟踪，但以下运行结果必须继续留在已忽略的 `tools\result` 中：

- DB Key 或 `wx-key-found.txt`
- `all_keys.json`、初始化配置和探测日志
- 解密后的微信数据库
- 真实消息、联系人或账户数据

不要在 README、Issue、日志或提交信息中写入 DB Key、消息正文或账户身份。
