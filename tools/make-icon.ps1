# 从设计稿导出的 PNG 源图生成多尺寸应用图标（ICO），并顺手产出预览图。
#
# 为什么改成"读源图"而不是继续用 GDI+ 手绘：图标的样子应该由设计稿决定，
# 脚本只负责三件可复现的事——取景（裁到主体）、按预乘 alpha 缩放、打包成 ICO。
# 源图 src/SeriTerm.App/Assets/seriterm.png 是带 alpha 通道的 1571x1571 设计稿导出，
# 图形只占画面中间一条（1527x617），所以整幅塞进正方形图标会让主体小到看不清。
#
# 用法：
#   pwsh -File tools/make-icon.ps1                          # 默认：裁到设备主体
#   pwsh -File tools/make-icon.ps1 -CropMode Content        # 用整幅构图（含波形与箭头）
#   pwsh -File tools/make-icon.ps1 -CropMode Full           # 用完整画布（含空白）
#   pwsh -File tools/make-icon.ps1 -CropRect 386,463,911,616
#
# 在 Windows PowerShell 5.1 与 PowerShell 7 下都跑过。本文件必须存成**带 BOM 的 UTF-8**：
# 5.1 读无 BOM 的 UTF-8 会按 ANSI 解，中文全变乱码、连语法解析都过不去。

param(
    [string]$Source = 'src/SeriTerm.App/Assets/seriterm.png',
    [string]$OutputPath = 'src/SeriTerm.App/Assets/seriterm.ico',
    [string]$PreviewDir = 'artifacts',
    [ValidateSet('Subject', 'Content', 'Full')]
    [string]$CropMode = 'Subject',
    [int[]]$CropRect,
    [double]$Padding = 0.04
)

# ICO 里的帧尺寸固定用这七个（Windows 会按场合各取所需：16 标题栏、24/32 任务栏、48+ 资源管理器与快捷方式）
$iconSizes = @(16, 24, 32, 48, 64, 128, 256)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-RepoPath {
    param([string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return (Join-Path $repoRoot $Path)
}

$Source = Resolve-RepoPath $Source
$OutputPath = Resolve-RepoPath $OutputPath
$PreviewDir = Resolve-RepoPath $PreviewDir

# 预先乘 alpha：GDI+ 对 Format32bppArgb 做插值时不会先乘 alpha，
# 透明区域的 RGB（源图里既有 (255,255,255,0) 也有 (0,0,0,0)）会渗到边缘形成毛边。
# 全程在 Format32bppPArgb 上做，最后一步再还原成直通 alpha 写 PNG。
function ConvertTo-Premultiplied {
    param([System.Drawing.Image]$Image)
    $bitmap = New-Object System.Drawing.Bitmap($Image.Width, $Image.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $graphics.DrawImage($Image, (New-Object System.Drawing.Rectangle(0, 0, $Image.Width, $Image.Height)))
    $graphics.Dispose()
    return $bitmap
}

function ConvertTo-Straight {
    param([System.Drawing.Bitmap]$Bitmap)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $Bitmap.Width, $Bitmap.Height)
    $locked = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $stride = $locked.Stride
    $bytes = New-Object byte[] ($stride * $Bitmap.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $bytes, 0, $bytes.Length)
    $Bitmap.UnlockBits($locked)

    for ($i = 0; $i -lt $bytes.Length; $i += 4) {
        $alpha = $bytes[$i + 3]
        if ($alpha -eq 0) {
            $bytes[$i] = 0; $bytes[$i + 1] = 0; $bytes[$i + 2] = 0
        }
        elseif ($alpha -ne 255) {
            for ($c = 0; $c -lt 3; $c++) {
                $value = [int][Math]::Round($bytes[$i + $c] * 255.0 / $alpha)
                $bytes[$i + $c] = [byte][Math]::Min(255, $value)
            }
        }
    }

    $straight = New-Object System.Drawing.Bitmap($Bitmap.Width, $Bitmap.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $dest = $straight.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $destStride = $dest.Stride
    $destBytes = New-Object byte[] ($destStride * $straight.Height)
    for ($y = 0; $y -lt $straight.Height; $y++) {
        [System.Array]::Copy($bytes, $y * $stride, $destBytes, $y * $destStride, $Bitmap.Width * 4)
    }
    [System.Runtime.InteropServices.Marshal]::Copy($destBytes, 0, $dest.Scan0, $destBytes.Length)
    $straight.UnlockBits($dest)
    return $straight
}

function Get-AlphaBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $Bitmap.Width, $Bitmap.Height)
    $locked = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $stride = $locked.Stride
    $bytes = New-Object byte[] ($stride * $Bitmap.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $bytes, 0, $bytes.Length)
    $Bitmap.UnlockBits($locked)
    return @{ Bytes = $bytes; Stride = $stride }
}

# 所有不透明像素的包围盒（整幅构图）
function Get-ContentBounds {
    param([System.Drawing.Bitmap]$Bitmap)
    $data = Get-AlphaBytes -Bitmap $Bitmap
    $bytes = $data.Bytes; $stride = $data.Stride
    $minX = $Bitmap.Width; $minY = $Bitmap.Height; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        $row = $y * $stride
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($bytes[$row + ($x * 4) + 3] -gt 8) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -lt 0) { throw "源图 '$Source' 里找不到不透明像素" }
    return New-Object System.Drawing.Rectangle($minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1))
}

# 主体包围盒：逐个统计每列/每行的不透明像素数，只保留"厚实"的那一片。
# 波形与箭头是约 13px 粗的细线，整列最多十几像素；外壳随便一列都有 380+ 像素，
# 用 0.2 倍图高当门槛能把两者干净地分开（实测 0.13~0.20 倍图高之间结果完全一致）。
function Get-SubjectBounds {
    param([System.Drawing.Bitmap]$Bitmap, [double]$HeightRatio = 0.2)
    $data = Get-AlphaBytes -Bitmap $Bitmap
    $bytes = $data.Bytes; $stride = $data.Stride
    $columnCounts = [int[]]::new($Bitmap.Width)
    $rowCounts = [int[]]::new($Bitmap.Height)
    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        $row = $y * $stride
        $count = 0
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($bytes[$row + ($x * 4) + 3] -gt 8) {
                $columnCounts[$x]++
                $count++
            }
        }
        $rowCounts[$y] = $count
    }

    $threshold = [int][Math]::Round($Bitmap.Height * $HeightRatio)
    $minX = -1; $maxX = -1; $minY = -1; $maxY = -1
    for ($x = 0; $x -lt $Bitmap.Width; $x++) {
        if ($columnCounts[$x] -ge $threshold) {
            if ($minX -lt 0) { $minX = $x }
            $maxX = $x
        }
    }
    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        if ($rowCounts[$y] -ge $threshold) {
            if ($minY -lt 0) { $minY = $y }
            $maxY = $y
        }
    }
    if ($minX -lt 0 -or $minY -lt 0) { return $null }
    return New-Object System.Drawing.Rectangle($minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1))
}

function Crop-Premultiplied {
    param([System.Drawing.Bitmap]$Source, [System.Drawing.Rectangle]$Crop)
    $dest = New-Object System.Drawing.Bitmap($Crop.Width, $Crop.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($dest)
    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $graphics.DrawImage($Source, (New-Object System.Drawing.Rectangle(0, 0, $Crop.Width, $Crop.Height)), $Crop, [System.Drawing.GraphicsUnit]::Pixel)
    $graphics.Dispose()
    return $dest
}

# 大比例缩小时先反复折半：bicubic 只取 4x4 邻域，一步缩到 1/98 会漏掉细节并产生锯齿
function Resize-Premultiplied {
    param([System.Drawing.Bitmap]$Source, [int]$Width, [int]$Height)
    $current = $Source
    while ($current.Width -ge ($Width * 4) -and $current.Height -ge ($Height * 4)) {
        $halfWidth = [Math]::Max($Width, [int]($current.Width / 2))
        $halfHeight = [Math]::Max($Height, [int]($current.Height / 2))
        $next = New-Object System.Drawing.Bitmap($halfWidth, $halfHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($next)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($current, (New-Object System.Drawing.Rectangle(0, 0, $halfWidth, $halfHeight)))
        $graphics.Dispose()
        if ($current -ne $Source) { $current.Dispose() }
        $current = $next
        if ($halfWidth -eq $Width -and $halfHeight -eq $Height) { break }
    }
    if ($current.Width -eq $Width -and $current.Height -eq $Height) { return $current }

    $dest = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($dest)
    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.DrawImage($current, (New-Object System.Drawing.Rectangle(0, 0, $Width, $Height)))
    $graphics.Dispose()
    if ($current -ne $Source) { $current.Dispose() }
    return $dest
}

# 把取景区域按 contain 方式摆进正方形透明画布，四周留 Padding 比例的空边
function New-IconBitmap {
    param([System.Drawing.Bitmap]$Source, [System.Drawing.Rectangle]$Crop, [int]$Size)
    $inner = $Size * (1 - 2 * $Padding)
    $scale = [Math]::Min($inner / $Crop.Width, $inner / $Crop.Height)
    $width = [Math]::Max(1, [int][Math]::Round($Crop.Width * $scale))
    $height = [Math]::Max(1, [int][Math]::Round($Crop.Height * $scale))

    $region = Crop-Premultiplied -Source $Source -Crop $Crop
    $scaled = Resize-Premultiplied -Source $region -Width $width -Height $height
    $region.Dispose()

    $canvas = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($scaled, (New-Object System.Drawing.Rectangle([int](($Size - $width) / 2), [int](($Size - $height) / 2), $width, $height)))
    $graphics.Dispose()
    $scaled.Dispose()
    return $canvas
}

function Save-IconPng {
    param([System.Drawing.Bitmap]$Bitmap, [string]$Path)
    $straight = ConvertTo-Straight -Bitmap $Bitmap
    $straight.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $straight.Dispose()
}

# 预览图：把各尺寸按最近邻放大并排摆开，浅色/深色底各一行，方便一眼看出小尺寸下糊不糊
function New-PreviewSheet {
    param([System.Drawing.Bitmap]$Source, [System.Drawing.Rectangle]$Crop, [string]$Path)
    $sizes = $iconSizes
    $zoom = 2
    $gap = 12
    $width = $gap
    foreach ($size in $sizes) { $width += $size * $zoom + $gap }
    $rowHeight = 256 * $zoom
    # 注意：New-Object 的参数表是"参数模式"，顶层写 a + b 会被当成两个参数，算式必须再包一层括号
    $sheetHeight = (2 * $rowHeight) + (3 * $gap)
    $sheet = New-Object System.Drawing.Bitmap($width, $sheetHeight)
    $graphics = [System.Drawing.Graphics]::FromImage($sheet)
    $graphics.Clear([System.Drawing.Color]::FromArgb(243, 243, 243))
    $darkTop = $rowHeight + (2 * $gap)
    $darkHeight = $rowHeight + $gap
    $darkBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(32, 32, 32))
    $graphics.FillRectangle($darkBrush, 0, $darkTop, $width, $darkHeight)
    $darkBrush.Dispose()
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half

    for ($row = 0; $row -lt 2; $row++) {
        $x = $gap
        foreach ($size in $sizes) {
            $icon = New-IconBitmap -Source $Source -Crop $Crop -Size $size
            $drawSize = $size * $zoom
            $drawY = $gap + ($row * ($rowHeight + $gap)) + ($rowHeight - $drawSize)
            $graphics.DrawImage($icon, (New-Object System.Drawing.Rectangle($x, $drawY, $drawSize, $drawSize)))
            $icon.Dispose()
            $x += $drawSize + $gap
        }
    }
    $graphics.Dispose()
    $sheet.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
}

if (-not (Test-Path -LiteralPath $Source)) { throw "找不到图标源图：$Source" }

$sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
$image = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source).Path)
$premultiplied = ConvertTo-Premultiplied -Image $image
$image.Dispose()

$fullBounds = New-Object System.Drawing.Rectangle(0, 0, $premultiplied.Width, $premultiplied.Height)
$contentBounds = Get-ContentBounds -Bitmap $premultiplied
$subjectBounds = Get-SubjectBounds -Bitmap $premultiplied

switch ($CropMode) {
    'Full' { $crop = $fullBounds }
    'Content' { $crop = $contentBounds }
    'Subject' {
        # 主体判定得离谱时（比如源图压根没有"厚实"的大块）退回整幅构图，避免裁出个碎片
        if ($null -eq $subjectBounds -or
            $subjectBounds.Width -lt ($premultiplied.Width * 0.25) -or
            $subjectBounds.Height -lt ($premultiplied.Height * 0.25)) {
            Write-Warning "按 0.2 倍图高判定不出主体，退回整幅构图（Content）"
            $crop = $contentBounds
        }
        else {
            $crop = $subjectBounds
        }
    }
}

if ($CropRect -and $CropRect.Count -eq 4) {
    $crop = New-Object System.Drawing.Rectangle($CropRect[0], $CropRect[1], $CropRect[2], $CropRect[3])
}

Write-Output "源图：$Source"
Write-Output ("      尺寸 {0}x{1}，SHA256 {2}" -f $premultiplied.Width, $premultiplied.Height, $sourceHash)
Write-Output ("      整幅包围盒 {0}；主体包围盒 {1}" -f $contentBounds, $subjectBounds)
Write-Output ("取景：{0} -> {1}（{2}x{3}）" -f $CropMode, $crop, $crop.Width, $crop.Height)

if (-not (Test-Path -LiteralPath $PreviewDir)) {
    New-Item -ItemType Directory -Path $PreviewDir -Force | Out-Null
}
$previewPath = Join-Path $PreviewDir 'icon-preview.png'
$previewSheetPath = Join-Path $PreviewDir 'icon-preview-sizes.png'
$preview = New-IconBitmap -Source $premultiplied -Crop $crop -Size 256
Save-IconPng -Bitmap $preview -Path $previewPath
$preview.Dispose()
New-PreviewSheet -Source $premultiplied -Crop $crop -Path $previewSheetPath

$sizeList = $iconSizes
$images = @()
foreach ($size in $sizeList) {
    $bitmap = New-IconBitmap -Source $premultiplied -Crop $crop -Size $size
    $straight = ConvertTo-Straight -Bitmap $bitmap
    $bitmap.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $straight.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $straight.Dispose()

    $images += , @{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
}

$premultiplied.Dispose()

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
foreach ($image in $images) { $writer.Write($image.Bytes) }
$writer.Flush()

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
[System.IO.File]::WriteAllBytes($OutputPath, $output.ToArray())
$writer.Dispose()
$output.Dispose()

$info = Get-Item -LiteralPath $OutputPath
Write-Output ("已生成图标：{0}（{1} 字节，含 {2} 尺寸）" -f $info.FullName, $info.Length, ($sizeList -join '/'))
Write-Output ("      SHA256 {0}" -f (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash)
Write-Output "已生成预览：$previewPath、$previewSheetPath"
