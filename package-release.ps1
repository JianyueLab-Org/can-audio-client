# 生成仅包含 C# 程序与打包语音引擎的便携 ZIP。
# 需要已按 README 建好 .venv 并安装 requirements-build.txt；可用 -Python 指定其他解释器。
param([string]$Python = "$PSScriptRoot/.venv/Scripts/python.exe")
$ErrorActionPreference = 'Stop'
$version = '1.0.0'
$dist = Join-Path $PSScriptRoot 'dist'
$controller = Join-Path $PSScriptRoot 'controller'
$stage = Join-Path $dist "can-audio-client-$version-win-x64"

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

$archive = Join-Path $dist "can-audio-client-$version-win-x64.zip"
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
Compress-Archive -Path $stage -DestinationPath $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $(Split-Path $archive -Leaf)`n", [Text.UTF8Encoding]::new($false))
$archive
