module FsHotWatch.FsHwPaths

open System.IO

/// Absolute path to the .fshw/ state directory inside the given repo root.
/// All daemon on-disk artifacts (caches, errors, test-run logs, etc.) live here.
let root (repoRoot: string) = Path.Combine(repoRoot, ".fshw")

/// Write contents atomically (temp file + rename). Used wherever we need a
/// torn-write-safe persistence step — caches, history files, etc. — so a
/// daemon crash mid-write can't leave a half-written file at `path`.
let atomicWriteAllText (path: string) (contents: string) : unit =
    let dir = Path.GetDirectoryName(path)

    if not (System.String.IsNullOrEmpty dir) then
        Directory.CreateDirectory(dir) |> ignore

    let tmp = path + ".tmp"
    File.WriteAllText(tmp, contents)
    File.Move(tmp, path, overwrite = true)
