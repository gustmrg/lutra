namespace Lutra.Core.History;

public enum HistoryOperationStatus
{
    Creating,
    Verifying,
    Uploading,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted
}
