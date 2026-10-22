# CAN Audio Client

独立的 CAN 管制语音客户端（Audio for CAN），提供多频率 RX/TX/XC 电台栈、键盘/鼠标/摇杆 PTT、音频设备选择、麦克风校准、中英文界面、窗口置顶和精简模式。

## 环境与安装

- Windows x64，Python **3.12**，Git（安装固定提交的 `pymumble-udp` 依赖需要）。
- `controller/opus.dll` 已包含在项目中。
- 可选的 RNNoise 降噪库见下方说明；缺少该库时仍可正常收发语音。

在项目根目录执行：

```powershell
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe run.py
```

`run.py` 会设置 Opus 库搜索路径及客户端工作目录，也支持从其他目录用绝对路径启动。调试日志可用 `python run.py --debug` 开启。设置保存在 `controller/radio_settings.json`，日志为 `controller/audio-for-can.log`。

## 使用

1. 在登录页的设置中选择语音服务器，或填写自定义主机和端口（默认 `64738`）。
2. 选择麦克风、扬声器并设置 PTT（默认 `V`）；按需执行麦克风校准。
3. 用 CAN 账号连接，添加频率，或点击在线席位将其加入电台栈。
4. RX 控制收听，TX 控制发话目标，XC 用于跨频耦合，按住 PTT 发话。

客户端默认连接 CAN 网络，仍需要可用的 Mumble 语音服务和账号。多频率监听需要 Mumble 1.4+ 及相应频道权限；服务端支持时使用 UDP 音频，否则回退 TCP。

**保留了原客户端的席位规则：** CAN 数据源确认账号正在有效管制席位时才允许 TX；未上席位时只能收听。这个程序本身不负责登录 FSD 或建立管制席位。独立运行指无需原源码仓库或其他客户端，并非离线语音服务。

为避免独立版本提示下载旧项目的发行包，默认关闭原更新检查。服务器列表和在线席位数据仍使用 CAN 的网络接口。

## 可选：麦克风降噪

需要 CMake 和 Visual Studio C++ x64 构建工具，在根目录执行：

```powershell
pwsh -File native/rnnoise/build.ps1 -Out controller
```

脚本下载并校验 RNNoise v0.2，生成 `controller/rnnoise.dll` 和 `controller/RNNOISE-COPYING`。重启客户端后即可使用降噪。

## 本地打包

```powershell
.\.venv\Scripts\python.exe -m pip install -r requirements-build.txt
cd controller
..\.venv\Scripts\python.exe -m PyInstaller --noconfirm gui.spec
```

产物为 `controller/dist/audio-for-can/`。分发整个目录，运行其中的 `audio-for-can.exe`；不能只复制 exe。Opus 和 Qt 库会随包分发；RNNoise 已构建时也会随包带上。

版本号在 `controller/version.py` 中维护。本地正式打包时可先设置 `$env:CAN_VERSION = "2.2.0"`，版本信息会固化到包中，不需要 CI 或原仓库的 Git 历史。

## 验证

在项目根目录打开 PowerShell：

```powershell
$env:PATH = "$PWD\controller;$env:PATH"
cd controller
..\.venv\Scripts\python.exe -m unittest discover -v
..\.venv\Scripts\python.exe smoke_gui.py
```

测试覆盖电台栈、语音协议兼容、设置、设备校准、PTT 等，GUI 冒烟检查使用离屏窗口，不需要登录服务器。

## 来源

客户端提取自 `JianyueLab-Org/can-audio` 的 `controller/`，已同步至源提交 `e724bff58ddad12eb965aba79817b4b58b30b489`。保留客户端代码、图标、Opus 库、客户端测试及可选 RNNoise 构建支持。本项目使用自身 Git 仓库，不包含源仓库历史、服务端、其他客户端或 CI 工作流。
