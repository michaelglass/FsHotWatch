/// File inputs reached through the BCL overloads the original shim set left out: one
/// call per family, so a woven run shows whether each family is recorded.
module FxLib.InputReads

open System.IO

/// A `DirectoryInfo` listing of directories.
let directoryInfoDirectories (dir: string) =
    DirectoryInfo(dir).GetDirectories().Length

/// A listing that takes `EnumerationOptions`.
let listingWithOptions (dir: string) =
    Directory.GetFiles(dir, "*", EnumerationOptions()).Length

/// A `StreamReader` opened by path with the encoding-detection flag.
let readerWithDetection (path: string) =
    use reader = new StreamReader(path, true)
    reader.ReadToEnd().Length
