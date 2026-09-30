using System.Runtime.InteropServices;

namespace SeriTerm.App.Services;

/// <summary>
/// 窗口级输入法控制。
///
/// 为什么需要它：终端模式下按键必须原样进串口，但装了中文输入法时，
/// 按键会先被系统输入法截获做组合（WPF 只报 <c>Key.ImeProcessed</c>），
/// 回车/退格/Ctrl+C 甚至会被输入法直接吃掉——终端完全收不到。
/// 只设 <c>InputMethod.SetIsInputMethodEnabled</c> 不够（那是 WPF 层面的），
/// 必须把窗口的输入法上下文摘掉（<c>ImmAssociateContext(hwnd, NULL)</c>）。
/// </summary>
internal static class ImeControl
{
    [DllImport("imm32.dll", SetLastError = true)]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

    /// <summary>摘掉输入法上下文，返回原来的上下文（用于恢复）。</summary>
    public static IntPtr Detach(IntPtr hwnd) => ImmAssociateContext(hwnd, IntPtr.Zero);

    /// <summary>恢复之前摘掉的输入法上下文。</summary>
    public static void Restore(IntPtr hwnd, IntPtr hImc)
    {
        if (hwnd != IntPtr.Zero)
        {
            ImmAssociateContext(hwnd, hImc);
        }
    }
}
