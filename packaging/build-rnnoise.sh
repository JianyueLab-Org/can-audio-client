#!/usr/bin/env bash
# macOS / Linux 版的 can-audio/native/rnnoise/build.ps1：下载 xiph RNNoise v0.2、校验、构建共享库。
# 用法：build-rnnoise.sh <can-audio 检出> <输出目录>
# 输出 librnnoise.so / librnnoise.dylib 和 RNNOISE-COPYING；denoise.py 按这两个文件名在随包目录里找。
set -euo pipefail
repo=$(cd "$1" && pwd)
mkdir -p "$2"
out=$(cd "$2" && pwd)
url=https://github.com/xiph/rnnoise/releases/download/v0.2/rnnoise-0.2.tar.gz
sha=90fce4b00b9ff24c08dbfe31b82ffd43bae383d85c5535676d28b0a2b11c0d37
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
curl -fsSL -o "$work/rnnoise.tar.gz" "$url"
echo "$sha  $work/rnnoise.tar.gz" | shasum -a 256 -c -
tar -xzf "$work/rnnoise.tar.gz" -C "$work"
cmake -S "$repo/native/rnnoise" -B "$work/build" -DCMAKE_BUILD_TYPE=Release -DRNNOISE_SRC="$work/rnnoise-0.2" \
      ${MACOSX_DEPLOYMENT_TARGET:+-DCMAKE_OSX_DEPLOYMENT_TARGET=$MACOSX_DEPLOYMENT_TARGET}
cmake --build "$work/build" --config Release
find "$work/build" -maxdepth 1 \( -name 'librnnoise.so' -o -name 'librnnoise.dylib' \) -exec cp {} "$out/" \;
cp "$work/rnnoise-0.2/COPYING" "$out/RNNOISE-COPYING"
ls "$out"
