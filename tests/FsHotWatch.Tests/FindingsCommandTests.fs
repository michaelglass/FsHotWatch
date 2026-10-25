module FsHotWatch.Tests.FindingsCommandTests

open System

open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Analyzers.AnalyzerFindings
open FsHotWatch.Cli.Findings

let private tree (hash: string) : FsHotWatch.TreeHash.Tree =
    { Hash = hash
      FileCount = 1
      SkippedCount = 0
      DeclaredCount = 0
      AbsentDeclarationCount = 0 }

let private stamp (text: string) : AnalyzedFile =
    { SourceHash = sourceHashOf text
      ModelGeneration = Some 7L
      AnalyzerSet = Some "set-1"
      ConfigDigest = "cfg"
      Typed = true }

let private finding (code: string) : ErrorEntry =
    { Message = $"[promoted from warning] rule %s{code}"
      Severity = DiagnosticSeverity.Error
      Line = 3
      Column = 4
      Code = Some code
      Detail = None }

/// A reply that analyzed `/repo/src/A.fs` from `analyzedText`.
let private replyJson (analyzedText: string) (entries: ErrorEntry list) : string =
    serializeReply
        { ModelGeneration = Some 7L
          Expected = [ "/repo/src/A.fs" ]
          AnalyzerSet = Result.Ok "set-1"
          FailOnSeverity = DiagnosticSeverity.Hint
          ScopeRoot = Some "/repo"
          AnalyzerCount = 43
          Files =
            [ { File = "/repo/src/A.fs"
                Analyzed = stamp analyzedText
                Entries = entries } ] }

/// A fake daemon and disk. `replies` is what successive `analyzer-findings` calls
/// return; the disk holds `/repo/src/A.fs` with `onDisk`.
type private Fake(running: bool, replies: string list, onDisk: string) =
    let mutable replies = replies
    let mutable clock = DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc)
    member val Rests = ResizeArray<int>()
    member val Reads = 0 with get, set
    member val Busy: string option = None with get, set

    member this.Seams: Seams =
        { IsRunning = fun () -> running
          WaitForRest =
            fun timeoutMs ->
                this.Rests.Add timeoutMs

                match this.Busy with
                | Some why -> "busy: " + why
                | None -> "rest"
          ReadReply =
            fun () ->
                this.Reads <- this.Reads + 1

                match replies with
                | [ last ] -> last
                | next :: rest ->
                    replies <- rest
                    next
                | [] -> failwith "no reply scripted"
          TreeHash = fun () -> tree "sha256:tree"
          Disk =
            fun _ ->
                { SourceHash =
                    fun file ->
                        if file = "/repo/src/A.fs" then
                            Some(sourceHashOf onDisk)
                        else
                            None
                  ConfigDigest = fun _ -> "cfg" }
          Now = fun () -> clock
          Sleep = fun span -> clock <- clock + span }

let private waiting =
    { Wait = true
      Timeout = TimeSpan.FromSeconds 30.0
      WarningsAreFailures = true }

let private once = { waiting with Wait = false }

[<Fact>]
let ``with no daemon running it refuses at once and asks the daemon nothing`` () =
    let fake = Fake(false, [ replyJson "a" [] ], "a")
    let answer = answer fake.Seams waiting
    test <@ answer.Outcome = NoDaemon @>
    test <@ Outcome.exitCode answer.Outcome = 5 @>
    test <@ fake.Rests.Count = 0 && fake.Reads = 0 @>

[<Fact>]
let ``a current answer carries every finding with its code and exits 1`` () =
    let fake = Fake(true, [ replyJson "a" [ finding "FL0065" ] ], "a")
    let answer = answer fake.Seams waiting

    test <@ answer.Outcome = Findings 1 @>
    test <@ Outcome.exitCode answer.Outcome = 1 @>
    test <@ answer.Findings |> List.map (fun (_, e) -> e.Code) = [ Some "FL0065" ] @>
    test <@ answer.TreeHash = Some "sha256:tree" @>

[<Fact>]
let ``a current answer with nothing failing exits 0`` () =
    let fake = Fake(true, [ replyJson "a" [] ], "a")
    test <@ (answer fake.Seams waiting).Outcome = Clean @>

[<Fact>]
let ``a stale reply is never answered: without --wait it refuses with exit 4 and no findings`` () =
    // The daemon analyzed the old text clean; the file on disk now has a finding.
    let fake = Fake(true, [ replyJson "old" [] ], "new")
    let answer = answer fake.Seams once

    test <@ answer.Outcome = Stale @>
    test <@ Outcome.exitCode answer.Outcome = 4 @>
    test <@ List.isEmpty answer.Findings @>
    test <@ answer.Reasons |> List.exists (fun r -> r.Contains "/repo/src/A.fs") @>

[<Fact>]
let ``with --wait a stale reply is waited out until the daemon has analyzed the tree on disk`` () =
    let fake =
        Fake(true, [ replyJson "old" []; replyJson "old" []; replyJson "new" [ finding "FL0065" ] ], "new")

    let answer = answer fake.Seams waiting

    test <@ answer.Outcome = Findings 1 @>
    test <@ fake.Reads = 3 @>
    // Every read was preceded by a wait for the daemon to come to rest.
    test <@ fake.Rests.Count = 3 && fake.Rests |> Seq.forall (fun ms -> ms > 0) @>

[<Fact>]
let ``with --wait a reply that stays stale refuses once the timeout passes`` () =
    let fake = Fake(true, [ replyJson "old" [] ], "new")

    let answer =
        answer
            fake.Seams
            { waiting with
                Timeout = TimeSpan.FromSeconds 5.0 }

    test <@ answer.Outcome = Stale @>
    test <@ List.isEmpty answer.Findings @>

[<Fact>]
let ``a daemon still working is not read without --wait`` () =
    let fake = Fake(true, [ replyJson "a" [] ], "a")
    fake.Busy <- Some "analyzers running"
    let answer = answer fake.Seams once

    test <@ answer.Outcome = Stale @>
    test <@ fake.Rests |> Seq.toList = [ 0 ] @>
    test <@ fake.Reads = 0 @>

[<Fact>]
let ``an expected file with no analysis refuses as incomplete with exit 2`` () =
    let json =
        serializeReply
            { ModelGeneration = Some 7L
              Expected = [ "/repo/src/A.fs"; "/repo/src/B.fs" ]
              AnalyzerSet = Result.Ok "set-1"
              FailOnSeverity = DiagnosticSeverity.Hint
              ScopeRoot = Some "/repo"
              AnalyzerCount = 43
              Files =
                [ { File = "/repo/src/A.fs"
                    Analyzed = stamp "a"
                    Entries = [ finding "FL0065" ] } ] }

    let fake = Fake(true, [ json ], "a")
    let answer = answer fake.Seams waiting

    test <@ answer.Outcome = Incomplete @>
    test <@ Outcome.exitCode answer.Outcome = 2 @>
    test <@ List.isEmpty answer.Findings @>
    test <@ answer.Missing = [ "/repo/src/B.fs" ] @>

[<Fact>]
let ``a daemon without the command is refused with exit 6`` () =
    let fake = Fake(true, [ FsHotWatch.Ipc.unknownCommandReply CommandName ], "a")
    let answer = answer fake.Seams waiting

    test <@ answer.Outcome = Refused @>
    test <@ Outcome.exitCode answer.Outcome = 6 @>

[<Fact>]
let ``a tree that moves while the answer is read is not answered`` () =
    let fake = Fake(true, [ replyJson "a" [] ], "a")
    let hashes = ref [ "sha256:one"; "sha256:two" ]

    let seams =
        { fake.Seams with
            TreeHash =
                fun () ->
                    match hashes.Value with
                    | next :: rest ->
                        hashes.Value <- rest
                        tree next
                    | [] -> tree "sha256:two" }

    let answer = answer seams once
    test <@ answer.Outcome = Stale @>

[<Fact>]
let ``the JSON answer is versioned and carries the tree, the analyzer set, the sets and the codes`` () =
    let fake = Fake(true, [ replyJson "a" [ finding "FL0065" ] ], "a")
    let json = toJson "/repo" (answer fake.Seams waiting)
    let root = System.Text.Json.Nodes.JsonNode.Parse json
    let text (node: System.Text.Json.Nodes.JsonNode) (name: string) = node[name].GetValue<string>()

    test <@ text root "schema" = Schema @>
    test <@ Schema = "fshw-findings-v1" @>
    test <@ text root "outcome" = "findings" @>
    test <@ root["exit"].GetValue<int>() = 1 @>
    test <@ text root "treeHash" = "sha256:tree" @>
    test <@ text root "analyzerSet" = "set-1" @>
    test <@ text root "failOnSeverity" = "hint" @>
    test <@ root["modelGeneration"].GetValue<int64>() = 7L @>

    let names (property: string) =
        root[property].AsArray() |> Seq.map _.GetValue<string>() |> Seq.toList

    test <@ names "expectedFiles" = [ "src/A.fs" ] @>
    test <@ names "analyzedFiles" = [ "src/A.fs" ] @>

    let first = root["findings"].AsArray() |> Seq.head
    test <@ text first "code" = "FL0065" @>
    test <@ text first "file" = "src/A.fs" @>
    test <@ first["line"].GetValue<int>() = 3 @>

[<Fact>]
let ``the human answer prints each finding in lint's file(line,col) form with its code`` () =
    let fake = Fake(true, [ replyJson "a" [ finding "FL0065" ] ], "a")
    let lines = render "/repo" (answer fake.Seams waiting)

    test
        <@
            lines
            |> List.contains "src/A.fs(3,4): error FL0065: [promoted from warning] rule FL0065"
        @>

// --- the verb as the CLI parses and routes it ---

[<Fact>]
let ``findings parses its flags`` () =
    match FsHotWatch.Cli.Program.globalSpec.Parse [| "findings"; "--json"; "--wait"; "--timeout-sec"; "5" |] with
    | Ok(_, FsHotWatch.Cli.Program.Command.Findings flags) ->
        test
            <@
                Set.ofList flags = Set.ofList
                    [ FsHotWatch.Cli.Program.FindingsFlag.Json
                      FsHotWatch.Cli.Program.FindingsFlag.Wait
                      FsHotWatch.Cli.Program.FindingsFlag.TimeoutSec 5 ]
            @>
    | other -> failwith $"expected findings, got %A{other}"

[<Fact>]
let ``findings never brings a daemon up`` () =
    test <@ not (FsHotWatch.Cli.Program.needsDaemon (FsHotWatch.Cli.Program.Command.Findings [])) @>

[<Fact>]
let ``findings prints its own answer, ungated by the agent envelope`` () =
    test
        <@
            FsHotWatch.Cli.Program.agentGateFor (
                FsHotWatch.Cli.Program.Command.Findings [ FsHotWatch.Cli.Program.FindingsFlag.Json ]
            ) = FsHotWatch.Cli.Program.AgentGate.Ungated
        @>

// --- the remaining outcomes, as each surface reports them ---

[<Fact>]
let ``a reply this build cannot read is refused`` () =
    let fake = Fake(true, [ "{\"schema\":\"something-else\"}" ], "a")
    test <@ (answer fake.Seams waiting).Outcome = Refused @>

[<Fact>]
let ``an analyzer set with no identity is refused, with the reason`` () =
    let json =
        serializeReply
            { ModelGeneration = Some 7L
              Expected = [ "/repo/src/A.fs" ]
              AnalyzerSet = Result.Error "x.dll: no portable PDB"
              FailOnSeverity = DiagnosticSeverity.Hint
              ScopeRoot = Some "/repo"
              AnalyzerCount = 1
              Files = [] }

    let fake = Fake(true, [ json ], "a")
    let answered = answer fake.Seams waiting
    test <@ answered.Outcome = Refused @>
    test <@ answered.Reasons |> List.exists (fun r -> r.Contains "no portable PDB") @>

    let root = System.Text.Json.Nodes.JsonNode.Parse(toJson "/repo" answered)
    test <@ isNull root["analyzerSet"] @>
    test <@ root["analyzerSetRefusal"].GetValue<string>() = "x.dll: no portable PDB" @>
    test <@ render "/repo" answered |> List.head |> (fun h -> h.Contains "set unidentified") @>

[<Fact>]
let ``a refusal with no reply renders its reason and carries no tree`` () =
    let refusal = refused "the daemon went away"
    test <@ Outcome.exitCode refusal.Outcome = 6 @>

    test
        <@
            render "/repo" refusal = [ "findings: refused — no answer about the tree on disk"
                                       "  the daemon went away" ]
        @>

    let root = System.Text.Json.Nodes.JsonNode.Parse(toJson "/repo" refusal)

    test
        <@
            isNull root["treeHash"]
            && isNull root["modelGeneration"]
            && isNull root["analyzerSet"]
        @>

    test <@ root["expectedFiles"].AsArray().Count = 0 @>

[<Fact>]
let ``a clean answer says so, and a non-failing finding is still listed`` () =
    let hint =
        { finding "FL0001" with
            Severity = DiagnosticSeverity.Hint
            Code = None }

    let fake = Fake(true, [ replyJson "a" [ hint ] ], "a")
    let answered = answer fake.Seams waiting

    test <@ answered.Outcome = Clean @>
    test <@ Outcome.token answered.Outcome = "clean" @>
    let lines = render "/repo" answered
    test <@ (List.head lines).StartsWith "findings: clean" @>
    // No rule produced it: no code in the line.
    test
        <@
            lines
            |> List.contains "src/A.fs(3,4): hint: [promoted from warning] rule FL0001"
        @>

[<Fact>]
let ``an incomplete answer names the missing files, and a path outside the repository stays absolute`` () =
    let json =
        serializeReply
            { ModelGeneration = Some 7L
              Expected = [ "/elsewhere/B.fs"; "/repo/src/A.fs" ]
              AnalyzerSet = Result.Ok "set-1"
              FailOnSeverity = DiagnosticSeverity.Hint
              ScopeRoot = Some "/repo"
              AnalyzerCount = 1
              Files =
                [ { File = "/repo/src/A.fs"
                    Analyzed = stamp "a"
                    Entries = [] } ] }

    let answered = answer (Fake(true, [ json ], "a")).Seams waiting
    test <@ render "/repo" answered |> List.contains "  missing: /elsewhere/B.fs" @>

    let root = System.Text.Json.Nodes.JsonNode.Parse(toJson "/repo" answered)
    test <@ root["missingFiles"].AsArray() |> Seq.map _.GetValue<string>() |> Seq.toList = [ "/elsewhere/B.fs" ] @>

[<Fact>]
let ``a stale answer lists its stale files in the JSON`` () =
    let answered = answer (Fake(true, [ replyJson "old" [] ], "new")).Seams once
    let root = System.Text.Json.Nodes.JsonNode.Parse(toJson "/repo" answered)
    test <@ root["staleFiles"].AsArray() |> Seq.map _.GetValue<string>() |> Seq.toList = [ "src/A.fs" ] @>
    test <@ root["findings"].AsArray().Count = 0 @>
    test <@ Outcome.token NoDaemon = "no-daemon" && Outcome.token Incomplete = "incomplete" @>
