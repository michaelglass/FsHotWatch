module FsHotWatch.Tests.WatcherTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Watcher

[<Fact>]
let ``watcher detects new fs file in src`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshotwatch-test-{Guid.NewGuid():N}")
    let srcDir = Path.Combine(tmpDir, "src")
    Directory.CreateDirectory(srcDir) |> ignore
    let mutable changes = []
    let onChange change = changes <- change :: changes

    use watcher = FileWatcher.create tmpDir onChange
    Thread.Sleep(300)
    File.WriteAllText(Path.Combine(srcDir, "Test.fs"), "module Test")
    Thread.Sleep(1000)
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
    Thread.Sleep(300)
    File.WriteAllText(Path.Combine(objDir, "Generated.fs"), "module Gen")
    Thread.Sleep(1000)
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
    Thread.Sleep(300)
    File.WriteAllText(Path.Combine(srcDir, "Test.fsproj"), "<Project/>")
    Thread.Sleep(1000)
    test <@ changes |> List.exists (fun c -> match c with ProjectChanged _ -> true | _ -> false) @>
    Directory.Delete(tmpDir, true)
