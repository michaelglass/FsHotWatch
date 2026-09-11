using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace FsHotWatch.ProcessOwnership;

/// <summary>Retains a native containment boundary and its separate target-exit receipt.</summary>
public sealed class OwnedChild : IDisposable
{
    private readonly object sync = new();
    private readonly NamedPipeServerStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly Containment containment;
    private readonly Task<int> receipt;
    private bool cleanupVerified;
    private bool terminationRequested;
    private bool disposed;

    public Process Process { get; }

    private OwnedChild(Process process, NamedPipeServerStream pipe, StreamReader reader,
        StreamWriter writer, Containment containment)
    {
        Process = process;
        this.pipe = pipe;
        this.reader = reader;
        this.writer = writer;
        this.containment = containment;
        receipt = ReadReceiptAsync(reader);
        // Cancellation intentionally has no target receipt. Observe that task's exception
        // even when the caller only terminates; awaiting TargetExitCode still throws it.
        _ = receipt.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public static OwnedChild Start(ProcessStartInfo target, string hostDll) => StartCore(target, hostDll, null);

    public static OwnedChild Start(ProcessStartInfo target, string hostDll, Action<OwnedChild> register)
    {
        ArgumentNullException.ThrowIfNull(register);
        return StartCore(target, hostDll, register);
    }

    private static OwnedChild StartCore(ProcessStartInfo target, string hostDll, Action<OwnedChild>? register)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.UseShellExecute || target.ArgumentList.Count != 0)
            throw new ArgumentException("Owned processes require UseShellExecute=false and the original Arguments string.", nameof(target));
        if (!File.Exists(hostDll))
            throw new FileNotFoundException("Process host is missing.", hostDll);

        var pipeName = "fshw-" + Guid.NewGuid().ToString("N");
        var startInfo = CreateHostStartInfo(target, hostDll, pipeName);
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var process = new Process { StartInfo = startInfo };
        StreamReader? reader = null;
        StreamWriter? writer = null;
        Containment? containment = null;
        OwnedChild? owned = null;
        var started = false;
        try
        {
            if (!process.Start())
                throw new IOException("Process host did not start.");
            started = true;
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            pipe.WaitForConnectionAsync(startup.Token).GetAwaiter().GetResult();
            reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true);
            using var ready = ParseMessage(reader.ReadLineAsync(startup.Token).AsTask().GetAwaiter().GetResult());
            var root = ready.RootElement;
            if (root.GetProperty("kind").GetString() != "ready")
                throw ProtocolFailure(root);
            containment = Containment.Open(root, process.Id, pipeName + "-job");
            owned = new OwnedChild(process, pipe, reader, writer, containment);
            // Registration is part of admission. A closed scope can synchronously revoke
            // this owner, in which case no target will ever start.
            register?.Invoke(owned);
            lock (owned.sync)
            {
                if (owned.terminationRequested || owned.disposed)
                    throw new OperationCanceledException("The process owner was stopped before target admission.");
                Send(writer, "start");
            }
            return owned;
        }
        catch (Exception startupError)
        {
            if (owned is not null)
            {
                try { owned.Terminate(); }
                catch (Exception cleanupError)
                {
                    // A registered owner keeps its handles so the registry can retain and
                    // report the cleanup failure rather than forgetting a surviving child.
                    throw new AggregateException("Process admission and containment cleanup failed.", startupError, cleanupError);
                }
                if (register is null)
                    owned.Dispose();
                throw;
            }
            // Closing the control connection revokes admission even when ready was lost.
            pipe.Dispose();
            try
            {
                if (started)
                {
                    containment?.TerminateWindowsJob();
                    if (!process.WaitForExit(5000))
                        throw new IOException("Process host startup failed and its shutdown is unconfirmed.");
                    if (containment is not null)
                        WaitForContainment(containment, Stopwatch.StartNew());
                }
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Process startup and containment cleanup failed.", startupError, cleanupError);
            }
            finally
            {
                containment?.Dispose();
                reader?.Dispose();
                DisposeWriterAfterPipeClosed(writer);
                process.Dispose();
            }
            throw;
        }
    }

    /// <summary>Returns the original exit only after its receipt and all owned cleanup are verified.</summary>
    public int TargetExitCode
    {
        get
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var elapsed = Stopwatch.StartNew();
                WaitForHost(elapsed);
                var exitCode = receipt.WaitAsync(Remaining(elapsed)).GetAwaiter().GetResult();
                if (Process.ExitCode != 137)
                    throw new IOException($"Unexpected process host exit {Process.ExitCode}; target result is unconfirmed.");
                WaitForContainment(containment, elapsed);
                cleanupVerified = true;
                return exitCode;
            }
        }
    }

    /// <summary>Revokes the target and verifies cleanup; throws if any ownership remains unknown.</summary>
    public void Terminate()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            terminationRequested = true;
            if (cleanupVerified)
                return;
            var elapsed = Stopwatch.StartNew();
            try { Send(writer, "stop"); }
            catch (IOException) { pipe.Dispose(); }
            catch (OperationCanceledException) { pipe.Dispose(); }
            // A retained Windows handle remains authoritative even if the helper already died.
            containment.TerminateWindowsJob();
            WaitForHost(elapsed);
            WaitForContainment(containment, elapsed);
            cleanupVerified = true;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            // Preserve handles for a caller's retry if cleanup is still unconfirmed.
            Terminate();
            disposed = true;
            pipe.Dispose();
            reader.Dispose();
            DisposeWriterAfterPipeClosed(writer);
            containment.Dispose();
            Process.Dispose();
        }
    }

    private static void DisposeWriterAfterPipeClosed(StreamWriter? writer)
    {
        try { writer?.Dispose(); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void WaitForHost(Stopwatch elapsed)
    {
        if (!Process.WaitForExit((int)Math.Ceiling(Remaining(elapsed).TotalMilliseconds)))
            throw new TimeoutException("Process host did not finish owned cleanup within five seconds.");
    }

    private static void WaitForContainment(Containment containment, Stopwatch elapsed)
    {
        while (!containment.IsEmpty())
        {
            var remaining = Remaining(elapsed);
            Thread.Sleep((int)Math.Min(20, Math.Ceiling(remaining.TotalMilliseconds)));
        }
    }

    private static TimeSpan Remaining(Stopwatch elapsed)
    {
        var remaining = TimeSpan.FromSeconds(5) - elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("Process containment cleanup could not be confirmed within five seconds.");
        return remaining;
    }

    private static async Task<int> ReadReceiptAsync(StreamReader reader)
    {
        using var message = ParseMessage(await reader.ReadLineAsync());
        var root = message.RootElement;
        if (root.GetProperty("kind").GetString() != "exit")
            throw ProtocolFailure(root);
        var exitCode = root.GetProperty("exitCode").GetInt32();
        // A target receipt is exactly one terminal record followed by connection closure.
        if (await reader.ReadLineAsync() is not null)
            throw new IOException("Unexpected data after the target exit receipt.");
        return exitCode;
    }

    private static JsonDocument ParseMessage(string? line) => line is null
        ? throw new IOException("Process host closed its control pipe without the required receipt.")
        : JsonDocument.Parse(line);

    private static IOException ProtocolFailure(JsonElement message) => new(
        message.TryGetProperty("message", out var error)
            ? "Process host failed: " + error.GetString()
            : "Unexpected process host protocol message.");

    private static void Send(StreamWriter writer, string line)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        writer.WriteLineAsync(line.AsMemory(), timeout.Token).GetAwaiter().GetResult();
        writer.FlushAsync(timeout.Token).GetAwaiter().GetResult();
    }

    private static ProcessStartInfo CreateHostStartInfo(ProcessStartInfo target, string hostDll, string pipeName)
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtime.Parent?.Parent?.Parent?.FullName
            ?? throw new IOException("Cannot locate the current dotnet host.");
        var dotnet = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(dotnet))
            throw new FileNotFoundException("The current runtime's dotnet host is missing.", dotnet);
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            WorkingDirectory = target.WorkingDirectory,
            CreateNoWindow = target.CreateNoWindow,
            RedirectStandardOutput = target.RedirectStandardOutput,
            RedirectStandardError = target.RedirectStandardError,
            RedirectStandardInput = false
        };
        if (target.RedirectStandardOutput)
            start.StandardOutputEncoding = target.StandardOutputEncoding;
        if (target.RedirectStandardError)
            start.StandardErrorEncoding = target.StandardErrorEncoding;
        start.Environment.Clear();
        foreach (var entry in target.Environment)
            start.Environment[entry.Key] = entry.Value;
        foreach (var argument in new[] { Path.GetFullPath(hostDll), "--pipe", pipeName,
            "--command", target.FileName, "--arguments", target.Arguments })
            start.ArgumentList.Add(argument);
        return start;
    }
}
