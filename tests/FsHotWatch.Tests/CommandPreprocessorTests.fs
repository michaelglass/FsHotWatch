module FsHotWatch.Tests.CommandPreprocessorTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Plugin
open FsHotWatch.CommandPreprocessor
open FsHotWatch.Watcher
open FsHotWatch.Tests.TestHelpers

/// A spec that runs `script` through `sh -c` from `dir`, with `writes` declared.
let private shellSpec (dir: string) (name: string) (script: string) (writes: string list) : Spec =
    { Name = name
      Command = "sh"
      Args = "-c \"" + script.Replace("\"", "\\\"") + "\""
      WorkDir = dir
      Trigger = Trigger.Always
      Writes = writes |> List.map (fun w -> Path.Combine(dir, w))
      Timeout = TimeSpan.FromSeconds 30.0 }

let private run (spec: Spec) (batch: string list) =
    let preprocessor = create spec
    test <@ preprocessor.Name = spec.Name @>

    try
        preprocessor.Process batch spec.WorkDir
    finally
        preprocessor.Dispose()

[<Fact(Timeout = 30000)>]
let ``a declared file the command rewrites is Modified, and its new bytes are on disk`` () =
    withTempDir "cmdpre-write" (fun dir ->
        let target = Path.Combine(dir, "Gen.fs")
        File.WriteAllText(target, "module Gen\nlet old = 1\n")

        let spec =
            shellSpec dir "gen" "printf 'module Gen\\nlet fresh = 2\\n' > Gen.fs" [ "Gen.fs" ]

        match run spec [ Path.Combine(dir, "Trigger.fs") ] with
        | Ok result ->
            test <@ result.Modified = [ target ] @>
            test <@ result.Considered = 1 @>
            test <@ result.Evidence.Contains "sh" && result.Evidence.Contains "exit 0" @>
            test <@ File.ReadAllText(target).Contains "fresh" @>
        | Error reason -> failwith $"expected Ok, got Error %s{reason}")

[<Fact(Timeout = 30000)>]
let ``a declared file the command leaves as it was is not Modified`` () =
    // The attribution is by CONTENT, so a no-op run suppresses nothing: a later real edit
    // to the declared file must not be swallowed as the command's own echo.
    withTempDir "cmdpre-noop" (fun dir ->
        let target = Path.Combine(dir, "Gen.fs")
        File.WriteAllText(target, "module Gen\n")
        let spec = shellSpec dir "gen" "true" [ "Gen.fs" ]

        match run spec [ Path.Combine(dir, "Trigger.fs") ] with
        | Ok result ->
            test <@ result.Modified = [] @>
            test <@ result.Considered = 1 @>
        | Error reason -> failwith $"expected Ok, got Error %s{reason}")

[<Fact(Timeout = 30000)>]
let ``a declared file the command creates is Modified`` () =
    withTempDir "cmdpre-create" (fun dir ->
        let target = Path.Combine(dir, "Gen.fs")
        let spec = shellSpec dir "gen" "printf 'module Gen\\n' > Gen.fs" [ "Gen.fs" ]

        match run spec [ Path.Combine(dir, "Trigger.fs") ] with
        | Ok result -> test <@ result.Modified = [ target ] @>
        | Error reason -> failwith $"expected Ok, got Error %s{reason}")

[<Fact(Timeout = 30000)>]
let ``a file the command rewrites without declaring it is not attributed`` () =
    withTempDir "cmdpre-undeclared" (fun dir ->
        let spec = shellSpec dir "gen" "printf 'module Other\\n' > Other.fs" [ "Gen.fs" ]

        match run spec [ Path.Combine(dir, "Trigger.fs") ] with
        | Ok result ->
            test <@ result.Modified = [] @>
            test <@ File.Exists(Path.Combine(dir, "Other.fs")) @>
        | Error reason -> failwith $"expected Ok, got Error %s{reason}")

[<Fact(Timeout = 30000)>]
let ``a trigger that matches nothing in the batch does not run the command`` () =
    withTempDir "cmdpre-untriggered" (fun dir ->
        let spec =
            { shellSpec dir "gen" "printf 'module Gen\\n' > Gen.fs" [ "Gen.fs" ] with
                Trigger = Trigger.Matching [ FilePattern.parse "*.sql" ] }

        match run spec [ Path.Combine(dir, "src", "Lib.fs") ] with
        | Ok result ->
            test <@ result.Modified = [] @>
            test <@ result.Considered = 0 @>
            test <@ result.Evidence.Contains "not triggered" @>
            test <@ not (File.Exists(Path.Combine(dir, "Gen.fs"))) @>
        | Error reason -> failwith $"expected Ok, got Error %s{reason}")

[<Fact(Timeout = 30000)>]
let ``a trigger that matches a changed file runs the command`` () =
    withTempDir "cmdpre-triggered" (fun dir ->
        let spec =
            { shellSpec dir "gen" "printf 'module Gen\\n' > Gen.fs" [ "Gen.fs" ] with
                Trigger = Trigger.Matching [ FilePattern.parse "*.sql" ] }

        match
            run
                spec
                [ Path.Combine(dir, "src", "Lib.fs")
                  Path.Combine(dir, "migrations", "001.sql") ]
        with
        | Ok result -> test <@ result.Modified = [ Path.Combine(dir, "Gen.fs") ] @>
        | Error reason -> failwith $"expected Ok, got Error %s{reason}")

[<Fact(Timeout = 30000)>]
let ``an empty batch never runs the command, even when the trigger is Always`` () =
    withTempDir "cmdpre-empty" (fun dir ->
        let spec = shellSpec dir "gen" "printf 'module Gen\\n' > Gen.fs" [ "Gen.fs" ]

        match run spec [] with
        | Ok result ->
            test <@ result.Modified = [] @>
            test <@ not (File.Exists(Path.Combine(dir, "Gen.fs"))) @>
        | Error reason -> failwith $"expected Ok, got Error %s{reason}")

[<Fact(Timeout = 30000)>]
let ``a non-zero exit is a refusal naming the exit code and the output`` () =
    withTempDir "cmdpre-exit" (fun dir ->
        let spec = shellSpec dir "gen" "echo regeneration-broke >&2; exit 3" [ "Gen.fs" ]

        match run spec [ Path.Combine(dir, "Trigger.fs") ] with
        | Ok _ -> failwith "a failing command must be a refusal"
        | Error reason ->
            test <@ reason.Contains "exited 3" @>
            test <@ reason.Contains "regeneration-broke" @>)

[<Fact(Timeout = 30000)>]
let ``a command that overruns its timeout is a refusal`` () =
    withTempDir "cmdpre-timeout" (fun dir ->
        let spec =
            { shellSpec dir "gen" "sleep 30" [ "Gen.fs" ] with
                Timeout = TimeSpan.FromSeconds 1.0 }

        match run spec [ Path.Combine(dir, "Trigger.fs") ] with
        | Ok _ -> failwith "an overrunning command must be a refusal"
        | Error reason -> test <@ reason.Contains "timed out" @>)

[<Fact(Timeout = 30000)>]
let ``a command that cannot be started is a refusal`` () =
    withTempDir "cmdpre-nospawn" (fun dir ->
        let spec =
            { shellSpec dir "gen" "true" [ "Gen.fs" ] with
                Command = Path.Combine(dir, "no-such-binary") }

        match run spec [ Path.Combine(dir, "Trigger.fs") ] with
        | Ok _ -> failwith "an unstartable command must be a refusal"
        | Error reason -> test <@ reason.Contains "no-such-binary" @>)

// ---------------------------------------------------------------------------
// Path form: a write is reported the way the batch's watcher names paths.
// ---------------------------------------------------------------------------

/// `dir` reached through a symbolic link, beside it: `<dir>-link -> <dir>`.
let private withLinkedDir (dir: string) (body: string -> 'a) : 'a =
    let link = dir + "-link"
    Directory.CreateSymbolicLink(link, dir) |> ignore

    try
        body link
    finally
        Directory.Delete(link)

[<Fact(Timeout = 30000)>]
let ``realPathOf follows a symbolic link among the ancestors`` () =
    withTempDir "cmdpre-real" (fun dir ->
        let real = realPathOf dir
        Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore

        withLinkedDir dir (fun link ->
            test <@ realPathOf (Path.Combine(link, "src", "Gen.fs")) = Path.Combine(real, "src", "Gen.fs") @>))

[<Fact(Timeout = 30000)>]
let ``a write is reported under the real root when the batch arrived under it`` () =
    // A native watcher reports real paths; a repository configured through a link
    // would otherwise report its write in a form the watcher never uses, so the write's
    // echo would not be suppressed and the command would re-fire on it.
    withTempDir "cmdpre-form" (fun dir ->
        let real = realPathOf dir

        withLinkedDir dir (fun link ->
            let spec = shellSpec link "gen" "printf 'module Gen\\n' > Gen.fs" [ "Gen.fs" ]
            let realBatch = [ Path.Combine(real, "Trigger.fs") ]
            let linkBatch = [ Path.Combine(link, "Trigger.fs") ]

            match run spec realBatch with
            | Ok result -> test <@ result.Modified = [ Path.Combine(real, "Gen.fs") ] @>
            | Error reason -> failwith $"expected Ok, got Error %s{reason}"

            File.Delete(Path.Combine(dir, "Gen.fs"))

            match run spec linkBatch with
            | Ok result -> test <@ result.Modified = [ Path.Combine(link, "Gen.fs") ] @>
            | Error reason -> failwith $"expected Ok, got Error %s{reason}"))

[<Fact(Timeout = 30000)>]
let ``a write outside the repository root keeps its own path whatever form the batch arrived in`` () =
    // Only paths under the configured root have a twin under the real root; a declared
    // write elsewhere (an absolute path into another tree) is reported as written.
    withTempDir "cmdpre-outside" (fun dir ->
        withTempDir "cmdpre-elsewhere" (fun elsewhere ->
            let real = realPathOf dir
            let outside = Path.Combine(elsewhere, "Other.fs")

            withLinkedDir dir (fun link ->
                let spec =
                    { shellSpec
                          link
                          "gen"
                          $"printf 'module Gen\\n' > Gen.fs; printf 'module Other\\n' > '%s{outside}'"
                          [ "Gen.fs" ] with
                        Writes = [ Path.Combine(link, "Gen.fs"); outside ] }

                match run spec [ Path.Combine(real, "Trigger.fs") ] with
                | Ok result -> test <@ result.Modified = [ Path.Combine(real, "Gen.fs"); outside ] @>
                | Error reason -> failwith $"expected Ok, got Error %s{reason}")))

[<Fact(Timeout = 30000)>]
let ``a repository root that is its own real path reports writes as written`` () =
    // No link anywhere in the root: there is no second form for the batch to arrive
    // in, and a write is reported exactly as declared.
    withTempDir "cmdpre-plain" (fun dir ->
        let real = realPathOf dir
        let spec = shellSpec real "gen" "printf 'module Gen\\n' > Gen.fs" [ "Gen.fs" ]

        match run spec [ Path.Combine(real, "Trigger.fs") ] with
        | Ok result -> test <@ result.Modified = [ Path.Combine(real, "Gen.fs") ] @>
        | Error reason -> failwith $"expected Ok, got Error %s{reason}")

[<Fact(Timeout = 30000)>]
let ``a refusal carries the last lines of a long output, not the first`` () =
    withTempDir "cmdpre-tail" (fun dir ->
        let spec =
            shellSpec dir "gen" "for i in $(seq 1 30); do echo line-$i; done; exit 1" [ "Gen.fs" ]

        match run spec [ Path.Combine(dir, "Trigger.fs") ] with
        | Ok _ -> failwith "a failing command must be a refusal"
        | Error reason ->
            test <@ reason.Contains "line-30" && reason.Contains "line-11" @>
            test <@ not (reason.Contains "line-10\n") && not (reason.Contains "line-1\n") @>)
