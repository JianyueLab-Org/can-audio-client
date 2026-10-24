# -*- mode: python -*-
# macOS / Linux 的语音引擎打包（Windows 仍用 package-release.ps1 里的命令行参数）。
# package-release.ps1 通过环境变量传入：
#   CAN_VOICE_CONTROLLER  can-audio 的 controller/ 目录
#   CAN_VOICE_BINARIES    要随包的原生库（libopus、librnnoise），用路径分隔符隔开
import os
import sys

from PyInstaller.utils.hooks import collect_all

controller = os.environ["CAN_VOICE_CONTROLLER"]
binaries = [(path, ".") for path in os.environ.get("CAN_VOICE_BINARIES", "").split(os.pathsep) if path]
datas, hiddenimports = [], []
for collected, target in zip(collect_all("pymumble_py3"), (datas, binaries, hiddenimports)):
    target += collected

a = Analysis(
    [os.path.join(controller, "bridge.py")],
    pathex=[controller],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    runtime_hooks=[os.path.join(SPECPATH, "pyi_rth_opus.py")],
)
if sys.platform.startswith("linux"):
    # libasound 用系统的：ALSA 插件目录是编译进 libasound 的，Ubuntu 编的那份在 Arch 等发行版上
    # 找不到 pipewire / pulse 插件，"default" 设备就打不开。各发行版桌面环境都自带 libasound。
    a.binaries = [entry for entry in a.binaries if not os.path.basename(entry[0]).startswith("libasound.so")]

pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, a.binaries, a.datas, [], name="can-voice", console=True, upx=False)
