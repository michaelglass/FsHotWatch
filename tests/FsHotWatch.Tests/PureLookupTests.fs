/// `Lookup.ofRepo`: the lookup verdict of a checkout from its configuration, its tree, the
/// producer the daemon recorded and the outcome stores alone (AUTOMATION-1695, Phase 1 step C;
/// what experiment E5 times). The decision itself is `Lookup.answer`, which the shadow of a
/// `fshw verdict` read also calls; these pin what `ofRepo` feeds it.
module FsHotWatch.Tests.PureLookupTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

/// A configuration whose only requirement is the diagnostics record.
let private diagnosticsOnly =
    """{ "format": "off", "lint": false, "build": false }"""

let private producer = "1.2.3 0123456789abcdef"

let private ignoreLog (_: string) = ()

/// A source file under a discovery root (`src/`), so an edit to it moves the tree.
let private source (dir: string) = Path.Combine(dir, "src", "a.fs")

let private checkout (dir: string) (config: string) =
    File.WriteAllText(FsHwPaths.configFile dir, config)
    Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
    File.WriteAllText(source dir, "let a = 1")
    Directory.CreateDirectory(FsHwPaths.root dir) |> ignore
    FsHwPaths.atomicWriteAllText (DaemonIdentity.identityFilePath dir) producer

let private subjectOf (dir: string) (exclude: string list) : Requirements.Subject =
    { Tree = (TreeHash.compute dir exclude).Hash
      Producer = producer
      OutcomeKeys = Map.empty }

/// File `outcome` for `key` in `dir`'s shadow store, as a publication would.
let private record (dir: string) (key: ActionKey.ActionKey) (outcome: LookupVerdict.Outcome) =
    let store = Lookup.storeAt dir ignoreLog

    let r =
        OutcomeStore.Record.ofAction key "subject" outcome producer dir DateTimeOffset.UtcNow

    test <@ OutcomeStore.Through.file store r None @>

let private read (dir: string) =
    Lookup.ofRepo dir Requirements.Command.Check ignoreLog

[<Fact>]
let ``a recorded pass for every requirement of this tree is green`` () =
    withTempDir "pure-lookup-green" (fun dir ->
        checkout dir diagnosticsOnly
        let subject = subjectOf dir []
        record dir (Requirements.diagnosticsKey subject) LookupVerdict.Outcome.Pass

        test <@ read dir = Lookup.Read.Answered(subject.Tree, producer, LookupVerdict.Answer.Green) @>)

[<Fact>]
let ``a recorded fail is red`` () =
    withTempDir "pure-lookup-red" (fun dir ->
        checkout dir diagnosticsOnly
        let subject = subjectOf dir []
        record dir (Requirements.diagnosticsKey subject) LookupVerdict.Outcome.Fail

        test <@ read dir = Lookup.Read.Answered(subject.Tree, producer, LookupVerdict.Answer.Red [ "diagnostics" ]) @>)

[<Fact>]
let ``a record of another tree answers nothing: an edit since is missing`` () =
    withTempDir "pure-lookup-edited" (fun dir ->
        checkout dir diagnosticsOnly
        record dir (Requirements.diagnosticsKey (subjectOf dir [])) LookupVerdict.Outcome.Pass
        File.WriteAllText(source dir, "let a = 2")
        let now = subjectOf dir []

        test <@ read dir = Lookup.Read.Answered(now.Tree, producer, LookupVerdict.Answer.Missing [ "diagnostics" ]) @>)

[<Fact>]
let ``the configuration's exclude globs decide the tree, as the daemon's do`` () =
    withTempDir "pure-lookup-exclude" (fun dir ->
        checkout dir """{ "format": "off", "lint": false, "build": false, "exclude": [ "src/notes/**" ] }"""
        Directory.CreateDirectory(Path.Combine(dir, "src", "notes")) |> ignore
        let subject = subjectOf dir [ "src/notes/**" ]
        record dir (Requirements.diagnosticsKey subject) LookupVerdict.Outcome.Pass
        File.WriteAllText(Path.Combine(dir, "src", "notes", "n.md"), "an excluded edit")

        test <@ read dir = Lookup.Read.Answered(subject.Tree, producer, LookupVerdict.Answer.Green) @>)

[<Fact>]
let ``every configured plugin is required, recorded or not`` () =
    withTempDir "pure-lookup-plugins" (fun dir ->
        checkout dir """{ "format": "check", "lint": false, "build": false }"""
        let subject = subjectOf dir []
        record dir (Requirements.diagnosticsKey subject) LookupVerdict.Outcome.Pass

        test
            <@
                read dir = Lookup.Read.Answered(
                    subject.Tree,
                    producer,
                    LookupVerdict.Answer.Missing [ "plugin:format-check" ]
                )
            @>)

[<Fact>]
let ``without a recorded producer no key can be named`` () =
    withTempDir "pure-lookup-no-producer" (fun dir ->
        checkout dir diagnosticsOnly
        File.Delete(DaemonIdentity.identityFilePath dir)

        test <@ read dir = Lookup.Read.NoProducer (subjectOf dir []).Tree @>)

[<Fact>]
let ``a configuration that is not JSON leaves the requirements unknown`` () =
    withTempDir "pure-lookup-unconfigured" (fun dir ->
        checkout dir "{ not json"

        test
            <@
                match read dir with
                | Lookup.Read.Unconfigured reason -> reason.StartsWith FsHwPaths.ConfigFileName
                | _ -> false
            @>)
