# 生成仅包含 C# 程序与打包语音引擎的便携 ZIP，并验一遍包能不能用。
# 需要 PowerShell 7、.NET 10 SDK，以及语音引擎的检出：先跑 `sync-voice.ps1 -Venv`，它把
# JianyueLab-Org/can-audio 的 new-client-sys 拉到 can-audio/ 并在 can-audio/.venv 装好打包依赖。
#
# 版本号：设置了环境变量 CAN_VERSION（CI 数标签算出来的）就是正式包；否则取 csproj 的 CanVersion，
# 包名带 -dev，程序里显示 (Dev Build N)。有 can-audio/controller/rnnoise.dll 时一起打进语音引擎。
param(
    [string]$VoiceRepo = "$PSScriptRoot/can-audio",
    [string]$Python = "$VoiceRepo/.venv/Scripts/python.exe"
)
$ErrorActionPreference = 'Stop'
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
$name = "can-audio-client-$version-win-x64"
$stage = Join-Path $dist $name

& "$PSScriptRoot/build.ps1"
$arguments = @('-m', 'PyInstaller', '--noconfirm', '--clean', '--onefile', '--name', 'can-voice',
    '--icon', (Join-Path $PSScriptRoot 'ui/src/ControllerClient/app.ico'),
    '--distpath', (Join-Path $dist 'voice'), '--workpath', (Join-Path $PSScriptRoot 'build/voice'),
    '--specpath', (Join-Path $PSScriptRoot 'build'), '--paths', $controller, '--collect-all', 'pymumble_py3',
    '--add-binary', "$(Join-Path $controller 'opus.dll');.")
$rnnoise = Join-Path $controller 'rnnoise.dll'
$license = Join-Path $controller 'RNNOISE-COPYING'
if (Test-Path -LiteralPath $rnnoise) { $arguments += @('--add-binary', "$rnnoise;.") }
& $Python @arguments (Join-Path $controller 'bridge.py')
if ($LASTEXITCODE) { throw 'PyInstaller 打包语音引擎失败' }

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'controller') | Out-Null
Copy-Item (Join-Path $dist 'CanAudio-singlefile/ControllerClient.exe') $stage
Copy-Item (Join-Path $dist 'voice/can-voice.exe') (Join-Path $stage 'controller')
Copy-Item (Join-Path $PSScriptRoot 'README.md') $stage
if ((Test-Path -LiteralPath $rnnoise) -and (Test-Path -LiteralPath $license)) { Copy-Item $license $stage }
# 排查时要能知道这个包里的语音引擎是 can-audio 的哪一次提交
[IO.File]::WriteAllText((Join-Path $stage 'controller/voice-engine.txt'),
    "JianyueLab-Org/can-audio new-client-sys @ $voiceCommit`n", [Text.UTF8Encoding]::new($false))

# —— 验包 ——
# opus.dll / rnnoise.dll 是 ctypes 运行时加载的，PyInstaller 的静态分析看不见；漏了不会让打包失败，
# 只会让用户拿到一个装上就连不上或没有降噪的包。所以把冻结后的语音引擎真的跑一遍协议，并列出它带的库。
$voice = Join-Path $stage 'controller/can-voice.exe'
$bundled = & $Python -m PyInstaller.utils.cliutils.archive_viewer --list $voice
if ($LASTEXITCODE) { throw '读不了 can-voice.exe 的内容' }
$expected = @('opus.dll') + $(if (Test-Path -LiteralPath $rnnoise) { 'rnnoise.dll' } else { @() })
foreach ($dll in $expected) {
    if (-not ($bundled | Select-String -SimpleMatch $dll)) { throw "can-voice.exe 里没有 $dll" }
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
$product = (Get-Item (Join-Path $stage 'ControllerClient.exe')).VersionInfo.ProductVersion
$expectedVersion = $version -replace '-dev$', ''
if ($product -ne $expectedVersion) { throw "ControllerClient.exe 的版本是 $product，应当是 $expectedVersion" }
"验包通过：版本 $product，语音引擎 can-audio@$($voiceCommit.Substring(0, 7))，带 $($expected -join ' / ')"

$archive = Join-Path $dist "$name.zip"
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
Compress-Archive -Path $stage -DestinationPath $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $(Split-Path $archive -Leaf)`n", [Text.UTF8Encoding]::new($false))
$archive
