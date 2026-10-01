# 发布单文件绿色版（自包含，目标机器无需安装 .NET 运行时）。
#
# 用法：
#   pwsh -File tools/publish.ps1                 # 单文件自包含（推荐分发用，约 64 MB）
#   pwsh -File tools/publish.ps1 -FrameworkDependent   # 精简版约 1.3 MB，需目标机装 .NET 桌面运行时
#
# 注意：不要开 PublishTrimmed —— WPF 不支持裁剪，会得到运行时找不到类型的错误。
# 注意：单文件压缩只在自包含时可用。框架依赖 + EnableCompressionInSingleFile 会被 SDK 直接拒绝
#       （NETSDK1176：仅在发布独立应用程序时才支持在单个文件捆绑包中进行压缩），故该开关按模式传。
#
# 便携模式：尚未实现。计划是 exe 同目录放一个空的 seriterm.portable 文件后，配置与日志改写在该目录，
#           不再落到 %AppData% 与「文档」，方便放 U 盘/移动硬盘随身带；在那之前本脚本只负责出单文件产物。

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
    '-p:IncludeNativeLibrariesForSelfExtract=true'
)

# 只有自包含才能压缩单文件；框架依赖时加上这一项会让 dotnet publish 直接以 NETSDK1176 失败
if ($selfContained -eq 'true') { $arguments += '-p:EnableCompressionInSingleFile=true' }

$arguments += @(
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
