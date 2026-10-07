# 把 frames/ 里的 JPEG 序列编码成 mp4（1080p 主片 + 720p 分享版），并抽一张封面图。
$ErrorActionPreference = 'Stop'
$ff = Join-Path $PSScriptRoot 'node_modules\@ffmpeg-installer\win32-x64\ffmpeg.exe'
if (-not (Test-Path $ff)) { throw "找不到 ffmpeg: $ff" }

$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$frames = Join-Path $PSScriptRoot 'frames\f%05d.jpg'

$main = Join-Path $out 'SeriTerm-intro-1080p.mp4'
Write-Host '== 1080p =='
& $ff -y -hide_banner -loglevel warning -framerate 30 -i $frames `
  -c:v libx264 -preset slow -crf 20 -pix_fmt yuv420p -movflags +faststart $main

$small = Join-Path $out 'SeriTerm-intro-720p.mp4'
Write-Host '== 720p =='
& $ff -y -hide_banner -loglevel warning -framerate 30 -i $frames `
  -vf "scale=1280:720:flags=lanczos" -c:v libx264 -preset slow -crf 22 -pix_fmt yuv420p `
  -movflags +faststart $small

Write-Host '== 封面 =='
& $ff -y -hide_banner -loglevel warning -i $main -ss 2.2 -frames:v 1 (Join-Path $out 'poster.png')

Get-ChildItem $out | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize
Write-Host '完成'
