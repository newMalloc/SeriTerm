#Requires -Version 7.4
# 把逐帧截图和合成背景音编码为 1080p MP4。
param(
    [string]$FrameDirectory = (Join-Path $PSScriptRoot 'frames'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'out'),
    [string]$FfmpegPath
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if (-not $FfmpegPath) {
    Push-Location $PSScriptRoot
    try {
        $FfmpegPath = & node -p "require('@ffmpeg-installer/ffmpeg').path"
    } finally {
        Pop-Location
    }
}

$frames = Join-Path $FrameDirectory 'f%05d.jpg'
$first = Join-Path $FrameDirectory 'f00000.jpg'
if (-not (Test-Path -LiteralPath $first)) { throw "找不到首帧 $first" }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$video = Join-Path $OutputDirectory 'SeriTerm-intro-1080p.mp4'
$poster = Join-Path $OutputDirectory 'poster.png'

& node (Join-Path $PSScriptRoot 'music.mjs') --stdout |
    & $FfmpegPath -y -hide_banner -loglevel error -framerate 30 -i $frames -f wav -i pipe:0 `
    -map 0:v -map 1:a -c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p `
    -c:a aac -b:a 128k -shortest -movflags +faststart $video
& $FfmpegPath -y -hide_banner -loglevel error -ss 2.2 -i $video -frames:v 1 $poster

Get-Item -LiteralPath $video, $poster | Select-Object Name, Length
