using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FsHotWatch.ProcessHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 6 || args[0] != "--pipe" || args[2] != "--command"
            || args[4] != "--arguments" || string.IsNullOrWhiteSpace(args[1])
            || string.IsNullOrWhiteSpace(args[3]))
            return 125;

        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        OwnedProcessGroup? ownership = null;
        StreamWriter? writer = null;
        try
        {
            using (var connection = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await pipe.ConnectAsync(connection.Token);

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true);
            ownership = OwnedProcessGroup.Create();
            await SendAsync(writer, new { kind = "ready", processGroup = ownership.ProcessGroup });

            using (var admission = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                if (await reader.ReadLineAsync(admission.Token) != "start")
                    return 125;
            }

            // Listen before spawning: loss of the owner cannot strand a newly started child.
            // The helper itself belongs to the containment boundary, so stopping it cannot
            // race with a later spawn on another thread.
            _ = StopOnControlAsync(reader, ownership);
            using var target = new Process
            {
                StartInfo = new ProcessStartInfo(args[3], args[5])
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                }
            };
            if (!target.Start())
                throw new IOException("Target process did not start.");
            target.StandardInput.Close();
            await target.WaitForExitAsync();
            await SendAsync(writer, new { kind = "exit", exitCode = target.ExitCode });
            return 0;
        }
        catch (Exception error)
        {
            if (writer is not null)
            {
                try { await SendAsync(writer, new { kind = "error", message = error.Message }); }
                catch (Exception) { /* Broken owner connection still requires cleanup below. */ }
            }
            return 125;
        }
        finally
        {
            // Also runs after protocol write failures. Target success is reported separately;
            // the helper's deliberate termination is never the target's exit status.
            ownership?.Terminate();
            writer?.Dispose();
        }
    }

    private static async Task SendAsync(StreamWriter writer, object message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), timeout.Token);
        await writer.FlushAsync(timeout.Token);
    }

    private static async Task StopOnControlAsync(StreamReader reader, OwnedProcessGroup ownership)
    {
        try
        {
            // There are no valid commands after admission except stop. EOF, malformed input,
            // and pipe failures all revoke ownership instead of leaving an orphan helper.
            await reader.ReadLineAsync();
        }
        catch (Exception) { }
        finally { ownership.Terminate(); }
    }
}
