# 拉取 / 更新 Python 语音引擎：JianyueLab-Org/can-audio 的 new-client-sys 分支，放在本仓库的 can-audio/ 下（已 gitignore）。
# 语音引擎只在 can-audio 维护，本仓库不再存放任何 Python 代码；CI 每次也是拉这个分支的最新提交来打包。
# 加 -Venv 时顺便在 can-audio/.venv 建好 Python 环境并装上依赖。Windows 必须是 3.12（PyAudio 只有到 3.12 的 wheel）；
# macOS / Linux 上 PyAudio 从源码编，要先装 PortAudio：Arch `pacman -S portaudio opus`，
# Debian/Ubuntu `apt install portaudio19-dev libopus0`，macOS 见 packaging/macos/build-native.sh 或 `brew install portaudio opus`。
param(
    [string]$Ref = 'new-client-sys',
    [string]$Path = "$PSScriptRoot/can-audio",
    [switch]$Venv
)
$ErrorActionPreference = 'Stop'
$url = 'https://github.com/JianyueLab-Org/can-audio.git'
if (Test-Path -LiteralPath (Join-Path $Path '.git')) {
    git -C $Path fetch --quiet origin $Ref
    if ($LASTEXITCODE) { throw "拉取 can-audio 失败" }
    git -C $Path checkout --quiet --detach FETCH_HEAD
} else {
    git clone --quiet --branch $Ref $url $Path
}
if ($LASTEXITCODE) { throw "检出 can-audio 失败" }
"语音引擎：can-audio $Ref @ $(git -C $Path rev-parse --short HEAD)"

if ($Venv) {
    $windows = $IsWindows -or $env:OS -eq 'Windows_NT'
    $python = Join-Path $Path $(if ($windows) { '.venv/Scripts/python.exe' } else { '.venv/bin/python' })
    if (-not (Test-Path -LiteralPath $python)) {
        if ($windows) { py -3.12 -m venv (Join-Path $Path '.venv') }
        else {
            $base = @('python3.12', 'python3') | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
            if (-not $base) { throw '找不到 python3' }
            & $base -m venv (Join-Path $Path '.venv')
        }
        if ($LASTEXITCODE) { throw '创建 Python 虚拟环境失败' }
    }
    $env:PYTHONUTF8 = '1'   # requirements 里有中文注释，pip 默认按 GBK 读会失败
    & $python -m pip install --quiet -r (Join-Path $Path 'requirements-build.txt')
    if ($LASTEXITCODE) { throw "安装语音引擎依赖失败" }
}
