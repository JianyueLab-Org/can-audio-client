#!/bin/sh
# 给当前用户装桌面菜单入口，程序留在本目录不动（移动目录后重新运行一次）。
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}
mkdir -p "$data/applications" "$data/icons/hicolor/256x256/apps"
cp "$dir/can-audio-client.png" "$data/icons/hicolor/256x256/apps/can-audio-client.png"
sed "s|^Exec=.*|Exec=\"$dir/ControllerClient\"|" "$dir/can-audio-client.desktop" > "$data/applications/can-audio-client.desktop"
command -v update-desktop-database >/dev/null && update-desktop-database "$data/applications" || true
echo "已添加菜单入口：CAN Audio"
# 全局 PTT 直接读 /dev/input，需要 input 组；Wayland 下没有别的办法在任何窗口读到按键
if ! id -nG | tr ' ' '\n' | grep -qx input; then
    echo "提示：要在任何窗口使用键盘 / 鼠标侧键 PTT，请执行 sudo usermod -aG input \"\$USER\" 后重新登录。"
fi
