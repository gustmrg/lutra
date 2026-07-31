using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Lutra.Core.Encryption;

public static class AgeEncryption
{
    public static async Task EncryptAsync(
        string inputPath,
        string outputPath,
        string recipient,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "age",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "--recipient", recipient, "--output", outputPath, inputPath })
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start age.");
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"age encryption failed: {(await stderr).Trim()}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("age is required for encrypted targets but was not found in PATH.", ex);
        }
    }

    public static string RecipientFingerprint(string recipient)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(recipient.Trim()));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}
