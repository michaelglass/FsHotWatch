namespace FsHotWatch.ProcessOwnership

open System
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

module internal ChildProtocol =
    // A previous bounded termination attempt may still own this monitor. Retrying
    // must report uncertainty, rather than turn the outer timeout into an infinite wait.
    let withLock (sync: obj) action =
        if not (Monitor.TryEnter(sync, TimeSpan.FromSeconds 5.)) then
            raise (TimeoutException("Process ownership is still busy; containment cleanup remains unconfirmed."))

        try
            action ()
        finally
            Monitor.Exit sync

    // Cleanup failure must retain the original boundary error as the first cause.
    // Successful cleanup returns to the caller, which rethrows that original error.
    let cleanupAfterFailure (message: string) (primary: exn) (cleanup: unit -> unit) =
        try
            cleanup ()
        with cleanupError ->
            raise (AggregateException(message, primary, cleanupError))

    let parse (line: string) =
        if isNull line then
            raise (IOException("Process host closed its control pipe without the required receipt."))

        JsonDocument.Parse(line)

    let failure (message: JsonElement) =
        match message.TryGetProperty("message") with
        | true, error -> IOException("Process host failed: " + error.GetString())
        | _ -> IOException("Unexpected process host protocol message.")

    let send (writer: StreamWriter) (line: string) =
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 1.)
        writer.WriteLineAsync(line.AsMemory(), timeout.Token).GetAwaiter().GetResult()
        writer.FlushAsync(timeout.Token).GetAwaiter().GetResult()

    let receipt (reader: StreamReader) =
        task {
            let! line = reader.ReadLineAsync()
            use message = parse line
            let root = message.RootElement

            if root.GetProperty("kind").GetString() <> "exit" then
                raise (failure root)

            let code = root.GetProperty("exitCode").GetInt32()
            let! trailing = reader.ReadLineAsync()

            if not (isNull trailing) then
                raise (IOException("Unexpected data after the target exit receipt."))

            return code
        }

    let remaining (elapsed: Stopwatch) =
        let remaining = TimeSpan.FromSeconds 5. - elapsed.Elapsed

        if remaining <= TimeSpan.Zero then
            raise (TimeoutException("Process containment cleanup could not be confirmed within five seconds."))

        remaining

    let waitForContainment (containment: Containment) elapsed =
        while not (containment.IsEmpty()) do
            let remaining = remaining elapsed
            Thread.Sleep(int (min 20. (Math.Ceiling remaining.TotalMilliseconds)))

    let disposeWriter (writer: StreamWriter) =
        try
            writer.Dispose()
        with
        | :? IOException
        | :? ObjectDisposedException -> ()

    let hostStartInfo (target: ProcessStartInfo) hostDll pipeName =
        let runtime = DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory())
        let dotnetRoot = runtime.Parent.Parent.Parent.FullName

        let dotnet =
            Path.Combine(
                dotnetRoot,
                if OperatingSystem.IsWindows() then
                    "dotnet.exe"
                else
                    "dotnet"
            )

        if not (File.Exists dotnet) then
            raise (FileNotFoundException("The current runtime's dotnet host is missing.", dotnet))

        let start =
            ProcessStartInfo(
                dotnet,
                UseShellExecute = false,
                WorkingDirectory = target.WorkingDirectory,
                CreateNoWindow = target.CreateNoWindow,
                RedirectStandardOutput = target.RedirectStandardOutput,
                RedirectStandardError = target.RedirectStandardError,
                RedirectStandardInput = false
            )

        if target.RedirectStandardOutput then
            start.StandardOutputEncoding <- target.StandardOutputEncoding

        if target.RedirectStandardError then
            start.StandardErrorEncoding <- target.StandardErrorEncoding

        start.Environment.Clear()

        for entry in target.Environment do
            start.Environment[entry.Key] <- entry.Value

        for argument in
            [ Path.GetFullPath(hostDll)
              "--pipe"
              pipeName
              "--command"
              target.FileName
              "--arguments"
              target.Arguments ] do
            start.ArgumentList.Add argument

        start

/// Retains a native containment boundary and its separate target-exit receipt.
type internal OwnedChild
    private
    (proc: Process, pipe: NamedPipeServerStream, reader: StreamReader, writer: StreamWriter, containment: Containment) =
    let sync = obj ()
    let receipt = ChildProtocol.receipt reader
    let mutable cleanupVerified = false
    let mutable terminationRequested = false
    let mutable disposed = false

    do
        receipt.ContinueWith(
            (fun (completed: Task<int>) -> completed.Exception |> ignore),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
            ||| TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        )
        |> ignore

    member _.Process = proc

    member private _.WaitForHost elapsed =
        if not (proc.WaitForExit(int (Math.Ceiling((ChildProtocol.remaining elapsed).TotalMilliseconds)))) then
            raise (TimeoutException("Process host did not finish owned cleanup within five seconds."))

    member this.TargetExitCode =
        ChildProtocol.withLock sync (fun () ->
            ObjectDisposedException.ThrowIf(disposed, this)
            let elapsed = Stopwatch.StartNew()
            this.WaitForHost elapsed

            let code =
                receipt.WaitAsync(ChildProtocol.remaining elapsed).GetAwaiter().GetResult()

            if proc.ExitCode <> 137 then
                raise (IOException($"Unexpected process host exit {proc.ExitCode}; target result is unconfirmed."))

            ChildProtocol.waitForContainment containment elapsed
            cleanupVerified <- true
            code)

    member this.Terminate() =
        ChildProtocol.withLock sync (fun () ->
            ObjectDisposedException.ThrowIf(disposed, this)
            terminationRequested <- true

            if not cleanupVerified then
                let elapsed = Stopwatch.StartNew()

                try
                    ChildProtocol.send writer "stop"
                with
                | :? IOException
                | :? OperationCanceledException -> pipe.Dispose()
                | :? ObjectDisposedException -> ()

                containment.TerminateWindowsJob()
                this.WaitForHost elapsed
                ChildProtocol.waitForContainment containment elapsed
                cleanupVerified <- true)

    /// Dispose releases verified handles only; it never retries termination or masks a primary error.
    member _.Dispose() =
        ChildProtocol.withLock sync (fun () ->
            if not disposed then
                if not cleanupVerified then
                    invalidOp "Process ownership cannot be disposed before cleanup is verified."

                disposed <- true
                pipe.Dispose()
                reader.Dispose()
                ChildProtocol.disposeWriter writer
                (containment :> IDisposable).Dispose()
                proc.Dispose())

    // Admission is mandatory: the owner must retain cleanup before the target starts.
    static member Start(target: ProcessStartInfo, hostDll: string, register: Action<OwnedChild>) =
        ArgumentNullException.ThrowIfNull register
        ArgumentNullException.ThrowIfNull target

        if target.UseShellExecute || target.ArgumentList.Count <> 0 then
            invalidArg
                (nameof target)
                "Owned processes require UseShellExecute=false and the original Arguments string."

        if not (File.Exists hostDll) then
            raise (FileNotFoundException("Process host is missing.", hostDll))

        let pipeName = "fshw-" + Guid.NewGuid().ToString("N")

        let pipe =
            new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous ||| PipeOptions.CurrentUserOnly
            )

        let proc =
            new Process(StartInfo = ChildProtocol.hostStartInfo target hostDll pipeName)

        let mutable reader = None
        let mutable writer = None
        let mutable containment = None
        let mutable owned = None
        let mutable started = false

        try
            if not (proc.Start()) then
                raise (IOException("Process host did not start."))

            started <- true
            use startup = new CancellationTokenSource(TimeSpan.FromSeconds 5.)
            pipe.WaitForConnectionAsync(startup.Token).GetAwaiter().GetResult()
            let controlReader = new StreamReader(pipe, UTF8Encoding(false), false, 1024, true)
            reader <- Some controlReader
            let controlWriter = new StreamWriter(pipe, UTF8Encoding(false), 1024, true)
            writer <- Some controlWriter

            use ready =
                ChildProtocol.parse (controlReader.ReadLineAsync(startup.Token).AsTask().GetAwaiter().GetResult())

            let root = ready.RootElement

            if root.GetProperty("kind").GetString() <> "ready" then
                raise (ChildProtocol.failure root)

            let boundary = Containment.Open(root, proc.Id, pipeName + "-job")
            containment <- Some boundary
            let child = new OwnedChild(proc, pipe, controlReader, controlWriter, boundary)
            owned <- Some child
            register.Invoke child

            ChildProtocol.withLock child.Sync (fun () ->
                if child.Stopped then
                    raise (OperationCanceledException("The proc owner was stopped before target admission."))

                ChildProtocol.send controlWriter "start")

            child
        with startupError ->
            match owned with
            | Some child ->
                ChildProtocol.cleanupAfterFailure
                    "Process admission and containment cleanup failed."
                    startupError
                    child.Terminate
            | None ->
                pipe.Dispose()

                try
                    ChildProtocol.cleanupAfterFailure
                        "Process startup and containment cleanup failed."
                        startupError
                        (fun () ->
                            if started then
                                containment |> Option.iter (fun boundary -> boundary.TerminateWindowsJob())

                                if not (proc.WaitForExit 5000) then
                                    raise (IOException("Process host startup failed and its shutdown is unconfirmed."))

                                containment
                                |> Option.iter (fun boundary ->
                                    ChildProtocol.waitForContainment boundary (Stopwatch.StartNew())))
                finally
                    containment |> Option.iter (fun boundary -> (boundary :> IDisposable).Dispose())
                    reader |> Option.iter (fun control -> control.Dispose())
                    writer |> Option.iter ChildProtocol.disposeWriter
                    proc.Dispose()

            reraise ()

    member private _.Sync = sync
    member private _.Stopped = terminationRequested || disposed
