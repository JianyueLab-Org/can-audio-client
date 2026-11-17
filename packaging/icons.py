"""从 app.ico 生成 Linux 桌面图标（PNG）和 macOS 图标（ICNS）。用法：python icons.py app.ico 输出目录"""
import os
import sys

from PIL import Image

source, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
with Image.open(source) as ico:
    image = ico.convert("RGBA")
    # 源 app.ico 是 256 px 的 16–256 多尺寸图标；macOS 的 1024 由 256 放大，换更大的源图这里不用改
    image.resize((256, 256), Image.LANCZOS).save(os.path.join(out, "can-audio-client.png"))
    image.resize((1024, 1024), Image.LANCZOS).save(os.path.join(out, "app.icns"))
