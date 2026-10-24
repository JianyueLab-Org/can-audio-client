# PyInstaller 运行时钩子（macOS / Linux 的 can-voice）。
# opuslib 用 ctypes.util.find_library("opus") 找 libopus；冻结后的程序里这个函数只看系统库路径，
# 看不见随包解出来的那份，用户机器上没装 opus 就连不上。先在解包目录里找，找不到再交给原函数。
import ctypes.util
import glob
import os
import sys

_find_library = ctypes.util.find_library


def _bundled_first(name):
    base = getattr(sys, "_MEIPASS", None)
    if base:
        for pattern in (f"lib{name}.so*", f"lib{name}*.dylib"):
            hits = sorted(glob.glob(os.path.join(base, pattern)))
            if hits:
                return hits[0]
    return _find_library(name)


ctypes.util.find_library = _bundled_first
