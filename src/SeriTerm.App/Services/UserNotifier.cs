using System.Windows;

namespace SeriTerm.App.Services;

/// <summary>对用户提示的抽象，便于将来换成非阻塞提示条。</summary>
public interface IUserNotifier
{
    void ShowError(string title, string message);

    void ShowInfo(string title, string message);

    /// <summary>弹出文本输入框；用户取消或输入为空时返回 null。</summary>
    string? AskText(string title, string prompt, string defaultValue);
}

public sealed class MessageBoxNotifier : IUserNotifier
{
    public string? AskText(string title, string prompt, string defaultValue)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return dispatcher.Invoke(() => AskText(title, prompt, defaultValue));
        }

        return TextPrompt.Show(Application.Current?.MainWindow, title, prompt, defaultValue);
    }
    public void ShowError(string title, string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ShowError(title, message));
            return;
        }

        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public void ShowInfo(string title, string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ShowInfo(title, message));
            return;
        }

        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
