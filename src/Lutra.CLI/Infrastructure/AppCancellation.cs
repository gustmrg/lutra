using System.Runtime.InteropServices;

namespace Lutra.CLI.Infrastructure;

/// <summary>
/// Turns Ctrl+C and SIGTERM (what <c>systemctl stop</c> sends) into a token the
/// commands hand to Core, so a running operation can stop its child processes,
/// remove temporary files and record itself as cancelled.
/// </summary>
internal static class AppCancellation
{
    private static readonly CancellationTokenSource Source = new();

    public static CancellationToken Token => Source.Token;

    public static IDisposable Register()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            // The first Ctrl+C asks for a clean stop; a second one terminates.
            e.Cancel = !Source.IsCancellationRequested;
            Source.Cancel();
        };

        return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            Source.Cancel();
        });
    }
}
