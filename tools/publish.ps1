# 发布 SeriTerm 的可分发产物。三种形态，按体积从小到大：
#
#   pwsh -File tools/publish.ps1                       # 【推荐分发】小体积启动器，约 2 MB
#   pwsh -File tools/publish.ps1 -All                  # 启动器 + 自包含完整版，两个都出
#   pwsh -File tools/publish.ps1 -SelfContained        # 只出自包含完整版，约 67 MB
#   pwsh -File tools/publish.ps1 -FrameworkDependent   # 只出框架依赖单文件，约 1.7 MB（开发自用）
#
# 三者分别是什么、为什么要有三个：
#
# 1) 启动器（SeriTerm-<版本>-<RID>.exe，约 2 MB）
#    它自己是用 NativeAOT 编的**原生 exe**，不含 .NET 运行时，因此在"机器上什么都没装"时也能跑起来。
#    肚子里的程序体是第 3 种产物经 Brotli 压缩后内嵌进去的（约 0.6 MB）。
#    启动时先探测 .NET 桌面运行时：装了就直接解包运行；没装就弹窗征得同意后，
#    从微软官方短链下载官方安装包（校验 Authenticode 签名后）静默安装，再继续启动。
#    对已经装了 .NET 8/9/10 桌面运行时的机器，用户只下载约 2 MB。
#
# 2) 自包含完整版（SeriTerm-<版本>-<RID>-full.exe，约 67 MB）
#    运行时整套塞进 exe，目标机器不需要装任何东西，也不需要联网。离线/内网分发用这个。
#    注意：WPF 不支持裁剪（PublishTrimmed 会让运行时找不到类型），所以体积下不来；
#    已开 EnableCompressionInSingleFile，把 147 MB 压到约 67 MB。
#
# 3) 框架依赖单文件（SeriTerm-<版本>-<RID>-fd.exe，约 1.7 MB）
#    开发自用：本机装了运行时，改完直接跑最快。它同时也是第 1 种的"程序体"原料。
#    注意：框架依赖 + EnableCompressionInSingleFile 会被 SDK 直接拒绝
#    （NETSDK1176：仅在发布独立应用程序时才支持在单个文件捆绑包中进行压缩），故该开关按模式传。
#
# 产物文件名一律带版本号（SeriTerm-<版本>-<RID>[-full|-fd].exe），版本号不是在这里写死的：
# 从刚发布出来的 exe 自己的版本资源里读（MSBuild 把 Directory.Build.props 的 <Version> 写了进去），
# 所以"exe 里显示的版本"和"文件名上的版本"永远一致，改版本只要改那一处。
#
# 便携模式：尚未实现。计划是 exe 同目录放一个空的 seriterm.portable 文件后，配置与日志改写在该目录，
#           不再落到 %AppData% 与「文档」，方便放 U 盘/移动硬盘随身带；在那之前本脚本只负责出产物。

param(
    [string]$OutputDir = 'artifacts/publish',
    [string]$Runtime = 'win-x64',
    [switch]$Bootstrapper,
    [switch]$SelfContained,
    [switch]$FrameworkDependent,
    [switch]$All
)

$ErrorActionPreference = 'Stop'

$appProject = 'src/SeriTerm.App/SeriTerm.App.csproj'
$launcherProject = 'src/SeriTerm.Launcher/SeriTerm.Launcher.csproj'

if (-not (Test-Path $appProject)) { throw "找不到项目文件：$appProject" }
if (-not (Test-Path $launcherProject)) { throw "找不到启动器项目文件：$launcherProject" }

$modes = @($Bootstrapper, $SelfContained, $FrameworkDependent, $All) | Where-Object { $_ }

if ($modes.Count -gt 1) { throw '-Bootstrapper / -SelfContained / -FrameworkDependent / -All 只能选一个。' }

# 不指定就出启动器：它是面向用户的那一个，其余两个是备选形态
$wantBootstrapper = $Bootstrapper -or $All -or ($modes.Count -eq 0)
$wantSelfContained = $SelfContained -or $All
$wantFrameworkDependent = $FrameworkDependent

if (Test-Path $OutputDir) {
    try {
        Remove-Item $OutputDir -Recurse -Force
    }
    catch {
        # 最常见的原因不是权限：有人正开着这个目录里的 exe（发布版就放在这里）
        throw "清空输出目录失败：$OutputDir。若有正在运行的 SeriTerm（或其它程序占着这里的文件），请先关掉它再试。原始错误：$($_.Exception.Message)"
    }
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

function Invoke-Publish {
    param([string]$Project, [string[]]$Arguments)

    Write-Output "== dotnet publish $Project $($Arguments -join ' ')"

    & dotnet publish $Project @Arguments --nologo

    if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码 $LASTEXITCODE" }
}

# 版本号取自产物自身：产品版本形如 "1.0.0+<提交号>"，取 "+" 之前那段
function Get-ArtifactVersion {
    param([string]$ExePath)

    $version = ($ExePath | Get-Item).VersionInfo.ProductVersion
    $version = ($version -split '\+')[0].Trim()

    if ([string]::IsNullOrWhiteSpace($version)) {
        $version = ($ExePath | Get-Item).VersionInfo.FileVersion
        Write-Warning "产物没有产品版本，退回文件版本 $version"
    }

    if ([string]::IsNullOrWhiteSpace($version)) { throw "没能从产物里读出版本号：$ExePath" }

    return $version
}

# 发布到临时目录、读出真实版本号、再以 SeriTerm-<版本>-<RID><后缀>.exe 落到输出目录。
# 走临时目录是为了让输出目录里只有最终产物——release 工作流会把该目录里的 SeriTerm-*.exe 全传上去。
function Publish-Artifact {
    param(
        [string[]]$Arguments,
        [string]$Template,
        [string]$Label
    )

    $temp = Join-Path $env:TEMP ("seriterm-publish-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))

    New-Item -ItemType Directory -Path $temp -Force | Out-Null

    try {
        Invoke-Publish -Project $appProject -Arguments ($Arguments + @('-o', $temp))

        $produced = Join-Path $temp 'SeriTerm.exe'
        if (-not (Test-Path $produced)) { throw "发布产物里没有 SeriTerm.exe（$Label）" }

        $version = Get-ArtifactVersion -ExePath $produced
        $named = Join-Path $OutputDir ($Template -f $version, $Runtime)

        Move-Item -LiteralPath $produced -Destination $named -Force

        Write-Output "== $Label → $named"

        return $named
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Compress-Brotli {
    param([string]$Source, [string]$Destination)

    $input = [System.IO.File]::OpenRead($Source)

    try {
        $output = [System.IO.File]::Create($Destination)

        try {
            $brotli = New-Object System.IO.Compression.BrotliStream(
                $output, [System.IO.Compression.CompressionLevel]::Optimal, $true)

            try { $input.CopyTo($brotli) } finally { $brotli.Dispose() }
        }
        finally { $output.Dispose() }
    }
    finally { $input.Dispose() }
}

function New-Bootstrapper {
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        throw '生成启动器需要 Brotli 压缩（System.IO.Compression.BrotliStream），请用 PowerShell 7（pwsh）运行本脚本。'
    }

    $temp = Join-Path $env:TEMP ("seriterm-boot-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $payloadDir = Join-Path $temp 'payload'
    $launcherDir = Join-Path $temp 'launcher'

    New-Item -ItemType Directory -Path $payloadDir, $launcherDir -Force | Out-Null

    try {
        # 1) 先出"程序体"：框架依赖单文件（约 1.7 MB）
        Invoke-Publish -Project $appProject -Arguments @(
            '-c', 'Release',
            '-r', $Runtime,
            '--self-contained=false',
            '-p:PublishSingleFile=true',
            '-p:DebugType=none',
            '-p:GenerateDocumentationFile=false',
            '-o', $payloadDir
        )

        $payloadExe = Join-Path $payloadDir 'SeriTerm.exe'
        if (-not (Test-Path $payloadExe)) { throw "程序体发布失败：没有 $payloadExe" }

        # 2) 压成 Brotli（1.7 MB → 约 0.6 MB），启动器按同样的算法解压
        $payloadBr = Join-Path $temp 'SeriTerm.payload.br'

        Compress-Brotli -Source $payloadExe -Destination $payloadBr

        # 3) 把压缩后的程序体嵌进启动器，用 NativeAOT 编成原生 exe（不需要任何 .NET 运行时）
        Invoke-Publish -Project $launcherProject -Arguments @(
            '-c', 'Release',
            '-r', $Runtime,
            "-p:SeriTermPayload=$payloadBr",
            '-p:DebugType=none',
            '-p:GenerateDocumentationFile=false',
            '-o', $launcherDir
        )

        $launcherExe = Join-Path $launcherDir 'SeriTerm.exe'
        if (-not (Test-Path $launcherExe)) { throw "启动器发布失败：没有 $launcherExe" }

        $version = Get-ArtifactVersion -ExePath $launcherExe
        $named = Join-Path $OutputDir ("SeriTerm-{0}-{1}.exe" -f $version, $Runtime)

        Move-Item -LiteralPath $launcherExe -Destination $named -Force

        $payloadSize = (Get-Item $payloadExe).Length
        $payloadBrSize = (Get-Item $payloadBr).Length
        $ratio = 100.0 * $payloadBrSize / $payloadSize

        Write-Output ("== 程序体：{0:N0} 字节，Brotli 后 {1:N0} 字节（{2:N1}%）" -f $payloadSize, $payloadBrSize, $ratio)
        Write-Output "== 启动器 → $named"

        return $named
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($wantBootstrapper) {
    New-Bootstrapper | Out-Null
}

if ($wantSelfContained) {
    Publish-Artifact -Label '自包含完整版' -Template 'SeriTerm-{0}-{1}-full.exe' -Arguments @(
        '-c', 'Release',
        '-r', $Runtime,
        '--self-contained=true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=none',
        '-p:GenerateDocumentationFile=false'
    ) | Out-Null
}

if ($wantFrameworkDependent) {
    Publish-Artifact -Label '框架依赖单文件' -Template 'SeriTerm-{0}-{1}-fd.exe' -Arguments @(
        '-c', 'Release',
        '-r', $Runtime,
        '--self-contained=false',
        '-p:PublishSingleFile=true',
        '-p:DebugType=none',
        '-p:GenerateDocumentationFile=false'
    ) | Out-Null
}

Write-Output ''
Write-Output '== 产物清单：'

Get-ChildItem $OutputDir | ForEach-Object {
    Write-Output ("   {0}  {1:N1} MB（{2:N0} 字节）" -f $_.Name, ($_.Length / 1MB), $_.Length)
}

Write-Output ''
Write-Output '== 文件名里带版本号（读自 exe 自身的版本资源），所以“程序里显示的版本”与“文件名”不会对不上。'
