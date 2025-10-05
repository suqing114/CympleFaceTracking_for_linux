# CympleFaceTracking
The VRCFT module for [Project Cymple](https://github.com/Dominocs/Project_Cymple), a low cost and open source DIY eye tracking solution.

---

## 项目简介

本仓库为 Cymple （Project Cymple）的 VRCFaceTracking 扩展模块，已针对 Linux 环境做适配。模块通过 UDP 接收来自 Cymple 主程序的面部/眼球追踪数据，并将其映射到 VRCFaceTracking 的 UnifiedTracking 数据结构。

## 主要改动摘要（Linux 适配）

- 将 Windows 专用的 DLL 引用替换为跨平台的 NuGet PackageReference（如 Microsoft.Extensions.Logging.Abstractions、System.Drawing.Common）。
- 移除了对 Windows API GetPrivateProfileString 的依赖，加入了简单跨平台的 INI 解析器。
- 消除了硬编码用户路径，改为动态读取当前用户主目录（HOME / SpecialFolder.UserProfile）。
- 增强了 UDP 端口绑定的容错（端口占用时不会抛出异常并崩溃）。
- 添加了调试日志（接收数据头与解析后关键字段），便于排查数据格式与字节序问题。

## 构建（Linux）

前置：安装 .NET 7 SDK。

在仓库根目录运行：

```bash
dotnet restore
dotnet build -c Release
```

构建产物：

bin/Release/net7.0/CympleFaceTracking.dll

## 部署到 VRCFaceTracking

把编译好的 `CympleFaceTracking.dll` 复制到 VRCFaceTracking 的 CustomLibs 目录（通常位于 `~/.config/VRCFaceTracking/CustomLibs/`）：

```bash
cp bin/Release/net7.0/CympleFaceTracking.dll ~/.config/VRCFaceTracking/CustomLibs/
```

重启 VRCFaceTracking，观察日志中是否出现模块加载与初始化信息。

## 配置 INI 文件位置

默认模块会在当前用户家目录下查找 Wine 的 ini 文件（路径示例）：

```
~/.wine/drive_c/Cymple/iniFile.ini
```

查找逻辑优先使用环境变量 `HOME`，若不可用则使用 `Environment.SpecialFolder.UserProfile`。你也可以通过手动在该位置创建/复制 ini 文件来覆盖默认配置。

推荐在 `[Function Switch]` 节中包含以下键（模块会读取 `cymple_eye_sw` 和 `cymple_mouth_sw`）：

```
[Function Switch]
cymple_eye_sw = true
cymple_mouth_sw = false
```

如果某个键缺失，则模块会把对应功能视为未启用（默认 false）。

## 日志与调试

模块使用 `Microsoft.Extensions.Logging`，日志由 VRCFaceTracking 主程序收集与输出。为了看到更详细的诊断信息（如接收的 UDP 头部和解析值），请将主程序的日志级别设置为 `Debug` 或更低。

关键诊断日志：

- `CympleFace: received N bytes, header: ...`（显示前 12 字节的十六进制，用于验证消息格式）
- `Cymple parsed: EyePitch=... EyeYaw_L=... EyeLidL=... MouthClose=...`（显示解析后的关键字段）

这些信息有助于判断发送端是否真的发送了右眼/嘴部数据，或是否存在字节序/字段顺序错误。

## UDP 测试脚本

仓库已包含一个简单的测试脚本：`tools/send_cymple_test.py`，可用于生成模拟数据包（按照模块期待的二进制布局）以验证模块解析和映射是否正确。

运行示例：

```bash
python3 tools/send_cymple_test.py
```

脚本会定期发送测试包，你可以观察模块日志来验证解析结果。

## 常见问题与排查顺序

1. 模块未加载：检查 `~/.config/VRCFaceTracking/CustomLibs/` 是否包含 `CympleFaceTracking.dll`。
2. 配置无效（eye/mouth 显示为 false）：确认 `iniFile.ini` 的 `[Function Switch]` 节包含 `cymple_eye_sw` 与 `cymple_mouth_sw`。
3. 只看到 eyesClosed：查看 `Cymple parsed:` 日志，确认解析出的各字段（EyePitch/EyeYaw/EyeLid/EyeSquint/MouthClose）是否为非零；若日志中有数值但游戏未反应，问题在映射/Avatar 配置。
4. 收到的 header 与预期不符：检查发送端是否为 OSC/text 格式而非本模块期待的二进制格式。

## 想要的后续改进（可选）

- 支持通过环境变量或 `module.json` 显式覆盖 INI 路径。
- 引入更完整的 INI 库（如 IniParser）以支持更复杂格式和注释。
- 将模块以配置方式支持 OSC 消息兼容或自动探测不同格式。

---

如果你需要我把 README 的变更 commit 并 push 到 GitHub（你当前的仓库已关联远程），我可以尝试为你提交并 push；如果出现凭证或权限问题，我会把需要运行的 git 命令给你，你可以在本地执行。
