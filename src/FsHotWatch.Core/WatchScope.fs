/// What a watcher's scope is made of, in the correctness core: a file command's pattern, and the
/// directories whose events the native stream drops. `PluginInputs` keys a plugin by the files its
/// pattern matches in that scope (AUTOMATION-1577), and a pure lookup computes that key too, so the
/// definitions live here and `Watcher` forwards to them.
module FsHotWatch.WatchScope

open System
open System.IO

/// How a FileCommandPlugin pattern string matches paths. Parsed once at
/// config-load time via `FilePattern.parse` so downstream code never has to
/// re-inspect string shape.
[<RequireQualifiedAccess; NoComparison>]
type FilePattern =
    /// `*.ratchet.json` → matches any path ending with the suffix (including the leading dot).
    | Wildcard of suffix: string
    /// `coverage-ratchet.json` → matches only paths whose basename equals the given filename.
    | Literal of fileName: string

module FilePattern =
    /// Parse a pattern string. A leading `*` denotes a wildcard suffix;
    /// anything else is treated as a literal filename.
    ///
    /// Patterns with an embedded (non-leading) `*` are REJECTED: the raw pattern
    /// doubles as the `FileSystemWatcher.Filter` glob (which would happily glob
    /// `schema.*.sql`), while the in-process `matches` treats it as a literal
    /// suffix/basename that never matches the same files — so the OS event would fire
    /// and then be silently dropped. Rejecting at config-load time makes that loud.
    let parse (pattern: string) : FilePattern =
        if String.IsNullOrEmpty(pattern) then
            invalidArg (nameof pattern) "File pattern must not be empty."
        elif pattern.StartsWith("*", StringComparison.Ordinal) then
            let suffix = pattern.Substring(1)

            if suffix.Contains("*") then
                invalidArg
                    (nameof pattern)
                    ("Unsupported file pattern '"
                     + pattern
                     + "': only a single leading '*' is supported (e.g. '*.ratchet.json' or a literal filename).")

            FilePattern.Wildcard suffix
        elif pattern.Contains("*") then
            invalidArg
                (nameof pattern)
                ("Unsupported file pattern '"
                 + pattern
                 + "': '*' is only supported as a leading wildcard (e.g. '*.ratchet.json' or a literal filename).")
        else
            FilePattern.Literal pattern

    /// Serialize back to the original pattern string. Used for the underlying
    /// `FileSystemWatcher.Filter` glob and for human-readable diagnostics.
    let toString (pattern: FilePattern) : string =
        match pattern with
        | FilePattern.Wildcard suffix -> "*" + suffix
        | FilePattern.Literal name -> name

    /// True when `path` matches the pattern.
    let matches (pattern: FilePattern) (path: string) : bool =
        match pattern with
        | FilePattern.Wildcard suffix -> path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
        | FilePattern.Literal name ->
            let fileName = Path.GetFileName(path)
            fileName.Equals(name, StringComparison.OrdinalIgnoreCase)

    /// A synthetic path that `matches` this pattern — used by rerun to emit a
    /// fake FileChanged event that triggers only the target plugin.
    ///
    /// Rooted at `root`, and takes it as an argument so a caller cannot produce the
    /// bare relative form by omission. A relative path here is resolved against the
    /// PROCESS working directory downstream, which is a directory that can be
    /// deleted out from under a long-lived daemon — and then a rerun fails on the
    /// vanished cwd rather than on anything to do with the plugin being re-fired.
    /// Rooting keeps matching intact: a literal still compares by file name, and a
    /// wildcard still compares by suffix.
    let syntheticPath (root: string) (pattern: FilePattern) : string =
        let name =
            match pattern with
            | FilePattern.Wildcard suffix -> "_fshw_rerun_" + suffix
            | FilePattern.Literal name -> name

        Path.Combine(root, name)

/// Top-level directories whose events FSEvents drops in the kernel, before they are
/// queued for the daemon. fseventsd buffers every per-file event for every client
/// until that client drains, so a directory no watcher reports from is still memory
/// in fseventsd while the daemon is busy. Every name is one the polling walk also
/// prunes (`SafeWalk.ToolingExcludedDirs`): what the native stream never sees, the
/// polling fallback never visits either. `.workspaces` holds sibling checkouts, each
/// watched by its own stream.
let internal kernelExcludedDirs =
    [ ".jj"
      ".git"
      ".fshw"
      "node_modules"
      ".devenv"
      ".direnv"
      ".idea"
      ".workspaces" ]
