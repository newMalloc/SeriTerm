# 构建压缩的框架依赖版到项目根目录的 SeriTerm.exe。
# 更新前关闭由该路径启动的 SeriTerm 进程。
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/SeriTerm.App/SeriTerm.App.csproj'
$launcherProject = Join-Path $root 'src/SeriTerm.Launcher/SeriTerm.Launcher.csproj'
$destination = Join-Path $root 'SeriTerm.exe'
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$buildTemp = Join-Path $tempRoot ('seriterm-local-' + [Guid]::NewGuid().ToString('N'))
$payloadDir = Join-Path $buildTemp 'payload'
$launcherDir = Join-Path $buildTemp 'launcher'
$compressed = Join-Path $buildTemp 'SeriTerm.payload.br'

New-Item -ItemType Directory -Path $payloadDir, $launcherDir -Force | Out-Null
try {
    $arguments = @(
        'publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=none', '-o', $payloadDir, '--nologo'
    )
    if ($Version) { $arguments += "-p:Version=$Version" }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "程序体编译失败，退出码 $LASTEXITCODE" }

    $inputStream = [System.IO.File]::OpenRead((Join-Path $payloadDir 'SeriTerm.exe'))
    try {
        $outputStream = [System.IO.File]::Create($compressed)
        try {
            $brotli = [System.IO.Compression.BrotliStream]::new($outputStream, [System.IO.Compression.CompressionLevel]::Optimal, $true)
            try { $inputStream.CopyTo($brotli) } finally { $brotli.Dispose() }
        } finally { $outputStream.Dispose() }
    } finally { $inputStream.Dispose() }

    $launcherArguments = @(
        'publish', $launcherProject, '-c', 'Release', '-r', 'win-x64',
        "-p:SeriTermPayload=$compressed", '-p:DebugType=none', '-o', $launcherDir, '--nologo'
    )
    if ($Version) { $launcherArguments += "-p:Version=$Version" }
    & dotnet @launcherArguments
    if ($LASTEXITCODE -ne 0) { throw "启动器编译失败，退出码 $LASTEXITCODE" }

    $runningInstances = @(Get-CimInstance Win32_Process -Filter "Name = 'SeriTerm.exe'" | Where-Object {
        ($_.ExecutablePath -and $_.ExecutablePath -ieq $destination) -or
        ($_.CommandLine -and $_.CommandLine.Contains('--seriterm-launcher-path') -and
            $_.CommandLine.Contains($destination, [StringComparison]::OrdinalIgnoreCase))
    })
    foreach ($instance in $runningInstances) {
        Stop-Process -Id $instance.ProcessId -Force -ErrorAction Stop
        Wait-Process -Id $instance.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
    }
    Copy-Item -LiteralPath (Join-Path $launcherDir 'SeriTerm.exe') -Destination $destination -Force
    Write-Output "已生成：$destination"
} finally {
    $resolvedTemp = [System.IO.Path]::GetFullPath($buildTemp)
    if ($resolvedTemp.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemp) -match '^seriterm-local-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
