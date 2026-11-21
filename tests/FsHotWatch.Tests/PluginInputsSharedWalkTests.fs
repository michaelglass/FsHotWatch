/// One walk of the repository root serves every declared pattern (E5, AUTOMATION-1696): each
/// plugin's key over the shared scope is the key its own walk gave before. The oracle is that
/// per-plugin walk, kept here only, as `PluginInputs` computed it before the scope was shared.
module FsHotWatch.Tests.PluginInputsSharedWalkTests

open System
open System.IO
open Xunit
open FsCheck
open FsCheck.FSharp
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

// --- the oracle: one walk per pattern, as before ------------------------------------------

let private oracleMatches (repoRoot: string) (pattern: WatchScope.FilePattern) =
    let root = Path.GetFullPath repoRoot

    let relative (abs: string) =
        Path.GetRelativePath(root, abs).Replace('\\', '/')

    let walked = SafeWalk.walk PluginInputs.ScopeExcludedDirs "*" root

    let files =
        walked.Files
        |> List.map _.FullName
        |> List.filter (WatchScope.FilePattern.matches pattern)
        |> List.map (fun abs -> relative abs, abs)
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

    let holes =
        walked.Skipped
        |> List.map (fun s -> relative s.Path)
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    files, holes

let private oracleDigest (repoRoot: string) (spec: PluginInputs.InputSpec) =
    match spec with
    | PluginInputs.InputSpec.Matching(pattern, entry) ->
        let files, holes = oracleMatches repoRoot pattern
        let config = FsHwPaths.configFile (Path.GetFullPath repoRoot)

        let toolchain =
            VerdictInputs.toolKnownInputs repoRoot
            |> List.filter (fun abs -> abs <> config)
            |> List.map (fun abs ->
                "toolchain:"
                + Path.GetRelativePath(Path.GetFullPath repoRoot, abs).Replace('\\', '/'),
                ContentHash.ofFile abs)

        (files |> List.map (fun (rel, abs) -> "match:" + rel, ContentHash.ofFile abs))
        @ (holes
           |> List.map (fun rel -> "hole:" + rel + "/", ContentHash.UnhashableContent))
        @ [ "entry", entry ]
        @ toolchain
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
        |> TreeHash.hashEntries
    // A load closure reads the files its scripts name, never the walk, so sharing the walk
    // cannot change it: its digest is its own.
    | PluginInputs.InputSpec.LoadClosure _ -> PluginInputs.digest repoRoot spec

let private oracleKey (repoRoot: string) (producer: string) (name: string) (specs: PluginInputs.InputSpec list) =
    ActionKey.create
        "plugin-inputs"
        [ "plugin", name
          "producer", producer
          yield!
              specs
              |> List.mapi (fun i spec ->
                  "inputs." + i.ToString(Globalization.CultureInfo.InvariantCulture), oracleDigest repoRoot spec) ]

// --- generated trees and plugin sets ------------------------------------------------------

/// Files a pattern may or may not match, in and out of the scope: build output, tooling
/// directories, case variants, nested copies, and a toolchain file every key includes.
let private paths =
    [ "build.fsx"
      "B.FSX"
      "scripts/x.fsx"
      "src/A/a.fs"
      "CHANGELOG.md"
      "src/A/CHANGELOG.md"
      "docs/changelog.md"
      "README.md"
      "x.ratchet.json"
      "src/A/bin/Debug/q.fsx"
      "src/A/obj/r.fsx"
      "node_modules/n.fsx"
      ".fshw/s.fsx"
      ".workspaces/w/t.fsx"
      "global.json"
      "Directory.Build.props" ]

let private patterns =
    [ "*.fsx"; "CHANGELOG.md"; "*.md"; "README.md"; "*.ratchet.json"; "*.json" ]

type private Case =
    { Files: string list
      Unreadable: string option
      Plugins: (string * string list) list }

let private caseGen: Gen<Case> =
    gen {
        let! files = Gen.subListOf paths
        let! unreadable = Gen.frequency [ 4, Gen.constant None; 1, Gen.elements [ Some "src/A"; Some "scripts" ] ]
        let! count = Gen.choose (1, 4)

        let! plugins =
            [ 0 .. count - 1 ]
            |> List.map (fun i ->
                gen {
                    let! specCount = Gen.choose (1, 2)
                    let! specPatterns = Gen.listOfLength specCount (Gen.elements patterns)
                    return "p" + string i, specPatterns
                })
            |> Gen.sequenceToList

        return
            { Files = files
              Unreadable = unreadable
              Plugins = plugins }
    }

let private pluginOf (name: string, specPatterns: string list) : PluginInputs.RequiredPlugin =
    let specs =
        specPatterns
        |> List.mapi (fun i p ->
            PluginInputs.InputSpec.Matching(WatchScope.FilePattern.parse p, name + "-entry-" + string i))

    { Name = name
      Eligibility = PluginInputs.LookupEligibility.DeclaredInputs(List.head specs, List.tail specs) }

[<Fact(Timeout = PropertyTestCapMs)>]
let ``every plugin's key over one shared walk is the key its own walk gives`` () =
    let compared = ref 0
    let withHoles = ref 0

    Check.One(
        Config.QuickThrowOnFailure.WithMaxTest(120),
        Prop.forAll (Arb.fromGen caseGen) (fun case ->
            withTempDir "plugin-inputs-shared" (fun root ->
                for rel in case.Files do
                    let path = Path.Combine(root, rel)
                    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                    File.WriteAllText(path, rel)

                let locked =
                    match case.Unreadable with
                    | Some rel when not (OperatingSystem.IsWindows()) && Directory.Exists(Path.Combine(root, rel)) ->
                        let dir = Path.Combine(root, rel)
                        File.SetUnixFileMode(dir, UnixFileMode.None)
                        Some dir
                    | _ -> None

                try
                    let scope = lazy (PluginInputs.scopeOf root)

                    let agree =
                        case.Plugins
                        |> List.forall (fun plugin ->
                            let p = pluginOf plugin
                            let specs = snd plugin |> List.length

                            let expected =
                                match p.Eligibility with
                                | PluginInputs.LookupEligibility.DeclaredInputs(first, rest) ->
                                    oracleKey root "prod" p.Name (first :: rest)
                                | PluginInputs.LookupEligibility.NoDeclaredInputs _ ->
                                    failwith "declared by construction"

                            compared.Value <- compared.Value + specs

                            match (PluginInputs.keyedIn scope "prod" p).Keying with
                            | PluginInputs.Keying.DeclaredInputs actual -> actual = expected
                            | PluginInputs.Keying.WholeTree -> false)

                    if locked.IsSome then
                        withHoles.Value <- withHoles.Value + 1

                    agree
                finally
                    locked
                    |> Option.iter (fun dir ->
                        File.SetUnixFileMode(
                            dir,
                            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                        ))))
    )

    // A property that never compares a hole, or barely any pattern, proves little about either.
    if
        compared.Value < 200
        || (not (OperatingSystem.IsWindows()) && withHoles.Value < 5)
    then
        failwith
            $"only {compared.Value} patterns compared, {withHoles.Value} cases with a hole: the generator misses a region"

[<Fact>]
let ``a shared scope walks the root once, however many plugins it keys`` () =
    withTempDir "plugin-inputs-once" (fun root ->
        File.WriteAllText(Path.Combine(root, "build.fsx"), "x")
        let walks = ref 0

        let scope =
            lazy
                (walks.Value <- walks.Value + 1
                 PluginInputs.scopeOf root)

        for name, pattern in [ "a", "*.fsx"; "b", "*.md"; "c", "*.json" ] do
            PluginInputs.keyedIn scope "prod" (pluginOf (name, [ pattern ])) |> ignore

        if walks.Value <> 1 then
            failwith $"the root was walked {walks.Value} times for three plugins")
