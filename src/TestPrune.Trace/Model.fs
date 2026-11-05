/// Records shared by the weaver, the dump reader, the joiner and the trace store.
module TestPrune.Trace.Model

/// What a probe id stands for.
type ProbeKind =
    | UserMethod
    | GeneratedMethod
    | StaticCtor
    | UnionCase
    | TypeUse

/// One row of a weave manifest: a probe id and the code it stands for.
type ManifestRow =
    {
        Id: int
        Kind: ProbeKind
        Assembly: string
        /// CLR full name, nested types with '+'.
        TypeName: string
        /// Method name, or case name for UnionCase, "" for TypeUse.
        Member: string
        /// Absolute path from the PDB.
        Document: string option
        FirstLine: int
        LastLine: int
    }

/// How much of an assembly the weaver probes.
type WeaveMode =
    | Full
    | SitesOnly

/// Every probe id of one weave, and the PDB source hash of each document.
type Manifest =
    {
        Rows: ManifestRow[]
        /// Path -> lowercase hex SHA-256 recorded in the PDB.
        Documents: Map<string, string>
        IdCount: int
    }

/// Named so no case shadows a core module (`List`) or a record label (`Exists`).
type InputKind =
    | FileRead
    | ExistenceProbe
    | DirectoryListing
    /// A listing that recursed into every subdirectory.
    | DeepDirectoryListing
    /// A read of a path's metadata (times, attributes, mode, length), not its content.
    | MetadataRead
    /// An assembly's types were enumerated (a whole-assembly sweep). Path: the assembly file.
    | TypeSet
    /// A type was inspected through union reflection. Path: `<assembly file>|<CLR full name>`.
    | TypeReflected
    /// A kind this reader does not know, written by a newer recorder. Its scope cannot be
    /// trusted to replay, so it is marked incomplete rather than failing the whole dump.
    | UnknownInputKind of kind: string

/// A file-system input a scope observed.
type RecordedInput = { Kind: InputKind; Path: string }

/// A child process a scope started.
type ChildNote =
    {
        Pid: int
        /// The executable as the start named it: a bare name, or a path. Dumps written before
        /// AUTOMATION-1496 hold only its file name.
        FileName: string
        EnvInjected: bool
    }

/// Untraced children that cannot leave a gap in a test's trace.
[<RequireQualifiedAccess>]
module ChildNote =
    /// Executables whose run depends only on their arguments and reads no file's content, so
    /// no change to the repository can change what they do: what they are told comes from the
    /// test's own (traced) code. A shell, an interpreter, `dotnet` or `git` runs or reads what
    /// it is given and is never on this list (AUTOMATION-1496).
    let TrustedExecutables = set [ "sleep"; "chmod" ]

    /// The executable's file name, as an incomplete reason names it.
    let name (c: ChildNote) = System.IO.Path.GetFileName c.FileName

    /// A trusted executable named bare (found on the PATH) or by a rooted path outside
    /// `repoRoot`. A path inside the repository is a repository file, whatever it is called.
    let isTrusted (repoRoot: string) (c: ChildNote) : bool =
        let outsideRepo () =
            let root =
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath repoRoot)
                + string System.IO.Path.DirectorySeparatorChar

            not (System.IO.Path.GetFullPath(c.FileName).StartsWith(root, System.StringComparison.Ordinal))

        TrustedExecutables.Contains(name c)
        && (System.IO.Path.GetFileName c.FileName = c.FileName
            || System.IO.Path.IsPathRooted c.FileName && outsideRepo ())

/// The xUnit identity of a test scope.
type TestIdentity =
    { Class: string
      Method: string
      Display: string }

/// One scope of a process dump.
type RecordedScope =
    { Key: string
      Test: TestIdentity option
      Parents: string list
      Links: string list
      Ids: int[]
      Inputs: RecordedInput list
      Children: ChildNote list }

/// Per-bucket probe-hit counters of one process.
type HitCounters =
    { Test: int64
      Class: int64
      Collection: int64
      Assembly: int64
      Override: int64
      StaticInit: int64
      Ambient: int64
      Overflow: int64 }

/// One process's recorder dump.
type ProcessDump =
    { Pid: int
      ParentScope: string option
      Runtime: string
      Os: string
      Arch: string
      IdCount: int
      CpuMs: int64
      Counters: HitCounters
      Scopes: RecordedScope list }

/// A test's outcome as the CTRF report states it.
type OutcomeKind =
    | Passed
    | Failed
    | Skipped
    | OtherOutcome

/// A CTRF test entry; `Name` is CTRF's "name" verbatim.
type TestOutcome = { Name: string; Outcome: OutcomeKind }

/// Why a stored trace is marked incomplete.
type IncompleteReason =
    | NoOutcome
    | NotPassed of OutcomeKind
    | SourceDrift of file: string
    | NotIndexed of file: string
    | ChildProcessUntraced of fileName: string
    | RecorderOverflow
    | TreeMoved
    | DumpRejected of reason: string
    /// Executed code the joiner could map to neither a symbol nor a source file.
    | UnmappedCode of detail: string
    /// An input whose kind this reader does not know, so its current state cannot be compared.
    | UnknownInput of kind: string
