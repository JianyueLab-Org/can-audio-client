"""从任意工作目录启动独立管制语音客户端。"""

import os
from pathlib import Path
import runpy
import sys


def main():
    client = Path(__file__).resolve().parent / "controller"
    # opuslib 通过 ctypes 查找 opus.dll；必须在导入语音模块之前配置。
    os.environ["PATH"] = str(client) + os.pathsep + os.environ.get("PATH", "")
    sys.path.insert(0, str(client))
    os.chdir(client)
    runpy.run_path(str(client / "gui.py"), run_name="__main__")


if __name__ == "__main__":
    main()
