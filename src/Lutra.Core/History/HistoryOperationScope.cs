namespace Lutra.Core.History;

/// <summary>Owns one active operation lease and its background heartbeat.</summary>
public sealed class HistoryOperationScope : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly IBackupHistoryService _history;
    private readonly CancellationTokenSource _heartbeatCancellation = new();
    private readonly Task _heartbeatTask;
    private int _terminal;

    private HistoryOperationScope(IBackupHistoryService history, HistoryOperationLease lease)
    {
        _history = history;
        OperationId = lease.OperationId;
        LeaseId = lease.LeaseId;
        _heartbeatTask = RunHeartbeatAsync();
    }

    public Guid OperationId { get; }
    public Guid LeaseId { get; }

    public static async Task<HistoryOperationScope> BeginAsync(
        IBackupHistoryService history,
        string targetName,
        HistoryOperationType operationType,
        CancellationToken cancellationToken = default)
    {
        var lease = await history.BeginOperationAsync(targetName, operationType, cancellationToken);
        return new HistoryOperationScope(history, lease);
    }

    public Task CompleteAsync(HistoryOperationCompletion completion)
        => RunTerminalAsync(token => _history.CompleteOperationAsync(
            OperationId, LeaseId, completion, token));

    public Task FailAsync(string errorMessage, long? durationMs = null)
        => RunTerminalAsync(token => _history.FailOperationAsync(
            OperationId, LeaseId, errorMessage, durationMs, token));

    public Task CancelAsync(string? errorMessage = null, long? durationMs = null)
        => RunTerminalAsync(token => _history.CancelOperationAsync(
            OperationId, LeaseId, errorMessage, durationMs, token));

    public async ValueTask DisposeAsync()
    {
        await _heartbeatCancellation.CancelAsync();
        try
        {
            await _heartbeatTask;
        }
        catch (OperationCanceledException)
        {
        }

        if (Volatile.Read(ref _terminal) != 0)
        {
            _heartbeatCancellation.Dispose();
            return;
        }

        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        try
        {
            await _history.InterruptOperationAsync(
                OperationId,
                LeaseId,
                "Operation scope ended without a terminal transition.",
                cleanup.Token);
            Interlocked.Exchange(ref _terminal, 1);
        }
        catch
        {
            // Expiry recovery is the final fallback if best-effort interruption fails.
        }
        finally
        {
            _heartbeatCancellation.Dispose();
        }
    }

    private async Task RunTerminalAsync(Func<CancellationToken, Task> transition)
    {
        if (Volatile.Read(ref _terminal) != 0)
            throw new InvalidOperationException("The history operation is already terminal.");

        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        await transition(cleanup.Token);
        Interlocked.Exchange(ref _terminal, 1);
        await _heartbeatCancellation.CancelAsync();
    }

    private async Task RunHeartbeatAsync()
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_heartbeatCancellation.Token))
            {
                if (Volatile.Read(ref _terminal) != 0)
                    return;
                try
                {
                    await _history.RenewLeaseAsync(
                        OperationId, LeaseId, _heartbeatCancellation.Token);
                }
                catch (OperationCanceledException) when (_heartbeatCancellation.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // Retry on the next heartbeat; expiry recovery remains the fallback.
                }
            }
        }
        catch (OperationCanceledException) when (_heartbeatCancellation.IsCancellationRequested)
        {
        }
    }
}
