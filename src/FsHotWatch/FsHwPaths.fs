module FsHotWatch.FsHwPaths

open System.IO

/// Absolute path to the .fshw/ state directory inside the given repo root.
/// All daemon on-disk artifacts (caches, errors, test-run logs, etc.) live here.
let root (repoRoot: string) = Path.Combine(repoRoot, ".fshw")
