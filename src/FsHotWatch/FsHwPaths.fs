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

    // The temp name is UNIQUE per write, not a fixed `path + ".tmp"`. Since
    // AUTOMATION-564 a cache store can be shared between the daemons of two
    // workspaces, so two processes can be mid-write to the SAME entry path at the
    // same time; with one shared temp name each would truncate the other's partial
    // file and one of the two `File.Move`s would fail on a file that vanished.
    // Distinct temp files make the rename the only contended step, and a rename is
    // atomic — last writer wins, and both wrote the same content anyway.
    let unique = System.Guid.NewGuid().ToString("N")
    let tmp = $"%s{path}.%s{unique}.tmp"
    File.WriteAllText(tmp, contents)
    File.Move(tmp, path, overwrite = true)

/// Environment override for the box-wide cache home. Set it to keep every shared
/// cache entry inside one directory (a sandbox, a CI cache mount, a test fixture).
[<Literal>]
let CacheHomeEnvVar = "FSHW_CACHE_HOME"

/// The box-wide directory shared caches live under, from the three values that
/// decide it. A pure function of its arguments so the precedence is testable without
/// mutating the process environment — which a parallel test suite cannot do safely.
let internal sharedCacheHomeFrom (cacheHomeOverride: string) (xdgCacheHome: string) (userProfile: string) =
    match cacheHomeOverride with
    | null
    | "" ->
        let baseDir =
            match xdgCacheHome with
            | null
            | "" -> Path.Combine(userProfile, ".cache")
            | xdg -> xdg

        Path.Combine(baseDir, "fshw")
    | explicitHome -> explicitHome

/// The box-wide directory shared caches live under — `$FSHW_CACHE_HOME`, else
/// `$XDG_CACHE_HOME/fshw`, else `~/.cache/fshw`.
///
/// Deliberately OUTSIDE any checkout: an entry keyed purely on content is valid for
/// every checkout of the repository on this machine, and a store under `.fshw/` is
/// destroyed by the very act this feature exists to make cheap — creating a fresh
/// workspace.
let sharedCacheHome () =
    sharedCacheHomeFrom
        (System.Environment.GetEnvironmentVariable CacheHomeEnvVar)
        (System.Environment.GetEnvironmentVariable "XDG_CACHE_HOME")
        (System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile)
