using System.Windows;
using System.Windows.Controls;

namespace SeriTerm.App.Services;

/// <summary>
/// 简易文本输入对话框。
/// WPF 没有内置 InputBox，这里用代码搭一个，省掉一个 XAML 文件；
/// 控件会命中 App 资源里的隐式样式，因此自动跟随深/浅主题。
/// </summary>
internal static class TextPrompt
{
    public static string? Show(Window? owner, string title, string prompt, string defaultValue)
    {
        var window = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
        };

        window.SetResourceReference(Control.BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(Control.ForegroundProperty, "ForegroundBrush");

        var label = new TextBlock
        {
            Text = prompt,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };

        var textBox = new TextBox
        {
            Text = defaultValue,
            MinWidth = 320,
        };

        var okButton = new Button
        {
            Content = "确定",
            MinWidth = 76,
            IsDefault = true,
        };

        var cancelButton = new Button
        {
            Content = "取消",
            MinWidth = 76,
            IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };

        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(label);
        panel.Children.Add(textBox);
        panel.Children.Add(buttons);

        window.Content = panel;
        window.Owner = owner;

        string? result = null;

        okButton.Click += (_, _) =>
        {
            result = textBox.Text?.Trim();
            window.DialogResult = true;
        };

        window.Loaded += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(result) ? result : null;
    }
}
