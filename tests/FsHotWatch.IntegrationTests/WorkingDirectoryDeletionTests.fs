module FsHotWatch.Tests.WorkingDirectoryDeletionTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote

// The real deletion of a process's working directory, in a process of its own. The unit
// suite drives the same code over a stand-in `getcwd` (EventTests), because a test that
// deletes its own host's working directory breaks every relative path, and every child
// process, of every test running beside it. This pins what that stand-in assumes: the
// runtime's `getcwd` in a deleted directory raises `FileNotFoundException`, and
// `AbsFilePath` turns it into `WorkingDirectoryMissingException` naming the directory.

let private script (fsHotWatchDll: string) =
    $"""#r @"%s{fsHotWatchDll}"
open System.IO
open FsHotWatch.Events

let say (line: string) =
    stdout.WriteLine line
    stdout.Flush()

let dir = Path.Combine(Path.GetTempPath(), "fshw-cwd-" + System.Guid.NewGuid().ToString "N")
Directory.CreateDirectory dir |> ignore
Directory.SetCurrentDirectory dir
let stood = Directory.GetCurrentDirectory()
say ("BEFORE|" + AbsFilePath.value (AbsFilePath.create "before.fs"))
Directory.Delete dir

try
    AbsFilePath.create "after.fs" |> ignore
    say "RESOLVED"
with :? WorkingDirectoryMissingException as ex ->
    say (sprintf "MISSING|%%s|%%b" (defaultArg ex.LastKnownDirectory "") (ex.InnerException :? FileNotFoundException))

let rooted = Path.Combine(Path.GetPathRoot stood, "repo", "src", "Lib.fs")
say ("ROOTED|" + AbsFilePath.value (AbsFilePath.create rooted))
say ("STOOD|" + stood)
"""

[<Fact(Timeout = 180000)>]
let ``a process whose working directory is deleted names it for a relative path and still resolves a rooted one`` () =
    let scratch = Path.Combine(Path.GetTempPath(), $"fshw-cwd-child-{Guid.NewGuid():N}")

    Directory.CreateDirectory scratch |> ignore

    try
        let scriptPath = Path.Combine(scratch, "deleted-cwd.fsx")
        File.WriteAllText(scriptPath, script (Path.Combine(AppContext.BaseDirectory, "FsHotWatch.dll")))

        let start = ProcessStartInfo("dotnet")
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        // Never the test host's own working directory: the child gets one that exists.
        start.WorkingDirectory <- AppContext.BaseDirectory
        [ "fsi"; "--quiet"; scriptPath ] |> List.iter start.ArgumentList.Add

        use child = Process.Start start
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        child.WaitForExit()

        let lines =
            stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)

        let field (tag: string) =
            lines
            |> Array.tryFind (fun l -> l.StartsWith(tag + "|"))
            |> Option.map (fun l -> l.Substring(tag.Length + 1))
            |> Option.defaultWith (fun () ->
                failwith $"no %s{tag} line; stdout:\n%s{stdout.Result}\nstderr:\n%s{stderr.Result}")

        let stood = field "STOOD"
        test <@ field "BEFORE" = Path.Combine(stood, "before.fs") @>
        test <@ field "MISSING" = $"%s{stood}|true" @>
        test <@ field "ROOTED" = Path.Combine(Path.GetPathRoot stood, "repo", "src", "Lib.fs") @>
    finally
        Directory.Delete(scratch, true)
