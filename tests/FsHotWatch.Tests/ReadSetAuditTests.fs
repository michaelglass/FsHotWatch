/// The read-set audit (ADR-059 §35 Change 1b, AUTOMATION-1634): an action that reads a file
/// its key did not declare fails, whether or not that file happened to differ.
///
/// Three layers, each with its mandatory pair of controls (an undeclared read fails; every
/// read declared passes):
///   * the comparison itself, over reads a test hands it;
///   * the strace tracer's parser, over strace output as Linux prints it (runs anywhere);
///   * the real tracers: FCS's file-system shim on a real check (runs anywhere), and strace on
///     a real child process (Linux only: required on the `read-set-audit` CI job, skipped
///     where strace is missing and the job's variable is not set).
module FsHotWatch.Tests.ReadSetAuditTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch
open FsHotWatch.ReadTrace
open FsHotWatch.ReadSetAudit
open FsHotWatch.Tests.TestHelpers

let private opened path = { Path = path; Kind = ReadKind.Open }
let private probed path = { Path = path; Kind = ReadKind.Probe }

let private action name (declared: string list) (reads: Read list) =
    { Action = name
      Declared = Declared.ofFiles declared
      Reads = reads }

// ----------------------------------------------------------------------------------------
// The comparison
// ----------------------------------------------------------------------------------------

[<Fact>]
let ``an action that reads only what its key declared is clean`` () =
    let report =
        audit [] [ action "check B.fs" [ "/r/A.fs"; "/r/B.fs" ] [ opened "/r/A.fs"; opened "/r/B.fs" ] ]

    test <@ List.isEmpty report.Undeclared @>
    test <@ report.Verdict = Verdict.Clean @>
    test <@ exitCode report.Verdict = 0 @>

[<Fact>]
let ``an action that reads a file its key did not declare fails, naming exactly that file`` () =
    let report =
        audit [] [ action "check B.fs" [ "/r/B.fs" ] [ opened "/r/A.fs"; opened "/r/B.fs"; opened "/r/A.fs" ] ]

    test
        <@
            report.Undeclared = [ { Action = "check B.fs"
                                    Path = "/r/A.fs"
                                    Kind = ReadKind.Open } ]
        @>

    test <@ report.Verdict = Verdict.Undeclared @>
    test <@ exitCode report.Verdict = 1 @>

[<Fact>]
let ``an undeclared probe fails too: whether a file exists is an input`` () =
    let report =
        audit [] [ action "run" [ "/r/a" ] [ opened "/r/a"; probed "/r/maybe.json" ] ]

    test <@ report.Undeclared |> List.map (fun u -> u.Path, u.Kind) = [ "/r/maybe.json", ReadKind.Probe ] @>

[<Fact>]
let ``an action whose tracer saw no read is no answer, never clean`` () =
    let report = audit [] [ action "check B.fs" [ "/r/B.fs" ] [] ]
    test <@ report.Verdict = Verdict.Vacuous @>
    test <@ exitCode report.Verdict = 3 @>
    test <@ (audit [] []).Verdict = Verdict.Vacuous @>

[<Fact>]
let ``a declared directory declares what is beneath it, and not a sibling that shares its prefix`` () =
    let declared =
        Declared.ofFiles [ "/r/B.fs" ] |> Declared.withDirectories [ "/sdk/10.0.400" ]

    let report =
        audit
            []
            [ { Action = "check B.fs"
                Declared = declared
                Reads =
                  [ opened "/r/B.fs"
                    opened "/sdk/10.0.400/ref/System.Runtime.dll"
                    opened "/sdk/10.0.4001/x.dll" ] } ]

    test <@ report.Undeclared |> List.map _.Path = [ "/sdk/10.0.4001/x.dll" ] @>

[<Fact>]
let ``an exempted read passes, and the report counts it under its exemption`` () =
    let os =
        Exemption.underDirectories "os-image" "identified by the runner image" [ "/usr/lib" ]

    let report =
        audit [ os ] [ action "run" [ "/r/a" ] [ opened "/r/a"; opened "/usr/lib/libc.so.6" ] ]

    test <@ report.Verdict = Verdict.Clean @>
    test <@ report.Exempted = [ "os-image", 1 ] @>

[<Fact>]
let ``a check may probe beside a declared reference, but an open there is an input`` () =
    let dll = "/sdk/ref/net10.0/System.Runtime.dll"

    let report =
        audit
            [ CheckDeclared.referenceDirectoryProbes ]
            [ action
                  "check B.fs"
                  [ "/r/B.fs"; dll ]
                  [ opened "/r/B.fs"
                    opened dll
                    probed "/sdk/ref/net10.0"
                    probed "/sdk/ref/net10.0/System.Runtime.xml"
                    probed "/sdk/ref/net10.0/Facades"
                    opened "/sdk/ref/net10.0/System.Other.dll"
                    probed "/sdk/ref/other/x.xml" ] ]

    test
        <@
            report.Undeclared |> List.map (fun u -> u.Path, u.Kind) = [ ("/sdk/ref/net10.0/System.Other.dll",
                                                                         ReadKind.Open)
                                                                        ("/sdk/ref/other/x.xml", ReadKind.Probe) ]
        @>

    test <@ report.Exempted = [ "reference-directory-probes", 3 ] @>

[<Fact>]
let ``the report names every action, its read count and what it read undeclared`` () =
    let report =
        audit
            []
            [ action "a" [ "/r/a" ] [ opened "/r/a" ]
              action "b" [ "/r/b" ] [ opened "/r/b"; opened "/r/c" ] ]

    test <@ report.Actions |> List.map (fun a -> a.Action, a.Reads, a.Undeclared) = [ "a", 1, 0; "b", 2, 1 ] @>
    let json = toJson report
    test <@ json.Contains "\"verdict\": \"undeclared\"" @>
    test <@ json.Contains "\"/r/c\"" @>
    let lines = render report
    test <@ lines |> List.exists (fun l -> l.Contains "UNDECLARED" && l.Contains "/r/c") @>

// ----------------------------------------------------------------------------------------
// strace's output, parsed
// ----------------------------------------------------------------------------------------

let private parse = Strace.parse "/work"

[<Fact>]
let ``strace: a successful read-only open is a read, a failed one a probe, a write none`` () =
    let reads =
        parse
            [ "101   openat(AT_FDCWD, \"/r/a.txt\", O_RDONLY) = 3"
              "101   openat(AT_FDCWD, \"/r/missing\", O_RDONLY|O_CLOEXEC) = -1 ENOENT (No such file or directory)"
              "101   openat(AT_FDCWD, \"/r/out.txt\", O_WRONLY|O_CREAT|O_TRUNC, 0666) = 4"
              "101   openat(AT_FDCWD, \"/r/rw.db\", O_RDWR) = 5" ]

    test <@ reads = [ opened "/r/a.txt"; probed "/r/missing"; opened "/r/rw.db" ] @>

[<Fact>]
let ``strace: stats and access are probes, an exec reads its program, a relative path resolves`` () =
    let reads =
        parse
            [ "7 execve(\"/usr/bin/cat\", [\"cat\", \"a\"], 0x7ffd /* 3 vars */) = 0"
              "7 newfstatat(AT_FDCWD, \"rel/x\", {st_mode=S_IFREG|0644, st_size=3, ...}, 0) = 0"
              "7 access(\"/etc/ld.so.preload\", R_OK) = -1 ENOENT (No such file or directory)"
              "7 statx(AT_FDCWD, \"/r/y\", AT_STATX_SYNC_AS_STAT, STATX_ALL, {stx_mask=STATX_BASIC_STATS}) = 0" ]

    test
        <@
            reads = [ opened "/usr/bin/cat"
                      probed "/work/rel/x"
                      probed "/etc/ld.so.preload"
                      probed "/r/y" ]
        @>

[<Fact>]
let ``strace: a directory fd names its directory, and a chdir moves where that process resolves`` () =
    let reads =
        parse
            [ "9 openat(3</r/dir>, \"in.txt\", O_RDONLY) = 4"
              "9 chdir(\"/r/sub\") = 0"
              "9 openat(AT_FDCWD, \"x.fs\", O_RDONLY) = 5"
              "10 openat(AT_FDCWD, \"x.fs\", O_RDONLY) = 5" ]

    test <@ reads = [ opened "/r/dir/in.txt"; opened "/r/sub/x.fs"; opened "/work/x.fs" ] @>

[<Fact>]
let ``strace: a call another process interrupted is read when it resumes`` () =
    let reads =
        parse
            [ "11 openat(AT_FDCWD, \"/r/slow\", O_RDONLY <unfinished ...>"
              "12 openat(AT_FDCWD, \"/r/fast\", O_RDONLY) = 3"
              "11 <... openat resumed>) = 4"
              "12 +++ exited with 0 +++" ]

    test <@ reads = [ opened "/r/fast"; opened "/r/slow" ] @>

[<Fact>]
let ``strace: an escaped quote in a path is part of the path`` () =
    let reads = parse [ "1 openat(AT_FDCWD, \"/r/a \\\"b\\\".txt\", O_RDONLY) = 3" ]
    test <@ reads = [ opened "/r/a \"b\".txt" ] @>

[<Fact>]
let ``strace: escapes, directories, failed execs and lines that are not calls`` () =
    let reads =
        parse
            [ "1 openat(AT_FDCWD, \"/r/new\\nline\\ttab\", O_RDONLY) = 3</r/new\\nline\\ttab>"
              "1 openat(AT_FDCWD, \"/r/dir\", O_RDONLY|O_NONBLOCK|O_CLOEXEC|O_DIRECTORY) = 4</r/dir>"
              "1 execve(\"/nope/tool\", [\"tool\"], 0x0 /* 0 vars */) = -1 ENOENT (No such file or directory)"
              "1 chdir(\"/missing\") = -1 ENOENT (No such file or directory)"
              "1 openat(AT_FDCWD, \"rel\", O_RDONLY) = 5"
              "1 openat(AT_FDCWD, \"/r/unterminated, O_RDONLY) = 3"
              "1 getcwd(\"/r\", 4096) = 3"
              "1 <... openat resumed>) = 3"
              "--- SIGCHLD {si_signo=SIGCHLD} ---"
              "openat(AT_FDCWD, \"/r/nopid\", O_RDONLY) = 3" ]

    test
        <@
            reads = [ opened "/r/new\nline\ttab"
                      probed "/r/dir"
                      probed "/nope/tool"
                      opened "/work/rel"
                      opened "/r/nopid" ]
        @>

[<Fact>]
let ``strace: tracing is offered only where strace is on PATH on Linux`` () =
    if OperatingSystem.IsLinux() then
        let onPath =
            Environment.GetEnvironmentVariable("PATH").Split(Path.PathSeparator)
            |> Array.exists (fun d -> d <> "" && File.Exists(Path.Combine(d, "strace")))

        test <@ Strace.available () = onPath @>
    else
        test <@ not (Strace.available ()) @>

[<Fact>]
let ``every verdict renders and serializes, with the exemptions that passed reads`` () =
    let os = Exemption.underDirectories "os-image" "the image" [ "/usr/lib" ]
    let unused = Exemption.underDirectories "unused" "never matches" [ "/nowhere" ]

    let clean =
        audit [ os; unused ] [ action "a" [ "/r/a" ] [ opened "/r/a"; opened "/usr/lib/x.so" ] ]

    test <@ clean.Exempted = [ "os-image", 1 ] @>
    test <@ (toJson clean).Contains "\"verdict\": \"clean\"" @>
    test <@ (toJson clean).Contains "\"os-image\": 1" @>
    test <@ render clean |> List.exists (fun l -> l.Contains "exempt (os-image): 1") @>
    test <@ render clean |> List.last |> (fun l -> l.Contains "CLEAN") @>

    let vacuous = audit [] [ action "a" [ "/r/a" ] [] ]
    test <@ (toJson vacuous).Contains "\"verdict\": \"vacuous\"" @>
    test <@ render vacuous |> List.last |> (fun l -> l.Contains "VACUOUS") @>

    let probe = audit [] [ action "a" [ "/r/a" ] [ probed "/r/b" ] ]
    test <@ (toJson probe).Contains "\"kind\": \"probe\"" @>

[<Fact>]
let ``a check declares a non-F# reference's output, and a file outside its project only itself after the project`` () =
    let options =
        { FSharpProjectOptions.ProjectFileName = "/r/P.fsproj"
          ProjectId = None
          SourceFiles = [| "/r/A.fs"; "/r/B.fs" |]
          OtherOptions = [| "-r:/r/lib/C.dll"; "--debug" |]
          ReferencedProjects =
            [| FSharpReferencedProject.ILModuleReference(
                   "/r/lib/D.dll",
                   (fun () -> DateTime.MinValue),
                   (fun () -> failwith "never read")
               ) |]
          IsIncompleteTypeCheckEnvironment = false
          UseScriptResolutionRules = false
          LoadTime = DateTime.MinValue
          UnresolvedReferences = None
          OriginalLoadReferences = []
          Stamp = None }

    let declared = CheckDeclared.ofCheck options "/r/Script.fs"
    test <@ declared.Files.Contains "/r/lib/C.dll" && declared.Files.Contains "/r/lib/D.dll" @>
    test <@ declared.Files.Contains "/r/A.fs" && declared.Files.Contains "/r/B.fs" @>
    test <@ declared.Files.Contains "/r/Script.fs" @>

// ----------------------------------------------------------------------------------------
// strace on a real child process (Linux)
// ----------------------------------------------------------------------------------------

/// Run only where strace can trace. The `read-set-audit` CI job sets the variable, so a
/// missing strace there fails instead of skipping: a skip would read as a pass.
let private requireStrace () =
    match Strace.available () with
    | true -> ()
    | false when Environment.GetEnvironmentVariable Strace.RequiredVariable = "required" ->
        failwith $"%s{Strace.RequiredVariable}=required, but strace is not on PATH (Linux only)"
    | false -> Assert.Skip "strace is not on PATH (it traces Linux processes only)"

let private straceFixture (dir: string) =
    let a = Path.Combine(dir, "a.txt")
    let b = Path.Combine(dir, "b.txt")
    File.WriteAllText(a, "a")
    File.WriteAllText(b, "b")
    a, b

/// `sh -c 'cat a; cat b'`: the reads happen in grandchildren, which only `-f` follows.
let private catBoth (dir: string) (a: string) (b: string) =
    Strace.trace (TimeSpan.FromSeconds 50.0) dir "sh" [ "-c"; $"cat '%s{a}' > /dev/null; cat '%s{b}' > /dev/null" ]

[<Fact(Timeout = 60000)>]
let ``strace: a child process that reads an undeclared file fails the audit`` () =
    requireStrace ()

    withTempDir "readset-strace-undeclared" (fun dir ->
        let a, b = straceFixture dir

        match catBoth dir a b with
        | Error e -> failwith e
        | Ok traced ->
            test <@ traced.ExitCode = 0 @>

            let report =
                audit Exemption.linuxImage [ action "sh: cat a; cat b" [ a; dir ] traced.Reads ]

            test <@ report.Undeclared |> List.map _.Path = [ b ] @>
            test <@ report.Verdict = Verdict.Undeclared @>)

[<Fact(Timeout = 60000)>]
let ``strace: a child process that reads only declared files passes the audit`` () =
    requireStrace ()

    withTempDir "readset-strace-declared" (fun dir ->
        let a, b = straceFixture dir

        match catBoth dir a b with
        | Error e -> failwith e
        | Ok traced ->
            test <@ traced.ExitCode = 0 @>
            test <@ traced.Reads |> List.exists (fun r -> r.Path = a) @>
            test <@ traced.Reads |> List.exists (fun r -> r.Path = b) @>

            let report =
                audit Exemption.linuxImage [ action "sh: cat a; cat b" [ a; b; dir ] traced.Reads ]

            test <@ List.isEmpty report.Undeclared @>
            test <@ report.Verdict = Verdict.Clean @>)

// ----------------------------------------------------------------------------------------
// FCS's file-system shim on a real check
// ----------------------------------------------------------------------------------------

/// Library A (A1.fs, A2.fs) and B (B1.fs, B2.fs), B referencing A as an F# project.
type private Tree =
    { Root: string
      A: FSharpProjectOptions
      B: FSharpProjectOptions }

let private treeIn (root: string) =
    Directory.CreateDirectory root |> ignore
    let path name = Path.Combine(root, name)
    File.WriteAllLines(path "A1.fs", [| "module A1"; "let v : int = 1" |])
    File.WriteAllLines(path "A2.fs", [| "module A2"; "let w : int = A1.v + 1" |])
    File.WriteAllLines(path "B1.fs", [| "module B1"; "let y : int = A2.w" |])
    File.WriteAllLines(path "B2.fs", [| "module B2"; "let z : int = B1.y + A1.v" |])
    let script = path "Base.fsx"
    File.WriteAllLines(script, [| "let placeholder = 0" |])
    let checker = FsHotWatch.Daemon.Daemon.createChecker ()

    let baseOptions, _ =
        scriptProjectOptions (checker, script, SourceText.ofString (File.ReadAllText script))
        |> Async.RunSynchronously

    let framework =
        baseOptions.OtherOptions |> Array.filter (fun o -> not (o.EndsWith ".fsx"))

    let aDll = Path.Combine(root, "A", "bin", "A.dll")
    Directory.CreateDirectory(Path.GetDirectoryName aDll) |> ignore

    let project name files refs =
        { baseOptions with
            ProjectFileName = path $"%s{name}.fsproj"
            SourceFiles = files |> List.map path |> Array.ofList
            OtherOptions = Array.append framework [| for out, _ in refs -> $"-r:%s{out}" |]
            ReferencedProjects = [| for out, o in refs -> FSharpReferencedProject.FSharpReference(out, o) |]
            UseScriptResolutionRules = false
            ProjectId = None
            Stamp = None }

    let a = project "A" [ "A1.fs"; "A2.fs" ] []

    { Root = root
      A = a
      B = project "B" [ "B1.fs"; "B2.fs" ] [ aDll, a ] }

/// Check `file` of `options` with a checker of its own, under FCS's file-system shim.
let private tracedCheck (tree: Tree) (options: FSharpProjectOptions) (file: string) =
    let checker = FsHotWatch.Daemon.Daemon.createChecker ()
    let pipeline = CheckPipeline.CheckPipeline(checker, repoRoot = tree.Root)
    pipeline.RegisterProject(tree.A.ProjectFileName, tree.A)
    pipeline.RegisterProject(tree.B.ProjectFileName, tree.B)

    let result, reads =
        ReadTrace.trace (fun () ->
            pipeline.CheckFile(FsHotWatch.Events.AbsFilePath.create file)
            |> Async.RunSynchronously)

    test <@ result.IsSome @>
    reads

[<Collection(ReadTrace.CollectionName)>]
type FcsShimTests() =

    [<Fact(Timeout = 240000)>]
    member _.``fcs: a check reads only what its check-record key declares``() =
        withTempDir "readset-fcs-declared" (fun dir ->
            let tree = treeIn (Path.Combine(dir, "repo"))
            let b2 = tree.B.SourceFiles[1]
            let reads = tracedCheck tree tree.B b2
            let path name = Path.Combine(tree.Root, name)
            // The check of B2 typed B1 and A's sources: the tracer saw them read.
            test <@ reads |> List.exists (fun r -> r.Path = path "B1.fs" && r.Kind = ReadKind.Open) @>
            test <@ reads |> List.exists (fun r -> r.Path = path "A1.fs" && r.Kind = ReadKind.Open) @>

            let report =
                audit
                    (CheckDeclared.exemptionsFor tree.B)
                    [ { Action = "check B2.fs"
                        Declared = CheckDeclared.ofCheck tree.B b2
                        Reads = reads } ]

            test <@ List.isEmpty report.Undeclared @>
            test <@ report.Verdict = Verdict.Clean @>)

    [<Fact(Timeout = 240000)>]
    member _.``fcs: a key that leaves out a file the check reads fails, naming exactly that file``() =
        // The planted bug: the declaration a key that ignored B1.fs would make. The check
        // still reads B1.fs, so the audit names it whatever B1.fs holds.
        withTempDir "readset-fcs-planted" (fun dir ->
            let tree = treeIn (Path.Combine(dir, "repo"))
            let b2 = tree.B.SourceFiles[1]
            let b1 = tree.B.SourceFiles[0]
            let reads = tracedCheck tree tree.B b2
            let planted = CheckDeclared.ofCheck tree.B b2 |> Declared.without b1

            let report =
                audit
                    (CheckDeclared.exemptionsFor tree.B)
                    [ { Action = "check B2.fs"
                        Declared = planted
                        Reads = reads } ]

            test <@ report.Undeclared |> List.map _.Path |> List.distinct = [ b1 ] @>
            test <@ report.Verdict = Verdict.Undeclared @>)

    [<Fact(Timeout = 240000)>]
    member _.``fcs: a file after the checked one in compile order is not declared, and not read``() =
        withTempDir "readset-fcs-later" (fun dir ->
            let tree = treeIn (Path.Combine(dir, "repo"))
            let b1 = tree.B.SourceFiles[0]
            let b2 = tree.B.SourceFiles[1]
            test <@ not ((CheckDeclared.ofCheck tree.B b1).Files.Contains b2) @>
            let reads = tracedCheck tree tree.B b1

            let report =
                audit
                    (CheckDeclared.exemptionsFor tree.B)
                    [ { Action = "check B1.fs"
                        Declared = CheckDeclared.ofCheck tree.B b1
                        Reads = reads } ]

            test <@ report.Verdict = Verdict.Clean @>)

    [<Fact>]
    member _.``fcs: every FCS shim read is recorded, and a read outside a trace is not``() =
        withTempDir "readset-shim" (fun dir ->
            let file = Path.Combine(dir, "f.txt")
            File.WriteAllText(file, "x")
            ReadTrace.opened file

            let fs () =
                FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem

            let (), reads =
                ReadTrace.trace (fun () ->
                    use _ = fs().OpenFileForReadShim file
                    fs().FileExistsShim file |> ignore
                    fs().DirectoryExistsShim dir |> ignore
                    fs().GetLastWriteTimeShim file |> ignore
                    fs().EnumerateFilesShim(dir, "*") |> Seq.toList |> ignore
                    fs().EnumerateDirectoriesShim dir |> Seq.toList |> ignore
                    ReadTrace.opened ""
                    ReadTrace.opened "bad\000path")

            test
                <@
                    reads = [ { Path = file; Kind = ReadKind.Open }
                              { Path = file; Kind = ReadKind.Probe }
                              { Path = dir; Kind = ReadKind.Probe }
                              { Path = file; Kind = ReadKind.Probe }
                              { Path = dir; Kind = ReadKind.Probe }
                              { Path = dir; Kind = ReadKind.Probe }
                              { Path = "bad\000path"
                                Kind = ReadKind.Open } ]
                @>)

    [<Fact>]
    member _.``fcs: a trace inside a trace is refused, and the shim is restored after one``() =
        let before = FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem

        let inner () =
            ReadTrace.trace (fun () -> ReadTrace.trace ignore |> ignore) |> ignore

        raises<InvalidOperationException> <@ inner () @>
        test <@ obj.ReferenceEquals(FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem, before) @>

/// A trace owns the process: its tests run alone.
[<CollectionDefinition(ReadTrace.CollectionName, DisableParallelization = true)>]
type ReadTraceCollection() = class end
