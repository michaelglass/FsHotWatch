/// Reading macOS `footprint --json` for one process.
///
/// What the numbers mean, measured on this box rather than assumed:
///
/// * `phys_footprint` (the kernel's number, `footprint`'s headline and vmmap's
///   "Physical footprint") equals the SUM of every category's `dirty` bytes, exactly.
///   The kernel's `phys_footprint` counts compressed pages, so `dirty` INCLUDES pages
///   that are compressed or swapped out. `swapped` is therefore a SUBSET of `dirty`,
///   not an addition to it, and the bytes actually resident in RAM are
///   `dirty - swapped`. Both are recorded; neither is silently preferred.
/// * `clean` pages (mapped files, __TEXT) are NOT in the footprint: the kernel can drop
///   and re-read them, so they do not cost the box memory it cannot get back.
/// * `ps` refuses the rss/%cpu fields on this box and `top`'s MEM column is a different
///   quantity again; this module is the only process-memory reading the harness trusts.
module FsHotWatch.Bench.Footprint

open System
open System.Text.Json

/// One `footprint` category row, bytes.
type Category =
    { Dirty: int64
      Swapped: int64
      Clean: int64 }

/// Where a footprint category's bytes come from, for a .NET process.
[<RequireQualifiedAccess>]
type Bucket =
    /// `Untagged` (anonymous VM_ALLOCATE). The GC heap lives here, and so do the
    /// runtime's own loader heaps, JIT code and stubs. Split it with the GC's
    /// committed-bytes figure: untagged minus GC committed is runtime-native.
    | Untagged
    /// `Malloc *` zones: native malloc — the runtime's C++ allocations and any
    /// native library. Not the GC heap.
    | Malloc
    /// `mapped file`: dirty pages of memory-mapped files (clean ones are free).
    | MappedFile
    /// Mach-O image segments (`__TEXT`, `__DATA*`, `__LINKEDIT`, `__AUTH*`, `__OBJC*`, dyld).
    | Image
    /// Thread stacks.
    | Stack
    /// Everything else (page tables, kernel bookkeeping, tags we do not name).
    | Other

/// Classify a `footprint` category name.
let bucketOf (category: string) : Bucket =
    if
        category = "Untagged"
        || category.StartsWith("VM_ALLOCATE", StringComparison.Ordinal)
    then
        Bucket.Untagged
    elif category.StartsWith("Malloc", StringComparison.Ordinal) then
        Bucket.Malloc
    elif category = "mapped file" then
        Bucket.MappedFile
    elif
        category.StartsWith("__", StringComparison.Ordinal)
        || category.Contains("dyld", StringComparison.Ordinal)
    then
        Bucket.Image
    elif category = "Stack" then
        Bucket.Stack
    else
        Bucket.Other

/// Short stable key for a bucket, used as a JSON field name.
let bucketKey (bucket: Bucket) : string =
    match bucket with
    | Bucket.Untagged -> "untagged"
    | Bucket.Malloc -> "malloc"
    | Bucket.MappedFile -> "mappedFile"
    | Bucket.Image -> "image"
    | Bucket.Stack -> "stack"
    | Bucket.Other -> "other"

/// Every bucket, in reporting order.
let allBuckets =
    [ Bucket.Untagged
      Bucket.Malloc
      Bucket.MappedFile
      Bucket.Image
      Bucket.Stack
      Bucket.Other ]

/// One process's footprint at one instant.
type Reading =
    {
        Pid: int
        /// Kernel `phys_footprint`, bytes. Includes compressed/swapped pages.
        PhysFootprint: int64
        /// Kernel `phys_footprint_peak`: the process's LIFETIME high-water mark. A
        /// peak read this way cannot miss a spike between two samples.
        PhysFootprintPeak: int64
        /// Sum of per-category `swapped` (compressed or swapped out), bytes.
        Swapped: int64
        /// Raw categories as reported.
        Categories: Map<string, Category>
    }

/// Bytes resident in RAM: footprint minus the compressed/swapped part of it.
let resident (reading: Reading) : int64 = reading.PhysFootprint - reading.Swapped

/// Dirty and swapped bytes per bucket.
let byBucket (reading: Reading) : Map<Bucket, Category> =
    reading.Categories
    |> Map.toList
    |> List.groupBy (fun (name, _) -> bucketOf name)
    |> List.map (fun (bucket, rows) ->
        bucket,
        { Dirty = rows |> List.sumBy (fun (_, c) -> c.Dirty)
          Swapped = rows |> List.sumBy (fun (_, c) -> c.Swapped)
          Clean = rows |> List.sumBy (fun (_, c) -> c.Clean) })
    |> Map.ofList

/// Dirty bytes in one bucket (0 when the process has none).
let dirtyIn (bucket: Bucket) (reading: Reading) : int64 =
    byBucket reading
    |> Map.tryFind bucket
    |> Option.map _.Dirty
    |> Option.defaultValue 0L

/// Parse the JSON `footprint --json <file> <pid>` writes, for `pid`. `Error` names what
/// was missing, so a changed tool format fails loudly instead of reading as zero.
let parseJson (pid: int) (json: string) : Result<Reading, string> =
    try
        use doc = JsonDocument.Parse(json)

        let processes =
            doc.RootElement.GetProperty("processes").EnumerateArray() |> Seq.toList

        match processes |> List.tryFind (fun p -> p.GetProperty("pid").GetInt32() = pid) with
        | None -> Error $"footprint output has no process with pid %d{pid}"
        | Some proc ->
            let aux = proc.GetProperty("auxiliary")

            let categories =
                proc.GetProperty("categories").EnumerateObject()
                |> Seq.map (fun c ->
                    c.Name,
                    { Dirty = c.Value.GetProperty("dirty").GetInt64()
                      Swapped = c.Value.GetProperty("swapped").GetInt64()
                      Clean = c.Value.GetProperty("clean").GetInt64() })
                |> Map.ofSeq

            Ok
                { Pid = pid
                  PhysFootprint = aux.GetProperty("phys_footprint").GetInt64()
                  PhysFootprintPeak = aux.GetProperty("phys_footprint_peak").GetInt64()
                  Swapped = categories |> Map.toSeq |> Seq.sumBy (fun (_, c) -> c.Swapped)
                  Categories = categories }
    with ex ->
        Error $"unreadable footprint JSON: %s{ex.Message}"
