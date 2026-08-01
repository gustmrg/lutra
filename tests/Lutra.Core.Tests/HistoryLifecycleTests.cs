using Lutra.Core.History;
using Lutra.Core.Persistence;

namespace Lutra.Core.Tests;

public sealed class HistoryLifecycleTests
{
    [Fact]
    public async Task BeginAndTerminalTransitions_PersistEveryLifecycleState()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
        var history = CreateHistory(temp, clock);

        var backup = await history.BeginOperationAsync("backup", HistoryOperationType.Backup);
        var verify = await history.BeginOperationAsync("verify", HistoryOperationType.Verify);
        var sync = await history.BeginOperationAsync("sync", HistoryOperationType.Sync);

        Assert.Equal(HistoryOperationStatus.Creating, (await FindAsync(history, backup)).Status);
        Assert.Equal(HistoryOperationStatus.Verifying, (await FindAsync(history, verify)).Status);
        Assert.Equal(HistoryOperationStatus.Uploading, (await FindAsync(history, sync)).Status);

        clock.Advance(TimeSpan.FromSeconds(3));
        await history.CompleteOperationAsync(backup.OperationId, backup.LeaseId, new HistoryOperationCompletion(
            FileName: "backup.tar",
            FileSizeBytes: 42,
            Sha256: "abc",
            ManifestFileName: "backup.tar.json",
            DurationMs: 3000));
        await history.FailOperationAsync(verify.OperationId, verify.LeaseId, "restore failed", 3000);
        await history.CancelOperationAsync(sync.OperationId, sync.LeaseId, "caller cancelled", 3000);
        await Assert.ThrowsAsync<InvalidOperationException>(() => history.FailOperationAsync(
            backup.OperationId, backup.LeaseId, "cannot overwrite terminal"));

        var completed = await FindAsync(history, backup);
        var failed = await FindAsync(history, verify);
        var cancelled = await FindAsync(history, sync);
        Assert.Equal(HistoryOperationStatus.Succeeded, completed.Status);
        Assert.Equal("backup.tar", completed.FileName);
        Assert.Equal(42, completed.FileSizeBytes);
        Assert.Equal("abc", completed.Sha256);
        Assert.Equal(HistoryOperationStatus.Failed, failed.Status);
        Assert.Equal("restore failed", failed.ErrorMessage);
        Assert.Equal(HistoryOperationStatus.Cancelled, cancelled.Status);
        Assert.All(new[] { completed, failed, cancelled }, record =>
        {
            Assert.NotNull(record.CompletedAt);
            Assert.Null(record.LeaseId);
            Assert.Null(record.LeaseExpiresAt);
        });
    }

    [Fact]
    public async Task WrongOrExpiredLease_CannotOverwriteOperation()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var history = CreateHistory(temp, clock);
        var lease = await history.BeginOperationAsync("target", HistoryOperationType.Backup);

        await Assert.ThrowsAsync<InvalidOperationException>(() => history.CompleteOperationAsync(
            lease.OperationId,
            Guid.NewGuid(),
            new HistoryOperationCompletion(DurationMs: 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => history.RenewLeaseAsync(
            lease.OperationId,
            Guid.NewGuid()));
        Assert.Equal(HistoryOperationStatus.Creating, (await FindAsync(history, lease)).Status);

        clock.Advance(TimeSpan.FromMinutes(6));
        await Assert.ThrowsAsync<InvalidOperationException>(() => history.FailOperationAsync(
            lease.OperationId,
            lease.LeaseId,
            "too late"));

        var recovered = await FindAsync(history, lease);
        Assert.Equal(HistoryOperationStatus.Interrupted, recovered.Status);
        Assert.Contains("lease expired", recovered.ErrorMessage);
    }

    [Fact]
    public async Task Heartbeat_ExtendsLeaseAndPreventsFalseInterruption()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var history = CreateHistory(temp, clock);
        var lease = await history.BeginOperationAsync("target", HistoryOperationType.Backup);
        var originalExpiry = (await FindAsync(history, lease)).LeaseExpiresAt;

        clock.Advance(TimeSpan.FromMinutes(4));
        await history.RenewLeaseAsync(lease.OperationId, lease.LeaseId);
        var renewed = await FindAsync(history, lease);

        Assert.True(renewed.LeaseExpiresAt > originalExpiry);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HistoryOperationStatus.Creating, (await FindAsync(history, lease)).Status);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(HistoryOperationStatus.Interrupted, (await FindAsync(history, lease)).Status);
    }

    [Fact]
    public async Task ScopeDisposeWithoutTerminalTransition_InterruptsOperation()
    {
        using var temp = new TempDirectory();
        var history = CreateHistory(temp, new ManualTimeProvider(DateTimeOffset.UtcNow));
        var scope = await HistoryOperationScope.BeginAsync(
            history, "target", HistoryOperationType.Verify);
        var operationId = scope.OperationId;

        await scope.DisposeAsync();

        var record = Assert.Single(await history.GetAllRecordsAsync(), item => item.Id == operationId);
        Assert.Equal(HistoryOperationStatus.Interrupted, record.Status);
        Assert.Contains("without a terminal transition", record.ErrorMessage);
    }

    [Fact]
    public async Task ActiveRowsAreNeverPruned_ButOldCancelledAndInterruptedRowsAre()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
        var history = CreateHistory(temp, clock);
        var active = await history.BeginOperationAsync("active", HistoryOperationType.Backup);
        var cancelled = await history.BeginOperationAsync("cancelled", HistoryOperationType.Verify);
        await history.CancelOperationAsync(cancelled.OperationId, cancelled.LeaseId);
        var interrupted = await history.BeginOperationAsync("interrupted", HistoryOperationType.Sync);
        await history.InterruptOperationAsync(interrupted.OperationId, interrupted.LeaseId, "abandoned");
        clock.Advance(TimeSpan.FromDays(10));

        var removed = await history.PruneOperationalRecordsAsync(clock.GetUtcNow().AddDays(-1));

        Assert.Equal(2, removed);
        var remaining = await history.GetAllRecordsAsync();
        Assert.Single(remaining);
        Assert.Equal(active.OperationId, remaining[0].Id);
        Assert.Equal(HistoryOperationStatus.Interrupted, remaining[0].Status);
    }

    private static SqliteBackupHistoryRepository CreateHistory(
        TempDirectory temp,
        TimeProvider timeProvider)
        => new(
            new LutraDatabase(
                Path.Combine(temp.Path, "state"),
                Path.Combine(temp.Path, "lutra.yaml"),
                Path.Combine(temp.Path, "backups")),
            timeProvider);

    private static async Task<HistoryRecord> FindAsync(
        SqliteBackupHistoryRepository history,
        HistoryOperationLease lease)
        => Assert.Single(
            await history.GetAllRecordsAsync(),
            record => record.Id == lease.OperationId);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
