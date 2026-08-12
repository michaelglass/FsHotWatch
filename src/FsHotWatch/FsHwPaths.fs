module FsHotWatch.FsHwPaths

open System.IO

/// Absolute path to the .fshw/ state directory inside the given repo root.
/// All daemon on-disk artifacts (caches, errors, test-run reports, etc.) live here.
let root (repoRoot: string) = Path.Combine(repoRoot, ".fshw")

/// The name of fshw's per-repo configuration file.
[<Literal>]
let ConfigFileName = ".fshw.json"

/// Absolute path to the repo's `.fshw.json`. Named in ONE place because the config is
/// an input to the tree hash the verdict is content-addressed by (`TreeHash`), and a
/// second spelling of this path would let the two disagree.
let configFile (repoRoot: string) = Path.Combine(repoRoot, ConfigFileName)

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
