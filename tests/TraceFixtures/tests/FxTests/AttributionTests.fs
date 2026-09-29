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

    // Row display names the isolation audit must still select one at a time: xUnit's
    // display-name filter reads `*` as a wildcard and rejects one anywhere but the ends.
    [<Theory>]
    [<InlineData("f (p: int * string) = \"p\"")>]
    [<InlineData("*lead and trail*")>]
    member _.``b theory row names``(s: string) =
        Assert.True(Logic.colorCode (Cyan s) > 0)

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

type ClassD() =
    [<Fact>]
    member _.``d reads a module value``() =
        Assert.True(Settings.toolConfigLines () > 0)

type ClassE() =
    [<Fact>]
    member _.``e first licence reader``() =
        Assert.Contains("License", SuiteFiles.licence)

    [<Fact>]
    member _.``e second licence reader``() =
        Assert.Contains("License", SuiteFiles.licence)

type ClassF() =
    [<Fact>]
    member _.``f generic union, class and method``() =
        let described =
            [ Generics.fromOption (Some 1)
              Generics.fromOption None
              Generics.CouldNotRead "unreadable" ]
            |> List.map Generics.describe

        Assert.Equal<string list>([ "found"; "absent"; "unreadable" ], described)
        let swapped: Generics.Either<string, int> = Generics.swap (Generics.Left 1)
        Assert.Equal(Generics.Right 1, swapped)
        Assert.Equal("2", Crate(2).Map(string).Item)
