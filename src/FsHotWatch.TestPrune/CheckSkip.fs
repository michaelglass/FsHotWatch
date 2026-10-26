/// `tests.projects[].checkSkipClasses`: test classes a `check` leaves out of a run that
/// widened the project to whole-project for a reason the change did not cause.
///
/// Impact selection is the seam a `check` relies on: a class runs when the change reaches
/// a symbol it covers. Some widenings ignore that seam and run a project in full anyway:
/// the first run of a daemon session, or runtime-coverage debt that names the project.
/// An expensive class that drives real repositories then runs on every such widening,
/// although nothing the change touched reaches it. A project names those classes here,
/// and a widened `check` runs it through `excludeFilterTemplate` without them.
///
/// The rule this module encodes, and nothing looser:
///
///   * `confirm` never skips: it runs every project in full, every class included.
///   * a class the run selected by name (a changed symbol it covers, a quarantined red,
///     an always-class, a symbol-free edit) runs, whatever widened the project.
///   * a project is run without the listed classes only when EVERY reason it runs whole
///     is a widening `checkSkipOn` lists. Any other reason (a dependency fanout by
///     default, an evidence gap, an outstanding project-level red, a hole in the impact
///     graph, an unreadable ledger, a missing full-suite baseline) runs them.
///   * a run that skipped classes is filtered: it is never a whole-project run, never a
///     full-suite scope, never published evidence, and its scope names what it skipped.
namespace FsHotWatch.TestPrune

open System
open System.Text.Json

/// A widening that may run a project without its `checkSkipClasses`.
[<RequireQualifiedAccess>]
type SkipWidening =
    /// No run has completed in this daemon session yet, so the zero-affected skip has
    /// nothing to be test-equivalent to and the run is widened to every project. Not the
    /// full-suite baseline (`FullSuiteCause.NoFullSuiteBaseline`): only a run that
    /// executes every project in full earns that, so a skip there could never end.
    | NoSessionBaseline
    /// Runtime coverage attributes a changed file to the project, so it runs in full.
    | RuntimeCoverageDebt
    /// A project the test project depends on changed its dependency fingerprint. Off by
    /// default: a binary change reaches every seam, the listed classes' included.
    | DependencyFanout

module SkipWidening =
    /// Every widening, in declaration order.
    let all: SkipWidening list =
        [ SkipWidening.NoSessionBaseline
          SkipWidening.RuntimeCoverageDebt
          SkipWidening.DependencyFanout ]

    /// The `.fshw.json` token.
    let token (widening: SkipWidening) : string =
        match widening with
        | SkipWidening.NoSessionBaseline -> "no-session-baseline"
        | SkipWidening.RuntimeCoverageDebt -> "runtime-coverage-debt"
        | SkipWidening.DependencyFanout -> "dependency-fanout"

    /// The widening a token names; `None` for a token this build does not know.
    let tryOfToken (wire: string) : SkipWidening option =
        all |> List.tryFind (fun widening -> token widening = wire)

    /// `checkSkipOn` when a project does not set it.
    let defaults: Set<SkipWidening> =
        Set.ofList [ SkipWidening.NoSessionBaseline; SkipWidening.RuntimeCoverageDebt ]

/// One reason a `check` launch runs a project whole.
[<RequireQualifiedAccess>]
type WholeCause =
    /// A widening a project may list in `checkSkipOn`.
    | Skippable of SkipWidening
    /// Any other reason; it always runs the listed classes. Says which, for the log.
    | Unskippable of reason: string

/// A project's declaration.
type CheckSkip =
    {
        /// The classes a widened `check` leaves out, as the runner names them.
        Classes: string list
        /// The widenings that may leave them out (`checkSkipOn`).
        On: Set<SkipWidening>
        /// The runner arguments that exclude classes; `{classes}` is replaced with the
        /// skipped classes joined by the project's `classJoin`.
        ExcludeTemplate: string
    }

module CheckSkip =
    /// The classes a launch leaves out of a project it runs whole for `causes`, keeping
    /// every class it `named`. Empty when it leaves out nothing.
    ///
    /// No cause leaves out nothing: a project nothing widened is not whole. One cause
    /// the declaration does not list is enough to run every class.
    let excluded (skip: CheckSkip) (causes: WholeCause list) (named: Set<string>) : string list =
        let listed (cause: WholeCause) =
            match cause with
            | WholeCause.Skippable widening -> Set.contains widening skip.On
            | WholeCause.Unskippable _ -> false

        if not (List.isEmpty causes) && List.forall listed causes then
            skip.Classes |> List.filter (fun cls -> not (Set.contains cls named))
        else
            []

    /// A reason a project runs every class, for the log line that says why it was not
    /// narrowed.
    let describeCause (cause: WholeCause) : string =
        match cause with
        | WholeCause.Skippable widening -> SkipWidening.token widening
        | WholeCause.Unskippable reason -> reason

    /// The class names in `element`, which must be a non-empty array of non-blank strings.
    let private classNames (key: string) (element: JsonElement) : Result<string list, string> =
        if element.ValueKind <> JsonValueKind.Array then
            Error $"%s{key}: %s{element.GetRawText()} — expected an array of class names"
        else
            let items = element.EnumerateArray() |> Seq.toList

            if List.isEmpty items then
                Error $"%s{key}: an empty list skips nothing — remove the key instead"
            elif
                items
                |> List.forall (fun item ->
                    item.ValueKind = JsonValueKind.String
                    && not (String.IsNullOrWhiteSpace(item.GetString())))
            then
                Ok(items |> List.map _.GetString() |> List.distinct)
            else
                Error $"%s{key}: %s{element.GetRawText()} — expected an array of non-blank class names"

    /// The widenings `element` names, which must be a non-empty array of known tokens.
    let private widenings (key: string) (element: JsonElement) : Result<Set<SkipWidening>, string> =
        let expected =
            SkipWidening.all
            |> List.map (fun widening -> $"\"%s{SkipWidening.token widening}\"")
            |> String.concat ", "

        let read (item: JsonElement) =
            if item.ValueKind = JsonValueKind.String then
                SkipWidening.tryOfToken (item.GetString())
            else
                None

        if element.ValueKind <> JsonValueKind.Array then
            Error $"%s{key}: %s{element.GetRawText()} — expected an array of %s{expected}"
        else
            let items = element.EnumerateArray() |> Seq.toList

            match items |> List.tryFind (read >> Option.isNone) with
            | Some unknown -> Error $"%s{key}: %s{unknown.GetRawText()} — expected one of %s{expected}"
            | None when List.isEmpty items ->
                Error $"%s{key}: an empty list never skips — remove checkSkipClasses instead"
            | None -> Ok(items |> List.choose read |> Set.ofList)

    /// Parse a `tests.projects[]` entry's `checkSkipClasses`, `checkSkipOn` and
    /// `excludeFilterTemplate`. `Ok None` when it declares no skip. Refuses anything it
    /// cannot honour, so a typo is a config error and never a skip that silently runs
    /// everything, or one that leaves out classes nobody asked it to.
    let parse (project: string) (entry: JsonElement) : Result<CheckSkip option, string> =
        let key name = $"tests.projects[%s{project}].%s{name}"
        let classesKey = key "checkSkipClasses"
        let onKey = key "checkSkipOn"
        let templateKey = key "excludeFilterTemplate"

        let property (name: string) =
            match entry.TryGetProperty name with
            | true, value when value.ValueKind <> JsonValueKind.Null -> Some value
            | _ -> None

        match property "checkSkipClasses", property "checkSkipOn" with
        | None, None -> Ok None
        | None, Some _ -> Error $"%s{onKey}: lists widenings for a project with no checkSkipClasses"
        | Some classes, on ->
            let template =
                match property "excludeFilterTemplate" with
                | None ->
                    Error
                        $"%s{classesKey}: needs %s{templateKey} (e.g. \"--filter-not-class {{classes}}\") — without one the classes cannot be left out"
                | Some value when value.ValueKind = JsonValueKind.String && value.GetString().Contains "{classes}" ->
                    Ok(value.GetString())
                | Some value ->
                    Error $"%s{templateKey}: %s{value.GetRawText()} — expected a string containing {{classes}}"

            match
                classNames classesKey classes,
                on
                |> Option.map (widenings onKey)
                |> Option.defaultValue (Ok SkipWidening.defaults),
                template
            with
            | Ok classes, Ok on, Ok template ->
                Ok(
                    Some
                        { Classes = classes
                          On = on
                          ExcludeTemplate = template }
                )
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error e
