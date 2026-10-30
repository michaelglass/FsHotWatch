/// Names the weaver emits calls to. The weaver resolves each one by name on the recorder
/// assembly it ships with, so a rename here is a compile error in the weaver, never a
/// runtime MissingMethodException in a consumer's test process.
module TestPrune.Trace.Recorder.Contract

/// Full name of the static class holding the probe entry points.
[<Literal>]
let ProbesType = "TestPrune.Trace.Recorder.Probes"

/// Full name of the static class holding the file-read shims.
[<Literal>]
let IoType = "TestPrune.Trace.Recorder.Io"

/// Full name of the static class holding the child-process shims.
[<Literal>]
let ProcessShimsType = "TestPrune.Trace.Recorder.ProcessShims"

/// Records one probe id.
[<Literal>]
let Hit = "Hit"

/// Records a probe id when the value on the stack is not null.
[<Literal>]
let HitIfNotNull = "HitIfNotNull"

/// Records a probe id when the value on the stack is true.
[<Literal>]
let HitIfTrue = "HitIfTrue"

/// Records `baseId + tag` for a union tag read.
[<Literal>]
let HitTag = "HitTag"

/// Entered at the start of a woven static constructor.
[<Literal>]
let EnterStatic = "EnterStatic"

/// Entered in the finally block of a woven static constructor.
[<Literal>]
let ExitStatic = "ExitStatic"

/// Directory the host asks a traced test process to write its dump into.
[<Literal>]
let OutEnv = "TESTPRUNE_TRACE_OUT"

/// Number of probe ids the weave allocated.
[<Literal>]
let IdsEnv = "TESTPRUNE_TRACE_IDS"

/// Repository root, used to keep only file inputs under it.
[<Literal>]
let RepoRootEnv = "TESTPRUNE_TRACE_REPO_ROOT"

/// Scope key a traced parent passes to a child process it starts.
[<Literal>]
let ParentScopeEnv = "TESTPRUNE_TRACE_PARENT_SCOPE"

/// Path of the JIT-verification report; set only in verify mode.
[<Literal>]
let VerifyEnv = "TESTPRUNE_TRACE_VERIFY"

/// Assemblies whose touched methods verify mode prepares.
[<Literal>]
let VerifyAssembliesEnv = "TESTPRUNE_TRACE_VERIFY_ASSEMBLIES"

/// The `format` value on a dump's header line.
[<Literal>]
let DumpFormat = "testprune-trace/1"
