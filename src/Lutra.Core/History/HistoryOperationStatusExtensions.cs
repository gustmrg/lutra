namespace Lutra.Core.History;

public static class HistoryOperationStatusExtensions
{
    public static bool IsActive(this HistoryOperationStatus status)
        => status is HistoryOperationStatus.Creating
            or HistoryOperationStatus.Verifying
            or HistoryOperationStatus.Uploading;

    public static bool IsTerminal(this HistoryOperationStatus status) => !status.IsActive();
}
