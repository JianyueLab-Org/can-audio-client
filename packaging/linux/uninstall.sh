#!/bin/sh
# 删除当前用户的菜单入口和图标，程序目录由用户自行删除。
set -eu
data=${XDG_DATA_HOME:-$HOME/.local/share}
rm -f "$data/applications/can-audio-client.desktop"
rm -f "$data/icons/hicolor/256x256/apps/can-audio-client.png"
command -v update-desktop-database >/dev/null && update-desktop-database "$data/applications" || true
echo "已移除 CAN Audio 菜单入口和图标。"
echo "关闭客户端后，可删除解压目录以移除程序；用户设置和日志保留。"
