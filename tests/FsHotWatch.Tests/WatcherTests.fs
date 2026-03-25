module FsHotWatch.Tests.WatcherTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Watcher

// macOS kqueue-based FileSystemWatcher is unreliable — use polling watcher
do
    if
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.OSX
        )
    then
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1")

[<Fact>]
let ``watcher detects new fs file in src`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshotwatch-test-{Guid.NewGuid():N}")
    let srcDir = Path.Combine(tmpDir, "src")
    Directory.CreateDirectory(srcDir) |> ignore
    let mutable changes = []
    let onChange change = changes <- change :: changes

    use watcher = FileWatcher.create tmpDir onChange
    Thread.Sleep(2000)
    let testFile = Path.Combine(srcDir, "Test.fs")
    File.WriteAllText(testFile, "module Test")
    // Touch the file again to ensure the watcher sees it (macOS kqueue can miss rapid creates)
    Thread.Sleep(500)
    File.SetLastWriteTimeUtc(testFile, System.DateTime.UtcNow)
    Thread.Sleep(5000)
    test <@ changes.Length >= 1 @>
    Directory.Delete(tmpDir, true)

[<Fact>]
let ``watcher ignores obj directory`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshotwatch-test-{Guid.NewGuid():N}")
    let objDir = Path.Combine(tmpDir, "src", "obj")
    Directory.CreateDirectory(objDir) |> ignore
    let mutable changes = []
    let onChange change = changes <- change :: changes

    use watcher = FileWatcher.create tmpDir onChange
    Thread.Sleep(2000)
    File.WriteAllText(Path.Combine(objDir, "Generated.fs"), "module Gen")
    Thread.Sleep(5000)
    test <@ changes |> List.isEmpty @>
    Directory.Delete(tmpDir, true)

[<Fact>]
let ``watcher classifies fsproj as ProjectChanged`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshotwatch-test-{Guid.NewGuid():N}")
    let srcDir = Path.Combine(tmpDir, "src")
    Directory.CreateDirectory(srcDir) |> ignore
    let mutable changes = []
    let onChange change = changes <- change :: changes

    use watcher = FileWatcher.create tmpDir onChange
    Thread.Sleep(2000)
    let testFile = Path.Combine(srcDir, "Test.fsproj")
    File.WriteAllText(testFile, "<Project/>")
    Thread.Sleep(500)
    File.SetLastWriteTimeUtc(testFile, System.DateTime.UtcNow)
    Thread.Sleep(5000)
    test <@ changes |> List.exists (fun c -> match c with ProjectChanged _ -> true | _ -> false) @>
    Directory.Delete(tmpDir, true)
