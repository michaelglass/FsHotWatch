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

/// A file-system input a scope observed.
type RecordedInput = { Kind: InputKind; Path: string }

/// A child process a scope started.
type ChildNote =
    { Pid: int
      FileName: string
      EnvInjected: bool }

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
