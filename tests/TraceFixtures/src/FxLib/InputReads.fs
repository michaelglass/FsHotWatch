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

/// A file's metadata through `File`'s static readers.
let fileMetadata (path: string) =
    File.GetLastWriteTimeUtc(path).Ticks + int64 (File.GetAttributes path)

/// A file's length, a `FileInfo` getter.
let fileLength (path: string) = FileInfo(path).Length

/// A file's write time through the getter `FileInfo` inherits from `FileSystemInfo`.
let fileInfoWriteTime (path: string) = FileInfo(path).LastWriteTimeUtc.Ticks

/// A directory's write time through `Directory`.
let directoryWriteTime (dir: string) =
    Directory.GetLastWriteTimeUtc(dir).Ticks
