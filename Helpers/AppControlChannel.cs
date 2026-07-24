using ClashWinUI.Models;
using ClashWinUI.Serialization;
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClashWinUI.Helpers
{
    internal sealed class AppControlChannel : IDisposable
    {
        private readonly string _pipeName;
        private readonly Func<AppControlCommand, Task> _commandHandler;
        private readonly CancellationTokenSource _cancellation = new();
        private Task? _serverLoopTask;
        private int _isDisposed;

        public AppControlChannel(AppProcessRole role, Func<AppControlCommand, Task> commandHandler)
        {
            _pipeName = GetPipeName(role);
            _commandHandler = commandHandler;
        }

        public void Start()
        {
            if (_serverLoopTask is not null || Volatile.Read(ref _isDisposed) == 1)
            {
                return;
            }

            _serverLoopTask = RunServerLoopAsync(_cancellation.Token);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            {
                return;
            }

            try
            {
                _cancellation.Cancel();
            }
            catch
            {
                // Best-effort shutdown only.
            }

            // Never block here. Waiting for the server loop can deadlock when Dispose is
            // triggered by a control-command handler that is still running on that loop.
            Task? serverLoopTask = _serverLoopTask;
            if (serverLoopTask is not null)
            {
                _ = serverLoopTask.ContinueWith(
                    static _ => { },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            try
            {
                _cancellation.Dispose();
            }
            catch
            {
                // Best-effort shutdown only.
            }
        }

        public static async Task<bool> TrySendAsync(
            AppProcessRole targetRole,
            AppControlCommand command,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);

            string payload = JsonSerializer.Serialize(command, ClashJsonContext.Default.AppControlCommand);

            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    GetPipeName(targetRole),
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedCts.CancelAfter(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(linkedCts.Token).ConfigureAwait(false);

                using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true);
                await writer.WriteAsync(payload.AsMemory(), linkedCts.Token).ConfigureAwait(false);
                await writer.FlushAsync(linkedCts.Token).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task RunServerLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                    string payload = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        continue;
                    }

                    AppControlCommand? command = JsonSerializer.Deserialize(payload, ClashJsonContext.Default.AppControlCommand);
                    if (command is null)
                    {
                        continue;
                    }

                    // Handle off the accept loop so shutdown/dispose cannot deadlock the pipe server.
                    Func<AppControlCommand, Task> handler = _commandHandler;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await handler(command).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Best-effort command handling only.
                        }
                    }, CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    try
                    {
                        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private static string GetPipeName(AppProcessRole role)
        {
            string roleSegment = role == AppProcessRole.Tray ? "tray" : "ui";
            string userSegment = Environment.UserName.Replace('\\', '_').Replace('/', '_');
            return $"ClashWinUI.{userSegment}.{roleSegment}";
        }
    }
}
