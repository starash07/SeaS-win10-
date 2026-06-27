using System.IO;
using System.IO.Pipes;
using System.Windows;
using SeaS.App.Services;

namespace SeaS.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\SeaS.Reader.SingleInstance.v1";
    private const string PipeName = "SeaS.Reader.Activation.v1";

    private Mutex? _instanceMutex;
    private CancellationTokenSource? _pipeCancellation;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if DEBUG
        DispatcherUnhandledException += (_, args) =>
            DebugLog.WriteException("DispatcherUnhandledException", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                DebugLog.WriteException("AppDomain.UnhandledException", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            DebugLog.WriteException("TaskScheduler.UnobservedTaskException", args.Exception);
#endif

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            SignalExistingInstance();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _pipeCancellation = new CancellationTokenSource();
        _ = ListenForActivationAsync(window, _pipeCancellation.Token);
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(400);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine("ACTIVATE");
                return;
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
            }

            Thread.Sleep(120);
        }
    }

    private async Task ListenForActivationAsync(MainWindow window, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                var command = await reader.ReadLineAsync(cancellationToken);
                if (string.Equals(command, "ACTIVATE", StringComparison.Ordinal))
                {
                    await Dispatcher.InvokeAsync(window.ActivateFromExternalRequest);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                await Task.Delay(120, cancellationToken);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCancellation?.Cancel();
        _pipeCancellation?.Dispose();
        _pipeCancellation = null;

        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        base.OnExit(e);
    }
}
