/// The PUBLISHED reference assembly: is `obj/<cfg>/<tfm>/ref/X.dll`, the file every
/// dependent compiles against, the one the compiler last wrote to `refint/X.dll`?
///
/// MSBuild's `CopyRefAssembly` copies `refint` to `ref` only when their MVIDs differ, on the
/// premise that a reference assembly's MVID changes whenever its API does. The F# compiler
/// breaks that premise: it derives a reference assembly's MVID from a 32-bit signature hash
/// that folds each file in with `(acc <<< 1) + fileHash + 631`, so a file that 32 or more
/// files follow is shifted out of the hash entirely. Change a public signature there and
/// `refint` gets the new API while the MVID stays, the copy is skipped, and dependents
/// compile against the OLD API: a false red (FS0003 on code that is correct) or, when the
/// dependent is not recompiled at all, a false green whose test host loads an assembly
/// the dependent was never checked against. Measured on a 34-file library under SDK
/// 10.0.401; the integration test `RefAssemblyBuildTests` pins it.
///
/// So the question is asked of the BYTES, and only when the cheap stat differs: an
/// implementation-only change rewrites `refint` with the same bytes and a newer timestamp
/// (the copy is rightly skipped), which must not count as stale.
module FsHotWatch.RefAssemblyFreshness

open System.IO

/// One project's reference-assembly pair under its `obj/` directory.
type RefPair =
    {
        /// The project's stem.
        Project: string
        /// `.../refint/X.dll` — what the compiler wrote.
        Intermediate: string
        /// `.../ref/X.dll` — what dependents compile against.
        Published: string
    }

/// The real (not symlinked) directories one level below each of `dirs`. An unlistable
/// directory yields none: its pairs go unchecked, as they would if it were absent.
let private below (dirs: string list) : string list =
    dirs
    |> List.collect (fun dir ->
        match SafeWalk.subdirectories dir with
        | Ok subs ->
            subs
            |> Array.toList
            |> List.filter (fun d -> isNull (DirectoryInfo(d).LinkTarget))
        | Error _ -> [])

/// The `refint` directories under `obj`: `<cfg>/<tfm>/refint`, or `<cfg>/<tfm>/<rid>/refint`.
/// Two fixed levels and one optional, never a recursive walk.
let private refintDirs (obj: string) : string list =
    let tfms = below (below [ obj ])
    let rids = below tfms

    tfms @ rids
    |> List.map (fun d -> Path.Combine(d, "refint"))
    |> List.filter Directory.Exists

/// Every reference-assembly pair under the project's own `obj/` whose published copy
/// exists. A missing `ref/X.dll` is not a pair: `CopyRefAssembly` always copies to an
/// absent destination, so the next build settles it.
let pairsOf (projectPath: string) : RefPair list =
    let projectDir = Path.GetDirectoryName projectPath

    if isNull projectDir then
        []
    else
        refintDirs (Path.Combine(projectDir, "obj"))
        |> List.collect (fun refint ->
            let published = Path.Combine(Path.GetDirectoryName refint, "ref")

            Directory.GetFiles(refint, "*.dll")
            |> Array.toList
            |> List.choose (fun intermediate ->
                let copy = Path.Combine(published, Path.GetFileName intermediate)

                if File.Exists copy then
                    Some
                        { Project = Path.GetFileNameWithoutExtension projectPath
                          Intermediate = intermediate
                          Published = copy }
                else
                    None))

/// Does the published copy hold other bytes than the intermediate? Same size and
/// timestamp is a copy `CopyRefAssembly` made, and needs no read. An unreadable file
/// counts as stale: a gate that cannot verify the reference may not pass on it.
let isStale (pair: RefPair) : bool =
    let intermediate = FileInfo pair.Intermediate
    let published = FileInfo pair.Published

    if not intermediate.Exists || not published.Exists then
        false
    elif
        intermediate.Length = published.Length
        && intermediate.LastWriteTimeUtc = published.LastWriteTimeUtc
    then
        false
    else
        let a = ContentHash.ofFile pair.Intermediate
        let b = ContentHash.ofFile pair.Published
        not (ContentHash.isReadable a && ContentHash.isReadable b && a = b)

/// Every stale pair across `projects`.
let stalePairs (projects: string list) : RefPair list =
    projects |> List.collect pairsOf |> List.filter isStale

/// Do what `CopyRefAssembly` skipped: copy the intermediate over the published copy. The
/// copy carries the intermediate's timestamp, newer than every dependent's output, so
/// the next build recompiles the dependents against the current API. Returns the pairs
/// it could not repair, with the reason.
let repair (pairs: RefPair list) : (RefPair * string) list =
    pairs
    |> List.choose (fun pair ->
        try
            File.Copy(pair.Intermediate, pair.Published, true)
            None
        with
        | :? IOException
        | :? System.UnauthorizedAccessException as ex -> Some(pair, ex.Message))
