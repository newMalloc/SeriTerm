using Microsoft.Win32;

namespace SeriTerm.App.Services;

public interface IFileDialogService
{
    /// <summary>弹出"另存为"对话框；用户取消时返回 null。</summary>
    string? AskSaveFile(string defaultFileName, string filter, string title);

    /// <summary>弹出"打开文件"对话框；用户取消时返回 null。</summary>
    string? AskOpenFile(string filter, string title);
}

public sealed class FileDialogService : IFileDialogService
{
    public string? AskSaveFile(string defaultFileName, string filter, string title)
    {
        var dialog = new SaveFileDialog
        {
            FileName = defaultFileName,
            Filter = filter,
            Title = title,
            OverwritePrompt = true,
            AddExtension = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? AskOpenFile(string filter, string title)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter,
            Title = title,
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
