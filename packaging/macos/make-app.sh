#!/usr/bin/env bash
# 把 osx-arm64 包目录打成 ARM64 CAN Audio.app，ad-hoc 签名后打成 zip。
# 用法：make-app.sh <版本号> <arm64 包目录> <app.icns> <输出目录>
# 包目录是 package-release.ps1 -Runtime osx-arm64 的产物：ControllerClient、controller/can-voice 等。
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
version=$1 arm=$2 icns=$3
mkdir -p "$4"
out=$(cd "$4" && pwd)
: "${MACOSX_DEPLOYMENT_TARGET:=14.0}"
app="$out/CAN Audio.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

# Contents/MacOS 下只放可执行文件：说明、许可之类的数据文件放这里会让签名出错
mkdir -p "$app/Contents/MacOS/arm64/controller"
cp "$arm/ControllerClient" "$app/Contents/MacOS/arm64/"
cp "$arm/controller/can-voice" "$app/Contents/MacOS/arm64/controller/"
cp "$arm/controller/voice-engine.txt" "$app/Contents/Resources/voice-engine-arm64.txt"
for doc in README.md RNNOISE-COPYING; do
    if [ -f "$arm/$doc" ]; then cp "$arm/$doc" "$app/Contents/Resources/"; fi
done
cp "$icns" "$app/Contents/Resources/app.icns"

clang -O2 -Wall -Werror -mmacosx-version-min="$MACOSX_DEPLOYMENT_TARGET" -arch arm64 \
      -o "$app/Contents/MacOS/CANAudio" "$here/launcher.c"
# CFBundleShortVersionString 只能是数字：本地包的 3.0.1-dev 写成 3.0.1
sed -e "s/@VERSION@/${version%%-*}/g" -e "s/@MINOS@/$MACOSX_DEPLOYMENT_TARGET/g" "$here/Info.plist" > "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist"

# 没有开发者证书，只能 ad-hoc 签名：Apple 芯片要求每个可执行文件都有签名，权限也要按签名记账
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"
lipo -info "$app/Contents/MacOS/CANAudio"

archive="$out/can-audio-client-$version-macos-arm64.zip"
rm -f "$archive"
ditto -c -k --sequesterRsrc --keepParent "$app" "$archive"
echo "$archive"
