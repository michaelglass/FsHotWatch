/// The `action { … }` builder, its `Need.*` reads and the environment oracles
/// (AUTOMATION-1636, S1 and S2 of ADR-059 §35).
///
/// S1: an action reads its inputs ONLY through `Need.*`; each read returns the value and
/// records the digest of what it read, and the action's key is that trace. S2: oracles are
/// typed questions about the environment, asked again on every run.
module FsHotWatch.Tests.NeedActionTests

open System
open System.Text
open Xunit
open Swensen.Unquote
open FsHotWatch.Core

[<Sealed>]
type NeedsKind private () =
    interface IRecordKind with
        static member Name = "needs-test"
        static member SchemaVersion = 1

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got Error %s{error.ToString()}"

let private path = RepoRelativePath.parse >> ok
let private label = OutputLabel.parse >> ok
let private variable = EnvVarName.parse >> ok
let private bytes (text: string) = Encoding.UTF8.GetBytes text

/// SHA-256 of "abc", the FIPS 180-2 test vector.
let private sha256OfAbc =
    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"

/// A world over fixed answers: `files` by path, `upstream` by label, and `variables`.
let private worldOf
    (files: (string * string) list)
    (upstream: (string * string) list)
    (variables: (string * string) list)
    : World =
    let files = files |> List.map (fun (p, text) -> path p, bytes text) |> Map.ofList

    let upstream =
        upstream
        |> List.map (fun (l, text) -> label l, OutputDigest.ofBytes (bytes text))
        |> Map.ofList

    let variables =
        variables |> List.map (fun (name, value) -> variable name, value) |> Map.ofList

    { ReadFile = fun p -> Map.tryFind p files
      Upstream = fun l -> Map.tryFind l upstream
      Oracles =
        { DotnetSdk = fun () -> "10.0.100"
          Variable = fun name -> Map.tryFind name variables
          ToolManifest = fun () -> Some(bytes """{"tools":{}}""")
          Platform = fun () -> "osx-arm64" } }

let private keyOf (trace: NeedTrace) : string =
    let key: ActionKey<NeedsKind> = NeedTrace.key trace |> ok
    (ActionKey.ref key).Canonical

let private run world action = Action.run world action |> ok

// ---------------------------------------------------------------------------------------
// Digests made by hashing

[<Fact>]
let ``hashing bytes gives the SHA-256 in the digest's one text form`` () =
    let abc = bytes "abc"
    test <@ (ContentDigest.ofBytes abc).Hex = sha256OfAbc @>
    test <@ (OutputDigest.ofBytes abc).Hex = sha256OfAbc @>
    test <@ (AnswerDigest.ofBytes abc).Hex = sha256OfAbc @>
    test <@ (AnswerDigest.parse sha256OfAbc |> ok) = AnswerDigest.ofBytes abc @>
    test <@ (AnswerDigest.ofBytes abc).ToString() = sha256OfAbc @>

// ---------------------------------------------------------------------------------------
// The names an action may ask for

[<Fact>]
let ``an environment variable name is refused when it could not name one`` () =
    test <@ (variable "DOTNET_ROOT").Text = "DOTNET_ROOT" @>
    test <@ (variable "DOTNET_ROOT").ToString() = "DOTNET_ROOT" @>
    test <@ EnvVarName.parse null = Error EnvVarNameError.Empty @>
    test <@ EnvVarName.parse "" = Error EnvVarNameError.Empty @>
    test <@ EnvVarName.parse "A=B" = Error EnvVarNameError.ContainsEquals @>
    test <@ EnvVarName.parse "A\nB" = Error EnvVarNameError.ControlCharacter @>
    test <@ EnvVarNameError.Empty.ToString() = "an environment variable name cannot be empty" @>
    test <@ EnvVarNameError.ContainsEquals.ToString() = "an environment variable name cannot contain '='" @>

    test
        <@
            EnvVarNameError.ControlCharacter.ToString() = "an environment variable name cannot contain control characters"
        @>

[<Fact>]
let ``an output label is lowercase words, digits, '-' and '/' only`` () =
    test <@ (label "check/fcs-signature").Text = "check/fcs-signature" @>
    test <@ (label "check/fcs-signature").ToString() = "check/fcs-signature" @>
    test <@ OutputLabel.parse null = Error OutputLabelError.Empty @>
    test <@ OutputLabel.parse "" = Error OutputLabelError.Empty @>
    test <@ OutputLabel.parse "Check" = Error(OutputLabelError.InvalidCharacter 0) @>
    test <@ OutputLabel.parse "a b" = Error(OutputLabelError.InvalidCharacter 1) @>
    test <@ OutputLabelError.Empty.ToString() = "an output label cannot be empty" @>

    test
        <@
            (OutputLabelError.InvalidCharacter 2).ToString() = "an output label is a-z, 0-9, '-' and '/'; character 2 is not"
        @>

[<Fact>]
let ``every oracle question has its own subject`` () =
    test <@ OracleQuestion.DotnetSdk.Subject = "dotnet-sdk" @>
    test <@ (OracleQuestion.Variable(variable "DOTNET_ROOT")).Subject = "env:DOTNET_ROOT" @>
    test <@ OracleQuestion.ToolManifest.Subject = "tool-manifest" @>
    test <@ OracleQuestion.Platform.Subject = "platform" @>

    test
        <@
            [ Oracle.question Oracle.dotnetSdk
              Oracle.question (Oracle.variable (variable "X"))
              Oracle.question Oracle.toolManifest
              Oracle.question Oracle.platform ] = [ OracleQuestion.DotnetSdk
                                                    OracleQuestion.Variable(variable "X")
                                                    OracleQuestion.ToolManifest
                                                    OracleQuestion.Platform ]
        @>

// ---------------------------------------------------------------------------------------
// S1: each Need returns the value and records what it read

[<Fact>]
let ``Need.file returns the bytes and records their digest`` () =
    let world = worldOf [ "src/A.fs", "let x = 1" ] [] []
    let content, trace = run world (Need.file (path "src/A.fs"))
    test <@ content = Some(bytes "let x = 1") @>

    test <@ NeedTrace.needs trace = [ Needed.File(path "src/A.fs", Some(ContentDigest.ofBytes (bytes "let x = 1"))) ] @>

[<Fact>]
let ``a file that is not there is needed as absent, and that is a different key`` () =
    let absent, trace = run (worldOf [] [] []) (Need.file (path "src/A.fs"))
    test <@ absent = None @>
    test <@ NeedTrace.needs trace = [ Needed.File(path "src/A.fs", None) ] @>

    let _, present =
        run (worldOf [ "src/A.fs", "" ] [] []) (Need.file (path "src/A.fs"))

    test <@ keyOf trace <> keyOf present @>
    test <@ (keyOf trace).EndsWith("\nabsent:src/A.fs\t-") @>

[<Fact>]
let ``Need.upstream returns the producer's digest, and an unproduced output is an error`` () =
    let world = worldOf [] [ "check/fcs-signature", "sig" ] []
    let digest, trace = run world (Need.upstream (label "check/fcs-signature"))
    test <@ digest = OutputDigest.ofBytes (bytes "sig") @>
    test <@ NeedTrace.needs trace = [ Needed.Upstream(label "check/fcs-signature", digest) ] @>

    test
        <@
            Action.run world (Need.upstream (label "project/options")) = Error(
                NeedError.NotProduced(label "project/options")
            )
        @>

    test <@ (NeedError.NotProduced(label "project/options")).ToString() = "no output 'project/options' was produced" @>

// ---------------------------------------------------------------------------------------
// S2: oracles are typed questions about the environment

[<Fact>]
let ``each oracle answers with its own type and records the answer's digest`` () =
    let world = worldOf [] [] [ "DOTNET_ROOT", "/usr/share/dotnet" ]

    let answers, trace =
        run
            world
            (action {
                let! sdk = Need.oracle Oracle.dotnetSdk
                let! root = Need.env (variable "DOTNET_ROOT")
                let! manifest = Need.oracle Oracle.toolManifest
                let! platform = Need.oracle Oracle.platform
                return sdk, root, manifest, platform
            })

    test <@ answers = ("10.0.100", Some "/usr/share/dotnet", Some(bytes """{"tools":{}}"""), "osx-arm64") @>

    let questions =
        NeedTrace.needs trace
        |> List.map (function
            | Needed.Oracle(question, _) -> question.Subject
            | other -> failwith $"not an oracle: %s{Needed.subject other}")

    test <@ questions = [ "dotnet-sdk"; "env:DOTNET_ROOT"; "platform"; "tool-manifest" ] @>

[<Fact>]
let ``an unset variable and one set to empty are different answers`` () =
    let name = variable "FSHW_X"
    let needX = Need.env name
    let _, unset = run (worldOf [] [] []) needX
    let _, empty = run (worldOf [] [] [ "FSHW_X", "" ]) needX
    test <@ keyOf unset <> keyOf empty @>

[<Fact>]
let ``a missing tool manifest is an answer too`` () =
    let world =
        { worldOf [] [] [] with
            Oracles =
                { (worldOf [] [] []).Oracles with
                    ToolManifest = fun () -> None } }

    let manifest, trace = run world (Need.oracle Oracle.toolManifest)
    let _, present = run (worldOf [] [] []) (Need.oracle Oracle.toolManifest)
    test <@ manifest = None @>
    test <@ keyOf trace <> keyOf present @>

[<Fact>]
let ``an oracle is asked again on every run`` () =
    let mutable asked = 0

    let world =
        { worldOf [] [] [] with
            Oracles =
                { (worldOf [] [] []).Oracles with
                    DotnetSdk =
                        fun () ->
                            asked <- asked + 1
                            $"10.0.%d{asked}" } }

    let first, one = run world (Need.oracle Oracle.dotnetSdk)
    let second, two = run world (Need.oracle Oracle.dotnetSdk)
    test <@ (first, second) = ("10.0.1", "10.0.2") @>
    test <@ keyOf one <> keyOf two @>

// ---------------------------------------------------------------------------------------
// The key IS the trace

let private analysis =
    action {
        let! source = Need.file (path "src/A.fs")
        let! _ = Need.upstream (label "check/fcs-signature")
        let! sdk = Need.oracle Oracle.dotnetSdk
        return (Option.map Array.length source, sdk)
    }

[<Fact>]
let ``the key is what was needed: same needs, same key; one input moved, another key`` () =
    let world = worldOf [ "src/A.fs", "let x = 1" ] [ "check/fcs-signature", "sig" ] []
    let value, trace = run world analysis
    let _, again = run world analysis
    test <@ value = (Some 9, "10.0.100") @>
    test <@ keyOf trace = keyOf again @>

    let _, edited =
        run (worldOf [ "src/A.fs", "let x = 2" ] [ "check/fcs-signature", "sig" ] []) analysis

    test <@ keyOf trace <> keyOf edited @>

[<Fact>]
let ``the order the needs were made in does not move the key`` () =
    let world = worldOf [ "src/A.fs", "a" ] [ "check/fcs-signature", "sig" ] []

    let reordered =
        action {
            let! _ = Need.oracle Oracle.dotnetSdk
            let! _ = Need.upstream (label "check/fcs-signature")
            let! _ = Need.file (path "src/A.fs")
            return ()
        }

    let _, one = run world analysis
    let _, other = run world reordered
    test <@ keyOf one = keyOf other @>

[<Fact>]
let ``the need key's canonical text names every need by subject`` () =
    let world = worldOf [ "src/A.fs", "abc" ] [ "check/fcs-signature", "abc" ] []
    let _, trace = run world analysis
    let lines = (keyOf trace).Split('\n')

    test
        <@
            lines = [| ActionKey.Recipe
                       "needs-test"
                       "1"
                       "content:src/A.fs\t" + sha256OfAbc
                       "oracle:dotnet-sdk\t" + (AnswerDigest.ofBytes (bytes "10.0.100")).Hex
                       "upstream:check/fcs-signature\t" + sha256OfAbc |]
        @>

[<Fact>]
let ``needing the same input twice with the same answer records it once`` () =
    let world = worldOf [ "src/A.fs", "a" ] [] []

    let twice =
        action {
            let! one = Need.file (path "src/A.fs")
            let! two = Need.file (path "src/A.fs")
            return one = two
        }

    let same, trace = run world twice
    test <@ same @>
    test <@ List.length (NeedTrace.needs trace) = 1 @>

[<Fact>]
let ``an input that answers differently within one run is refused, not keyed`` () =
    let mutable reads = 0

    let world =
        { worldOf [] [] [] with
            ReadFile =
                fun _ ->
                    reads <- reads + 1
                    Some(bytes $"version %d{reads}") }

    let twice =
        action {
            let! _ = Need.file (path "src/A.fs")
            let! _ = Need.file (path "src/A.fs")
            return ()
        }

    test <@ Action.run world twice = Error(NeedError.Inconsistent "file:src/A.fs") @>

    test
        <@ (NeedError.Inconsistent "file:src/A.fs").ToString() = "'file:src/A.fs' answered differently within one run" @>

[<Fact>]
let ``a failed need stops the action: nothing after it runs`` () =
    let mutable after = false

    let stopped =
        action {
            let! _ = Need.upstream (label "project/options")
            after <- true
            return ()
        }

    test <@ Result.isError (Action.run (worldOf [] [] []) stopped) @>
    test <@ not after @>

[<Fact>]
let ``an action that needs nothing has an empty trace`` () =
    let value, trace = run (worldOf [] [] []) (action { return 42 })
    test <@ value = 42 @>
    test <@ List.isEmpty (NeedTrace.needs trace) @>

[<Fact>]
let ``return! runs an action inside another`` () =
    let world = worldOf [ "src/A.fs", "a" ] [] []

    let outer =
        action {
            let! _ = Need.oracle Oracle.platform
            return! Need.file (path "src/A.fs")
        }

    let value, trace = run world outer
    test <@ value = Some(bytes "a") @>
    test <@ List.length (NeedTrace.needs trace) = 2 @>

// ---------------------------------------------------------------------------------------
// What moved between two traces: what the shadow names when keys disagree

[<Fact>]
let ``diff names each need added, removed or changed, by subject`` () =
    let before = worldOf [ "src/A.fs", "a" ] [ "check/fcs-signature", "s1" ] []
    let after = worldOf [ "src/A.fs", "a" ] [ "check/fcs-signature", "s2" ] []

    let first =
        action {
            let! _ = Need.file (path "src/A.fs")
            let! _ = Need.upstream (label "check/fcs-signature")
            let! _ = Need.oracle Oracle.platform
            return ()
        }

    let second =
        action {
            let! _ = Need.file (path "src/A.fs")
            let! _ = Need.upstream (label "check/fcs-signature")
            let! _ = Need.oracle Oracle.dotnetSdk
            return ()
        }

    let _, one = run before first
    let _, two = run after second

    let shown =
        NeedTrace.diff one two
        |> List.map (function
            | NeedChange.Added n -> "+" + Needed.subject n
            | NeedChange.Removed n -> "-" + Needed.subject n
            | NeedChange.Changed(b, a) -> "~" + Needed.subject b + "=" + Needed.subject a)

    test
        <@
            shown = [ "+oracle:dotnet-sdk"
                      "-oracle:platform"
                      "~upstream:check/fcs-signature=upstream:check/fcs-signature" ]
        @>

    test <@ List.isEmpty (NeedTrace.diff one one) @>

    test
        <@
            (NeedChange.Added(Needed.Oracle(OracleQuestion.Platform, AnswerDigest.ofBytes [||]))).Subject = "oracle:platform"
        @>

    test <@ (NeedChange.Removed(Needed.Upstream(label "x", OutputDigest.ofBytes [||]))).Subject = "upstream:x" @>
