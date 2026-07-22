module FsHotWatch.Cli.SourceRef

open System

/// The human-readable half of "is my gate running my fix?": which SOURCE a
/// binary was built from, parsed from its assembly informational version so
/// `fshw --version` can state it in words rather than `strings`-probing a cached
/// DLL. A local RefStamp pack embeds `-ref.<change-id>.g<commit-id>[.dirty]`
/// (jj) or `-ref.g<head-sha>[.dirty[.g<stash>]]` (git); a release/CI build
/// carries `+<sha>` build metadata (SourceLink); anything else is unknown.
[<RequireQualifiedAccess>]
type SourceRef =
    /// A RefStamp-stamped local pack: `ref` is the full stamp body (change id +
    /// commit id, plus any dirty marker/hash), `dirty` says the working copy
    /// had undescribed/uncommitted work when packed.
    | RefStamped of ref: string * dirty: bool
    /// A release/CI build: the git sha recorded as `+<sha>` build metadata.
    | CommitMetadata of sha: string
    /// No ref recorded — a plain (unpacked) build, or a pack that predates
    /// ref-stamping.
    | Unknown

/// Extract the source ref from an assembly informational version string.
/// A `-ref.` stamp wins over `+<sha>` metadata when both are present (the
/// stamp names the exact tree; the sha is coarser).
let parse (informationalVersion: string) : SourceRef =
    match informationalVersion with
    | null -> SourceRef.Unknown
    | v ->
        let refIdx = v.IndexOf("-ref.", StringComparison.Ordinal)

        if refIdx >= 0 then
            let rest = v.Substring(refIdx + "-ref.".Length)

            let body =
                match rest.IndexOf '+' with
                | -1 -> rest
                | plus -> rest.Substring(0, plus)

            if body.Length = 0 then
                SourceRef.Unknown
            else
                // Seq.exists, not Array.contains: the inlined Array.contains
                // loop carries a compiler-generated branch no input can reach
                // (Split never yields an empty array), which would force a
                // permanent <100% branch floor on this file for dead IL.
                let dirty = body.Split '.' |> Seq.exists (fun segment -> segment = "dirty")
                SourceRef.RefStamped(body, dirty)
        else
            match v.IndexOf '+' with
            | -1 -> SourceRef.Unknown
            | plus ->
                let sha = v.Substring(plus + 1)

                if sha.Length = 0 then
                    SourceRef.Unknown
                else
                    SourceRef.CommitMetadata sha

/// The one-line human rendering shown by `fshw --version`.
let describe (ref: SourceRef) : string =
    match ref with
    | SourceRef.RefStamped(r, false) -> $"source ref: %s{r} (local ref-stamped pack)"
    | SourceRef.RefStamped(r, true) -> $"source ref: %s{r} (local ref-stamped pack, dirty working copy)"
    | SourceRef.CommitMetadata sha -> $"source ref: %s{sha} (commit metadata, release/CI build)"
    | SourceRef.Unknown -> "source ref: unknown (a plain build, or a pack that predates ref-stamping)"

/// `describe << parse`: the line for a raw informational version string.
let line (informationalVersion: string) : string = describe (parse informationalVersion)
