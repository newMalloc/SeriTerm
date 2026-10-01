using System.IO;
using System.Text;
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

    /// <summary>
    /// 启动时就解析好错误提示器。退出过程中 <see cref="_services"/> 已经释放，
    /// 那时若在异常处理里再 GetService 会抛 ObjectDisposedException：
    /// 一次本来能被处理的界面异常会因此变成"未处理异常 → 崩溃弹窗"。
    /// </summary>
    private IUserNotifier? _notifier;

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
        _notifier = _services.GetRequiredService<IUserNotifier>();

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
            _services?.GetService<MainViewModel>()?.PersistSettings();
        }
        catch (Exception)
        {
            // 退出路径上的异常不再弹出，保证进程能正常结束
        }

        try
        {
            // 必须用 DisposeAsync：容器里的 MainViewModel 只实现了 IAsyncDisposable，
            // 同步 Dispose() 会抛 InvalidOperationException("Use DisposeAsync to dispose the container")，
            // 而这个异常以前会一路冒到未处理异常处理里，变成"每次关窗都弹崩溃框、进程还退不掉"。
            // 这里同时承担了 MainViewModel 的释放（它会 flush 日志、关串口）。
            _services?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 同上：退出阶段不再弹出任何东西
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 这里绝对不能再抛异常：退出过程中 DI 容器已释放，取服务会抛 ObjectDisposedException，
        // 于是"弹个提示框"变成进程崩溃。宁可吞掉提示也不能崩。
        WriteErrorLog(e.Exception);

        try
        {
            _notifier?.ShowError(
                "发生未处理的错误",
                $"{e.Exception.GetType().Name}: {e.Exception.Message}");
        }
        catch (Exception)
        {
            // 提示失败就算了，界面异常本身仍然按已处理对待
        }

        e.Handled = true;
    }

    /// <summary>
    /// 把未处理异常追加到 %AppData%\SeriTerm\ui-errors.log。
    /// 发布版是单文件 exe、没有控制台，日志提供程序也只是 Debug 输出（Release 下看不到），
    /// 出问题时除了一个弹窗什么线索都没有——正是在这里丢过一次真正的故障原因。
    /// </summary>
    private static void WriteErrorLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SeriTerm");
            Directory.CreateDirectory(directory);

            File.AppendAllText(
                Path.Combine(directory, "ui-errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception)
        {
            // 记不下来也不能影响程序继续运行
        }
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteErrorLog(exception);

            MessageBox.Show(
                $"{exception.GetType().Name}: {exception.Message}",
                "SeriTerm 发生严重错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
