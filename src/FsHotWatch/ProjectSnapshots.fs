/// The FCS project snapshots the check pipeline type-checks against.
module FsHotWatch.ProjectSnapshots

// FSharpProjectSnapshot is marked experimental; it is the checker's native input.
#nowarn "57"

open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.CodeAnalysis.ProjectSnapshot
open FSharp.Compiler.Text

/// The file being checked: the text read for it and the version that text is checked under.
type OpenFile =
    { Path: string
      Version: string
      Text: string }

/// Read the file about to be checked. A missing file reads as empty.
let readOpenFile (_hashFile: string -> string) (path: string) : OpenFile =
    let text =
        try
            File.ReadAllText path
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> ""

    { Path = path
      Version = ""
      Text = text }

/// The snapshot for checking `openFile` in `options`.
let build
    (_hashFile: string -> string)
    (_repoRoot: string option)
    (openFile: OpenFile)
    (options: FSharpProjectOptions)
    : Async<FSharpProjectSnapshot> =
    FSharpProjectSnapshot.FromOptions(
        options,
        openFile.Path,
        0,
        SourceText.ofString openFile.Text,
        DocumentSource.FileSystem
    )

/// Parse and type-check `path` against `snapshot`.
let parseAndCheck
    (checker: FSharpChecker)
    (path: string)
    (snapshot: FSharpProjectSnapshot)
    : Async<FSharpParseFileResults * FSharpCheckFileAnswer> =
    checker.ParseAndCheckFileInProject(path, snapshot)

/// Drop everything the checker holds for `options`' project.
let invalidate (checker: FSharpChecker) (options: FSharpProjectOptions) : unit =
    checker.InvalidateConfiguration(options)
