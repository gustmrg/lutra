namespace Lutra.Core.Persistence;

/// <summary>Raised when unrelated configurations try to share application state.</summary>
public sealed class LutraDatabaseOwnershipException : InvalidOperationException
{
    public LutraDatabaseOwnershipException(
        string stateDirectory,
        string? ownerConfigPath,
        string requestedConfigPath)
        : base(
            $"State directory '{stateDirectory}' belongs to configuration '{ownerConfigPath}', " +
            $"not '{requestedConfigPath}'. Select a distinct state_directory.")
    {
        StateDirectory = stateDirectory;
        OwnerConfigPath = ownerConfigPath;
        RequestedConfigPath = requestedConfigPath;
    }

    public string StateDirectory { get; }

    public string? OwnerConfigPath { get; }

    public string RequestedConfigPath { get; }
}
