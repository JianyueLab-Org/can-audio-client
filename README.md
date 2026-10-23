# CAN Audio Client

CAN 管制语音客户端 **1.0.0 正式版**，使用 C# / .NET 10 / Avalonia 12.1。UI 来自 [can-client-ui](https://github.com/JianyueLab-Org/can-client-ui) 的管制员界面（提交 `ee728d7e775cb692c3d5f6dbf5fd14dd7e9acd0f`）。PTT 全局输入（键盘、鼠标侧键、摇杆）、电台栈规则、席位数据和设置均在 C# 中实现；仅 Mumble 连接与音频收发由无界面的 Python 语音引擎处理，协议见 [controller/README.md](controller/README.md)。

本仓库仅维护 C# 版本和必要的 Python 语音引擎，不包含旧 Python GUI、飞行员或 ATIS 客户端。

## 下载和运行

在 [Releases](https://github.com/JianyueLab-Org/can-audio-client/releases) 下载 `can-audio-client-1.0.0-win-x64.zip`，解压后运行 `ControllerClient.exe`。需要 Windows x64；发行包自带 .NET 运行时和打包语音引擎，**无需安装 .NET 或 Python**。

```text
can-audio-client-1.0.0-win-x64/
  ControllerClient.exe
  controller/
    can-voice.exe
  README.md
```

保留整个目录，不要单独复制 EXE。

## 使用

1. 在「设置 → 用户信息」填写 CAN ID、密码、语音服务器和端口（默认 `audio.ceruleanavi.net:64738`）。
2. 在音频页选择麦克风和输出设备。
3. PTT 默认 V。点击「添加 PTT 按键或按钮」可添加多个键盘、鼠标侧键、摇杆按钮，支持去重、逐条删除、清空；点击「应用」或「确定」保存。任意一个按住即可发话，所有绑定松开后停止。
4. 点击连接。本人在 CAN 数据源中正在管制的频率自动加入并开启 TX；未上席位或数据源更新失败时仅收听。
5. 从在线席位加入收听，或手动输入频率 / 在线呼号；RX 控制收听、TX 控制发话目标、XC 控制跨频耦合。

多频率监听需要 Mumble 1.4+ 和相应频道权限。支持 UDP 音频，不可用时回退 TCP；断线有限重连，被踢后不自动抢回登录。

## 状态、设置与日志

- 主窗口顶部及置顶小窗显示实时语音状态，格式为「已连接 · UDP/TCP · 23 ms」。每秒读取语音引擎的真实平均 Ping RTT，无样本时显示 `-- ms`。
- FSD 状态每 15 秒依据 CAN 数据源确认账号在线情况，并标注「数据源确认」。本程序不直接建立 FSD 会话，不替代 EuroScope 等客户端上席位。HTTP 请求耗时单独显示，不当作 FSD 延迟。
- 设置位于 `%LOCALAPPDATA%/CAN/AudioClient/settings.json`。密码仅保存在内存，麦克风会话增益每次启动恢复默认值。
- 日志位于 **EXE 所在目录的 `logs/`**，点击「日志」按钮打开。主程序与语音分别写入 `client-YYYY-MM-DD-启动时间-PID.log`、`voice-YYYY-MM-DD-启动时间-PID.log`，跨日自动换文件、多实例分别记录，不记录连接请求密码。

## 从源码运行

需要 .NET 10 SDK、Python 3.12 x64、Git：

```powershell
py -3.12 -m venv .venv
$env:PYTHONUTF8 = 1
.\.venv\Scripts\python.exe -m pip install -r requirements-voice.txt
dotnet build CanAudio.slnx
pwsh -File ./run-csharp.ps1
```

默认使用本项目 `.venv/Scripts/python.exe`；设置 `CAN_PYTHON` 可指定其他 Python。发行包默认优先使用 `controller/can-voice.exe`。语音引擎不依赖 Qt、pynput 或 pygame；摇杆通过 C# 直接调用随包的 SDL2，设备和按钮序号与旧版一致。

## 验证与编译

```powershell
dotnet run --project tests/ControllerClient.Checks -c Release
.\.venv\Scripts\python.exe -m unittest discover -s controller -v
```

仅编译 C# 单文件程序（仍需本地 Python 环境）：

```powershell
pwsh -File ./build.ps1
```

输出 `dist/CanAudio-singlefile/`。使用 .NET 10 自包含单文件发布、压缩、原生库自解压，托管 DLL 合并到 EXE；不裁剪反射所需类型。

生成完整便携发行包（含 PyInstaller 打包的单文件语音引擎）：

```powershell
.\.venv\Scripts\python.exe -m pip install -r requirements-build.txt
pwsh -File ./package-release.ps1
```

输出 `dist/can-audio-client-1.0.0-win-x64.zip` 和 SHA-256 校验文件。

可选降噪库：使用 CMake 和 Visual Studio C++ x64 工具执行 `pwsh -File native/rnnoise/build.ps1 -Out controller`；存在 `rnnoise.dll` 时发行脚本会自动包含。缺少该库时语音正常收发，降噪不可用。

## 功能范围

已接入真实连接、设备枚举、音量、多个 PTT、席位规则、多频率 RX/TX/XC、收发指示、呼号映射、连接状态与延迟、置顶小窗及按日日志。

自动麦克风校准、中英文界面切换和服务器目录选择尚未迁入。压缩参数由现有 Mumble 引擎管理；内话、地图、UniATIS、Stream Deck 尚未接入。

## 来源

Python 语音引擎提取自 `JianyueLab-Org/can-audio` 的 `controller/`（源提交 `e724bff58ddad12eb965aba79817b4b58b30b489`）。仅保留 C# 客户端所需模块、Opus 库、相关测试和可选 RNNoise 构建支持。
