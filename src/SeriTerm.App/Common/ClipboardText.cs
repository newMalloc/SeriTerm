using System.Runtime.InteropServices;
using System.Windows;

namespace SeriTerm.App.Common;

/// <summary>
/// 往剪贴板写文本。剪贴板是全局资源，被别的进程占用时会抛
/// <see cref="COMException"/>（CLIPBRD_E_CANT_OPEN），所以统一在这里重试几次、
/// 失败也只是返回 false 让调用方去提示，而不是把异常丢给用户。
/// </summary>
public static class ClipboardText
{
    public static bool TrySet(string text)
    {
        // 剪贴板被别的进程占用时会抛 COMException（CLIPBRD_E_CANT_OPEN），重试几次再认输
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(60);
            }
        }

        return false;
    }
}
