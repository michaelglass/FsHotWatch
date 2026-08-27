module TestPrune.DiffParser

open System
open System.Collections.Generic
open System.Text

let private codeExtensions = set [ ".fs"; ".fsx"; ".fsproj" ]

let private isCodeFile (path: string) =
    codeExtensions
    |> Set.exists (fun ext -> path.EndsWith(ext, System.StringComparison.OrdinalIgnoreCase))

let isFsproj (path: string) =
    path.EndsWith(".fsproj", System.StringComparison.OrdinalIgnoreCase)

let private decodeQuotedPath (value: string) =
    let decoded = StringBuilder()
    let mutable index = 0

    let isOctal digit = digit >= '0' && digit <= '7'

    let octalLengthAt startIndex =
        let mutable length = 0

        while length < 3
              && startIndex + length < value.Length
              && isOctal value.[startIndex + length] do
            length <- length + 1

        length

    while index < value.Length do
        if value.[index] <> '\\' then
            decoded.Append(value.[index]) |> ignore
            index <- index + 1
        elif isOctal value.[index + 1] then
            let bytes = ResizeArray<byte>()

            while index + 1 < value.Length && value.[index] = '\\' && isOctal value.[index + 1] do
                let octalLength = octalLengthAt (index + 1)
                let octal = value.Substring(index + 1, octalLength)
                bytes.Add(Convert.ToByte(octal, 8))
                index <- index + 1 + octalLength

            decoded.Append(Encoding.UTF8.GetString(bytes.ToArray())) |> ignore
        else
            let escaped =
                match value.[index + 1] with
                | 'a' -> '\a'
                | 'b' -> '\b'
                | 't' -> '\t'
                | 'n' -> '\n'
                | 'v' -> '\v'
                | 'f' -> '\f'
                | 'r' -> '\r'
                | escaped -> escaped

            decoded.Append(escaped) |> ignore
            index <- index + 2

    decoded.ToString()

let private readQuotedPathToken (value: string) (startIndex: int) =
    let token = StringBuilder()
    let mutable index = startIndex + 1
    let mutable escaped = false
    let mutable closed = false

    while index < value.Length && not closed do
        let current = value.[index]

        if current = '"' && not escaped then
            closed <- true
        else
            token.Append(current) |> ignore
            escaped <- current = '\\' && not escaped

            if current <> '\\' then
                escaped <- false

        index <- index + 1

    if closed then
        Some(decodeQuotedPath (token.ToString()), index)
    else
        None

let private readOldPathToken (value: string) (startIndex: int) =
    if startIndex < value.Length && value.[startIndex] = '"' then
        readQuotedPathToken value startIndex
    else
        let mutable index = startIndex
        let mutable separator = -1
        let mutable newPathStart = -1

        while index < value.Length do
            if Char.IsWhiteSpace(value.[index]) then
                let mutable candidate = index

                while candidate < value.Length && Char.IsWhiteSpace(value.[candidate]) do
                    candidate <- candidate + 1

                let remainder = value.Substring(candidate)

                if remainder.StartsWith("\"b/", StringComparison.Ordinal) then
                    separator <- index
                    newPathStart <- candidate
                    index <- value.Length
                elif remainder.StartsWith("b/", StringComparison.Ordinal) then
                    separator <- index
                    newPathStart <- candidate
                    index <- candidate + 2
                else
                    index <- candidate
            else
                index <- index + 1

        if separator < 0 then
            None
        else
            Some(value.Substring(startIndex, separator - startIndex), newPathStart)

let private readNewPathToken (value: string) (startIndex: int) =
    if startIndex >= value.Length then
        None
    elif value.[startIndex] = '"' then
        match readQuotedPathToken value startIndex with
        | Some(path, endIndex) ->
            let mutable trailingIndex = endIndex

            while trailingIndex < value.Length && Char.IsWhiteSpace(value.[trailingIndex]) do
                trailingIndex <- trailingIndex + 1

            if trailingIndex = value.Length then Some path else None
        | None -> None
    else
        Some(value.Substring(startIndex).TrimEnd())

let private tryParseDiffHeader (line: string) =
    let prefix = "diff --git "

    match readOldPathToken line prefix.Length with
    | None -> None
    | Some(oldPath, nextIndex) ->
        let quotedOldPath = line.[prefix.Length] = '"'

        if
            quotedOldPath
            && (nextIndex >= line.Length || not (Char.IsWhiteSpace(line.[nextIndex])))
        then
            None
        else
            let newPathStart =
                let mutable index = nextIndex

                while index < line.Length && Char.IsWhiteSpace(line.[index]) do
                    index <- index + 1

                index

            match readNewPathToken line newPathStart with
            | Some newPath when
                oldPath.StartsWith("a/", StringComparison.Ordinal)
                && newPath.StartsWith("b/", StringComparison.Ordinal)
                ->
                Some(oldPath.Substring(2), newPath.Substring(2))
            | _ -> None

let private tryParseMetadataPath marker pathPrefix (line: string) =
    if line.StartsWith(marker, StringComparison.Ordinal) then
        let encodedPath = line.Substring(marker.Length)

        if encodedPath = "/dev/null" then
            Some None
        else
            let decodedPath =
                if encodedPath.StartsWith('"') then
                    readQuotedPathToken encodedPath 0 |> Option.map fst
                else
                    Some(encodedPath.TrimEnd())

            decodedPath
            |> Option.bind (fun path ->
                if String.IsNullOrEmpty(pathPrefix) then
                    Some(Some path)
                elif path.StartsWith(pathPrefix, StringComparison.Ordinal) then
                    Some(Some(path.Substring(pathPrefix.Length)))
                else
                    None)
    else
        None

let private parseDiffBlock (lines: string array) startIndex endIndex =
    let fallbackOld, fallbackNew =
        match tryParseDiffHeader lines.[startIndex] with
        | Some(oldPath, newPath) -> Some oldPath, Some newPath
        | None -> None, None

    let blockLines =
        lines |> Seq.skip (startIndex + 1) |> Seq.take (endIndex - startIndex - 1)

    let findMetadata marker pathPrefix =
        blockLines |> Seq.tryPick (tryParseMetadataPath marker pathPrefix)

    let oldMetadata =
        findMetadata "rename from " ""
        |> Option.orElseWith (fun () -> findMetadata "--- " "a/")

    let newMetadata =
        findMetadata "rename to " ""
        |> Option.orElseWith (fun () -> findMetadata "+++ " "b/")

    let oldPath = oldMetadata |> Option.defaultValue fallbackOld
    let newPath = newMetadata |> Option.defaultValue fallbackNew

    if oldPath.IsSome || newPath.IsSome then
        Some(oldPath, newPath)
    else
        None

let private parseChangedHeaders (diffText: string) =
    let lines = diffText.Split([| "\r\n"; "\n" |], StringSplitOptions.None)
    let headerPrefix = "diff --git "

    seq {
        let mutable index = 0

        while index < lines.Length do
            if lines.[index].StartsWith(headerPrefix, StringComparison.Ordinal) then
                let mutable nextHeader = index + 1

                while nextHeader < lines.Length
                      && not (lines.[nextHeader].StartsWith(headerPrefix, StringComparison.Ordinal)) do
                    nextHeader <- nextHeader + 1

                match parseDiffBlock lines index nextHeader with
                | Some paths -> yield paths
                | None -> ()

                index <- nextHeader
            else
                index <- index + 1
    }

/// Parse unified diff text into every changed repo-relative path.
/// Rename headers contribute both their old and new paths. Git C-quoted paths are decoded.
let parseChangedPaths (diffText: string) : string list =
    let seen = HashSet<string>(StringComparer.Ordinal)

    parseChangedHeaders diffText
    |> Seq.collect (fun (oldPath, newPath) -> [ oldPath; newPath ] |> List.choose id)
    |> Seq.filter seen.Add
    |> Seq.toList

/// Parse unified diff text (from jj diff --git or git diff) to extract changed file paths.
/// Only returns F# code files (.fs, .fsx, .fsproj).
let parseChangedFiles (diffText: string) : string list =
    parseChangedHeaders diffText
    |> Seq.choose (fun (oldPath, newPath) -> newPath |> Option.orElse oldPath)
    |> Seq.filter isCodeFile
    |> Seq.distinct
    |> Seq.toList

/// Returns true if any .fsproj file changed (triggers conservative fallback).
let hasFsprojChanges (changedFiles: string list) : bool = changedFiles |> List.exists isFsproj
