/// Writes a process's recorder state as one NDJSON dump (see `Contract.DumpFormat`).
module TestPrune.Trace.Recorder.DumpWriter

open System
open System.Diagnostics
open System.IO
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Text.Json

/// The dump's `os` value, given a platform test (injected so every branch is testable).
let internal osNameOf (isOs: OSPlatform -> bool) =
    if isOs OSPlatform.OSX then "OSX"
    elif isOs OSPlatform.Linux then "Linux"
    elif isOs OSPlatform.Windows then "Windows"
    else "Other"

let private counterNames =
    [| "test"
       "class"
       "collection"
       "assembly"
       "override"
       "staticInit"
       "ambient"
       "overflow" |]

let private writeHeader (w: Utf8JsonWriter) (state: RecorderState) =
    let c = state.Counters()
    w.WriteStartObject()
    w.WriteString("format", Contract.DumpFormat)
    w.WriteNumber("pid", Environment.ProcessId)

    if String.IsNullOrEmpty state.ParentScope then
        w.WriteNull "parentScope"
    else
        w.WriteString("parentScope", state.ParentScope)

    w.WriteString("runtime", RuntimeInformation.FrameworkDescription)
    w.WriteString("os", osNameOf RuntimeInformation.IsOSPlatform)
    w.WriteString("arch", string RuntimeInformation.ProcessArchitecture)
    w.WriteNumber("ids", state.IdCount)

    let cpu =
        using (Process.GetCurrentProcess()) (fun p -> p.TotalProcessorTime.TotalMilliseconds)

    w.WriteNumber("cpuMs", int64 cpu)
    w.WriteStartObject "counters"

    for i in 0 .. counterNames.Length - 1 do
        w.WriteNumber(counterNames.[i], c.[i])

    w.WriteEndObject()
    w.WriteEndObject()

/// Unordered sets are written sorted, so a dump is deterministic.
[<MethodImpl(MethodImplOptions.NoInlining)>]
let private sorted (values: seq<'T>) =
    let a = Array.ofSeq values
    Array.sortInPlace a
    a

let private writeStrings (w: Utf8JsonWriter) (name: string) (values: string[]) =
    w.WriteStartArray name

    for v in values do
        w.WriteStringValue v

    w.WriteEndArray()

let private writeScope (w: Utf8JsonWriter) (s: Scope) =
    w.WriteStartObject()
    w.WriteString("key", s.Key)

    if isNull s.TestMethod then
        w.WriteNull "test"
    else
        w.WriteStartObject "test"
        w.WriteString("class", s.TestClass)
        w.WriteString("method", s.TestMethod)
        w.WriteString("display", s.TestDisplay)
        w.WriteEndObject()

    writeStrings w "parents" s.Parents
    writeStrings w "links" (sorted s.Links.Keys)
    w.WriteStartArray "ids"

    for id in s.Ids() do
        w.WriteNumberValue id

    w.WriteEndArray()
    w.WriteStartArray "inputs"

    for struct (kind, p) in sorted s.Inputs.Keys do
        w.WriteStartObject()
        w.WriteString("kind", kind)
        w.WriteString("path", p)
        w.WriteEndObject()

    w.WriteEndArray()
    w.WriteStartArray "children"

    for struct (pid, file, env) in s.Children.ToArray() do
        w.WriteStartObject()
        w.WriteNumber("pid", pid)
        w.WriteString("file", file)
        w.WriteBoolean("env", env)
        w.WriteEndObject()

    w.WriteEndArray()
    w.WriteEndObject()

/// Write every non-empty scope (every T scope, even an empty one) to `path` via a
/// `.tmp` sibling and a rename, so a reader never sees a half-written file. The last
/// line is `{"end":true}`; a dump without it is truncated.
let write (path: string) (state: RecorderState) =
    let tmp = path + ".tmp"

    using (File.Create tmp) (fun stream ->
        using (new Utf8JsonWriter(stream)) (fun w ->
            let line () =
                w.Flush()
                stream.WriteByte(byte '\n')
                w.Reset()

            writeHeader w state
            line ()

            for s in Array.ofSeq state.Scopes do
                if s.Key.StartsWith("T:", StringComparison.Ordinal) || not s.IsEmpty then
                    writeScope w s
                    line ()

            w.WriteStartObject()
            w.WriteBoolean("end", true)
            w.WriteEndObject()
            line ()))

    File.Move(tmp, path, true)

/// The file name for this process inside the host-provided output directory.
let pathIn (dir: string) =
    Path.Combine(dir, $"trace-%d{Environment.ProcessId}.ndjson")
