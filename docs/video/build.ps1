# frames/ 里的 JPEG 序列 → 带背景音的 mp4（1080p 主片 + 720p 分享版），并抽一张封面图。
# 背景音由 music.mjs 现场合成，不依赖任何素材文件。
$ErrorActionPreference = 'Stop'
$ff = Join-Path $PSScriptRoot 'node_modules\@ffmpeg-installer\win32-x64\ffmpeg.exe'
if (-not (Test-Path $ff)) { throw "找不到 ffmpeg: $ff" }

$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$frames = Join-Path $PSScriptRoot 'frames\f%05d.jpg'
$theme = Join-Path $out 'theme.wav'

Write-Host '== 背景音 =='
& node (Join-Path $PSScriptRoot 'music.mjs') $theme

function Encode([string]$name, [string]$scale, [string]$crf) {
  $target = Join-Path $out $name
  $silent = Join-Path $env:TEMP ('seriterm-silent-' + $name)
  $vf = @()
  if ($scale) { $vf = @('-vf', "scale=$scale`:flags=lanczos") }
  & $ff -y -hide_banner -loglevel warning -framerate 30 -i $frames @vf `
    -c:v libx264 -preset slow -crf $crf -pix_fmt yuv420p $silent
  & $ff -y -hide_banner -loglevel warning -i $silent -i $theme -map 0:v -map 1:a `
    -c:v copy -c:a aac -b:a 192k -shortest -movflags +faststart $target
  Remove-Item $silent -Force
}

Write-Host '== 1080p =='
Encode 'SeriTerm-intro-1080p.mp4' '' 20
Write-Host '== 720p =='
Encode 'SeriTerm-intro-720p.mp4' '1280:720' 22
Write-Host '== 封面 =='
& $ff -y -hide_banner -loglevel warning -i (Join-Path $out 'SeriTerm-intro-1080p.mp4') -ss 2.2 -frames:v 1 (Join-Path $out 'poster.png')

Get-ChildItem $out | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize
Write-Host '完成'
