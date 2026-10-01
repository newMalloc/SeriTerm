using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SeriTerm.App.Services;

/// <summary>
/// 自绘的"模糊桌面背景"。
///
/// 为什么不用 DWM 的亚克力（<c>SetWindowCompositionAttribute</c>）：
/// 本机（Windows 10 19045，远程/虚拟显示会话）实测该 API 一律返回成功、但对窗口毫无效果——
/// AccentState 1/2/3/4/5/6、AccentFlags 0/2、深浅两种底色共九种组合，外加
/// <c>DwmExtendFrameIntoClientArea(-1)</c> 把玻璃区扩到整个客户区，像素一个都没变；
/// 而同一时刻系统任务栏的亚克力是正常工作的，说明 DWM 支持模糊，只是拒绝为第三方窗口做这套旧接口。
///
/// 所以改成自己模糊壁纸：拿桌面壁纸做一次降采样 + 多次盒式模糊，得到一张很小的图，
/// 再由 GPU 拉伸铺满窗口。效果与亚克力等价（最大化时窗口背后本来就是这块壁纸），
/// 而且不依赖 DWM、任何会话都能用，也不需要每帧跑模糊。
/// </summary>
internal static class DesktopBackdrop
{
    /// <summary>模糊后的图宽（像素）。很低频就够了：越接近"色块"越像亚克力。</summary>
    private const int TargetWidth = 320;

    /// <summary>盒式模糊重复次数，用来逼近高斯模糊。</summary>
    private const int BlurPasses = 3;

    /// <summary>壁纸能不能拿到（只查文件和注册表，不解码图像）。</summary>
    public static bool IsAvailable => ResolveWallpaperPath() is not null;

    /// <summary>生成模糊壁纸。任何一步失败都返回 null，调用方应保持不透明背景。</summary>
    public static ImageSource? Create()
    {
        try
        {
            var path = ResolveWallpaperPath();
            if (path is null)
            {
                return null;
            }

            var pixels = Load(path);
            if (pixels is null || pixels.Width < 2 || pixels.Height < 2)
            {
                return null;
            }

            var small = Downsample(pixels, TargetWidth);
            Blur(small, Math.Max(2, small.Width / 14), BlurPasses);

            var bitmap = BitmapSource.Create(
                small.Width, small.Height, 96, 96, PixelFormats.Bgra32, null, small.Data, small.Stride);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // 壁纸文件被删/格式怪异/权限不足：当作没有背景，不要让窗口启动失败
            return null;
        }
    }

    /// <summary>
    /// 取壁纸文件路径。优先注册表里用户设置的壁纸；
    /// 取不到时退回 Windows 自己缓存的转码壁纸（幻灯片、Windows 聚焦都靠它）。
    /// </summary>
    private static string? ResolveWallpaperPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key?.GetValue("WallPaper") is string configured
                && !string.IsNullOrWhiteSpace(configured)
                && File.Exists(configured))
            {
                return configured;
            }
        }
        catch (Exception)
        {
            // 注册表读不到就继续尝试转码文件
        }

        var transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Windows",
            "Themes",
            "TranscodedWallpaper");

        return File.Exists(transcoded) ? transcoded : null;
    }

    private static Pixels? Load(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            return null;
        }

        // 统一成 Bgra32：后面按字节做降采样和模糊，必须先确定通道顺序
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var data = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(data, stride, 0);

        return new Pixels(data, converted.PixelWidth, converted.PixelHeight, stride);
    }

    /// <summary>盒式平均降采样，避免直接丢像素产生噪点。</summary>
    private static Pixels Downsample(Pixels source, int targetWidth)
    {
        var width = Math.Min(targetWidth, source.Width);
        var height = Math.Max(1, (int)Math.Round(source.Height * (double)width / source.Width));
        var stride = width * 4;
        var data = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            var y0 = y * source.Height / height;
            var y1 = Math.Max(y0 + 1, (y + 1) * source.Height / height);

            for (var x = 0; x < width; x++)
            {
                var x0 = x * source.Width / width;
                var x1 = Math.Max(x0 + 1, (x + 1) * source.Width / width);

                int b = 0, g = 0, r = 0, a = 0, count = 0;

                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * source.Stride;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var p = row + (sx * 4);
                        b += source.Data[p];
                        g += source.Data[p + 1];
                        r += source.Data[p + 2];
                        a += source.Data[p + 3];
                        count++;
                    }
                }

                var q = (y * stride) + (x * 4);
                data[q] = (byte)(b / count);
                data[q + 1] = (byte)(g / count);
                data[q + 2] = (byte)(r / count);
                data[q + 3] = (byte)(a / count);
            }
        }

        return new Pixels(data, width, height, stride);
    }

    /// <summary>可分离盒式模糊（先横后纵，各用滑动窗口累加）。</summary>
    private static void Blur(Pixels image, int radius, int passes)
    {
        if (radius < 1)
        {
            return;
        }

        var scratch = new byte[image.Data.Length];

        for (var pass = 0; pass < passes; pass++)
        {
            BlurHorizontal(image.Data, scratch, image, radius);
            BlurVertical(scratch, image.Data, image, radius);
        }
    }

    private static void BlurHorizontal(byte[] source, byte[] target, Pixels image, int radius)
    {
        var window = (radius * 2) + 1;

        for (var y = 0; y < image.Height; y++)
        {
            var row = y * image.Stride;
            int b = 0, g = 0, r = 0, a = 0;

            for (var i = -radius; i <= radius; i++)
            {
                var p = row + (Math.Clamp(i, 0, image.Width - 1) * 4);
                b += source[p];
                g += source[p + 1];
                r += source[p + 2];
                a += source[p + 3];
            }

            for (var x = 0; x < image.Width; x++)
            {
                var q = row + (x * 4);
                target[q] = (byte)(b / window);
                target[q + 1] = (byte)(g / window);
                target[q + 2] = (byte)(r / window);
                target[q + 3] = (byte)(a / window);

                var add = row + (Math.Clamp(x + radius + 1, 0, image.Width - 1) * 4);
                var sub = row + (Math.Clamp(x - radius, 0, image.Width - 1) * 4);
                b += source[add] - source[sub];
                g += source[add + 1] - source[sub + 1];
                r += source[add + 2] - source[sub + 2];
                a += source[add + 3] - source[sub + 3];
            }
        }
    }

    private static void BlurVertical(byte[] source, byte[] target, Pixels image, int radius)
    {
        var window = (radius * 2) + 1;

        for (var x = 0; x < image.Width; x++)
        {
            var column = x * 4;
            int b = 0, g = 0, r = 0, a = 0;

            for (var i = -radius; i <= radius; i++)
            {
                var p = (Math.Clamp(i, 0, image.Height - 1) * image.Stride) + column;
                b += source[p];
                g += source[p + 1];
                r += source[p + 2];
                a += source[p + 3];
            }

            for (var y = 0; y < image.Height; y++)
            {
                var q = (y * image.Stride) + column;
                target[q] = (byte)(b / window);
                target[q + 1] = (byte)(g / window);
                target[q + 2] = (byte)(r / window);
                target[q + 3] = (byte)(a / window);

                var add = (Math.Clamp(y + radius + 1, 0, image.Height - 1) * image.Stride) + column;
                var sub = (Math.Clamp(y - radius, 0, image.Height - 1) * image.Stride) + column;
                b += source[add] - source[sub];
                g += source[add + 1] - source[sub + 1];
                r += source[add + 2] - source[sub + 2];
                a += source[add + 3] - source[sub + 3];
            }
        }
    }

    /// <summary>内存里的一张 Bgra32 位图。</summary>
    private sealed record Pixels(byte[] Data, int Width, int Height, int Stride);
}
