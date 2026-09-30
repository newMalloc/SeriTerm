# 启动 SeriTerm 并截取主窗口，用于开发期的界面回归检查。
# 用法：pwsh -File tools/capture-window.ps1 -ExePath <exe> -OutFile <png> [-WaitSeconds 4]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [int]$WaitSeconds = 4
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) { throw "找不到可执行文件：$ExePath" }

if (-not ('WinCapture' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    // 截图进程必须自己是 DPI 感知的，否则 GetWindowRect 返回的是被系统缩放的逻辑坐标，
    // 截出来的图会"被放大且右侧/底部被裁掉"。
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int awareness);
}
'@
}

[void][WinCapture]::SetProcessDPIAware()
try { [void][WinCapture]::SetProcessDpiAwareness(2) } catch { }

Add-Type -AssemblyName System.Drawing

$process = Start-Process -FilePath $ExePath -PassThru
try {
    Start-Sleep -Seconds $WaitSeconds
    $process.Refresh()

    $handle = $process.MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw '主窗口尚未创建，可能需要增加 -WaitSeconds' }

    [void][WinCapture]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 400

    $rect = New-Object WinCapture+RECT
    [void][WinCapture]::GetWindowRect($handle, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { throw '窗口尺寸无效' }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        # flags = 2 (PW_RENDERFULLCONTENT)：WPF 需要它才能截到内容
        [void][WinCapture]::PrintWindow($handle, $hdc, 2)
    }
    finally {
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()
    }

    $directory = Split-Path -Parent $OutFile
    if ($directory -and -not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }

    $bitmap.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    Write-Output "已保存截图：$OutFile (${width}x${height})"
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
}
