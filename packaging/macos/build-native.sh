#!/usr/bin/env bash
# 从源码构建语音引擎要的 libopus 和 PortAudio，装到 <prefix>。用法：build-native.sh <prefix>
# 不用 Homebrew：bottle 只支持 runner 自己的 macOS 版本，打出来的包在旧一点的系统上加载失败。
# 这里按 MACOSX_DEPLOYMENT_TARGET 编，安装名写成绝对路径，PyInstaller 才找得到并随包带走。
set -euo pipefail
mkdir -p "$1"
prefix=$(cd "$1" && pwd)
: "${MACOSX_DEPLOYMENT_TARGET:=14.0}"
export MACOSX_DEPLOYMENT_TARGET
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

fetch() { # 名字 URL SHA-256
    curl -fsSL -o "$work/$1.tar.gz" "$2"
    echo "$3  $work/$1.tar.gz" | shasum -a 256 -c -
    mkdir -p "$work/$1" && tar -xzf "$work/$1.tar.gz" -C "$work/$1" --strip-components 1
}
build() { # 源码目录 额外 CMake 参数...
    local src=$1; shift
    cmake -S "$src" -B "$src/build" -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$prefix" \
          -DCMAKE_INSTALL_NAME_DIR="$prefix/lib" -DCMAKE_OSX_DEPLOYMENT_TARGET="$MACOSX_DEPLOYMENT_TARGET" "$@"
    cmake --build "$src/build" --config Release -j "$(sysctl -n hw.ncpu)"
    cmake --install "$src/build"
}

fetch opus https://downloads.xiph.org/releases/opus/opus-1.5.2.tar.gz \
      65c1d2f78b9f2fb20082c38cbe47c951ad5839345876e46941612ee87f9a7ce1
build "$work/opus" -DBUILD_SHARED_LIBS=ON -DOPUS_BUILD_PROGRAMS=OFF -DOPUS_BUILD_TESTING=OFF

fetch portaudio https://github.com/PortAudio/portaudio/archive/refs/tags/v19.7.0.tar.gz \
      5af29ba58bbdbb7bbcefaaecc77ec8fc413f0db6f4c4e286c40c3e1b83174fa0
build "$work/portaudio" -DPA_BUILD_SHARED=ON -DPA_BUILD_STATIC=OFF
# PyAudio 链 -lportaudio；只留动态库，免得链到静态库还得补一串 CoreAudio 框架参数
rm -f "$prefix"/lib/libportaudio*.a
ls -l "$prefix/lib"
