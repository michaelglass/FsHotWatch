module FxTests.AttributionTests

open System
open System.Diagnostics
open System.Threading.Tasks
open Xunit
open FxLib

type SharedFixture() =
    member val Seed = Logic.sumPoint { X = 1; Y = 1 }

type ClassA(fixture: SharedFixture) =
    interface IClassFixture<SharedFixture>

    [<Fact>]
    member _.``a sync area``() =
        Assert.Equal(12.0, Logic.area (Circle 2.0))

    [<Fact>]
    member _.``a async task turn``() =
        task {
            do! Task.Yield()
            Assert.Equal(South, Logic.turn East)
        }

    [<Fact>]
    member _.``a Task.Run describe``() =
        task {
            let! s = Task.Run(fun () -> Logic.describe (box (Dog())))
            Assert.Equal("dog woof", s)
        }

type ClassB() =
    [<Fact>]
    member _.``b async block colorCode``() =
        async {
            do! Async.Sleep 1
            Assert.Equal(23, Logic.colorCode (Blue 3))
        }
        |> Async.StartAsTask

    [<Fact>]
    member _.``b Async.Parallel sval``() =
        let r =
            [ async { return Logic.sval (SOk 1) }; async { return Logic.sval (SErr "ab") } ]
            |> Async.Parallel
            |> Async.RunSynchronously

        Assert.Equal<int[]>([| 1; 2 |], r)

    [<Theory>]
    [<InlineData(1)>]
    [<InlineData(20)>]
    member _.``b theory aboveThreshold``(x: int) =
        Assert.Equal(x > 42, Logic.aboveThreshold x)

type ClassC() =
    [<Fact>]
    member _.``c reads a repo file``() =
        let root = Environment.GetEnvironmentVariable "TESTPRUNE_TRACE_REPO_ROOT"

        if not (String.IsNullOrEmpty root) then
            Assert.NotEmpty(Logic.readRepoFile (IO.Path.Combine(root, "global.json")))

    [<Fact>]
    member _.``c starts a child process``() =
        let psi =
            ProcessStartInfo("/bin/echo", "hi", UseShellExecute = false, RedirectStandardOutput = true)

        use p = Process.Start psi
        p.WaitForExit()
        Assert.Equal(0, p.ExitCode)
