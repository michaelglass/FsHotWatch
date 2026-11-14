/// EXPERIMENT ONLY (AUTOMATION-1626 step 0, heap attribution). Not for landing.
/// `fshw diag-release <step>` reaches this through the CLI's plugin-command passthrough.
///   measure  — process + GC numbers, no collection
///   gc       — aggressive compacting full GC, then measure
///   census   — per-cache entry counts of every TransparentCompiler cache (reflection)
///   A        — ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients + InvalidateAll
///              on every checker partition, then aggressive GC, then measure
///   B        — `dropOwn` (fshw's own holders, supplied by the daemon), then aggressive GC
module FsHotWatch.DiagRelease

open System
open System.Diagnostics
open System.Reflection
open System.Runtime
open FSharp.Compiler.CodeAnalysis

let private gb (b: int64) = float b / 1024.0 / 1024.0 / 1024.0

let measure (label: string) : string =
    let p = Process.GetCurrentProcess()
    p.Refresh()
    let info = GC.GetGCMemoryInfo(GCKind.Any)
    let gens =
        info.GenerationInfo.ToArray()
        |> Array.mapi (fun i g -> $"g%d{i}=%.2f{gb g.SizeAfterBytes}/frag%.2f{gb g.FragmentationAfterBytes}")
        |> String.concat " "

    $"diag[%s{label}] pid=%d{p.Id} ws=%.2f{gb p.WorkingSet64}GB managed=%.2f{gb (GC.GetTotalMemory false)}GB "
    + $"heap=%.2f{gb info.HeapSizeBytes}GB frag=%.2f{gb info.FragmentedBytes}GB committed=%.2f{gb info.TotalCommittedBytes}GB "
    + $"promoted=%.2f{gb info.PromotedBytes}GB gcIndex=%d{info.Index} gen=%d{info.Generation} compacted=%b{info.Compacted} "
    + $"gc0=%d{GC.CollectionCount 0} gc1=%d{GC.CollectionCount 1} gc2=%d{GC.CollectionCount 2} "
    + $"server=%b{GCSettings.IsServerGC} latency=%A{GCSettings.LatencyMode} %s{gens}"

let aggressive () =
    GCSettings.LargeObjectHeapCompactionMode <- GCLargeObjectHeapCompactionMode.CompactOnce
    GC.Collect(2, GCCollectionMode.Aggressive, true, true)
    GC.WaitForPendingFinalizers()
    GCSettings.LargeObjectHeapCompactionMode <- GCLargeObjectHeapCompactionMode.CompactOnce
    GC.Collect(2, GCCollectionMode.Aggressive, true, true)

let private flags =
    BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic

let private tryRead (name: string) (target: obj) : obj option =
    if isNull target then None
    else
        let t = target.GetType()
        match t.GetProperty(name, flags) with
        | null ->
            match t.GetField(name, flags) with
            | null -> None
            | f -> Some(f.GetValue target)
        | p when p.GetIndexParameters().Length = 0 -> (try Some(p.GetValue target) with _ -> None)
        | _ -> None

/// Each TransparentCompiler cache's own DebuggerDisplay (strong/weak counts), by name.
let census (checker: FSharpChecker) : string =
    match tryRead "backgroundCompiler" (box checker) |> Option.bind (tryRead "Caches") with
    | None -> "census: no backgroundCompiler.Caches"
    | Some caches ->
        caches.GetType().GetProperties(flags)
        |> Array.map (fun p ->
            let v = try p.GetValue caches with _ -> null
            let shown =
                [ "DebuggerDisplay"; "Count" ]
                |> List.tryPick (fun n -> tryRead n v |> Option.map string)
                |> Option.orElse (tryRead "cache" v |> Option.bind (tryRead "DebuggerDisplay") |> Option.map string)
                |> Option.defaultValue "?"
            $"  %s{p.Name}: %s{shown}")
        |> String.concat "\n"

let releaseFcs (checkers: FSharpChecker list) : string =
    let before = checkers |> List.map census |> String.concat "\n--\n"
    for c in checkers do
        c.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()
        c.InvalidateAll()
    aggressive ()
    let after = checkers |> List.map census |> String.concat "\n--\n"
    $"checkers=%d{List.length checkers}\nBEFORE A:\n%s{before}\nAFTER A:\n%s{after}"

/// Walk from `root` through cache plumbing (never into typed trees or closures) and empty
/// the ResizeArray fields of every TcResultsSinkImpl found. Returns (sinks, items cleared, visited).
let stripSinks (root: obj) : int * int64 * int =
    let visited = System.Collections.Generic.HashSet<obj>(System.Collections.Generic.ReferenceEqualityComparer.Instance)
    let queue = System.Collections.Generic.Queue<obj>()
    let mutable sinks = 0
    let mutable items = 0L
    let blocked (t: Type) =
        let n = t.FullName |> Option.ofObj |> Option.defaultValue t.Name
        t.IsPrimitive || t = typeof<string> || t.IsPointer
        || n.Contains "@" || n.Contains "<>c" || n.Contains "DisplayClass"
        || [ "FSharp.Compiler.TypedTree"; "FSharp.Compiler.Syntax"; "FSharp.Compiler.TcGlobals"
             "FSharp.Compiler.CompilerImports"; "FSharp.Compiler.CheckBasics"; "FSharp.Compiler.AbstractIL"
             "FSharp.Compiler.Text"; "FSharp.Compiler.CompilerConfig"; "System.Reflection"; "System.Type"
             "System.RuntimeType"; "System.Threading.Thread"; "System.Delegate"; "System.Action"; "System.Func"
             "Microsoft.FSharp.Core.FSharpFunc"; "FSharp.Compiler.ParseAndCheckInputs" ]
           |> List.exists n.StartsWith
    queue.Enqueue root
    while queue.Count > 0 && visited.Count < 3_000_000 do
        let o = queue.Dequeue()
        if not (isNull o) && visited.Add o then
            let t = o.GetType()
            if t.Name = "TcResultsSinkImpl" then
                sinks <- sinks + 1
                for f in t.GetFields(flags) do
                    match f.GetValue o with
                    | :? Collections.IList as l when not l.IsFixedSize ->
                        items <- items + int64 l.Count
                        l.Clear()
                    | :? Collections.IDictionary as d ->
                        items <- items + int64 d.Count
                        d.Clear()
                    | _ -> ()
            elif t.IsArray then
                if not (t.GetElementType().IsPrimitive) then
                    for x in (o :?> Array) do
                        if not (isNull x) && not (blocked (x.GetType())) then queue.Enqueue x
            elif o :? WeakReference then
                let target = (o :?> WeakReference).Target
                if not (isNull target) then queue.Enqueue target
            elif not (blocked t) then
                let mutable ty = t
                while not (isNull ty) do
                    for f in ty.GetFields(flags ||| BindingFlags.DeclaredOnly) do
                        if not f.FieldType.IsPrimitive && not f.FieldType.IsPointer then
                            match (try f.GetValue o with _ -> null) with
                            | null -> ()
                            | v when blocked (v.GetType()) && v.GetType().Name <> "TcResultsSinkImpl" -> ()
                            | v -> queue.Enqueue v
                    ty <- ty.BaseType
    sinks, items, visited.Count

/// Walk from `root` (closures and tuples included, typed trees and environments excluded)
/// and null every field holding a CheckedImplFile, or an option of one. Measurement only:
/// the checker is unusable for project-level assembly contents afterwards.
let stripImpl (root: obj) : int * int =
    let visited = System.Collections.Generic.HashSet<obj>(System.Collections.Generic.ReferenceEqualityComparer.Instance)
    let queue = System.Collections.Generic.Queue<obj>()
    let mutable nulled = 0
    let isImpl (t: Type) =
        not (isNull t)
        && (t.FullName = "FSharp.Compiler.TypedTree+CheckedImplFile"
            || (t.IsGenericType && t.GetGenericArguments() |> Array.exists (fun a -> a.FullName = "FSharp.Compiler.TypedTree+CheckedImplFile")))
    let blocked (t: Type) =
        let n = t.FullName |> Option.ofObj |> Option.defaultValue t.Name
        t.IsPrimitive || t = typeof<string> || t.IsPointer
        || [ "FSharp.Compiler.TypedTree"; "FSharp.Compiler.Syntax"; "FSharp.Compiler.TcGlobals"
             "FSharp.Compiler.CompilerImports"; "FSharp.Compiler.CheckBasics"; "FSharp.Compiler.AbstractIL"
             "FSharp.Compiler.Text"; "FSharp.Compiler.CompilerConfig"; "FSharp.Compiler.NameResolution"
             "System.Reflection"; "System.RuntimeType"; "System.Threading.Thread" ]
           |> List.exists n.StartsWith
    queue.Enqueue root
    while queue.Count > 0 && visited.Count < 3_000_000 do
        let o = queue.Dequeue()
        if not (isNull o) && visited.Add o then
            let t = o.GetType()
            if t.IsArray then
                if not (t.GetElementType().IsPrimitive) then
                    for x in (o :?> Array) do
                        if not (isNull x) && not (blocked (x.GetType())) then queue.Enqueue x
            elif o :? WeakReference then
                let target = (o :?> WeakReference).Target
                if not (isNull target) then queue.Enqueue target
            elif not (blocked t) then
                let mutable ty = t
                while not (isNull ty) do
                    for f in ty.GetFields(flags ||| BindingFlags.DeclaredOnly) do
                        if not f.FieldType.IsPrimitive && not f.FieldType.IsPointer then
                            match (try f.GetValue o with _ -> null) with
                            | null -> ()
                            | v when isImpl (v.GetType()) || isImpl f.FieldType ->
                                (try
                                    f.SetValue(o, null)
                                    nulled <- nulled + 1
                                 with _ -> ())
                            | v when blocked (v.GetType()) -> ()
                            | v -> queue.Enqueue v
                    ty <- ty.BaseType
    nulled, visited.Count

let handler (checkers: unit -> FSharpChecker list) (dropOwn: unit -> string) (projectOptions: unit -> FSharpProjectOptions list) (args: string array) : Async<string> =
    async {
        let step = (String.concat " " args).Trim([| ' '; '"'; '['; ']' |])
        let out =
            match step with
            | "measure" -> measure "measure"
            | s when s.StartsWith "release-except:" ->
                // Project-scoped retention candidate: ClearCache every project except those whose path contains the token.
                let hot = s.Substring(15)
                let cold = projectOptions () |> List.filter (fun o -> not (o.ProjectFileName.Contains hot))
                for c in checkers () do
                    c.ClearCache(cold, "m1626-release")
                aggressive ()
                let names = cold |> List.map (fun o -> IO.Path.GetFileNameWithoutExtension o.ProjectFileName) |> String.concat ","
                $"released %d{List.length cold} projects (kept *%s{hot}*): %s{names}\n" + measure s
            | "gc" ->
                aggressive ()
                measure "gc"
            | "census" -> checkers () |> List.map census |> String.concat "\n--\n"
            | "A" ->
                let detail = releaseFcs (checkers ())
                detail + "\n" + measure "A"
            | "B" ->
                let detail = dropOwn ()
                aggressive ()
                detail + "\n" + measure "B"
            | "strip-sinks" ->
                let results =
                    [ for c in checkers () do
                          match tryRead "backgroundCompiler" (box c) |> Option.bind (tryRead "Caches") |> Option.bind (tryRead "TcIntermediate") with
                          | None -> yield "no TcIntermediate cache"
                          | Some memo ->
                              let s, i, v = stripSinks memo
                              yield $"sinks=%d{s} itemsCleared=%d{i} visited=%d{v}" ]
                aggressive ()
                (String.concat "; " results) + "\n" + measure "strip-sinks"
            | "strip-impl" ->
                let results =
                    [ for c in checkers () do
                          match tryRead "backgroundCompiler" (box c) |> Option.bind (tryRead "Caches") |> Option.bind (tryRead "TcIntermediate") with
                          | None -> yield "no TcIntermediate cache"
                          | Some memo ->
                              let n, v = stripImpl memo
                              yield $"implFieldsNulled=%d{n} visited=%d{v}" ]
                aggressive ()
                (String.concat "; " results) + "\n" + measure "strip-impl"
            | s when s.StartsWith "clear:" ->
                // clear:ParseFile,TcIntermediate — clear only the named TransparentCompiler caches.
                let names = s.Substring(6).Split(',', StringSplitOptions.RemoveEmptyEntries)
                let cleared =
                    [ for c in checkers () do
                          match tryRead "backgroundCompiler" (box c) |> Option.bind (tryRead "Caches") with
                          | None -> yield "no caches"
                          | Some caches ->
                              for n in names do
                                  match tryRead n caches with
                                  | None -> yield $"%s{n}: missing"
                                  | Some memo ->
                                      match memo.GetType().GetMethod("Clear", flags, Type.EmptyTypes) with
                                      | null -> yield $"%s{n}: no Clear()"
                                      | m ->
                                          m.Invoke(memo, [||]) |> ignore
                                          yield $"%s{n}: cleared" ]
                aggressive ()
                (String.concat "; " cleared) + "\n" + measure s
            | other -> $"diag-release: unknown step '%s{other}' (measure|gc|census|A|B|clear:<names>)"
        Logging.info "diag" out
        return out
    }
