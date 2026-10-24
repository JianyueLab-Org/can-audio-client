# 编译 C# 单文件程序。-Runtime 默认取本机：win-x64 / linux-x64 / osx-arm64 / osx-x64。
param(
    [string]$Runtime = $(if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'win-x64' }
                         elseif ($IsMacOS) { if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'osx-arm64' } else { 'osx-x64' } }
                         else { 'linux-x64' }),
    [string]$Out = "$PSScriptRoot/dist/CanAudio-$Runtime"
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $PSScriptRoot -PathType Container)) { throw '项目目录不存在' }
dotnet run --project "$PSScriptRoot/tests/ControllerClient.Checks" -c Release
if ($LASTEXITCODE) { throw 'C# 检查失败' }
# 与 MSFS 新版一致：自包含单文件、压缩、原生库自解压；保留反射所需类型。
dotnet publish "$PSScriptRoot/ui/src/ControllerClient/ControllerClient.csproj" `
    -p:PublishProfile=SingleFile -r $Runtime -o $Out
if ($LASTEXITCODE) { throw '发布失败' }
"发布完成：$Out/ControllerClient$(if ($Runtime -like 'win-*') { '.exe' })"
