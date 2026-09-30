# 生成应用图标（橙色圆角方块 + 双向数据箭头），产出多尺寸 ICO。
# 之所以用脚本生成而不是塞一个二进制文件进仓库：图标可复现、可微调，评审时看得见画的是什么。
#
# 用法：pwsh -File tools/make-icon.ps1 [-OutputPath src/SeriTerm.App/Assets/seriterm.ico]

param(
    [string]$OutputPath = 'src/SeriTerm.App/Assets/seriterm.ico'
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()

function New-IconBitmap {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $scale = $Size / 256.0

    # 圆角底：深蓝到亮蓝的竖向渐变
    $radius = [Math]::Max(2, [int](44 * $scale))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $radius * 2
    $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $path.AddArc($Size - $diameter, 0, $diameter, $diameter, 270, 90)
    $path.AddArc($Size - $diameter, $Size - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc(0, $Size - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point(0, $Size)),
        [System.Drawing.Color]::FromArgb(255, 14, 122, 212),
        [System.Drawing.Color]::FromArgb(255, 10, 74, 138))
    $graphics.FillPath($brush, $path)

    # 双向箭头（TX / RX）
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [float][Math]::Max(1.4, 16 * $scale))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $left = 62 * $scale
    $right = 194 * $scale
    $upper = 100 * $scale
    $lower = 156 * $scale

    $graphics.DrawLine($pen, $left, $upper, $right, $upper)
    $graphics.DrawLine($pen, $left, $lower, $right, $lower)

    # 箭头头部
    $head = 26 * $scale
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

    $graphics.FillPolygon($white, @(
            (New-Object System.Drawing.PointF([float]($right + $head * 0.2), [float]$upper)),
            (New-Object System.Drawing.PointF([float]($right - $head), [float]($upper - $head * 0.75))),
            (New-Object System.Drawing.PointF([float]($right - $head), [float]($upper + $head * 0.75)))
        ))

    $graphics.FillPolygon($white, @(
            (New-Object System.Drawing.PointF([float]($left - $head * 0.2), [float]$lower)),
            (New-Object System.Drawing.PointF([float]($left + $head), [float]($lower - $head * 0.75))),
            (New-Object System.Drawing.PointF([float]($left + $head), [float]($lower + $head * 0.75)))
        ))

    $pen.Dispose()
    $brush.Dispose()
    $white.Dispose()
    $path.Dispose()
    $graphics.Dispose()

    return $bitmap
}

foreach ($size in $sizes) {
    $bitmap = New-IconBitmap -Size $size
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    $images += , @{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
}

# 组装 ICO：ICONDIR + 每个图像的目录项 + PNG 数据
$output = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($output)

$writer.Write([uint16]0)                 # reserved
$writer.Write([uint16]1)                 # type = icon
$writer.Write([uint16]$images.Count)     # image count

$offset = 6 + (16 * $images.Count)

foreach ($image in $images) {
    $size = [int]$image.Size
    $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $writer.Write([byte]0)               # 调色板数
    $writer.Write([byte]0)               # reserved
    $writer.Write([uint16]1)             # planes
    $writer.Write([uint16]32)            # bpp
    $writer.Write([uint32]$image.Bytes.Length)
    $writer.Write([uint32]$offset)

    $offset += $image.Bytes.Length
}

foreach ($image in $images) {
    $writer.Write($image.Bytes)
}

$writer.Flush()

$directory = Split-Path -Parent $OutputPath
if ($directory -and -not (Test-Path $directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

[System.IO.File]::WriteAllBytes((Resolve-Path -LiteralPath '.').Path + '/' + $OutputPath, $output.ToArray())

$writer.Dispose()
$output.Dispose()

$info = Get-Item $OutputPath
Write-Output "已生成图标：$($info.FullName)（$($info.Length) 字节，含 $($sizes -join '/') 尺寸）"

# 另外存一张 PNG 预览，方便在仓库/评审里直接看图标长什么样
$previewPath = 'artifacts/icon-preview.png'
$previewDirectory = Split-Path -Parent $previewPath
if ($previewDirectory -and -not (Test-Path $previewDirectory)) {
    New-Item -ItemType Directory -Path $previewDirectory -Force | Out-Null
}

$preview = New-IconBitmap -Size 256
$preview.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

Write-Output "已生成预览：$previewPath"
