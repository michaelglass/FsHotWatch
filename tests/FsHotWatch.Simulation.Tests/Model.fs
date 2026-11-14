/// The simulated world and its reference interpreter.
///
/// A tree is a map from repository-relative path to content. Content is a version token
/// with at most one FAULT MARKER; the reference interpreter decides every requirement from
/// the markers alone, from scratch, with no store and no memory. That is the "clean,
/// hermetic, from-scratch run" the plan's theorem compares a lookup with.
module FsHotWatch.Simulation.Model

open System
open System.Security.Cryptography
open System.Text

/// A fault a file can carry, and the requirement it reddens.
[<RequireQualifiedAccess>]
type Fault =
    | Build
    | Lint
    | Format
    | TypeError
    | TestBreak

module Fault =
    let all =
        [ Fault.Build; Fault.Lint; Fault.Format; Fault.TypeError; Fault.TestBreak ]

    let marker (fault: Fault) : string =
        match fault with
        | Fault.Build -> "!build"
        | Fault.Lint -> "!lint"
        | Fault.Format -> "!format"
        | Fault.TypeError -> "!type"
        | Fault.TestBreak -> "!test"

/// A file's content: a version, and the fault it carries when it carries one.
type Content = { Version: int; Fault: Fault option }

module Content =
    let render (c: Content) : string =
        match c.Fault with
        | Some fault -> $"v%d{c.Version}%s{Fault.marker fault}"
        | None -> $"v%d{c.Version}"

/// A tree: every file, by repository-relative path.
type Tree = Map<string, Content>

/// The plugins the simulated configuration requires, by the name `ShadowVerdict` keys.
let plugins = [ "build"; "lint"; "format" ]

/// The source projects; each has a test project `<name>.Tests`.
let sourceProjects = [ "A"; "B" ]

let testProjectOf (source: string) = source + ".Tests"

let testProjects = sourceProjects |> List.map testProjectOf

/// Every path a tree holds: two source files per project and one test file.
let paths =
    [ for p in sourceProjects do
          $"%s{p}/One.fs"
          $"%s{p}/Two.fs"
          $"%s{testProjectOf p}/Tests.fs" ]

let initialTree: Tree =
    paths |> List.map (fun p -> p, { Version = 0; Fault = None }) |> Map.ofList

/// The paths a test project's run reads: its own and its source project's.
let inputsOf (testProject: string) : string list =
    let source = testProject.Substring(0, testProject.Length - ".Tests".Length)

    paths
    |> List.filter (fun p -> p.StartsWith(source + "/") || p.StartsWith(testProject + "/"))

let private sha256 (text: string) =
    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes text))

/// The digest of `files` of `tree`, in path order: path and rendered content, length-prefixed.
let digestOf (files: string list) (tree: Tree) : string =
    files
    |> List.sort
    |> List.map (fun p ->
        let c = Content.render tree[p]
        $"%d{p.Length}:%s{p}|%d{c.Length}:%s{c}|")
    |> String.concat ""
    |> sha256

/// The tree digest: every file.
let treeDigest (tree: Tree) : string = digestOf paths tree

let private carries (fault: Fault) (files: string list) (tree: Tree) =
    files |> List.exists (fun p -> tree[p].Fault = Some fault)

/// The requirement names a clean run of `tree` finds failing, as `ShadowVerdict.requirements`
/// names them: `plugin:<name>`, `diagnostics`, `test:<project>`.
let cleanFailing (tree: Tree) : Set<string> =
    set
        [ if carries Fault.Build paths tree then
              "plugin:build"
          if carries Fault.Lint paths tree then
              "plugin:lint"
          if carries Fault.Format paths tree then
              "plugin:format"
          if carries Fault.TypeError paths tree then
              "diagnostics"
          for tp in testProjects do
              if carries Fault.TestBreak (inputsOf tp) tree then
                  "test:" + tp ]

/// Whether a clean run of `tree` fails the test project `testProject`.
let testFails (testProject: string) (tree: Tree) : bool =
    cleanFailing tree |> Set.contains ("test:" + testProject)
