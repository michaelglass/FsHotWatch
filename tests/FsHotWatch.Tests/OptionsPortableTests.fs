/// Options records that replay across checkouts, in SHADOW (AUTOMATION-1602 (b)): beside the
/// root-keyed records, an evaluation is filed with the checkout root as a token, and a later
/// evaluation in another checkout of the same tree logs what the portable record WOULD have
/// replayed and whether it equals what was evaluated. Shadow never serves a replay.
///
/// As in `OptionsRecordsTests`, the design-time half is a counting stand-in and the inputs
/// are listed by a real MSBuild evaluation of a real tree.
module FsHotWatch.Tests.OptionsPortableTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open Ionide.ProjInfo
open FsHotWatch
open FsHotWatch.OptionsRecords
open FsHotWatch.Tests.TestHelpers

/// `dir` as the file system spells it: a scratch directory under a symlinked temp directory
/// (macOS: /var -> /private/var) is not a portable root until resolved.
let private canonical (dir: string) =
    match RepositoryIdentity.canonicalize dir with
    | Ok path -> path.Value
    | Error e -> failwith $"%A{e}"

// --- the root token ---

[<Fact(Timeout = 15000)>]
let ``the root is replaced only where it starts and ends at a boundary, and case matters`` () =
    let t = PathToken.tokenize "/w/a"
    test <@ t "-r:/w/a/x.dll" = "-r:${repo}/x.dll" @>
    test <@ t "/w/a" = "${repo}" @>
    test <@ t "--doc:/w/a/obj/x.xml" = "--doc:${repo}/obj/x.xml" @>
    test <@ t "a;/w/a/x;/w/a" = "a;${repo}/x;${repo}" @>
    test <@ t "\"/w/a\"" = "\"${repo}\"" @>
    test <@ t "(/w/a)" = "(${repo})" @>
    test <@ t "[/w/a]" = "[${repo}]" @>
    test <@ t "x=/w/a/y" = "x=${repo}/y" @>
    test <@ t "@/w/a/rsp" = "@${repo}/rsp" @>
    test <@ t "a|/w/a|b" = "a|${repo}|b" @>
    test <@ t "a, /w/a/x" = "a, ${repo}/x" @>
    test <@ t "a\t/w/a/x" = "a\t${repo}/x" @>
    test <@ t "'/w/a'" = "'${repo}'" @>
    // Not at a right boundary: another checkout whose name starts with this one's.
    test <@ t "/w/ab/x" = "/w/ab/x" @>
    test <@ t "/w/a-old/x.dll" = "/w/a-old/x.dll" @>
    // Not at a left boundary: the root's spelling inside some other path.
    test <@ t "/v/w/a/x" = "/v/w/a/x" @>
    test <@ t "/w/a/w/a/x" = "${repo}/w/a/x" @>
    // Ordinal: another case is another path.
    test <@ t "/W/A/x" = "/W/A/x" @>
    test <@ t "" = "" @>

[<Fact(Timeout = 15000)>]
let ``a tokenized string rebinds to another root of any length`` () =
    let cases =
        [ "-r:/w/a/x.dll"; "/w/a"; "a;/w/a/x;/w/ab/y"; "/v/w/a/x"; "--define:DEBUG" ]

    for s in cases do
        let tokenized = PathToken.tokenize "/w/a" s

        let expected =
            PathToken.tokenize "/w/a" s
            |> fun tok -> tok.Replace(PathToken.Token, "/w/a-much-longer-root")

        test <@ PathToken.detokenize "/w/a-much-longer-root" tokenized = expected @>
        test <@ PathToken.detokenize "/w/a" tokenized = s @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``only a full, canonical, non-trivial root on macOS or Linux is portable`` () =
    withTempDir "portable-root" (fun dir ->
        let root = canonical dir
        test <@ PathToken.portableRoot root = Ok root @>
        test <@ Result.isError (PathToken.portableRoot (root + "/")) @>
        test <@ Result.isError (PathToken.portableRoot "/") @>
        test <@ Result.isError (PathToken.portableRoot "/single") @>
        test <@ Result.isError (PathToken.portableRoot "") @>
        test <@ Result.isError (PathToken.portableRoot (Path.Combine(root, "not-there"))) @>

        let link = Path.Combine(root, "link")
        let target = Path.Combine(root, "target")
        Directory.CreateDirectory target |> ignore
        Directory.CreateSymbolicLink(link, target) |> ignore
        test <@ Result.isError (PathToken.portableRoot link) @>
        test <@ Result.isError (PathToken.portableRoot (root.ToUpperInvariant())) @>)

// --- a checkout tree ---

/// One SDK project in `root`, as `OptionsRecordsTests` builds it, with package assets that
/// name the checkout root, as a restore writes them.
type private Tree =
    { Root: string
      Project: string
      Assets: string
      Generated: string }

    member t.Path([<ParamArray>] parts: string array) =
        Path.Combine(Array.append [| t.Root |] parts)

let private writeFile (path: string) (text: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

let private treeAt (root: string) : Tree =
    Directory.CreateDirectory root |> ignore
    Daemon.Daemon.msbuildWorkspaceLoader root |> ignore

    writeFile
        (Path.Combine(root, "Directory.Build.props"))
        """<Project>
  <Import Project="$(MSBuildThisFileDirectory)build/common.props" />
  <Import Project="$(MSBuildThisFileDirectory)extra/*.props" />
  <PropertyGroup><Knob>$(FSHW_OPTIONS_KNOB)</Knob></PropertyGroup>
</Project>
"""

    writeFile
        (Path.Combine(root, "build", "common.props"))
        "<Project><PropertyGroup><Common>1</Common></PropertyGroup></Project>\n"

    Directory.CreateDirectory(Path.Combine(root, "extra")) |> ignore
    let project = Path.Combine(root, "src", "App", "App.fsproj")

    writeFile
        project
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Lib.fs" /></ItemGroup>
</Project>
"""

    writeFile (Path.Combine(root, "src", "App", "Lib.fs")) "module Lib\n"
    let assets = Path.Combine(root, "src", "App", "obj", "project.assets.json")
    writeFile assets $"""{{"version":3,"restore":1,"projectPath":"%s{project}"}}"""

    let generated =
        Path.Combine(root, "src", "App", "obj", "Debug", "net10.0", "App.AssemblyInfo.fs")

    writeFile generated "module AssemblyInfo\n"

    { Root = root
      Project = project
      Assets = assets
      Generated = generated }

/// One project's evaluated options in `t`, every root-relative field naming `t`'s root.
let private sampleOptions (t: Tree) : Types.ProjectOptions =
    let dir = Path.GetDirectoryName t.Project

    { ProjectId = Some "app-id"
      ProjectFileName = t.Project
      TargetFramework = "net10.0"
      SourceFiles = [ t.Generated; Path.Combine(dir, "Lib.fs") ]
      OtherOptions =
        [ "--define:DEBUG"
          "-r:/sdk/ref/System.Runtime.dll"
          "-o:" + Path.Combine(dir, "obj", "App.dll") ]
      ReferencedProjects = []
      PackageReferences =
        [ { Name = "FSharp.Core"
            Version = "10.0.100"
            FullPath = "/packages/fsharp.core/10.0.100" } ]
      LoadTime = DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc)
      TargetPath = Path.Combine(dir, "bin", "App.dll")
      TargetRefPath = None
      ProjectOutputType = Types.ProjectOutputType.Library
      ProjectSdkInfo =
        { IsTestProject = false
          Configuration = "Debug"
          IsPackable = true
          TargetFramework = "net10.0"
          TargetFrameworkIdentifier = ".NETCoreApp"
          TargetFrameworkVersion = "v10.0"
          MSBuildAllProjects = [ t.Project ]
          MSBuildToolsVersion = "Current"
          ProjectAssetsFile = t.Assets
          RestoreSuccess = true
          Configurations = [ "Debug" ]
          TargetFrameworks = [ "net10.0" ]
          RunArguments = None
          RunCommand = None
          IsPublishable = None }
      Items = [ Types.ProjectItem.Compile("Lib.fs", Path.Combine(dir, "Lib.fs"), None) ]
      Properties =
        [ { Name = "OutputType"
            Value = "Library" } ]
      CustomProperties = []
      AllProperties = Map.empty
      AllItems = Map.empty
      Analyzers = [] }

/// The design-time half of an evaluation, counted.
type private CountingLoader(answer: unit -> Types.ProjectOptions list, during: unit -> unit) =
    let notifications = Event<Types.WorkspaceProjectState>()
    let mutable loads = 0

    member _.Loads = loads

    member private _.Load(_paths: string list) : seq<Types.ProjectOptions> =
        loads <- loads + 1
        during ()
        answer () |> Seq.ofList

    interface IWorkspaceLoader with
        member this.LoadProjects(paths) = this.Load paths
        member this.LoadProjects(paths, _customProperties, _binaryLog) = this.Load paths
        member this.LoadSln(_sln) = this.Load []
        member this.LoadSln(_sln, _customProperties, _binaryLog) = this.Load []

        [<CLIEvent>]
        member _.Notifications = notifications.Publish

/// Two checkouts of one tree side by side, with roots of different lengths, sharing one
/// outcome store and one shadow log outside both.
type private Box =
    { A: Tree
      B: Tree
      Store: OutcomeStore.Store
      ShadowLog: string
      Messages: Collections.Concurrent.ConcurrentQueue<string> }

let private withBox (body: Box -> unit) =
    withTempDir "options-portable" (fun dir ->
        let dir = canonical dir

        let store =
            OutcomeStore.Store(
                { Root = Path.Combine(dir, "store", "v1")
                  Ttl = OutcomeStore.defaultTtl
                  MaxBytes = OutcomeStore.defaultMaxBytes
                  Log = ignore }
            )

        body
            { A = treeAt (Path.Combine(dir, "a"))
              B = treeAt (Path.Combine(dir, "b-a-considerably-longer-checkout"))
              Store = store
              ShadowLog = Path.Combine(dir, "store", "shadow", "options-portable.jsonl")
              Messages = Collections.Concurrent.ConcurrentQueue<string>() })

let private request (t: Tree) : Request =
    { Root = t.Root
      Projects = [ t.Project ]
      Toolchain = "fshw-test-toolchain"
      Sdk = "sdk-10.0.400"
      GlobalProperties = [ "Configuration", "Debug" ] }

let private inTree (t: Tree) (path: string) : FileStamp.Stamp voption =
    if path.StartsWith(t.Root, StringComparison.Ordinal) then
        FileStamp.read path
    else
        ValueNone

let private environment = [ "FSHW_OPTIONS_KNOB", "a" ]

/// One daemon's loader over `t`, with the portable shadow on; how many evaluations it ran,
/// and the options it returned.
let private loadIn
    (box: Box)
    (t: Tree)
    (env: (string * string) list)
    (answer: unit -> Types.ProjectOptions list)
    (during: unit -> unit)
    (commit: unit -> string option)
    =
    let inner = CountingLoader(answer, during)

    let shadow =
        OptionsPortable.shadow box.Store t.Root box.ShadowLog "fshw-test" commit box.Messages.Enqueue

    let loader =
        CachingLoader(
            inner,
            OptionsInputs.list,
            box.Store,
            (fun _ -> request t),
            (fun () -> env),
            box.Messages.Enqueue,
            racyWindow = TimeSpan.Zero,
            stamp = inTree t,
            ?shadow = shadow
        )

    let options = (loader :> IWorkspaceLoader).LoadProjects [ t.Project ] |> Seq.toList
    inner.Loads, options

let private plainLoad (box: Box) (t: Tree) =
    loadIn box t environment (fun () -> [ sampleOptions t ]) ignore (fun () -> Some "commit-1")

/// The shadow log's lines, oldest first.
let private shadowLines (box: Box) : Nodes.JsonNode list =
    if File.Exists box.ShadowLog then
        [ for line in File.ReadAllLines box.ShadowLog do
              if line.Trim() <> "" then
                  Nodes.JsonNode.Parse line ]
    else
        []

let private lastLine (box: Box) = shadowLines box |> List.last

let private bool (node: Nodes.JsonNode) (name: string) = node[name].GetValue<bool>()

let private int (node: Nodes.JsonNode) (name: string) = node[name].GetValue<int>()

let private str (node: Nodes.JsonNode) (name: string) =
    match node[name] with
    | null -> null
    | v -> v.GetValue<string>()

let private strings (node: Nodes.JsonNode) (name: string) =
    [ for v in node[name].AsArray() -> v.GetValue<string>() ]

let private portableFiled (box: Box) (t: Tree) =
    (box.Store.LookupWithOutputs(OptionsPortable.requestKey (request t))).IsSome

// --- P2: a record filed in one checkout would replay, and agree, in another ---

[<Fact(Timeout = 120000)>]
let ``an evaluation filed in one checkout would replay in another of a different length and agree, and shadow still evaluates``
    ()
    =
    withBox (fun box ->
        let firstLoads, _ = plainLoad box box.A
        test <@ firstLoads = 1 @>
        test <@ portableFiled box box.A @>

        let loads, options = plainLoad box box.B
        // Shadow never serves: B evaluated, and was answered with B's own evaluation.
        test <@ loads = 1 @>
        test <@ options |> List.map _.ProjectFileName = [ box.B.Project ] @>

        let line = lastLine box
        test <@ str line "schema" = "fshw-options-portable-shadow-v1" @>
        test <@ str line "pathToken" = PathToken.Version @>
        test <@ str line "root" = box.B.Root @>
        test <@ str line "filedRoot" = box.A.Root @>
        test <@ int line "rootLength" = box.B.Root.Length @>
        test <@ str line "commit" = "commit-1" @>
        test <@ bool line "crossRoot" @>
        test <@ bool line "keyMatch" @>
        test <@ List.isEmpty (strings line "moved") @>
        test <@ bool line "compared" @>
        test <@ bool line "agree" @>
        test <@ int line["generated"] "present" = 1 @>
        test <@ bool line["generated"] "bytesAgree" @>
        test <@ isNull line["error"] @>)

[<Fact(Timeout = 120000)>]
let ``the first evaluation of a request logs that nothing portable was filed, and compares nothing`` () =
    withBox (fun box ->
        plainLoad box box.A |> ignore
        let line = lastLine box
        test <@ not (bool line "compared") @>
        test <@ not (bool line "keyMatch") @>
        test <@ (str line "missed").Contains("no portable evaluation") @>)

// --- P3: the generated compile items a fresh checkout has not written yet ---

[<Fact(Timeout = 120000)>]
let ``a checkout whose generated compile items are not written yet still matches, and the design-time build's bytes agree``
    ()
    =
    withBox (fun box ->
        plainLoad box box.A |> ignore
        File.Delete box.B.Generated

        let writeGenerated () =
            if not (File.Exists box.B.Generated) then
                writeFile box.B.Generated "module AssemblyInfo\n"

        loadIn box box.B environment (fun () -> [ sampleOptions box.B ]) writeGenerated (fun () -> Some "c")
        |> ignore

        let line = lastLine box
        test <@ bool line "keyMatch" @>
        test <@ int line["generated"] "absent" = 1 @>
        test <@ int line["generated"] "present" = 0 @>
        test <@ bool line["generated"] "bytesAgree" @>
        test <@ bool line "agree" @>)

// --- negative controls ---

[<Fact(Timeout = 120000)>]
let ``a byte changed in the other checkout's project file is a miss naming it`` () =
    withBox (fun box ->
        plainLoad box box.A |> ignore
        File.AppendAllText(box.B.Project, "<!-- changed -->\n")
        plainLoad box box.B |> ignore
        let line = lastLine box
        test <@ not (bool line "keyMatch") @>
        test <@ strings line "moved" |> List.contains "read:${repo}/src/App/App.fsproj" @>)

[<Fact(Timeout = 120000)>]
let ``a byte other than the root changed in the other checkout's package assets is a miss`` () =
    withBox (fun box ->
        plainLoad box box.A |> ignore
        File.WriteAllText(box.B.Assets, File.ReadAllText(box.B.Assets).Replace("\"version\":3", "\"version\":4"))
        plainLoad box box.B |> ignore
        let line = lastLine box
        test <@ not (bool line "keyMatch") @>

        test
            <@
                strings line "moved"
                |> List.contains "read:${repo}/src/App/obj/project.assets.json"
            @>)

[<Fact(Timeout = 120000)>]
let ``an evaluation that differs from the filed one by one option disagrees, naming where`` () =
    withBox (fun box ->
        plainLoad box box.A |> ignore

        let differing () =
            let o = sampleOptions box.B

            [ { o with
                  OtherOptions = o.OtherOptions @ [ "--warnon:3390" ] } ]

        let _, options = loadIn box box.B environment differing ignore (fun () -> Some "c")

        test <@ options.Head.OtherOptions |> List.contains "--warnon:3390" @>
        let line = lastLine box
        test <@ bool line "keyMatch" @>
        test <@ bool line "compared" @>
        test <@ not (bool line "agree") @>
        let first = line["differences"].[0]
        test <@ str first "project" = "${repo}/src/App/App.fsproj" @>
        test <@ str first "pointer" = "/otherOptions/3" @>
        test <@ str first "filed" = "null" @>
        test <@ str first "evaluated" = "\"--warnon:3390\"" @>)

[<Fact(Timeout = 120000)>]
let ``a root reached through a symlink, or spelled in another case, is not portable and says so`` () =
    withBox (fun box ->
        let link = box.A.Root + "-link"
        Directory.CreateSymbolicLink(link, box.A.Root) |> ignore
        let messages = Collections.Generic.List<string>()
        test <@ (OptionsPortable.shadow box.Store link box.ShadowLog "v" (fun () -> None) messages.Add).IsNone @>

        test
            <@
                (OptionsPortable.shadow
                    box.Store
                    (box.A.Root.ToUpperInvariant())
                    box.ShadowLog
                    "v"
                    (fun () -> None)
                    messages.Add)
                    .IsNone
            @>

        test <@ messages.Count = 2 @>
        test <@ messages |> Seq.forall (fun m -> m.Contains "not portable") @>)

[<Fact(Timeout = 120000)>]
let ``an option naming a temporary path is never filed portably`` () =
    withBox (fun box ->
        let withTemp () =
            let o = sampleOptions box.A

            [ { o with
                  OtherOptions = o.OtherOptions @ [ "-r:/tmp/scratch/x.dll" ] } ]

        loadIn box box.A environment withTemp ignore (fun () -> None) |> ignore
        test <@ not (portableFiled box box.A) @>
        test <@ box.Messages |> Seq.exists (fun m -> m.Contains "temporary") @>)

[<Fact(Timeout = 120000)>]
let ``a value the root survives tokenizing in is never filed portably`` () =
    withBox (fun box ->
        let rootSurvives () =
            let o = sampleOptions box.A

            [ { o with
                  Properties =
                      o.Properties
                      @ [ { Name = "Sibling"
                            Value = box.A.Root + "2/x" } ] } ]

        loadIn box box.A environment rootSurvives ignore (fun () -> None) |> ignore
        test <@ not (portableFiled box box.A) @>
        test <@ box.Messages |> Seq.exists (fun m -> m.Contains "survives") @>)

[<Fact(Timeout = 120000)>]
let ``a path into a checkout nested in this one is never filed portably`` () =
    withBox (fun box ->
        let nested = box.A.Path(".workspaces", "x")
        Directory.CreateDirectory(Path.Combine(nested, ".jj")) |> ignore

        let intoNested () =
            let o = sampleOptions box.A

            [ { o with
                  SourceFiles = o.SourceFiles @ [ Path.Combine(nested, "src", "Y.fs") ] } ]

        loadIn box box.A environment intoNested ignore (fun () -> None) |> ignore
        test <@ not (portableFiled box box.A) @>
        test <@ box.Messages |> Seq.exists (fun m -> m.Contains "nested checkout") @>)

[<Fact(Timeout = 120000)>]
let ``an environment value that differs only by the root is a miss`` () =
    withBox (fun box ->
        loadIn
            box
            box.A
            [ "FSHW_OPTIONS_KNOB", box.A.Root + "/k" ]
            (fun () -> [ sampleOptions box.A ])
            ignore
            (fun () -> None)
        |> ignore

        loadIn
            box
            box.B
            [ "FSHW_OPTIONS_KNOB", box.B.Root + "/k" ]
            (fun () -> [ sampleOptions box.B ])
            ignore
            (fun () -> None)
        |> ignore

        let line = lastLine box
        test <@ not (bool line "keyMatch") @>
        test <@ strings line "moved" |> List.contains "env:FSHW_OPTIONS_KNOB" @>)

[<Fact(Timeout = 120000)>]
let ``a generated compile item with other bytes is a miss, and its bytes disagree`` () =
    withBox (fun box ->
        plainLoad box box.A |> ignore
        File.WriteAllText(box.B.Generated, "module AssemblyInfo // InformationalVersion+abc\n")
        plainLoad box box.B |> ignore
        let line = lastLine box
        test <@ not (bool line "keyMatch") @>

        test
            <@
                strings line "moved"
                |> List.contains "gen:${repo}/src/App/obj/Debug/net10.0/App.AssemblyInfo.fs"
            @>

        test <@ bool line "compared" @>
        test <@ not (bool line["generated"] "bytesAgree") @>
        test <@ not (bool line "agree") @>)

[<Fact(Timeout = 120000)>]
let ``an exception inside the shadow leaves what the loader returns unchanged, and the line carries it`` () =
    withBox (fun box ->
        plainLoad box box.A |> ignore

        let loads, options =
            loadIn box box.B environment (fun () -> [ sampleOptions box.B ]) ignore (fun () ->
                failwith "commit unreadable")

        test <@ loads = 1 @>
        test <@ options |> List.map _.ProjectFileName = [ box.B.Project ] @>
        let line = lastLine box
        test <@ (str line "error").Contains("commit unreadable") @>
        test <@ not (bool line "agree") @>)

[<Fact(Timeout = 120000)>]
let ``a shadow log that cannot be written leaves what the loader returns unchanged`` () =
    withBox (fun box ->
        // A directory where the log file should be: every append throws.
        Directory.CreateDirectory box.ShadowLog |> ignore
        let loads, options = plainLoad box box.A
        test <@ loads = 1 @>
        test <@ options.Length = 1 @>
        test <@ box.Messages |> Seq.exists (fun m -> m.Contains "portable shadow failed") @>)

[<Fact(Timeout = 120000)>]
let ``a shadow that throws before the evaluation leaves the load to the evaluation`` () =
    withBox (fun box ->
        let inner = CountingLoader((fun () -> [ sampleOptions box.A ]), ignore)

        let throwing: OptionsShadow =
            { BeforeEvaluation = fun _ _ -> failwith "lookup broke"
              AfterFiled = fun _ _ _ _ -> () }

        let loader =
            CachingLoader(
                inner,
                OptionsInputs.list,
                box.Store,
                (fun _ -> request box.A),
                (fun () -> environment),
                box.Messages.Enqueue,
                racyWindow = TimeSpan.Zero,
                stamp = inTree box.A,
                shadow = throwing
            )

        let options =
            (loader :> IWorkspaceLoader).LoadProjects [ box.A.Project ] |> Seq.toList

        test <@ inner.Loads = 1 @>
        test <@ options.Length = 1 @>
        test <@ box.Messages |> Seq.exists (fun m -> m.Contains "lookup broke") @>)

[<Fact(Timeout = 120000)>]
let ``an option that already holds the root token is never filed portably`` () =
    withBox (fun box ->
        let holdsToken () =
            let o = sampleOptions box.A

            [ { o with
                  OtherOptions = o.OtherOptions @ [ "--define:${repo}" ] } ]

        loadIn box box.A environment holdsToken ignore (fun () -> None) |> ignore
        test <@ not (portableFiled box box.A) @>
        test <@ box.Messages |> Seq.exists (fun m -> m.Contains "already holds the root token") @>)

[<Fact(Timeout = 120000)>]
let ``a generated compile item that is not text, or not there, is never filed portably`` () =
    withBox (fun box ->
        File.WriteAllBytes(box.A.Generated, [| 0xffuy; 0xfeuy; 0x00uy |])
        plainLoad box box.A |> ignore
        test <@ not (portableFiled box box.A) @>

        test
            <@
                box.Messages
                |> Seq.exists (fun m -> m.Contains "is not text the root token can rebind")
            @>

        File.Delete box.B.Generated
        plainLoad box box.B |> ignore
        test <@ not (portableFiled box box.B) @>
        test <@ box.Messages |> Seq.exists (fun m -> m.Contains "is not there") @>)

[<Fact(Timeout = 120000)>]
let ``a generated compile item the evaluation never wrote disagrees`` () =
    withBox (fun box ->
        plainLoad box box.A |> ignore
        File.Delete box.B.Generated
        plainLoad box box.B |> ignore
        let line = lastLine box
        test <@ bool line "keyMatch" @>
        test <@ int line["generated"] "absent" = 1 @>
        test <@ not (bool line["generated"] "bytesAgree") @>
        test <@ not (bool line "agree") @>

        test
            <@
                line["differences"].AsArray()
                |> Seq.exists (fun d -> (str d "pointer").StartsWith("generated:") && str d "evaluated" = "(absent)")
            @>)

[<Fact(Timeout = 15000)>]
let ``the shadow log sits beside the store, box-wide`` () =
    test <@ OptionsPortable.logPathFor "/cache/outcomes/ns/v1/" = "/cache/outcomes/ns/shadow/options-portable.jsonl" @>

// --- the daemon's wiring ---

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the shadow log names a jj checkout's commit, and none where there is no repository`` () =
    let commit = Daemon.Daemon.workingCopyCommit (RepoTasks.repoRoot ())

    test
        <@
            commit
            |> Option.exists (fun c -> c.Length = 40 && c |> Seq.forall Uri.IsHexDigit)
        @>

    withTempDir "portable-no-repository" (fun dir ->
        test <@ (Daemon.Daemon.workingCopyCommit (canonical dir)).IsNone @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the daemon's options loader takes the portable shadow only when asked`` () =
    withTempDir "portable-loader" (fun dir ->
        let root = canonical dir

        for mode in [ OptionsPortable.Mode.Off; OptionsPortable.Mode.Shadow ] do
            let loader = Daemon.Daemon.optionsRecordsLoader root mode
            test <@ loader :? OptionsRecords.CachingLoader @>)
