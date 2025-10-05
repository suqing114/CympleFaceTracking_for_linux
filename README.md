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

默认模块会在当前用户家目录下查找 Wine 中的 Cymple 软件生成的 ini 文件（路径示例）：

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

