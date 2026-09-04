namespace FsHotWatch

open System
open System.IO
open System.Security

/// A cache-key path whose representation says whether it is safe to move to
/// another checkout of the same repository.
[<RequireQualifiedAccess>]
type CachePathIdentity =
    | RepoRelative of string
    | ExternalAbsolute of string

/// Creates and decodes unambiguous cache-key path identities.
[<RequireQualifiedAccess>]
module CachePathIdentity =
    let private repoPrefix = "repo:"
    let private externalPrefix = "external:"

    let private normalizeNativeSeparators (path: string) =
        if Path.DirectorySeparatorChar = '\\' then
            path.Replace('\\', '/')
        else
            path

    let private reservedWindowsNames =
        set
            [ "CON"
              "PRN"
              "AUX"
              "NUL"
              "COM¹"
              "COM²"
              "COM³"
              "LPT¹"
              "LPT²"
              "LPT³"
              yield!
                  [ for n in 1..9 do
                        $"COM{n}"
                        $"LPT{n}" ] ]

    let private hasPortableSegmentGrammar (segment: string) =
        let windowsStem =
            match segment.IndexOf('.') with
            | -1 -> segment
            | index -> segment.Substring(0, index)

        not (String.IsNullOrEmpty segment)
        && segment <> "."
        && segment <> ".."
        && not (segment.EndsWith(' '))
        && not (segment.EndsWith('.'))
        && not (segment |> Seq.exists Char.IsControl)
        && segment.IndexOfAny([| '<'; '>'; ':'; '"'; '\\'; '|'; '?'; '*' |]) < 0
        && not (reservedWindowsNames.Contains(windowsStem.ToUpperInvariant()))

    let private isPortableRelative (path: string) =
        path = "."
        || (not (String.IsNullOrEmpty path)
            && not (Path.IsPathRooted path)
            && path.Split('/') |> Array.forall hasPortableSegmentGrammar)

    let private tryPathOperation operation =
        try
            Some(operation ())
        with
        | :? ArgumentException
        | :? IOException
        | :? NotSupportedException
        | :? SecurityException -> None

    /// Creates an identity for `path`. Paths lexically beneath `repoRoot` are
    /// portable; outside paths remain explicitly machine-local.
    let ofPath (repoRoot: string) (path: string) =
        let root = Path.GetFullPath repoRoot

        let absolute =
            if Path.IsPathRooted path then
                Path.GetFullPath path
            else
                Path.GetFullPath(path, root)

        let relative = Path.GetRelativePath(root, absolute) |> normalizeNativeSeparators

        if isPortableRelative relative then
            CachePathIdentity.RepoRelative relative
        else
            CachePathIdentity.ExternalAbsolute absolute

    /// Encodes an identity as a stable, tagged cache key.
    let toKey identity =
        match identity with
        | CachePathIdentity.RepoRelative relative -> repoPrefix + relative
        | CachePathIdentity.ExternalAbsolute absolute -> externalPrefix + absolute

    /// Decodes a cache key, rejecting non-canonical or escaping relative paths.
    let tryParse (key: string) =
        if not (isNull key) && key.StartsWith(repoPrefix, StringComparison.Ordinal) then
            let relative = key.Substring(repoPrefix.Length)

            if isPortableRelative relative then
                Some(CachePathIdentity.RepoRelative relative)
            else
                None
        elif not (isNull key) && key.StartsWith(externalPrefix, StringComparison.Ordinal) then
            let absolute = key.Substring(externalPrefix.Length)

            tryPathOperation (fun () ->
                if Path.IsPathRooted absolute && Path.GetFullPath absolute = absolute then
                    Some(CachePathIdentity.ExternalAbsolute absolute)
                else
                    None)
            |> Option.flatten
        else
            None

    /// Resolves a portable identity beneath `repoRoot`. Machine-local external
    /// identities deliberately cannot be rebound into another checkout.
    let tryRebind (repoRoot: string) identity =
        match identity with
        | CachePathIdentity.ExternalAbsolute _ -> None
        | CachePathIdentity.RepoRelative relative ->
            tryPathOperation (fun () ->
                if not (isPortableRelative relative) then
                    None
                else
                    let root = Path.GetFullPath repoRoot
                    let absolute = Path.GetFullPath(relative, root)

                    let reboundRelative =
                        Path.GetRelativePath(root, absolute) |> normalizeNativeSeparators

                    if reboundRelative = relative then Some absolute else None)
            |> Option.flatten

    /// The portable cache-key spelling of `path`. With a `repoRoot`, paths beneath it
    /// become `repo:`-relative so two checkouts of one repository agree; without one,
    /// the path stays explicitly machine-local rather than pretending to be portable.
    let keyOf (repoRoot: string option) (path: string) =
        match repoRoot with
        | Some root -> ofPath root path |> toKey
        | None ->
            let absolute =
                match tryPathOperation (fun () -> Path.GetFullPath path) with
                | Some full -> full
                | None -> path

            toKey (CachePathIdentity.ExternalAbsolute absolute)
