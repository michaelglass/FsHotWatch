/// A hardlink mirror of a build output directory, and the one safe way to change a file in it.
module TestPrune.Trace.HardLink

open System
open System.IO
open System.Reflection.PortableExecutable
open System.Runtime.InteropServices

module private Native =
    [<DllImport("libc", SetLastError = true, EntryPoint = "link")>]
    extern int link(string oldpath, string newpath)

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool CreateHardLinkW(string newName, string existing, nativeint security)

/// Hardlink `src` at `dst`; false when the OS refuses (e.g. across devices).
let private nativeLink: string -> string -> bool =
    if OperatingSystem.IsWindows() then
        fun src dst -> Native.CreateHardLinkW(dst, src, 0n)
    else
        fun src dst -> Native.link (src, dst) = 0

/// What a mirror did.
type MirrorStats =
    {
        /// Files hardlinked from the source.
        Linked: int
        /// Files copied because linking failed.
        Copied: int
        /// Shadow files deleted because the source no longer has them.
        Removed: int
    }

let private files (dir: string) =
    let opts =
        EnumerationOptions(RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint)

    Directory.EnumerateFiles(dir, "*", opts)
    |> Seq.map (fun f -> Path.GetRelativePath(dir, f))
    |> Set.ofSeq

/// Whether `path` is an assembly carrying an embedded portable PDB. A file that is not a
/// managed assembly (a native library, a resource) carries none.
let private embedsPdb (path: string) =
    try
        use stream = File.OpenRead path
        use pe = new PEReader(stream)

        pe.HasMetadata
        && pe.ReadDebugDirectory()
           |> Seq.exists (fun e -> e.Type = DebugDirectoryEntryType.EmbeddedPortablePdb)
    with :? BadImageFormatException ->
        false

/// Whether a coverage collector may rewrite `path` in place: an assembly with symbols (a
/// sibling `.pdb` or an embedded one), or that sibling `.pdb`. MS CodeCoverage instruments
/// such an assembly by writing into the file, then restores it by replacing it, so through a
/// hardlink the instrumented bytes would reach the build output and stay there.
let internal instrumentable (path: string) =
    let ext = Path.GetExtension path

    if ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) then
        File.Exists(Path.ChangeExtension(path, ".pdb")) || embedsPdb path
    elif ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) then
        File.Exists(Path.ChangeExtension(path, ".dll"))
    else
        false

/// `mirror` with the link primitive passed in, so the copy fallback is testable.
let internal mirrorWith (link: string -> string -> bool) (sourceDir: string) (shadowDir: string) : MirrorStats =
    Directory.CreateDirectory shadowDir |> ignore
    let src = files sourceDir
    let stale = files shadowDir - src

    for rel in stale do
        File.Delete(Path.Combine(shadowDir, rel))

    let mutable linked, copied = 0, 0

    for rel in src do
        let s, d = Path.Combine(sourceDir, rel), Path.Combine(shadowDir, rel)
        Directory.CreateDirectory(Path.GetDirectoryName d) |> ignore
        // Delete first: link(2) refuses an existing name, and the old entry may be a
        // woven replacement or a link to a since-rebuilt (different) inode.
        File.Delete d

        if not (instrumentable s) && link s d then
            linked <- linked + 1
        else
            File.Copy(s, d)
            copied <- copied + 1

    { Linked = linked
      Copied = copied
      Removed = stale.Count }

/// Make `shadowDir` a hardlink mirror of `sourceDir`. EVERY file is re-linked each time:
/// a rebuilt source file is a new inode, and a surviving old link would keep serving the
/// old bytes. Files the source no longer has are deleted. An assembly a coverage collector
/// may instrument (see `instrumentable`) is copied, never linked; so is a file the OS
/// refuses to link (e.g. across devices).
let mirror (sourceDir: string) (shadowDir: string) : MirrorStats =
    mirrorWith nativeLink sourceDir shadowDir

/// Replace `path` by letting `write` fill a temp sibling, then renaming it over. A rename
/// replaces the DIRECTORY ENTRY; writing into `path` would write through a hardlink into
/// the build output it mirrors.
let replaceWith (path: string) (write: string -> unit) =
    let tmp = path + ".tp-tmp"
    File.Delete tmp
    write tmp
    File.Move(tmp, path, true)
