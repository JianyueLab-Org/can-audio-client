# 生成仅包含 C# 程序与打包语音引擎的便携包，并验一遍包能不能用。Windows、macOS、Linux 通用。
# 需要 PowerShell 7、.NET 10 SDK，以及语音引擎的检出：先跑 `sync-voice.ps1 -Venv`，它把
# JianyueLab-Org/can-audio 的 new-client-sys 拉到 can-audio/ 并在 can-audio/.venv 装好打包依赖。
#
# 版本号：设置了环境变量 CAN_VERSION（CI 数标签算出来的）就是正式包；否则取 csproj 的 CanVersion，
# 包名带 -dev，程序里显示 (Dev Build N)。
#
# 原生库：Windows 用 can-audio/controller 里的 opus.dll、rnnoise.dll（有就带上）；
# macOS / Linux 用 -OpusLibrary 指定的 libopus（Linux 不给时找系统的），controller/ 里有
# librnnoise.so / librnnoise.dylib 时一起带上（packaging/build-rnnoise.sh 构建）。
#
# 产物：win-x64 → zip；linux-x64 → tar.gz；
# osx-arm64 → tar.gz，再由 packaging/macos/make-app.sh 打成 ARM64 .app。
param(
    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')]
    [string]$Runtime = $(if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'win-x64' }
                         elseif ($IsMacOS) { 'osx-arm64' }
                         else { 'linux-x64' }),
    [string]$VoiceRepo = "$PSScriptRoot/can-audio",
    [string]$Python = $(if ($IsWindows -or $env:OS -eq 'Windows_NT') { "$VoiceRepo/.venv/Scripts/python.exe" } else { "$VoiceRepo/.venv/bin/python" }),
    [string]$OpusLibrary
)
$ErrorActionPreference = 'Stop'
$windows = $Runtime -like 'win-*'
$mac = $Runtime -like 'osx-*'
$exe = if ($windows) { '.exe' } else { '' }
$controller = Join-Path $VoiceRepo 'controller'
if (-not (Test-Path -LiteralPath (Join-Path $controller 'bridge.py'))) {
    throw "找不到 $controller/bridge.py：先运行 sync-voice.ps1 拉取 can-audio"
}
$voiceCommit = (git -C $VoiceRepo rev-parse HEAD).Trim()
if ($LASTEXITCODE) { throw '读不出 can-audio 的提交' }
$project = Join-Path $PSScriptRoot 'ui/src/ControllerClient/ControllerClient.csproj'
$version = $env:CAN_VERSION
if (-not $version) {
    $version = (dotnet msbuild $project -getProperty:CanVersion).Trim() + '-dev'
    if ($LASTEXITCODE) { throw '读不出 csproj 的 CanVersion' }
}
$dist = Join-Path $PSScriptRoot 'dist'
$name = "can-audio-client-$version-$Runtime"
$stage = Join-Path $dist $name
$voiceDist = Join-Path $dist "voice-$Runtime"
$packaging = Join-Path $PSScriptRoot 'packaging'

& "$PSScriptRoot/build.ps1" -Runtime $Runtime -Out (Join-Path $dist "CanAudio-$Runtime")

# —— 语音引擎 ——
$license = Join-Path $controller 'RNNOISE-COPYING'
if ($windows) {
    $rnnoise = Join-Path $controller 'rnnoise.dll'
    $expected = @('opus.dll') + $(if (Test-Path -LiteralPath $rnnoise) { 'rnnoise.dll' } else { @() })
    $arguments = @('-m', 'PyInstaller', '--noconfirm', '--clean', '--onefile', '--name', 'can-voice',
        '--icon', (Join-Path $PSScriptRoot 'ui/src/ControllerClient/app.ico'),
        '--distpath', $voiceDist, '--workpath', (Join-Path $PSScriptRoot "build/voice-$Runtime"),
        '--specpath', (Join-Path $PSScriptRoot 'build'), '--paths', $controller, '--collect-all', 'pymumble_py3',
        '--add-binary', "$(Join-Path $controller 'opus.dll');.")
    if (Test-Path -LiteralPath $rnnoise) { $arguments += @('--add-binary', "$rnnoise;.") }
    & $Python @arguments (Join-Path $controller 'bridge.py')
} else {
    if (-not $OpusLibrary) {
        if ($mac) { throw 'macOS 需要用 -OpusLibrary 指定 libopus（packaging/macos/build-native.sh 构建）' }
        $OpusLibrary = @('/usr/lib/x86_64-linux-gnu/libopus.so.0', '/usr/lib64/libopus.so.0', '/usr/lib/libopus.so.0') |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $OpusLibrary) { throw '找不到 libopus.so.0：先安装 opus（Arch: opus，Debian/Ubuntu: libopus0），或用 -OpusLibrary 指定' }
    }
    # PyInstaller 按给的文件名放进包里（符号链接取目标内容），运行时钩子按 libopus* 找
    $opus = $OpusLibrary
    $rnnoise = Join-Path $controller $(if ($mac) { 'librnnoise.dylib' } else { 'librnnoise.so' })
    $binaries = @($opus) + $(if (Test-Path -LiteralPath $rnnoise) { $rnnoise } else { @() })
    $expected = @('libopus') + $(if (Test-Path -LiteralPath $rnnoise) { 'librnnoise' } else { @() })
    $env:CAN_VOICE_CONTROLLER = $controller
    $env:CAN_VOICE_BINARIES = $binaries -join [IO.Path]::PathSeparator
    & $Python -m PyInstaller --noconfirm --clean --distpath $voiceDist `
        --workpath (Join-Path $PSScriptRoot "build/voice-$Runtime") (Join-Path $packaging 'can-voice.spec')
}
if ($LASTEXITCODE) { throw 'PyInstaller 打包语音引擎失败' }

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'controller') | Out-Null
Copy-Item (Join-Path $dist "CanAudio-$Runtime/ControllerClient$exe") $stage
Copy-Item (Join-Path $voiceDist "can-voice$exe") (Join-Path $stage 'controller')
Copy-Item (Join-Path $PSScriptRoot 'README.md') $stage
if ((Test-Path -LiteralPath $rnnoise) -and (Test-Path -LiteralPath $license)) { Copy-Item $license $stage }
if ($Runtime -like 'linux-*') {
    Copy-Item (Join-Path $packaging 'linux/can-audio-client.desktop'), (Join-Path $packaging 'linux/install.sh') $stage
    $icon = Join-Path $dist 'icons/can-audio-client.png'
    & $Python (Join-Path $packaging 'icons.py') (Join-Path $PSScriptRoot 'ui/src/ControllerClient/app.ico') (Split-Path $icon)
    if ($LASTEXITCODE) { throw '生成图标失败（需要 pip install pillow）' }
    Copy-Item $icon $stage
}
# 排查时要能知道这个包里的语音引擎是 can-audio 的哪一次提交
[IO.File]::WriteAllText((Join-Path $stage 'controller/voice-engine.txt'),
    "JianyueLab-Org/can-audio new-client-sys @ $voiceCommit`n", [Text.UTF8Encoding]::new($false))
if (-not $windows) {
    # Copy-Item 在 Unix 上不一定保留可执行位，tar 包里的权限就是用户解压后的权限
    chmod 755 (Join-Path $stage 'ControllerClient') (Join-Path $stage 'controller/can-voice')
    if ($Runtime -like 'linux-*') { chmod 755 (Join-Path $stage 'install.sh') }
}

# —— 验包 ——
# opus / rnnoise 是 ctypes 运行时加载的，PyInstaller 的静态分析看不见；漏了不会让打包失败，
# 只会让用户拿到一个装上就连不上或没有降噪的包。所以把冻结后的语音引擎真的跑一遍协议，并列出它带的库。
$voice = Join-Path $stage "controller/can-voice$exe"
$bundled = & $Python -m PyInstaller.utils.cliutils.archive_viewer --list $voice
if ($LASTEXITCODE) { throw "读不了 can-voice$exe 的内容" }
foreach ($library in $expected) {
    if (-not ($bundled | Select-String -SimpleMatch $library)) { throw "can-voice$exe 里没有 $library" }
}
# 引擎的 stdout 固定是 UTF-8；控制台默认代码页（中文系统是 GBK）会把中文设备名解成乱码，JSON 跟着坏掉
$consoleEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = $OutputEncoding = [Text.UTF8Encoding]::new($false)
try { $reply = '{"id":1,"command":"devices"}', '{"id":2,"command":"quit"}' | & $voice 2>$null }
finally { [Console]::OutputEncoding = $consoleEncoding }
$messages = $reply | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }
if (-not ($messages | Where-Object { $_.event -eq 'ready' })) { throw "冻结的语音引擎没有报 ready：$reply" }
if (-not ($messages | Where-Object { $_.id -eq 1 -and $_.PSObject.Properties.Name -contains 'result' })) {
    throw "冻结的语音引擎没有回 devices：$reply"
}
$expectedVersion = $version -replace '-dev$', ''
if ($windows) {
    # 只有 Windows 的 exe 带版本资源；其他系统的版本号由 CI 的 CAN_VERSION 同一个来源写入
    $product = (Get-Item (Join-Path $stage 'ControllerClient.exe')).VersionInfo.ProductVersion
    if ($product -ne $expectedVersion) { throw "ControllerClient.exe 的版本是 $product，应当是 $expectedVersion" }
}
"验包通过：$Runtime 版本 $expectedVersion，语音引擎 can-audio@$($voiceCommit.Substring(0, 7))，带 $($expected -join ' / ')"

function Write-Checksum([string]$Path) {
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$Path.sha256", "$hash  $(Split-Path $Path -Leaf)`n", [Text.UTF8Encoding]::new($false))
    $hash
}

if ($windows) {
    $archive = Join-Path $dist "$name.zip"
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
    Compress-Archive -Path $stage -DestinationPath $archive
} else {
    # tar 保留可执行位，zip 在 Unix 上解压后常常丢掉
    $archive = Join-Path $dist "$name.tar.gz"
    tar -czf $archive -C $dist $name
    if ($LASTEXITCODE) { throw '打 tar 包失败' }
}
Write-Checksum $archive | Out-Null
$archive
