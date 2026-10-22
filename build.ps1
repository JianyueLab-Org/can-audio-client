param([string]$Out = "$PSScriptRoot/dist/CanAudio-singlefile")
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $PSScriptRoot -PathType Container)) { throw '项目目录不存在' }
dotnet run --project "$PSScriptRoot/tests/ControllerClient.Checks" -c Release
if ($LASTEXITCODE) { throw 'C# 检查失败' }
# 与 MSFS 新版一致：自包含单文件、压缩、原生库自解压；保留反射所需类型。
dotnet publish "$PSScriptRoot/ui/src/ControllerClient/ControllerClient.csproj" `
    -p:PublishProfile=WindowsSingleFile -o $Out
if ($LASTEXITCODE) { throw '发布失败' }
"发布完成：$Out/ControllerClient.exe"
