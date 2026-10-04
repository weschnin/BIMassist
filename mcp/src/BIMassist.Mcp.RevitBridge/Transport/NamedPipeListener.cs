using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Dispatch;
using BIMassist.Mcp.RevitBridge.Lifecycle;

namespace BIMassist.Mcp.RevitBridge.Transport;

public enum NamedPipeListenerState
{
    Created,
    Running,
    Stopping,
    Stopped,
    Faulted
}

public sealed class NamedPipeListener : IBridgeRuntimeComponent, IAsyncDisposable
{
    private static readonly TimeSpan DefaultApplyDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultSearchDeadline = TimeSpan.FromSeconds(8);
    private readonly TimeSpan _applyDeadline;
    private readonly TimeSpan _searchDeadline;
    private readonly string _endpointName;
    private readonly BridgeRequestQueue _queue;
    private readonly object _sync = new();
    private CancellationTokenSource? _stopSource;
    private Task? _runTask;
    private NamedPipeServerStream? _currentServer;
    private int _state = (int)NamedPipeListenerState.Created;
    private int _disposed;

    public NamedPipeListener(string endpointName, BridgeRequestQueue queue)
        : this(endpointName, queue, DefaultApplyDeadline)
    {
    }

    internal NamedPipeListener(string endpointName, BridgeRequestQueue queue, TimeSpan applyDeadline)
        : this(endpointName, queue, applyDeadline, DefaultSearchDeadline)
    {
    }

    internal NamedPipeListener(string endpointName, BridgeRequestQueue queue, TimeSpan applyDeadline,
        TimeSpan searchDeadline)
    {
        if (applyDeadline <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(applyDeadline));
        if (searchDeadline <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(searchDeadline));
        _applyDeadline = applyDeadline;
        _searchDeadline = searchDeadline;
        _endpointName = string.IsNullOrWhiteSpace(endpointName)
            ? throw new ArgumentException("A pipe endpoint name is required.", nameof(endpointName))
            : endpointName;
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public NamedPipeListenerState State => (NamedPipeListenerState)Volatile.Read(ref _state);

    public Task StartAsync(CancellationToken lifetimeToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(
                ref _state,
                (int)NamedPipeListenerState.Running,
                (int)NamedPipeListenerState.Created) != (int)NamedPipeListenerState.Created)
        {
            throw new InvalidOperationException($"Named-pipe listener cannot start from state {State}.");
        }

        _stopSource = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _runTask = RunAsync(_stopSource.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        NamedPipeListenerState state = State;
        if (state == NamedPipeListenerState.Stopped)
        {
            return;
        }

        if (state == NamedPipeListenerState.Created)
        {
            Interlocked.Exchange(ref _state, (int)NamedPipeListenerState.Stopped);
            return;
        }

        Interlocked.Exchange(ref _state, (int)NamedPipeListenerState.Stopping);
        _stopSource?.Cancel();
        lock (_sync)
        {
            _currentServer?.Dispose();
        }

        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopSource?.IsCancellationRequested == true)
            {
            }
        }

        Interlocked.Exchange(ref _state, (int)NamedPipeListenerState.Stopped);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopSource?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await using NamedPipeServerStream server = CurrentUserPipeSecurity.CreateServer(_endpointName);
                lock (_sync)
                {
                    _currentServer = server;
                }

                try
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await HandleClientAsync(server, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException) when (cancellationToken.IsCancellationRequested || !server.IsConnected)
                {
                }
                catch (EndOfStreamException)
                {
                }
                catch (InvalidDataException)
                {
                }
                catch (JsonException)
                {
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_currentServer, server))
                        {
                            _currentServer = null;
                        }
                    }
                }
            }
        }
        catch
        {
            Interlocked.Exchange(ref _state, (int)NamedPipeListenerState.Faulted);
            throw;
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        while (server.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            BridgeRequest request = await LengthPrefixedJsonProtocol
                .ReadAsync<BridgeRequest>(server, cancellationToken)
                .ConfigureAwait(false);
            // A modal confirmation can outlive a disconnected client. Observe the
            // kernel pipe state without consuming bytes (a second ReadAsync would
            // corrupt the next frame on persistent connections). The bounded token
            // remains observable by the ExternalEvent after execution starts.
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (request.Operation == BridgeOperations.ApplyChangePlan)
                pending.CancelAfter(_applyDeadline);
            else if (request.Operation == BridgeOperations.SearchDocumentParameters)
                pending.CancelAfter(_searchDeadline);
            Task monitor = MonitorConnectionAsync(server, pending);
            try
            {
                QueuedBridgeRequest queued = _queue.Enqueue(request, pending.Token);
                BridgeResponse response;
                try
                {
                    response = await queued.Completion.WaitAsync(pending.Token).ConfigureAwait(false);
                }
                catch (Exception) when (!pending.IsCancellationRequested)
                {
                    response = new BridgeResponse
                    {
                        RequestId = request.RequestId,
                        Success = false,
                        Warnings = [],
                        Error = new BridgeError(
                            BridgeErrorCodes.BridgeInternalError,
                            "The bridge could not complete the request."),
                        DurationMs = 0
                    };
                }
                await LengthPrefixedJsonProtocol.WriteAsync(server, response, pending.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pending.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // A timed-out or disconnected client cannot receive a reliable reply.
                // The running callback keeps its canceled token and must not start a write.
                return;
            }
            finally
            {
                pending.Cancel();
                await monitor.ConfigureAwait(false);
            }
        }
    }

    private static async Task MonitorConnectionAsync(NamedPipeServerStream server, CancellationTokenSource pending)
    {
        while (!pending.IsCancellationRequested)
        {
            // A failed peek indicates a broken pipe. No Revit API is touched here.
            if (!PeekNamedPipe(server.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero,
                    out _, IntPtr.Zero))
            {
                pending.Cancel();
                return;
            }
            try
            {
                await Task.Delay(50, pending.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pending.IsCancellationRequested)
            {
                return;
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(Microsoft.Win32.SafeHandles.SafePipeHandle handle,
        IntPtr buffer, uint bufferSize, IntPtr bytesRead, out uint totalBytesAvailable,
        IntPtr bytesLeftThisMessage);
}
