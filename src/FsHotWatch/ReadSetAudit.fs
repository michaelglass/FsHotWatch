/// The read-set audit (ADR-059 §35 Change 1b, AUTOMATION-1634): Rattle's hazard check used
/// as a test, not as the cache.
///
/// A key is built from DECLARED inputs. The clean-recompute audit (`RecordAudit`, 1a) finds
/// an undeclared input only on a night it happened to differ between the stored run and the
/// clean one. This audit finds it structurally: trace every file an action actually READ
/// and fail when one is outside what its key declared, whatever that file holds.
///
/// Reads come from a tracer: FCS's file-system shim for a check (`ReadTrace`), strace for a
/// child process (`Strace`, Linux only), or a test. What an action declares comes from the
/// same walk its key folds (`CheckDeclared` for a check record).
///
/// WHAT A CLEAN AUDIT PROVES, and what it does not (ADR-059 §37): every file the traced run
/// read is one its key names. It says nothing about inputs that are not reads: FCS state
/// left by earlier checks in the same process, the process environment, the clock, or a
/// value derived from a file's PATH rather than its bytes (`[<CallerFilePath>]`, which bakes
/// the checkout's absolute path into a check's output while every byte it read is
/// declared). Those stay the clean-recompute audit's to find.
module FsHotWatch.ReadSetAudit

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.ReadTrace

/// The report format.
[<Literal>]
let ReportSchema = "fshw-read-set-audit-v1"

let private full (path: string) =
    Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)

let private isUnder (directory: string) (path: string) =
    path = directory
    || path.StartsWith(directory + string Path.DirectorySeparatorChar, StringComparison.Ordinal)

/// What an action's key names: files, and directories whose whole content it names (a
/// toolchain identified by its closure, a declared tree).
type Declared =
    { Files: Set<string>
      Directories: string list }

module Declared =
    let empty = { Files = Set.empty; Directories = [] }

    let ofFiles (files: string seq) =
        { empty with
            Files = files |> Seq.map full |> Set.ofSeq }

    let withDirectories (directories: string seq) (declared: Declared) =
        { declared with
            Directories = declared.Directories @ (directories |> Seq.map full |> List.ofSeq) }

    /// `declared` without `file`: what a key that left `file` out would declare.
    let without (file: string) (declared: Declared) =
        { declared with
            Files = Set.remove (full file) declared.Files }

    let covers (declared: Declared) (path: string) =
        declared.Files.Contains path
        || declared.Directories |> List.exists (fun d -> isUnder d path)

/// Reads that are not an action's inputs by any key, each with the reason it is exempt.
/// Every exempted read is counted in the report under its name, so nothing hides here.
[<NoComparison; NoEquality>]
type Exemption =
    {
        Name: string
        Reason: string
        /// Whether a read the action's declaration does not cover is exempt.
        Matches: Declared -> Read -> bool
    }

module Exemption =
    let underDirectories (name: string) (reason: string) (directories: string list) =
        let directories = directories |> List.map full

        { Name = name
          Reason = reason
          Matches = fun _ read -> directories |> List.exists (fun d -> isUnder d read.Path) }

    /// A Linux child process's loader, libc, locale and kernel interfaces: the runner image,
    /// which a CI job is pinned to, not anything a key in this repository names.
    let linuxImage: Exemption list =
        [ underDirectories
              "os-image"
              "the runner image's loader, libraries and programs: identified by the image, not by a key"
              [ "/etc/ld.so.cache"
                "/etc/ld.so.preload"
                "/lib"
                "/lib64"
                "/usr/lib"
                "/usr/lib64"
                "/usr/libexec"
                "/sbin"
                "/usr/sbin"
                "/usr/local/sbin"
                "/usr/local/bin"
                "/bin"
                "/usr/bin"
                "/usr/share/locale"
                "/etc/localtime" ]
          underDirectories "kernel" "kernel interfaces, not files" [ "/proc"; "/sys"; "/dev" ] ]

/// One traced run of one action.
type ActionReads =
    { Action: string
      Declared: Declared
      Reads: Read list }

/// A read outside the action's declaration: a key that missed an input.
type Undeclared =
    { Action: string
      Path: string
      Kind: ReadKind }

type ActionTally =
    {
        Action: string
        /// Distinct paths read.
        Reads: int
        /// Of those, not declared and not exempt.
        Undeclared: int
    }

[<RequireQualifiedAccess>]
type Verdict =
    /// Every action read something, and every read was declared or exempt.
    | Clean
    /// An action read a file its key did not declare.
    | Undeclared
    /// No action, or an action whose tracer saw no read: no answer, never clean.
    | Vacuous

type Report =
    {
        Actions: ActionTally list
        Undeclared: Undeclared list
        /// Reads passed by each exemption, by name.
        Exempted: (string * int) list
    }

    member this.Verdict =
        if not this.Undeclared.IsEmpty then
            Verdict.Undeclared
        elif this.Actions.IsEmpty || this.Actions |> List.exists (fun a -> a.Reads = 0) then
            Verdict.Vacuous
        else
            Verdict.Clean

/// 0 clean, 1 undeclared, 3 vacuous (as `RecordAudit`).
let exitCode (verdict: Verdict) : int =
    match verdict with
    | Verdict.Clean -> 0
    | Verdict.Undeclared -> 1
    | Verdict.Vacuous -> 3

/// Audit each action's reads against its declaration. A path read more than once is one
/// read; when it was both opened and probed, the open is reported.
let audit (exemptions: Exemption list) (actions: ActionReads list) : Report =
    let exempted = Collections.Generic.Dictionary<string, int>()

    let perAction =
        actions
        |> List.map (fun a ->
            let distinct =
                a.Reads
                |> List.groupBy _.Path
                |> List.map (fun (path, reads) ->
                    path,
                    (if reads |> List.exists (fun r -> r.Kind = ReadKind.Open) then
                         ReadKind.Open
                     else
                         ReadKind.Probe))

            let undeclared =
                distinct
                |> List.filter (fun (path, kind) ->
                    if Declared.covers a.Declared path then
                        false
                    else
                        let read = { Path = path; Kind = kind }

                        match exemptions |> List.tryFind (fun e -> e.Matches a.Declared read) with
                        | Some e ->
                            exempted[e.Name] <-
                                (match exempted.TryGetValue e.Name with
                                 | true, n -> n
                                 | _ -> 0)
                                + 1

                            false
                        | None -> true)
                |> List.map (fun (path, kind) ->
                    { Undeclared.Action = a.Action
                      Path = path
                      Kind = kind })

            { ActionTally.Action = a.Action
              Reads = distinct.Length
              Undeclared = undeclared.Length },
            undeclared)

    { Actions = perAction |> List.map fst
      Undeclared = perAction |> List.collect snd
      Exempted =
        exemptions
        |> List.choose (fun e ->
            match exempted.TryGetValue e.Name with
            | true, n -> Some(e.Name, n)
            | _ -> None) }

let private kindToken (kind: ReadKind) =
    match kind with
    | ReadKind.Open -> "open"
    | ReadKind.Probe -> "probe"

let private verdictToken (verdict: Verdict) =
    match verdict with
    | Verdict.Clean -> "clean"
    | Verdict.Undeclared -> "undeclared"
    | Verdict.Vacuous -> "vacuous"

/// The report as one `fshw-read-set-audit-v1` JSON object.
let toJson (report: Report) : string =
    let o = JsonObject()
    o["schema"] <- ReportSchema
    o["verdict"] <- verdictToken report.Verdict
    o["exitCode"] <- exitCode report.Verdict
    let actions = JsonArray()

    for a in report.Actions do
        let e = JsonObject()
        e["action"] <- a.Action
        e["reads"] <- a.Reads
        e["undeclared"] <- a.Undeclared
        actions.Add e

    o["actions"] <- actions
    let undeclared = JsonArray()

    for u in report.Undeclared do
        let e = JsonObject()
        e["action"] <- u.Action
        e["path"] <- u.Path
        e["kind"] <- kindToken u.Kind
        undeclared.Add e

    o["undeclared"] <- undeclared
    let exempted = JsonObject()

    for name, n in report.Exempted do
        exempted[name] <- n

    o["exempted"] <- exempted
    o.ToJsonString(JsonSerializerOptions(WriteIndented = true))

/// The report for a person.
let render (report: Report) : string list =
    [ for a in report.Actions do
          $"  %-40s{a.Action} read %d{a.Reads}, undeclared %d{a.Undeclared}"
      for u in report.Undeclared do
          $"  UNDECLARED %s{kindToken u.Kind} %s{u.Path} by %s{u.Action}"
      for name, n in report.Exempted do
          $"  exempt (%s{name}): %d{n}"
      let verdict =
          match report.Verdict with
          | Verdict.Clean -> "CLEAN: every read was declared by its action's key"
          | Verdict.Undeclared ->
              $"UNDECLARED: %d{report.Undeclared.Length} read(s) outside their action's key: a key missed an input"
          | Verdict.Vacuous -> "VACUOUS: an action's tracer saw no read; no answer"

      $"read-set audit: %s{verdict}" ]

/// What a check record's key declares (`CheckRecords`, `CheckCache.upstreamFingerprintsWith`):
/// the checked file and the files before it in its project's compile order, every `-r:`
/// assembly (a referenced project's output among them: `project-outputs`), every source of
/// every F# project it references, transitively, with their `-r:`s, and every non-F#
/// project reference's output. The same walk as the key's, written beside it; a check that
/// reads anything else fails the audit.
module CheckDeclared =
    let private references (options: FSharpProjectOptions) =
        options.OtherOptions
        |> Seq.filter (fun o -> o.StartsWith("-r:", StringComparison.Ordinal))
        |> Seq.map (fun o -> o.Substring 3)

    let ofCheck (options: FSharpProjectOptions) (file: string) : Declared =
        let files = Collections.Generic.HashSet<string>()
        let visited = Collections.Generic.HashSet<string>()

        let rec addReferences (opts: FSharpProjectOptions) =
            references opts |> Seq.iter (files.Add >> ignore)

            for reference in opts.ReferencedProjects do
                files.Add reference.OutputFile |> ignore

                match reference with
                | FSharpReferencedProject.FSharpReference(_, referenced) when visited.Add referenced.ProjectFileName ->
                    referenced.SourceFiles |> Seq.iter (files.Add >> ignore)
                    addReferences referenced
                | _ -> ()

        visited.Add options.ProjectFileName |> ignore
        addReferences options

        let upTo =
            match Array.tryFindIndex (fun f -> f = file) options.SourceFiles with
            | Some i -> options.SourceFiles[..i]
            | None -> Array.append options.SourceFiles [| file |]

        upTo |> Seq.iter (files.Add >> ignore)
        Declared.ofFiles files

    /// A PROBE (never an open) of an entry directly inside the directory of a declared
    /// assembly: FCS asks whether each reference has an XML documentation file beside it
    /// (`System.Runtime.xml`) and whether a reference pack has `Facades`. No check-record
    /// output (diagnostics, signatures) carries a referenced assembly's documentation. An
    /// OPEN there still fails: a byte read beside a reference is an input.
    let referenceDirectoryProbes: Exemption =
        { Name = "reference-directory-probes"
          Reason = "FCS probes for documentation and facades beside each reference; no check output carries them"
          Matches =
            fun declared read ->
                read.Kind = ReadKind.Probe
                && (let directory = Path.GetDirectoryName read.Path

                    declared.Files
                    |> Set.exists (fun f ->
                        f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && (let home = Path.GetDirectoryName f
                            home = directory || home = read.Path))) }

    /// A probe inside the directory the checker was loaded from: FCS looks for .NET Framework
    /// version folders (`v4.8`, ...) beside its own assembly. The key names the checker build
    /// (`toolchain`), not that directory's listing: a residual undeclared input, recorded
    /// here rather than keyed (ADR-059 §37).
    let checkerHostProbes: Exemption =
        { Name = "checker-host-probes"
          Reason = "FCS probes for .NET Framework folders beside its own assembly; the key names the checker build"
          Matches = fun _ read -> read.Kind = ReadKind.Probe && isUnder (full AppContext.BaseDirectory) read.Path }

    /// OPEN FINDING (ADR-059 §37): a probe inside the .NET runtime directory the checker
    /// runs on (FCS asks whether `System.ValueTuple.dll` is there). The checker build in the
    /// key names FCS, FSharp.Core and fshw, not the runtime hosting them. Exempted so the
    /// controls can run; counted on every audit until the key names the runtime.
    let runtimeDirectoryProbes: Exemption =
        { Name = "runtime-directory-probes"
          Reason = "OPEN FINDING: FCS probes the host runtime's directory; the key does not name the runtime"
          Matches =
            fun _ read ->
                read.Kind = ReadKind.Probe
                && isUnder (full (Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory())) read.Path }

    /// OPEN FINDING (ADR-059 §37): a probe of a name directly inside the project's directory
    /// (`mscorlib`, `FSharp.Core`, `System.Windows.Forms.dll`, ...): FCS looks for assemblies
    /// by simple name there. Their ABSENCE is an input no key names: a file of that name
    /// would change resolution without moving a key. Seen with script-derived options; not
    /// yet measured with MSBuild-derived ones. Exempted so the controls can run; counted.
    let projectDirectoryProbes (options: FSharpProjectOptions) : Exemption =
        let projectDirectory = full (Path.GetDirectoryName options.ProjectFileName)

        { Name = "project-directory-probes"
          Reason =
            "OPEN FINDING: FCS probes the project directory for assemblies by simple name; no key names their absence"
          Matches =
            fun declared read ->
                read.Kind = ReadKind.Probe
                && Path.GetDirectoryName read.Path = projectDirectory
                && (let ext = Path.GetExtension(read.Path).ToLowerInvariant()
                    ext <> ".fs" && ext <> ".fsi" && ext <> ".fsx")
                && not (declared.Files.Contains read.Path) }

    /// What the audit of a check in `options` exempts.
    let exemptionsFor (options: FSharpProjectOptions) : Exemption list =
        [ referenceDirectoryProbes
          checkerHostProbes
          runtimeDirectoryProbes
          projectDirectoryProbes options ]
