#!/bin/sh
# 给当前用户装桌面菜单入口，程序留在本目录不动（移动目录后重新运行一次）。
set -eu
dir=$(cd "$(dirname "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}
for file in ControllerClient controller/can-voice can-audio-client.png can-audio-client.desktop; do
    if [ ! -f "$dir/$file" ]; then
        echo "安装失败：缺少 $file，请完整解压发布包。" >&2
        exit 1
    fi
done
chmod +x "$dir/ControllerClient" "$dir/controller/can-voice" "$dir/uninstall.sh"
mkdir -p "$data/applications" "$data/icons/hicolor/256x256/apps"
cp "$dir/can-audio-client.png" "$data/icons/hicolor/256x256/apps/can-audio-client.png"
sed "s|^Exec=.*|Exec=\"$dir/ControllerClient\"|" "$dir/can-audio-client.desktop" > "$data/applications/can-audio-client.desktop"
command -v update-desktop-database >/dev/null && update-desktop-database "$data/applications" || true
echo "已添加菜单入口：CAN Audio"
echo "卸载菜单入口：运行 $dir/uninstall.sh；随后可删除解压目录。"
# 全局 PTT 直接读 /dev/input，需要 input 组；Wayland 下没有别的办法在任何窗口读到按键
if ! id -nG | tr ' ' '\n' | grep -qx input; then
    echo "提示：要在任何窗口使用键盘 / 鼠标侧键 PTT，请执行 sudo usermod -aG input \"\$USER\" 后重新登录。"
fi
