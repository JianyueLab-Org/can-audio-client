"""从 app.ico 生成 Linux 桌面图标（PNG）和 macOS 图标（ICNS）。用法：python icons.py app.ico 输出目录"""
import os
import sys

from PIL import Image

source, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
with Image.open(source) as ico:
    image = ico.convert("RGBA")
    # 原图只有 64 px，放大到 256 / 1024 会发虚；换了大图标源这里不用改
    image.resize((256, 256), Image.LANCZOS).save(os.path.join(out, "can-audio-client.png"))
    image.resize((1024, 1024), Image.LANCZOS).save(os.path.join(out, "app.icns"))
