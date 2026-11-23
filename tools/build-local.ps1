# 构建自包含程序到项目根目录的 SeriTerm.exe。
# 更新前关闭占用目标文件的 SeriTerm 进程。
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/SeriTerm.App/SeriTerm.App.csproj'
$staging = Join-Path $root 'src/SeriTerm.App/bin/Release/current'
$destination = Join-Path $root 'SeriTerm.exe'
$stagingExe = Join-Path $staging 'SeriTerm.exe'
$runningInstances = @(Get-Process -Name 'SeriTerm' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -ieq $destination -or $_.Path -ieq $stagingExe) })
foreach ($instance in $runningInstances) {
    Stop-Process -Id $instance.Id -Force -ErrorAction Stop
    if (-not $instance.WaitForExit(5000)) { throw "无法关闭 SeriTerm 进程 $($instance.Id)。" }
}
$arguments = @(
    'publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', '-o', $staging, '--nologo'
)
if ($Version) { $arguments += "-p:Version=$Version" }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }
try {
    Copy-Item -LiteralPath $stagingExe -Destination $destination -Force
} catch {
    throw "无法更新 $destination：$($_.Exception.Message)"
}
Write-Output "已生成：$destination"
