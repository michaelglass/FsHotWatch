/// Project-attributed runtime coverage for additive test-impact selection.
/// Separate from `coverage_points`, whose raw hit counts retain high-water semantics.
module TestPrune.CoverageImpact

open System
open System.Globalization
open System.IO
open System.Xml.Linq

type CoverageReportHealth =
    | Complete
    | Empty
    | Malformed of detail: string
    | Incomplete of rejectedRows: int

/// `BaselineTree` is the tree exercised by the trailing run, not the current tree.
type ProjectCoverageSnapshot =
    private
        { TestProject: string
          SourceFiles: Set<string>
          BaselineTree: string
          ObservedAt: DateTimeOffset
          Health: CoverageReportHealth
          RejectedPaths: Set<string> }

type ProjectSelectionReason =
    | AstImpact
    | RuntimeCoverage of sourceFile: string
    | CoverageMissing
    | CoverageFromUnexpectedBaseline of observedBaseline: string * expectedBaseline: string
    | CoverageExpired of age: TimeSpan * maximumAge: TimeSpan
    | CoverageClockSkew of observedAt: DateTimeOffset * now: DateTimeOffset
    | CoverageReportEmpty
    | CoverageReportMalformed of detail: string
    | CoverageReportIncomplete of rejectedRows: int
    | CoveragePathRejected of path: string

module ProjectSelectionReason =
    let describe reason =
        match reason with
        | AstImpact -> "AST dependency"
        | RuntimeCoverage sourceFile -> $"runtime coverage of '%s{sourceFile}'"
        | CoverageMissing -> "coverage snapshot missing; widened selection"
        | CoverageFromUnexpectedBaseline(observed, expected) ->
            $"coverage baseline '%s{observed}' is not expected baseline '%s{expected}'; widened selection"
        | CoverageExpired(age, maximumAge) -> $"coverage snapshot age %O{age} exceeds %O{maximumAge}; widened selection"
        | CoverageClockSkew(observedAt, now) ->
            $"coverage timestamp %O{observedAt} is later than %O{now}; widened selection"
        | CoverageReportEmpty -> "coverage report is empty; widened selection"
        | CoverageReportMalformed detail -> $"coverage report is malformed (%s{detail}); widened selection"
        | CoverageReportIncomplete rejected -> $"coverage report rejected %d{rejected} row(s); widened selection"
        | CoveragePathRejected path ->
            $"coverage path '%s{path}' is invalid or outside the repository; widened selection"

type CoverageFreshnessPolicy =
    { RepoRoot: string
      ExpectedBaseline: string
      Now: DateTimeOffset
      MaximumAge: TimeSpan }

type ProjectSelection = Map<string, ProjectSelectionReason list>

let private canonicalizePath (repoRoot: string) (path: string) : string option =
    try
        if String.IsNullOrWhiteSpace path then
            None
        else
            let slashNormalized = path.Replace('\\', '/')

            let foreignDriveRoot =
                slashNormalized.Length >= 3
                && Char.IsLetter slashNormalized[0]
                && slashNormalized[1] = ':'
                && slashNormalized[2] = '/'

            let foreignUncRoot = slashNormalized.StartsWith("//", StringComparison.Ordinal)

            if not (OperatingSystem.IsWindows()) && (foreignDriveRoot || foreignUncRoot) then
                None
            else
                let root = Path.GetFullPath repoRoot
                let normalized = slashNormalized.Replace('/', Path.DirectorySeparatorChar)

                let absolute =
                    if Path.IsPathRooted normalized then
                        Path.GetFullPath normalized
                    else
                        Path.GetFullPath(normalized, root)

                let relative = Path.GetRelativePath(root, absolute)

                if
                    relative = ".."
                    || relative.StartsWith($"..%c{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || Path.IsPathRooted relative
                then
                    None
                else
                    let canonical = relative.Replace(Path.DirectorySeparatorChar, '/')

                    if canonical.StartsWith("./", StringComparison.Ordinal) then
                        Some(canonical.Substring 2)
                    else
                        Some canonical
    with
    | :? ArgumentException
    | :? NotSupportedException
    | :? PathTooLongException -> None

let private requireNonBlank parameterName value =
    if String.IsNullOrWhiteSpace value then
        invalidArg parameterName "must not be blank"

let private validateIdentity repoRoot testProject baselineTree =
    requireNonBlank (nameof repoRoot) repoRoot
    requireNonBlank (nameof testProject) testProject
    requireNonBlank (nameof baselineTree) baselineTree

let private createValidatedSnapshot repoRoot testProject baselineTree observedAt health sourceFiles rejectedPaths =
    validateIdentity repoRoot testProject baselineTree

    let canonical, allRejected =
        sourceFiles
        |> Seq.fold
            (fun (accepted, rejected) path ->
                match canonicalizePath repoRoot path with
                | Some value -> Set.add value accepted, rejected
                | None -> accepted, Set.add path rejected)
            (Set.empty, rejectedPaths)

    let extraRejected = allRejected.Count - rejectedPaths.Count

    let finalHealth =
        match health, extraRejected with
        | Complete, extra when extra > 0 -> Incomplete extra
        | Incomplete count, extra when extra > 0 -> Incomplete(count + extra)
        | existing, _ -> existing

    { TestProject = testProject
      SourceFiles = canonical
      BaselineTree = baselineTree
      ObservedAt = observedAt
      Health = finalHealth
      RejectedPaths = allRejected }

/// Construction funnel for restored snapshots; noncanonical source paths cannot
/// bypass validation merely because they came from persistence rather than XML.
let createSnapshot repoRoot testProject baselineTree observedAt health sourceFiles =
    createValidatedSnapshot repoRoot testProject baselineTree observedAt health sourceFiles Set.empty

let snapshotSourceFiles snapshot = snapshot.SourceFiles
let snapshotHealth snapshot = snapshot.Health

let private xn value = XName.Get value

let private attributeValue name (element: XElement) =
    let attribute = element.Attribute(xn name)
    if isNull attribute then None else Some attribute.Value

let private tryInt (value: string) =
    match Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
    | true, parsed -> Some parsed
    | false, _ -> None

/// Validate and attribute a report. Unhealthy reports remain represented so
/// selection widens explicitly rather than reading absent evidence as no impact.
let snapshotFromCobertura repoRoot testProject baselineTree observedAt xml : ProjectCoverageSnapshot =
    validateIdentity repoRoot testProject baselineTree

    let empty health =
        createValidatedSnapshot repoRoot testProject baselineTree observedAt health Set.empty Set.empty

    if String.IsNullOrWhiteSpace xml then
        empty Empty
    else
        try
            let document = XDocument.Parse xml
            let root = document.Root

            if isNull root || root.Name.LocalName <> "coverage" then
                invalidArg (nameof xml) "root element must be <coverage>"

            let classes = document.Descendants(xn "class") |> Seq.toList

            let lines =
                classes |> Seq.collect (fun cls -> cls.Descendants(xn "line")) |> Seq.toList

            if lines.IsEmpty then
                if classes.IsEmpty then
                    empty Empty
                else
                    empty (Incomplete classes.Length)
            else
                let accepted, rejected, rejectedPaths =
                    lines
                    |> List.fold
                        (fun (files, rejected, rejectedPaths) line ->
                            let reportedPath =
                                line.Ancestors(xn "class")
                                |> Seq.tryHead
                                |> Option.bind (attributeValue "filename")

                            let canonicalPath = reportedPath |> Option.bind (canonicalizePath repoRoot)

                            let parsed =
                                match line.Ancestors(xn "class") |> Seq.tryHead with
                                | None -> None
                                | Some cls ->
                                    match
                                        attributeValue "filename" cls,
                                        attributeValue "number" line,
                                        attributeValue "hits" line
                                    with
                                    | Some _, Some number, Some hits ->
                                        match canonicalPath, tryInt number, tryInt hits with
                                        | Some canonical, Some lineNumber, Some hitCount when
                                            lineNumber > 0 && hitCount >= 0
                                            ->
                                            Some(canonical, hitCount)
                                        | _ -> None
                                    | _ -> None

                            match parsed with
                            | Some(file, hits) ->
                                (if hits > 0 then Set.add file files else files), rejected, rejectedPaths
                            | None ->
                                let paths =
                                    match reportedPath, canonicalPath with
                                    | Some path, None -> Set.add path rejectedPaths
                                    | _ -> rejectedPaths

                                files, rejected + 1, paths)
                        (Set.empty, 0, Set.empty)

                createValidatedSnapshot
                    repoRoot
                    testProject
                    baselineTree
                    observedAt
                    (if rejected = 0 then Complete else Incomplete rejected)
                    accepted
                    rejectedPaths
        with ex ->
            empty (Malformed ex.Message)

let private addReason project reason (selection: ProjectSelection) =
    selection
    |> Map.change project (fun current ->
        let reasons = current |> Option.defaultValue []

        if List.contains reason reasons then
            Some reasons
        else
            Some(reasons @ [ reason ]))

let private reasonsForSnapshot policy changedFiles snapshot =
    if snapshot.BaselineTree <> policy.ExpectedBaseline then
        [ CoverageFromUnexpectedBaseline(snapshot.BaselineTree, policy.ExpectedBaseline) ]
    else
        let age = policy.Now - snapshot.ObservedAt

        if age > policy.MaximumAge then
            [ CoverageExpired(age, policy.MaximumAge) ]
        else
            let rejectedPathReasons =
                snapshot.RejectedPaths
                |> Set.toList
                |> List.sort
                |> List.map CoveragePathRejected

            match snapshot.Health with
            | Empty -> rejectedPathReasons @ [ CoverageReportEmpty ]
            | Malformed detail -> rejectedPathReasons @ [ CoverageReportMalformed detail ]
            | Incomplete rejected -> rejectedPathReasons @ [ CoverageReportIncomplete rejected ]
            | Complete ->
                rejectedPathReasons
                @ (Set.intersect changedFiles snapshot.SourceFiles
                   |> Set.toList
                   |> List.sort
                   |> List.map RuntimeCoverage)

/// Union AST attribution with the newest usable trailing snapshot. A future
/// candidate never supersedes a valid older one; if all are future, clock skew widens.
let selectProjects policy allTestProjects astSelectedProjects changedFiles snapshots : ProjectSelection =
    requireNonBlank "policy.RepoRoot" policy.RepoRoot
    requireNonBlank "policy.ExpectedBaseline" policy.ExpectedBaseline

    if policy.MaximumAge < TimeSpan.Zero then
        invalidArg "policy.MaximumAge" "must not be negative"

    allTestProjects |> List.iter (requireNonBlank (nameof allTestProjects))
    astSelectedProjects |> List.iter (requireNonBlank (nameof astSelectedProjects))

    let canonicalChanges, rejectedChanges =
        changedFiles
        |> List.fold
            (fun (accepted, rejected) path ->
                match canonicalizePath policy.RepoRoot path with
                | Some canonical -> Set.add canonical accepted, rejected
                | None -> accepted, path :: rejected)
            (Set.empty, [])

    let snapshotsByProject = snapshots |> List.groupBy _.TestProject |> Map.ofList

    let initial =
        astSelectedProjects
        |> List.distinct
        |> List.fold (fun selected project -> addReason project AstImpact selected) Map.empty

    allTestProjects
    |> List.distinct
    |> List.fold
        (fun selected project ->
            let selected =
                rejectedChanges
                |> List.sort
                |> List.fold (fun state path -> addReason project (CoveragePathRejected path) state) selected

            match Map.tryFind project snapshotsByProject with
            | None -> addReason project CoverageMissing selected
            | Some candidates ->
                let usable =
                    candidates |> List.filter (fun candidate -> candidate.ObservedAt <= policy.Now)

                match usable |> List.sortByDescending _.ObservedAt |> List.tryHead with
                | Some snapshot ->
                    reasonsForSnapshot policy canonicalChanges snapshot
                    |> List.fold (fun state reason -> addReason project reason state) selected
                | None ->
                    let future = candidates |> List.minBy _.ObservedAt
                    addReason project (CoverageClockSkew(future.ObservedAt, policy.Now)) selected)
        initial
