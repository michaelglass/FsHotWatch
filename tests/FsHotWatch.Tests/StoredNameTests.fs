module FsHotWatch.Tests.StoredNameTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.StoredName
open FsHotWatch.Tests.TestHelpers

/// A listing fallback that records whether it was consulted.
let private listingAnswering (answer: string option) =
    let asked = ref 0

    let listing (_: string) (_: string) =
        asked.Value <- asked.Value + 1
        answer

    listing, asked

let private nothingExists (_: string) = false

// === every source, on every platform ===

[<Fact(Timeout = 15000)>]
let ``a native answer is taken as it is, and the directory is never listed`` () =
    let listing, asked = listingAnswering (Some "Listed")

    let native answer = Source.Native(fun _ -> answer)

    test <@ lookupWith (native (Answer.Stored "Repo")) listing nothingExists "/d" "repo" = Some "Repo" @>
    test <@ lookupWith (native Answer.Absent) listing nothingExists "/d" "repo" = None @>
    test <@ asked.Value = 0 @>

[<Fact(Timeout = 15000)>]
let ``a native lookup that cannot answer falls back to the listing`` () =
    let listing, asked = listingAnswering (Some "Listed")

    test <@ lookupWith (Source.Native(fun _ -> Answer.Unanswered)) listing nothingExists "/d" "repo" = Some "Listed" @>
    test <@ asked.Value = 1 @>

[<Fact(Timeout = 15000)>]
let ``the native lookup is asked about the entry itself`` () =
    let listing, _ = listingAnswering None
    let mutable askedAbout = ""

    let ask path =
        askedAbout <- path
        Answer.Absent

    lookupWith (Source.Native ask) listing nothingExists "/d" "repo" |> ignore
    test <@ askedAbout = Path.Combine("/d", "repo") @>

[<Fact(Timeout = 15000)>]
let ``as given, an existing entry keeps the name it was asked by and a missing one is absent`` () =
    let listing, asked = listingAnswering (Some "Listed")
    let exists (path: string) = path = Path.Combine("/d", "Repo")

    test <@ lookupWith Source.AsGiven listing exists "/d" "Repo" = Some "Repo" @>
    test <@ lookupWith Source.AsGiven listing exists "/d" "repo" = None @>
    test <@ asked.Value = 0 @>

[<Fact(Timeout = 15000)>]
let ``by listing, the listing decides`` () =
    let listing, asked = listingAnswering (Some "Listed")
    test <@ lookupWith Source.Listing listing nothingExists "/d" "repo" = Some "Listed" @>
    test <@ asked.Value = 1 @>

[<Fact(Timeout = 15000)>]
let ``the listing names every entry of a directory`` () =
    withTempDir "stored-listing" (fun dir ->
        File.WriteAllText(Path.Combine(dir, "File.fs"), "")
        Directory.CreateDirectory(Path.Combine(dir, "Sub")) |> ignore
        test <@ listNames dir |> Seq.sort |> List.ofSeq = [ "File.fs"; "Sub" ] @>)

// === this platform ===

[<Fact(Timeout = 15000)>]
let ``macOS asks the entry, Linux takes the name as given, and anything else lists`` () =
    let expected =
        if OperatingSystem.IsMacOS() then "native"
        elif OperatingSystem.IsLinux() then "as given"
        else "listing"

    let actual =
        match current with
        | Source.Native _ -> "native"
        | Source.AsGiven -> "as given"
        | Source.Listing -> "listing"

    test <@ actual = expected @>

[<Fact(Timeout = 15000)>]
let ``macOS getattrlist names the stored spelling, reports a missing entry, and gives up on an unsearchable parent``
    ()
    =
    if not (OperatingSystem.IsMacOS()) then
        Assert.Skip "getattrlist is macOS's"
    else
        withTempDir "stored-name" (fun dir ->
            Directory.CreateDirectory(Path.Combine(dir, "MyRepo")) |> ignore
            test <@ askMacOS (Path.Combine(dir, "myrepo")) = Answer.Stored "MyRepo" @>
            test <@ askMacOS (Path.Combine(dir, "missing")) = Answer.Absent @>

            // A parent the process may not search: the volume will not say, which is not
            // the same as the entry being absent.
            let locked = Path.Combine(dir, "locked")
            Directory.CreateDirectory(Path.Combine(locked, "inside")) |> ignore
            File.SetUnixFileMode(locked, UnixFileMode.None)

            try
                test <@ askMacOS (Path.Combine(locked, "inside")) = Answer.Unanswered @>
            finally
                File.SetUnixFileMode(
                    locked,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ))
