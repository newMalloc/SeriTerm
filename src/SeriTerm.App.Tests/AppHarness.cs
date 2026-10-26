using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using SeriTerm.App.Services;
using SeriTerm.App.ViewModels;
using SeriTerm.Core.Framing;
using SeriTerm.Core.Serial;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SeriTerm.App.Tests;

internal static class UiThread
{
    private static readonly Task<Dispatcher> Ready = Start();

    private static Task<Dispatcher> Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SeriTerm;component/Themes/Shared.xaml", UriKind.Relative) });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SeriTerm;component/Themes/Dark.xaml", UriKind.Relative) });
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    public static async Task Run(Func<Task> test)
    {
        var dispatcher = await Ready.WaitAsync(TimeSpan.FromSeconds(20));
        await (await dispatcher.InvokeAsync(test)).WaitAsync(TimeSpan.FromSeconds(30));
    }

    public static async Task Drain() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
}

internal sealed class AppHarness : IAsyncDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "SeriTerm-tests-" + Guid.NewGuid().ToString("N"));
    public FakeTransport Transport { get; } = new();
    public FakeNotifier Notifier { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public ThemeService Theme { get; } = new();
    public MainViewModel Vm { get; }

    public AppHarness(bool logging = false)
    {
        Directory.CreateDirectory(DirectoryPath);
        var settings = new AppSettings
        {
            McpEnabled = false, AutoReconnect = false, Framing = FramingMode.None,
            SaveRawLog = logging, LogDirectory = DirectoryPath,
        };
        Theme.Apply(ThemeMode.Dark);
        Vm = new MainViewModel(Transport, new PortFriendlyNameProvider(), Notifier,
            new MemorySettings(settings), Theme, Dialogs, NullLogger<MainViewModel>.Instance);
    }

    public async Task Connect()
    {
        await Transport.OpenAsync(new SerialSettings { PortName = "COM_TEST" });
        await UiThread.Drain();
    }

    public async ValueTask DisposeAsync()
    {
        await Vm.DisposeAsync();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}

internal sealed class FakeTransport : ISerialTransport
{
    public TransportState State { get; private set; }
    public SerialSettings? CurrentSettings { get; private set; }
    public event EventHandler<TransportStateChangedEventArgs>? StateChanged;
    public event EventHandler<BytesReceivedEventArgs>? BytesReceived;
    public List<byte[]> Writes { get; } = [];
    public Func<CancellationToken, Task>? BeforeWrite { get; set; }

    public void SetState(TransportState state)
    {
        var old = State;
        State = state;
        StateChanged?.Invoke(this, new TransportStateChangedEventArgs(old, state));
    }
    public Task OpenAsync(SerialSettings settings, CancellationToken cancellationToken = default)
    { CurrentSettings = settings; SetState(TransportState.Open); return Task.CompletedTask; }
    public Task CloseAsync() { SetState(TransportState.Closed); return Task.CompletedTask; }
    public Task SetBaudRateAsync(int baudRate, CancellationToken cancellationToken = default)
    { CurrentSettings = CurrentSettings! with { BaudRate = baudRate }; return Task.CompletedTask; }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (BeforeWrite is not null) await BeforeWrite(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (Writes) Writes.Add(data.ToArray());
    }
    public void Receive(byte[] bytes) => BytesReceived?.Invoke(this, new BytesReceivedEventArgs(bytes, Stopwatch.GetTimestamp()));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class MemorySettings(AppSettings settings) : ISettingsStore
{
    public string FilePath => string.Empty;
    public AppSettings Load() => settings;
    public void Save(AppSettings value) { }
}

internal sealed class FakeNotifier : IUserNotifier
{
    public List<string> Errors { get; } = [];
    public void ShowError(string title, string message) => Errors.Add(title + ": " + message);
    public void ShowInfo(string title, string message) { }
    public string? AskText(string title, string prompt, string defaultValue) => null;
}

internal sealed class FakeDialogs : IFileDialogService
{
    public string? OpenPath { get; set; }
    public string? SavePath { get; set; }
    public string? AskOpenFile(string filter, string title) => OpenPath;
    public string? AskSaveFile(string defaultFileName, string filter, string title) => SavePath;
    public string? AskFolder(string title, string? initialDirectory = null) => null;
}
