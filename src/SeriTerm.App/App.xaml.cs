using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SeriTerm.App.Services;
using SeriTerm.App.ViewModels;
using SeriTerm.Core.Serial;

namespace SeriTerm.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // 先应用上次的主题，再创建窗口，避免启动瞬间闪一下另一种配色
        var settingsStore = _services.GetRequiredService<ISettingsStore>();
        _services.GetRequiredService<IThemeService>().Apply(settingsStore.Load().Theme);

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    private static void ConfigureServices(ServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // 服务
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IUserNotifier, MessageBoxNotifier>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<PortFriendlyNameProvider>();

        // 传输层：将来接 TCP/UDP 时，这里换成对应实现即可
        services.AddSingleton<ISerialTransport, SerialPortTransport>();

        // 界面
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_services is not null)
            {
                var viewModel = _services.GetRequiredService<MainViewModel>();
                viewModel.PersistSettings();
                viewModel.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception)
        {
            // 退出路径上的异常不再弹出，保证进程能正常结束
        }

        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.GetService<IUserNotifier>()?.ShowError(
            "发生未处理的错误",
            $"{e.Exception.GetType().Name}: {e.Exception.Message}");
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            MessageBox.Show(
                $"{exception.GetType().Name}: {exception.Message}",
                "SeriTerm 发生严重错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
