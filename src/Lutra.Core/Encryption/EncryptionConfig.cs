namespace Lutra.Core.Encryption;

public sealed class EncryptionConfig
{
    public string Type { get; init; } = "age";
    public required string Recipient { get; init; }
}
