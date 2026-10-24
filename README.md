# CAN Audio Client

CAN 管制语音客户端正式版，使用 C# / .NET 10 / Avalonia 12.1。UI 来自 [can-client-ui](https://github.com/JianyueLab-Org/can-client-ui) 的管制员界面（提交 `ee728d7e775cb692c3d5f6dbf5fd14dd7e9acd0f`）。PTT 全局输入（键盘、鼠标侧键、摇杆）、电台栈规则、席位数据和设置均在 C# 中实现；仅 Mumble 连接与音频收发由无界面的 Python 语音引擎处理。

**本仓库只有 C# 代码。** Python 语音引擎只在 [JianyueLab-Org/can-audio](https://github.com/JianyueLab-Org/can-audio/tree/new-client-sys) 的 `new-client-sys` 分支维护（`controller/`，协议见那里的 [controller/README.md](https://github.com/JianyueLab-Org/can-audio/blob/new-client-sys/controller/README.md)）；CI 每次都取该分支的最新提交编进发行包。

## 下载和运行

在 [Releases](https://github.com/JianyueLab-Org/can-audio-client/releases) 下载对应系统的包。发行包自带 .NET 运行时和打包语音引擎，**无需安装 .NET 或 Python**。

| 系统 | 下载 |
|---|---|
| Windows x64 | `can-audio-client-<版本>-win-x64.zip` |
| macOS 14+，Apple 芯片和 Intel 通用 | `can-audio-client-<版本>-macos-universal.zip` |
| Linux x64（glibc 2.35+：Arch、Ubuntu 22.04+、Debian 12+、Fedora 36+ 等） | `can-audio-client-<版本>-linux-x64.tar.gz` |
| Arch Linux | `PKGBUILD` + `can-audio-client.install` |

### Windows

解压后运行 `ControllerClient.exe`：

```text
can-audio-client-<版本>-win-x64/
  ControllerClient.exe
  controller/
    can-voice.exe          语音引擎
    voice-engine.txt       语音引擎来自 can-audio 的哪个提交
  README.md
  RNNOISE-COPYING          降噪库许可
```

保留整个目录，不要单独复制 EXE。

### macOS

解压后把 `CAN Audio.app` 拖进「应用程序」。一个 .app 同时带 Apple 芯片和 Intel 两套程序，启动时按本机架构选择。

- 包只有 ad-hoc 签名、未经 Apple 公证，第一次打开会被拦下：在「系统设置 → 隐私与安全性」底部点「仍要打开」，
  或在终端执行 `xattr -dr com.apple.quarantine "/Applications/CAN Audio.app"`。
- 键盘 / 鼠标侧键 PTT 需要「隐私与安全性 → 输入监控」权限，第一次启动会弹出请求，授权后重启客户端；麦克风同样需要授权。
  ad-hoc 签名的权限跟着具体的程序走，**每次更新后可能要重新授权**（先在列表里删掉旧的 CAN Audio 再允许）。
- 键名按美式键位取：非美式布局下符号键的名字可能和键帽不同，用「添加 PTT」录制即可，不影响使用。

### Linux

```sh
tar -xzf can-audio-client-<版本>-linux-x64.tar.gz
cd can-audio-client-<版本>-linux-x64
./ControllerClient        # 直接运行
./install.sh              # 可选：给当前用户加菜单入口（程序留在原目录）
```

Arch 用户也可以下载 Release 里的 `PKGBUILD` 和 `can-audio-client.install` 放在同一目录执行 `makepkg -si`，
装到 `/opt/can-audio-client`，命令为 `can-audio-client`。

- **全局 PTT 需要 `input` 组**：程序直接读 `/dev/input`，X11 和 Wayland 下都能在任何窗口响应，且不拦截按键。
  执行 `sudo usermod -aG input "$USER"` 后重新登录。不在 `input` 组时键盘 PTT 退回 X11 轮询（Wayland 下只在
  X11 程序有焦点时有效），鼠标侧键 PTT 不可用，设置页的 PTT 区域会提示原因。
- 运行依赖一般桌面发行版都有：`icu`、`fontconfig`、`libx11`、`libice`、`libsm`、`alsa-lib`（Debian 系是
  `libicu*`、`libfontconfig1`、`libx11-6`、`libice6`、`libsm6`、`libasound2`）。中文字体建议装 Noto Sans CJK。
  音频走系统的 ALSA，PipeWire / PulseAudio 下需要对应的 ALSA 插件（Arch：`pipewire-alsa` 或 `pulseaudio-alsa`）。
- 摇杆用随包的 SDL2；需要访问 `/dev/input/js*`、`event*`，与上面的 `input` 组是同一个条件。

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
- 设置位于 `%LOCALAPPDATA%/CAN/AudioClient/settings.json`（macOS：`~/Library/Application Support/CAN/AudioClient/`，
  Linux：`~/.local/share/CAN/AudioClient/`）。三个系统的 PTT 键名相同，设置文件可以互相拷贝。密码仅保存在内存，麦克风会话增益每次启动恢复默认值。
- 日志位于 **程序所在目录的 `logs/`**，点击「日志」按钮打开。macOS 写在 `~/Library/Logs/CAN/AudioClient/`（不能写进 .app 里）；
  Linux 程序目录不可写时（如装在 `/opt`）写在 `~/.local/state/can-audio-client/logs/`。主程序与语音分别写入 `client-YYYY-MM-DD-启动时间-PID.log`、`voice-YYYY-MM-DD-启动时间-PID.log`，跨日自动换文件、多实例分别记录，不记录连接请求密码。

## 从源码运行

需要 .NET 10 SDK、PowerShell 7（Arch：AUR `powershell-bin`）、Python（Windows 必须 3.12 x64）、Git。
macOS / Linux 上 PyAudio 从源码编，先装 PortAudio 和 opus：Arch `sudo pacman -S portaudio opus`，
Debian/Ubuntu `sudo apt install portaudio19-dev libopus0`，macOS `brew install portaudio opus`。
先把语音引擎拉到本仓库的 `can-audio/`（已 gitignore），`-Venv` 顺便在 `can-audio/.venv` 建好 Python 环境：

```powershell
pwsh -File ./sync-voice.ps1 -Venv
dotnet build CanAudio.slnx
pwsh -File ./run-csharp.ps1
```

以后再跑 `sync-voice.ps1` 即可更新到 `new-client-sys` 的最新提交。也可以用环境变量 `CAN_AUDIO_DIR` 指向别处的
can-audio 检出、`CAN_PYTHON` 指定解释器。发行包则直接用 `controller/can-voice.exe`（macOS / Linux 为 `controller/can-voice`）。语音引擎不依赖 Qt、pynput
或 pygame；摇杆通过 C# 直接调用随包的 SDL2，设备和按钮序号与旧版一致。

## 验证与编译

```powershell
dotnet run --project tests/ControllerClient.Checks -c Release
can-audio\.venv\Scripts\python.exe -m unittest discover -s can-audio/controller -v
```

仅编译 C# 单文件程序：`pwsh -File ./build.ps1`，输出 `dist/CanAudio-<运行时>/`；`-Runtime` 可选 `win-x64`、`linux-x64`、
`osx-arm64`、`osx-x64`，默认本机（C# 部分可以在任何系统上交叉编译）。使用 .NET 10 自包含单文件发布、
压缩、原生库自解压，托管 DLL 合并到 EXE；不裁剪反射所需类型。

本地生成完整便携包（PyInstaller 把 `can-audio/controller` 打成 `can-voice.exe`，并验包）：

```powershell
pwsh -File ./package-release.ps1
```

输出 `dist/can-audio-client-<版本>-dev-<运行时>.zip`（Windows）或 `.tar.gz`（macOS / Linux）。本地包显示成
「下一个补丁号 (Dev Build N)」，与正式包区分。PyInstaller 不能交叉编译，语音引擎在哪个系统上打就只能在哪个系统上用。

- Linux：自动使用系统的 `libopus.so.0`；Ubuntu 22.04 上编的包能在 glibc 2.35 以上的发行版运行，在 Arch 上编的只保证 Arch。
  `libasound` 不随包，用目标系统自己的（ALSA 插件路径是编进库里的，带别的发行版的会找不到 PipeWire 插件）。
- macOS：先 `packaging/macos/build-native.sh native` 编 libopus 和 PortAudio（按 `MACOSX_DEPLOYMENT_TARGET`，默认 14.0），
  用它装 PyAudio，再 `pwsh -File ./package-release.ps1 -OpusLibrary native/lib/libopus.0.dylib`。两种芯片各打一份后，
  `packaging/macos/make-app.sh <版本> <arm64 包目录> <x64 包目录> <app.icns> <输出目录>` 合成通用的 `CAN Audio.app`
  （图标用 `python packaging/icons.py ui/src/ControllerClient/app.ico <目录>` 生成）。CI 的 `build-macos` / `macos-app` 就是这几步。

可选降噪库：Windows 用 CMake 和 Visual Studio C++ x64 工具执行 `pwsh -File can-audio/native/rnnoise/build.ps1 -Out can-audio/controller`；
macOS / Linux 执行 `packaging/build-rnnoise.sh can-audio can-audio/controller`。存在 `rnnoise.dll` / `librnnoise.*` 时会一并打进语音引擎；
缺少时语音正常收发，降噪不可用。

## 发布与版本号

推到 `main` 就由 [`.github/workflows/release.yml`](.github/workflows/release.yml) 编译、测试、打包并发 Release；
手动运行该工作流、或往 `main` 开 PR，只跑测试和打包（产物挂在 Actions 里），不占版本号。

- 版本号 `主.次.补丁`：主次版本写在 `ui/src/ControllerClient/ControllerClient.csproj` 的 `<CanVersion>`（现为 3.0，接在 Python 版 2.x 之后）；
  补丁号由 CI 数已有的 `v3.0.*` 标签取最大加一，这个系列第一次发布是 `v3.0.0`。要发 3.1 就把 `CanVersion` 改成 `3.1.0`。
- 程序标题栏显示版本号，日志写完整版本与 build 号（`提交数.短哈希`）。正式包报它被打成的标签；本地或从源码编译的显示
  下一个补丁号加 `(Dev Build N)`。
- 每次都取 can-audio `new-client-sys` 的最新提交打包，用到的提交号写在 Release 说明和包里的 `controller/voice-engine.txt`。
  can-audio 有更新而本仓库没有改动时，在 Actions 里手动运行可以先试包；要发版需要往 `main` 推一个提交。
- 发布前会跑 can-audio `controller/` 的单元测试和本仓库的 C# 检查（Windows、Linux、macOS 各一遍），并验包：exe 版本号、
  语音引擎带齐 opus / rnnoise、冻结后的语音引擎能回协议。三个系统的包全部打成功才发 Release。任何一步失败都不打标签、不发 Release。

## 功能范围

已接入真实连接、设备枚举、音量、多个 PTT、席位规则、多频率 RX/TX/XC、收发指示、呼号映射、连接状态与延迟、置顶小窗及按日日志。

自动麦克风校准、中英文界面切换和服务器目录选择尚未迁入。压缩参数由现有 Mumble 引擎管理；内话、地图、UniATIS、Stream Deck 尚未接入。

## 来源

Python 语音引擎最初提取自 `JianyueLab-Org/can-audio` 的 `controller/`（源提交 `e724bff58ddad12eb965aba79817b4b58b30b489`），
现在只在 can-audio 的 `new-client-sys` 分支维护。
