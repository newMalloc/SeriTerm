using System.Windows;

namespace SeriTerm.App.Services;

/// <summary>对用户提示的抽象，便于将来换成非阻塞提示条。</summary>
public interface IUserNotifier
{
    void ShowError(string title, string message);

    void ShowInfo(string title, string message);
}

public sealed class MessageBoxNotifier : IUserNotifier
{
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
