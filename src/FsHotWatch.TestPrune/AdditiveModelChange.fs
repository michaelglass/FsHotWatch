/// AUTOMATION-1900: whether a project-model change may narrow "no whole-project run under the
/// current project model", decided on ANALYSES rather than on the project-file diff.
///
/// A land that adds a source file changes its project's compile-input identity, so every
/// project whose identity moved has no whole-project run under the current model and runs
/// whole (`EarnedEvidence.wholeProjectGap`). Narrowing that by the project-file diff alone is
/// unsound: F# resolves names in compile order, so a file inserted before an UNCHANGED later
/// file can rebind a name there (a shadowing module in an opened namespace, a new
/// `[<AutoOpen>]` module, an extension member, an overload) with no text change in it.
///
/// So the change is narrowable only when
///   (a) it ADDS compile items and nothing else: no project added or removed, no item removed
///       or reordered, no define, reference (package) or other compiler-option change; and
///   (b) every pre-existing file of each changed project and of every project depending on
///       one has the same resolved symbol uses (`SymbolUses`) as its analysis at the baseline
///       tree T, or is itself in the diff T to now. A rebinding changes that file's symbol uses
///       (the real-compiler fixtures in `AdditiveRebindingFixtureTests` show it per mechanism),
///       so it counts as changed and its dependents' tests are selected.
///
/// `judge` is PURE and decides nothing on its own: today it only feeds the shadow line
/// (`describe`), and selection is unchanged.
module FsHotWatch.TestPrune.AdditiveModelChange

open System
open System.IO
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer

/// One resolved use in a file: the symbol it is in, the symbol it resolved to, and how.
type SymbolUse =
    { From: string
      To: string
      Kind: DependencyKind }

/// A file's resolved symbol uses, from its analysis.
module SymbolUses =
    /// The uses `dependencies` record. The edge's `Source` (which analyser emitted it) is not
    /// part of a use: two analysers naming one edge resolve the same name once.
    let ofDependencies (dependencies: Dependency list) : Set<SymbolUse> =
        dependencies
        |> List.map (fun d ->
            { From = d.FromSymbol
              To = d.ToSymbol
              Kind = d.Kind })
        |> Set.ofList

    /// The uses `analysis` records.
    let ofAnalysis (analysis: AnalysisResult) : Set<SymbolUse> = ofDependencies analysis.Dependencies

/// What one project compiles, read from its compiler options with every in-repository path
/// relative to the checkout, so two checkouts of one tree read the same shape. References and
/// the remaining options are kept as digests: the decision only asks whether they moved, and a
/// baseline record files one shape per project (`TestBaseline`).
type ProjectShape =
    {
        /// The source files, repo-relative, in compile order.
        CompileItems: string list
        /// `--define:` symbols, sorted.
        Defines: string list
        /// The digest of the `-r:` references (packages and referenced projects' outputs), sorted.
        References: string
        /// The digest of every other compiler option, in order.
        Options: string
    }

module ProjectShape =
    let private prefixed (prefixes: string list) (option: string) =
        prefixes
        |> List.tryPick (fun prefix ->
            if option.StartsWith(prefix, StringComparison.Ordinal) then
                Some(option.Substring prefix.Length)
            else
                None)

    let private digest (values: string seq) =
        "sha256:" + FsHotWatch.CheckCache.sha256Hex (String.concat "\n" values)

    /// The shape `options` describe, read under the checkout `root`.
    let ofOptions (root: string) (options: FSharpProjectOptions) : ProjectShape =
        let relative = FsHotWatch.CheckCache.relativizeOption (Some root)

        let defines =
            options.OtherOptions
            |> Array.choose (prefixed [ "--define:"; "-d:" ])
            |> Array.sort
            |> Array.toList

        let references =
            options.OtherOptions
            |> Array.choose (prefixed [ "-r:"; "--reference:" ])
            |> Array.map relative
            |> Array.sort

        let others =
            options.OtherOptions
            |> Array.filter (fun option -> (prefixed [ "--define:"; "-d:"; "-r:"; "--reference:" ] option).IsNone)
            |> Array.map relative

        { CompileItems =
            options.SourceFiles
            |> Array.map (fun file -> Path.GetRelativePath(root, file).Replace('\\', '/'))
            |> Array.toList
          Defines = defines
          References = digest references
          Options = digest others }

/// A project's shape as the check results of one model generation carried it.
type ObservedShape =
    { Generation: int64 option
      Shape: ProjectShape }

/// Each project's shape under the model the plugin's check results came from, by project name:
/// what a test-baseline record files as its tree's compile model, and the "now" side of the
/// shadow decision (`AdditiveShadow`).
module ObservedShapes =
    /// `shapes` with the project `options` describe read again, unless it was already read
    /// under `generation`: a model generation's options are fixed, so one read per project
    /// and generation suffices.
    let observe
        (root: string)
        (generation: int64 option)
        (options: FSharpProjectOptions)
        (shapes: Map<string, ObservedShape>)
        : Map<string, ObservedShape> =
        let project = AnalysisRecords.projectNameOf options.ProjectFileName

        match Map.tryFind project shapes with
        | Some seen when generation.IsSome && seen.Generation = generation -> shapes
        | _ ->
            Map.add
                project
                { Generation = generation
                  Shape = ProjectShape.ofOptions root options }
                shapes

    /// The shapes alone.
    let current (shapes: Map<string, ObservedShape>) : Map<string, ProjectShape> =
        shapes |> Map.map (fun _ observed -> observed.Shape)

/// Everything `judge` decides from: the model at T and now, by project name, and every file's
/// symbol uses at T and now, by repo-relative path.
type Change =
    {
        Before: Map<string, ProjectShape>
        After: Map<string, ProjectShape>
        /// Each project's transitive dependents under the current model.
        Dependents: Map<string, Set<string>>
        UsesAtT: Map<string, Set<SymbolUse>>
        UsesNow: Map<string, Set<SymbolUse>>
        /// The files whose content differs between T and now.
        Diff: Set<string>
    }

/// Why a change may not narrow.
type Reason =
    /// No project's compile items grew: whatever moved the model, it was not an addition.
    | NoCompileItemsAdded
    | ProjectsAddedOrRemoved of added: string list * removed: string list
    | ItemsRemoved of project: string * removed: string list
    | ItemsReordered of project: string
    | DefinesChanged of project: string
    | ReferencesChanged of project: string
    | OptionsChanged of project: string
    /// A pre-existing file outside the diff resolves differently from its analysis at T.
    | SymbolUsesChanged of project: string * file: string
    | NoAnalysisAtT of project: string * file: string
    | NoAnalysisNow of project: string * file: string

/// What a narrowable change would narrow.
type Narrowing =
    {
        /// Each changed project and the compile items it gained, in compile order.
        Added: Map<string, string list>
        /// Every project whose pre-existing files were compared: the changed ones and their
        /// dependents.
        Checked: Set<string>
    }

type Verdict =
    | Narrowable of Narrowing
    | NotNarrowable of Reason

module Verdict =
    let isNarrowable (verdict: Verdict) =
        match verdict with
        | Narrowable _ -> true
        | NotNarrowable _ -> false

/// The compile items `after` gained over `before`, or why `project`'s change is not one.
let private additionsOf (project: string) (before: ProjectShape) (after: ProjectShape) : Result<string list, Reason> =
    let kept = Set.ofList after.CompileItems

    let removed =
        before.CompileItems |> List.filter (fun item -> not (kept.Contains item))

    let existed = Set.ofList before.CompileItems

    if before.Defines <> after.Defines then
        Error(DefinesChanged project)
    elif before.References <> after.References then
        Error(ReferencesChanged project)
    elif before.Options <> after.Options then
        Error(OptionsChanged project)
    elif not removed.IsEmpty then
        Error(ItemsRemoved(project, removed))
    elif after.CompileItems |> List.filter existed.Contains <> before.CompileItems then
        Error(ItemsReordered project)
    else
        Ok(after.CompileItems |> List.filter (fun item -> not (existed.Contains item)))

/// The first pre-existing file of `project` outside the diff whose uses differ from T's.
let private firstRebound (change: Change) (project: string) (items: string list) : Reason option =
    items
    |> List.filter (fun file -> not (change.Diff.Contains file))
    |> List.tryPick (fun file ->
        match Map.tryFind file change.UsesAtT, Map.tryFind file change.UsesNow with
        | None, _ -> Some(NoAnalysisAtT(project, file))
        | Some _, None -> Some(NoAnalysisNow(project, file))
        | Some atT, Some now when atT <> now -> Some(SymbolUsesChanged(project, file))
        | Some _, Some _ -> None)

/// Whether `change` may narrow, and if not, the first reason, judged project by project in
/// name order and file by file in compile order.
let judge (change: Change) : Verdict =
    let beforeProjects = change.Before |> Map.keys |> Set.ofSeq
    let afterProjects = change.After |> Map.keys |> Set.ofSeq

    if beforeProjects <> afterProjects then
        NotNarrowable(
            ProjectsAddedOrRemoved(
                Set.difference afterProjects beforeProjects |> Set.toList,
                Set.difference beforeProjects afterProjects |> Set.toList
            )
        )
    else
        // Each changed project's additions, or the first reason one is not an addition. A
        // project whose shape changed with no other reason gained items, so no entry is empty.
        let rec additions (acc: (string * string list) list) (changed: (string * ProjectShape) list) =
            match changed with
            | [] -> Ok(Map.ofList acc)
            | (project, before) :: rest ->
                match additionsOf project before change.After[project] with
                | Error why -> Error why
                | Ok items -> additions ((project, items) :: acc) rest

        match
            change.Before
            |> Map.toList
            |> List.filter (fun (project, before) -> change.After[project] <> before)
            |> additions []
        with
        | Error why -> NotNarrowable why
        | Ok added ->
            if added.IsEmpty then
                NotNarrowable NoCompileItemsAdded
            else
                let checkedProjects =
                    added
                    |> Map.keys
                    |> Seq.collect (fun project ->
                        Seq.append [ project ] (Map.tryFind project change.Dependents |> Option.defaultValue Set.empty))
                    |> Set.ofSeq

                match
                    checkedProjects
                    |> Set.toList
                    |> List.tryPick (fun project -> firstRebound change project change.Before[project].CompileItems)
                with
                | Some why -> NotNarrowable why
                | None ->
                    Narrowable
                        { Added = added
                          Checked = checkedProjects }

let private joined (values: string seq) = String.concat "," values

/// The verdict's shadow line. Every line starts `additive-model: `, a refusal carries one
/// `reason=` token, and a narrowable change names its projects, so a daemon log can be
/// counted and parsed by grep.
let describe (verdict: Verdict) : string =
    let body =
        match verdict with
        | Narrowable narrowing ->
            let projects = narrowing.Added |> Map.keys
            let added = narrowing.Added |> Map.values |> Seq.concat
            $"would-narrow projects=%s{joined projects} checked=%s{joined narrowing.Checked} added=%s{joined added}"
        | NotNarrowable why ->
            let reason =
                match why with
                | NoCompileItemsAdded -> "no-compile-items-added"
                | ProjectsAddedOrRemoved(added, removed) ->
                    $"projects-added-or-removed added=%s{joined added} removed=%s{joined removed}"
                | ItemsRemoved(project, removed) -> $"items-removed project=%s{project} items=%s{joined removed}"
                | ItemsReordered project -> $"items-reordered project=%s{project}"
                | DefinesChanged project -> $"defines-changed project=%s{project}"
                | ReferencesChanged project -> $"references-changed project=%s{project}"
                | OptionsChanged project -> $"options-changed project=%s{project}"
                | SymbolUsesChanged(project, file) -> $"symbol-uses-changed project=%s{project} file=%s{file}"
                | NoAnalysisAtT(project, file) -> $"no-analysis-at-t project=%s{project} file=%s{file}"
                | NoAnalysisNow(project, file) -> $"no-analysis-now project=%s{project} file=%s{file}"

            $"not-narrowable reason=%s{reason}"

    "additive-model: " + body
