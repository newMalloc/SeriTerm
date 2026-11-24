using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SeriTerm.App.Services;

namespace SeriTerm.App;

public partial class MainWindow
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private IInputElement? _focusBeforeSettings;
    private bool _choosingBackgroundImage;
    private bool _restoreSettingsFocus = true;
    private string? _loadedWallpaperPath;
    private ImageSource? _customWallpaper;

    private bool SettingsOpen => SettingsButton.IsChecked == true;

    private bool HandleSettingsKey(Key key, KeyEventArgs e, bool control, bool shift)
    {
        if (control && !shift && key == Key.OemComma)
        {
            if (!e.IsRepeat)
                SettingsButton.IsChecked = !SettingsOpen;
            e.Handled = true;
            return true;
        }

        if (!SettingsOpen)
            return false;

        if (key == Key.Escape && !SettingsPanel.HasOpenDropDown)
        {
            CloseSettings();
            e.Handled = true;
        }
        else if (key == Key.F1 && !e.IsRepeat)
        {
            ShowAbout();
            e.Handled = true;
        }

        return true;
    }

    private void OnSettingsToggled(object sender, RoutedEventArgs e)
    {
        if (SettingsOpen)
        {
            _focusBeforeSettings = Keyboard.FocusedElement;
            SettingsOverlay.Visibility = Visibility.Visible;
            SettingsPanel.Open();
            return;
        }

        SettingsOverlay.Visibility = Visibility.Collapsed;
        _viewModel.Settings.WindowTopmost = Topmost;
        _viewModel.PersistSettings();
        if (_restoreSettingsFocus && IsActive && !_closing)
        {
            if (_focusBeforeSettings is UIElement { IsVisible: true, IsEnabled: true } previous)
                previous.Focus();
            else
                SettingsButton.Focus();
        }
        _focusBeforeSettings = null;
    }

    private void CloseSettings(bool restoreFocus = true)
    {
        if (!SettingsOpen)
            return;

        _restoreSettingsFocus = restoreFocus;
        try
        {
            SettingsButton.IsChecked = false;
        }
        finally
        {
            _restoreSettingsFocus = true;
        }
    }

    private void OnSettingsCloseRequested(object sender, RoutedEventArgs e) => CloseSettings();

    private void OnSettingsOutsideClick(object sender, MouseButtonEventArgs e)
    {
        CloseSettings();
        e.Handled = true;
    }

    private void OnChooseSettingsImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 SeriTerm 背景图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
        };

        _choosingBackgroundImage = true;
        try
        {
            if (dialog.ShowDialog(this) != true)
                return;

            var image = LoadCustomWallpaper(dialog.FileName);
            if (image is null)
            {
                MessageBox.Show(this, "无法读取此图片，请选择有效的 PNG、JPG 或 BMP 文件。", "无法加载背景图片",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _loadedWallpaperPath = dialog.FileName;
            _customWallpaper = image;
            _viewModel.BackgroundImageLabel = Path.GetFileName(dialog.FileName);
            if (_viewModel.BackgroundImagePath == dialog.FileName)
                ApplyBackdrop();
            else
                _viewModel.BackgroundImagePath = dialog.FileName;
        }
        finally
        {
            _choosingBackgroundImage = false;
        }
    }

    private ImageSource? GetCustomWallpaper()
    {
        var path = _viewModel.BackgroundImagePath;
        if (string.Equals(_loadedWallpaperPath, path, StringComparison.OrdinalIgnoreCase))
            return _customWallpaper;

        _loadedWallpaperPath = path;
        _customWallpaper = string.IsNullOrWhiteSpace(path) ? null : LoadCustomWallpaper(path);
        _viewModel.BackgroundImageLabel = string.IsNullOrWhiteSpace(path) ? "未选择图片"
            : _customWallpaper is null ? "图片无法读取，请重新选择" : Path.GetFileName(path);
        return _customWallpaper;
    }

    private static ImageSource? LoadCustomWallpaper(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            const int maxDimension = 1920;
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (width >= height && width > maxDimension) image.DecodePixelWidth = maxDimension;
            else if (height > maxDimension) image.DecodePixelHeight = maxDimension;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            NotSupportedException or FormatException or ArgumentException or COMException)
        {
            return null;
        }
    }

    private static void SetKeepDisplayAwake(bool enabled)
        => SetThreadExecutionState(EsContinuous | (enabled ? EsDisplayRequired | EsSystemRequired : 0));

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint executionState);
}
