# 发布单文件绿色版（自包含，目标机器无需安装 .NET 运行时）。
#
# 用法：
#   pwsh -File tools/publish.ps1                 # 单文件自包含（推荐分发用）
#   pwsh -File tools/publish.ps1 -FrameworkDependent   # 精简版，需目标机装 .NET 桌面运行时
#
# 注意：不要开 PublishTrimmed —— WPF 不支持裁剪，会得到运行时找不到类型的错误。

param(
    [string]$OutputDir = 'artifacts/publish',
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'

$project = 'src/SeriTerm.App/SeriTerm.App.csproj'

if (-not (Test-Path $project)) { throw "找不到项目文件：$project" }

if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    "--self-contained=$selfContained",
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=none',
    '-p:GenerateDocumentationFile=false',
    '-o', $OutputDir,
    '--nologo'
)

Write-Output "== dotnet $($arguments -join ' ')"
& dotnet @arguments

if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码 $LASTEXITCODE" }

$exe = Join-Path $OutputDir 'SeriTerm.exe'
if (-not (Test-Path $exe)) { throw "发布产物里没有 SeriTerm.exe" }

$info = Get-Item $exe
$sizeMb = [Math]::Round($info.Length / 1MB, 1)

Write-Output ''
Write-Output "== 产物：$($info.FullName)"
Write-Output "== 大小：$sizeMb MB（$($info.Length) 字节）"
Write-Output "== 目录内容："
Get-ChildItem $OutputDir | ForEach-Object { Write-Output ("   {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
