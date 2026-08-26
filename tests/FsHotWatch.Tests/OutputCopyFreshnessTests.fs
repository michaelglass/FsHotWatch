/// AUTOMATION-245 — the COPY half of build-output freshness, in core so both
/// `FsHotWatch.Build` and `FsHotWatch.TestPrune` can ask it.
///
/// Every absence assertion here is paired with a positive control on the SAME
/// predicate over the SAME tree, because both failure directions are live risks: a
/// predicate that has gone blind reports nothing and looks like a healthy tree
/// (this file's own subject shipped twice in that state), and one that is
/// over-strict reports everything and turns the build cache off.
module FsHotWatch.Tests.OutputCopyFreshnessTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.ProjectGraph
open FsHotWatch.Tests.TestHelpers

// ---------------------------------------------------------------------------
// A two-project tree on disk: Lib (producer) ← App (consumer), each with a
// `bin/Debug/net10.0` output dir, registered THE WAY THE DAEMON DOES —
// `RegisterProject` with real `ReferencedProjects` plus `RegisterProjectOutput` with
// MSBuild's `TargetPath`. AUTOMATION-368: fixtures that register through
// `RegisterFromFsproj`'s XML parse prove a gate works on a path no live daemon takes.
// ---------------------------------------------------------------------------

type private TwoProjects =
    {
        Graph: ProjectGraph
        /// `Lib/bin/Debug/net10.0/Lib.dll` — what the build produced.
        Origin: string
        /// `App/bin/Debug/net10.0/Lib.dll` — the copy a run would load.
        Copy: string
        /// `App/bin/Debug/net10.0/App.dll`.
        ConsumerDll: string
    }

/// `settled` mirrors what a successful build leaves: the copy carries the origin's
/// exact bytes, size and mtime (`File.Copy` propagates the timestamp, and MSBuild's
/// `SkipUnchangedFiles` is defined on exactly that pair). Measured on a real
/// two-project `dotnet build`, and on 37 of 37 dependency copies in a consuming repo.
let private withTwoProjects (label: string) (settled: bool) (body: TwoProjects -> 'a) : 'a =
    withTempDir label (fun tmpDir ->
        let mk name =
            let dir = Path.Combine(tmpDir, name)
            let outDir = Path.Combine(dir, "bin", "Debug", "net10.0")
            Directory.CreateDirectory(outDir) |> ignore
            let proj = Path.Combine(dir, name + ".fsproj")
            let src = Path.Combine(dir, "Lib.fs")
            writeMinimalFsproj proj "net10.0" [ "Lib.fs" ]
            File.WriteAllText(src, "let x = 1")
            proj, src, Path.Combine(outDir, name + ".dll")

        let libProj, libSrc, libDll = mk "Lib"
        let appProj, appSrc, appDll = mk "App"
        let copy = Path.Combine(Path.GetDirectoryName appDll, "Lib.dll")

        File.WriteAllText(libDll, "lib-bytes-v2")
        File.WriteAllText(appDll, "app-bytes")

        if settled then
            // A build that ran: identical bytes, and therefore identical size, and the
            // origin's timestamp carried across.
            File.Copy(libDll, copy, overwrite = true)
            File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc libDll)
        else
            // The merge-flip shape, measured live: the producer's output was refreshed
            // and the consumer's copy was left behind. Same length so nothing in the
            // predicate can be passing on a size difference alone.
            File.WriteAllText(copy, "lib-bytes-v1")
            File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(libDll).AddMinutes(-10.0))

        let graph = ProjectGraph()

        graph.RegisterProject(AbsProjectPath.create libProj, [ AbsFilePath.create libSrc ], [])

        graph.RegisterProject(
            AbsProjectPath.create appProj,
            [ AbsFilePath.create appSrc ],
            [ AbsProjectPath.create libProj ]
        )

        graph.RegisterProjectOutput(AbsProjectPath.create libProj, libDll)
        graph.RegisterProjectOutput(AbsProjectPath.create appProj, appDll)

        body
            { Graph = graph
              Origin = libDll
              Copy = copy
              ConsumerDll = appDll })

// ---------------------------------------------------------------------------
// `dependencyCopies` — is the gate looking at anything at all?
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``dependencyCopies finds the producer's assembly inside the consumer's output dir`` () =
    // The reachability question, asked of the daemon's own registration path. The two
    // gates that preceded this one in BuildPlugin examined NOTHING in every live daemon
    // for two releases while every test stayed green, because the pair they needed was
    // never enumerated.
    withTwoProjects "copyfresh-enumerate" true (fun t ->
        let pairs = OutputCopyFreshness.dependencyCopies t.Graph
        test <@ pairs |> List.map (fun p -> p.Copy) = [ t.Copy ] @>
        test <@ pairs |> List.map (fun p -> p.PrimaryOrigin) = [ t.Origin ] @>
        test <@ pairs |> List.map (fun p -> p.Producer, p.Consumer) = [ "Lib", "App" ] @>)

[<Fact(Timeout = 15000)>]
let ``dependencyCopies ignores a same-named file outside the reference closure`` () =
    // A NuGet package whose assembly name collides with a project's would otherwise be
    // condemned for bytes no project of ours produced. Scoped to the closure the graph
    // already tracks — and the positive control above proves the scoping did not simply
    // exclude everything.
    withTwoProjects "copyfresh-closure" true (fun t ->
        // Drop the ProjectReference; the file on disk is untouched.
        let appProj =
            t.Graph.GetAllProjects()
            |> List.find (fun p -> AbsProjectPath.value p |> Path.GetFileName = "App.fsproj")

        let unlinked = ProjectGraph()

        for p in t.Graph.GetAllProjects() do
            unlinked.RegisterProject(p, t.Graph.GetSourceFiles p, [])

            match t.Graph.GetCanonicalDllPath p with
            | Some dll -> unlinked.RegisterProjectOutput(p, dll)
            | None -> ()

        test <@ List.isEmpty (unlinked.GetDependents appProj) @>
        test <@ List.isEmpty (OutputCopyFreshness.dependencyCopies unlinked) @>)

[<Fact(Timeout = 15000)>]
let ``dependencyCopies skips a producer that has never been built`` () =
    // An absent producer output is `BuildPlugin`'s `DllMissing` finding, which has its
    // own gate. Two gates reporting one file is how a diagnostic stops being read.
    withTwoProjects "copyfresh-unbuilt" true (fun t ->
        // POSITIVE CONTROL on this exact tree: the pair IS enumerated while the origin
        // exists, so the emptiness below is the deletion talking.
        let before = OutputCopyFreshness.dependencyCopies t.Graph
        test <@ before.Length = 1 @>

        File.Delete t.Origin
        test <@ List.isEmpty (OutputCopyFreshness.dependencyCopies t.Graph) @>)

// ---------------------------------------------------------------------------
// `isPending` — MSBuild's own `SkipUnchangedFiles` predicate.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``a copy left behind by a refreshed producer is pending`` () =
    // THE WEDGE'S SHAPE, measured live: a merge refreshes `src/**` outputs, the test
    // projects' copies of them are not refreshed, and the build then REPLAYS
    // "built N projects (cached)" so nothing ever copies them.
    withTwoProjects "copyfresh-pending" false (fun t ->
        let pairs = OutputCopyFreshness.dependencyCopies t.Graph
        test <@ pairs |> List.filter OutputCopyFreshness.isPending |> List.map (fun p -> p.Copy) = [ t.Copy ] @>)

[<Fact(Timeout = 15000)>]
let ``a copy a build has settled is not pending`` () =
    // THE NEGATIVE CONTROL, and the one that matters most: this predicate gates a build
    // cache, so a version of it that fired on a healthy tree would not be a stale-copy
    // detector — it would be caching switched off.
    withTwoProjects "copyfresh-settled" true (fun t ->
        let pairs = OutputCopyFreshness.dependencyCopies t.Graph
        test <@ pairs.Length = 1 @>
        test <@ pairs |> List.filter OutputCopyFreshness.isPending |> List.isEmpty @>)

[<Fact(Timeout = 15000)>]
let ``a copy matching on timestamp but not on size is pending`` () =
    // `SkipUnchangedFiles` needs BOTH. Pinning size on its own, because a predicate
    // written as "mtime differs" would pass every other test in this file.
    withTwoProjects "copyfresh-size" true (fun t ->
        let mtime = File.GetLastWriteTimeUtc t.Copy

        // POSITIVE CONTROL: settled before the rewrite.
        test
            <@
                OutputCopyFreshness.dependencyCopies t.Graph
                |> List.filter OutputCopyFreshness.isPending
                |> List.isEmpty
            @>

        File.WriteAllText(t.Copy, "lib-bytes-v2-and-then-some")
        File.SetLastWriteTimeUtc(t.Copy, mtime)

        test
            <@
                OutputCopyFreshness.dependencyCopies t.Graph
                |> List.filter OutputCopyFreshness.isPending
                |> List.length = 1
            @>)

[<Fact(Timeout = 15000)>]
let ``a byte-divergent copy at the same size and timestamp is NOT pending`` () =
    // The class this predicate deliberately does not claim, pinned so nobody "fixes" it
    // into the gate. MEASURED: with size and mtime equal, MSBuild's incremental copy
    // skips, and a plain `dotnet build` leaves the destination byte-for-byte as it found
    // it. Bypassing the build cache over it would buy a rebuild that cannot repair it —
    // on every lookup, for ever. `verdict` below is what speaks about those bytes, on
    // the cold path.
    withTwoProjects "copyfresh-inverted" true (fun t ->
        let size = FileInfo(t.Copy).Length
        let mtime = File.GetLastWriteTimeUtc t.Copy
        File.WriteAllText(t.Copy, String('x', int size))
        File.SetLastWriteTimeUtc(t.Copy, mtime)

        let pairs = OutputCopyFreshness.dependencyCopies t.Graph
        test <@ pairs |> List.filter OutputCopyFreshness.isPending |> List.isEmpty @>

        // …and the content rule DOES see it, which is what makes the line above a
        // deliberate scope boundary rather than a blind spot.
        let pair = List.exactlyOne pairs

        test
            <@
                OutputCopyFreshness.verdict ContentHash.ofFile pair.Copy pair.PrimaryOrigin pair.OtherOrigins = OutputCopyFreshness.DiffersFromOrigins
                    t.Origin
            @>)

// ---------------------------------------------------------------------------
// `verdict` — the byte rule, shared with TestPrune's `ArtifactFreshness`.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``verdict matches a copy against any origin, not only the primary one`` () =
    // Which target framework MSBuild copied from is not knowable here — a net10.0
    // consumer takes a netstandard2.0 dependency's netstandard2.0 output quite happily
    // — so matching ANY current output is current. Naming the primary is presentation.
    let hashes = dict [ "copy", "aaa"; "primary", "bbb"; "other", "aaa" ]

    let hash (p: string) = hashes[p]

    test <@ OutputCopyFreshness.verdict hash "copy" "primary" [ "other" ] = OutputCopyFreshness.MatchesAnOrigin @>

    // NEGATIVE CONTROL on the same detector: drop the matching origin and it is a
    // finding, so the pass above is the match talking and not a rule that never fires.
    test <@ OutputCopyFreshness.verdict hash "copy" "primary" [] = OutputCopyFreshness.DiffersFromOrigins "primary" @>

[<Fact(Timeout = 15000)>]
let ``verdict refuses to call a copy stale when an origin could not be read`` () =
    // A mismatch we could not fully check is ignorance, not evidence. The sentinel
    // matches nothing, so without this arm an unreadable origin reads as a divergence.
    let hash (p: string) =
        if p = "other" then ContentHash.UnhashableContent else p

    test
        <@ OutputCopyFreshness.verdict hash "copy" "primary" [ "other" ] = OutputCopyFreshness.OriginUnreadable "other" @>

[<Fact(Timeout = 15000)>]
let ``verdict refuses to call an unreadable copy anything at all`` () =
    // The sentinel matches nothing, so an unreadable copy would otherwise masquerade as
    // a divergence — condemning a tree over a file nobody could look at.
    let hash (p: string) =
        if p = "copy" then ContentHash.UnhashableContent else "same"

    test <@ OutputCopyFreshness.verdict hash "copy" "primary" [] = OutputCopyFreshness.CopyUnreadable "copy" @>

// ---------------------------------------------------------------------------
// The arms that decide what the gate DOES NOT look at. Each one exists so the
// predicate cannot condemn a file no build of ours is responsible for, and each is
// paired with the positive control that proves the exclusion is not simply "nothing
// is ever enumerated".
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``a copy that is not on disk is not pending`` () =
    // A copy that is absent is the presence probe's business — a build in flight may
    // still land it — and `BuildPlugin`'s `DllMissing` owns the producer's own output.
    // This predicate speaks only about files that EXIST and are out of date.
    withTwoProjects "copyfresh-absent" true (fun t ->
        let pair = OutputCopyFreshness.dependencyCopies t.Graph |> List.exactlyOne

        // POSITIVE CONTROL: this very pair IS judged (and settled) while the file is
        // there, so the `false` below is the deletion talking.
        test <@ not (OutputCopyFreshness.isPending pair) @>

        File.Delete pair.Copy
        test <@ not (OutputCopyFreshness.isPending pair) @>)

[<Fact(Timeout = 15000)>]
let ``dependencyCopies skips a consumer whose output the graph cannot name`` () =
    // No recorded `TargetPath` and no parseable `<TargetFramework>` means we do not know
    // where that project's output dir IS. `artifactCoverageGap` is what reports that
    // class; inventing a directory here would be the silent-degradation shape instead.
    withTwoProjects "copyfresh-no-consumer-output" true (fun t ->
        let projects = t.Graph.GetAllProjects()

        let partial = ProjectGraph()

        for p in projects do
            partial.RegisterProject(p, t.Graph.GetSourceFiles p, t.Graph.GetReferences p)

        // Only the producer gets an output path recorded.
        let producer =
            projects
            |> List.find (fun p -> Path.GetFileName(AbsProjectPath.value p) = "Lib.fsproj")

        partial.RegisterProjectOutput(producer, t.Origin)

        test <@ List.isEmpty (OutputCopyFreshness.dependencyCopies partial) @>)

[<Fact(Timeout = 15000)>]
let ``dependencyCopies skips a producer whose recorded output directory does not exist`` () =
    // An evaluation that reported a `TargetPath` under a tree that was never created.
    // There is nothing to compare against, so there is nothing to say — and saying
    // something would be a permanent bypass over a directory no build will make.
    withTwoProjects "copyfresh-no-outdir" true (fun t ->
        let graph = ProjectGraph()

        for p in t.Graph.GetAllProjects() do
            graph.RegisterProject(p, t.Graph.GetSourceFiles p, t.Graph.GetReferences p)

            match t.Graph.GetCanonicalDllPath p with
            | Some dll when Path.GetFileNameWithoutExtension dll = "Lib" ->
                // Under a `bin/Debug` that was never created, so not even the per-TFM
                // directories the producer's other frameworks would live in exist.
                let libDir =
                    dll
                    |> Path.GetDirectoryName
                    |> Path.GetDirectoryName
                    |> Path.GetDirectoryName
                    |> Path.GetDirectoryName

                graph.RegisterProjectOutput(p, Path.Combine(libDir, "nowhere", "bin", "Debug", "net10.0", "Lib.dll"))
            | Some dll -> graph.RegisterProjectOutput(p, dll)
            | None -> ()

        test <@ List.isEmpty (OutputCopyFreshness.dependencyCopies graph) @>)

[<Fact(Timeout = 15000)>]
let ``dependencyCopies does not treat a shared output directory as a copy`` () =
    // Two projects writing to one output dir means the consumer holds the ORIGIN, not a
    // copy of it. Comparing a file against itself is always "settled", but enumerating
    // it at all would put a self-referential pair in every diagnostic.
    withTempDir "copyfresh-shared-out" (fun tmpDir ->
        let outDir = Path.Combine(tmpDir, "out", "net10.0")
        Directory.CreateDirectory(outDir) |> ignore
        let libDll = Path.Combine(outDir, "Lib.dll")
        let appDll = Path.Combine(outDir, "App.dll")
        File.WriteAllText(libDll, "lib")
        File.WriteAllText(appDll, "app")

        let mkProj name =
            let dir = Path.Combine(tmpDir, name)
            Directory.CreateDirectory(dir) |> ignore
            let proj = Path.Combine(dir, name + ".fsproj")
            let src = Path.Combine(dir, "Lib.fs")
            writeMinimalFsproj proj "net10.0" [ "Lib.fs" ]
            File.WriteAllText(src, "let x = 1")
            AbsProjectPath.create proj, AbsFilePath.create src

        let libProj, libSrc = mkProj "Lib"
        let appProj, appSrc = mkProj "App"

        let graph = ProjectGraph()
        graph.RegisterProject(libProj, [ libSrc ], [])
        graph.RegisterProject(appProj, [ appSrc ], [ libProj ])
        graph.RegisterProjectOutput(libProj, libDll)
        graph.RegisterProjectOutput(appProj, appDll)

        test <@ List.isEmpty (OutputCopyFreshness.dependencyCopies graph) @>)

[<Fact(Timeout = 15000)>]
let ``dependencyCopies offers every target framework the producer built`` () =
    // Which framework MSBuild copied from is not knowable from the graph, so a copy is
    // judged against ALL of the producer's per-TFM outputs. Naming the one built for the
    // consumer's own framework is presentation; the verdict is over the whole set.
    withTwoProjects "copyfresh-multi-tfm" true (fun t ->
        // A second output dir for the producer, as a multi-targeted project has.
        let net10Dir = Path.GetDirectoryName t.Origin
        let net8Dir = Path.Combine(Path.GetDirectoryName net10Dir, "net8.0")
        Directory.CreateDirectory(net8Dir) |> ignore
        let net8Dll = Path.Combine(net8Dir, "Lib.dll")
        File.WriteAllText(net8Dll, "lib-bytes-net8")

        let pair = OutputCopyFreshness.dependencyCopies t.Graph |> List.exactlyOne
        test <@ pair.PrimaryOrigin = t.Origin @>
        test <@ pair.OtherOrigins = [ net8Dll ] @>

        // The consumer's copy carrying the OTHER framework's bytes is still current —
        // matching any current output means the run loads code that matches the sources.
        File.WriteAllText(pair.Copy, "lib-bytes-net8")

        test
            <@
                OutputCopyFreshness.verdict ContentHash.ofFile pair.Copy pair.PrimaryOrigin pair.OtherOrigins = OutputCopyFreshness.MatchesAnOrigin
            @>)

[<Fact(Timeout = 15000)>]
let ``dependencyCopies names an origin even when none was built for the consumer's framework`` () =
    // A net10.0 consumer takes a netstandard2.0 dependency's netstandard2.0 output quite
    // happily, so "nothing matches my TFM" is a normal tree, not a gap. The pair must
    // still be enumerated, with a named origin.
    withTwoProjects "copyfresh-no-tfm-match" true (fun t ->
        let net10Dir = Path.GetDirectoryName t.Origin
        let otherDir = Path.Combine(Path.GetDirectoryName net10Dir, "netstandard2.0")
        Directory.CreateDirectory(otherDir) |> ignore
        let otherDll = Path.Combine(otherDir, "Lib.dll")
        File.Copy(t.Origin, otherDll)
        File.Delete t.Origin

        let pair = OutputCopyFreshness.dependencyCopies t.Graph |> List.exactlyOne
        test <@ pair.PrimaryOrigin = otherDll @>
        test <@ List.isEmpty pair.OtherOrigins @>)

[<Fact(Timeout = 15000)>]
let ``an origin that has vanished cannot settle a copy`` () =
    // `dependencyCopies` only offers origins that exist, but a pair outlives the walk
    // that produced it and the file can go between the two. Answering "settled" for an
    // origin nobody can stat would certify a copy against nothing at all; pending is the
    // fail-safe direction, and it costs one build.
    withTwoProjects "copyfresh-vanished-origin" true (fun t ->
        let pair = OutputCopyFreshness.dependencyCopies t.Graph |> List.exactlyOne

        // POSITIVE CONTROL: settled while the origin is there.
        test <@ not (OutputCopyFreshness.isPending pair) @>

        File.Delete pair.PrimaryOrigin
        test <@ OutputCopyFreshness.isPending pair @>)

[<Fact(Timeout = 15000)>]
let ``a diamond in the dependent graph yields each consumer once`` () =
    // `Lib ← Mid ← App` AND `Lib ← App`: the walk reaches App by two routes. Emitting it
    // twice would double every diagnostic and repair-count for a shape that is entirely
    // ordinary in a layered repo.
    withTempDir "copyfresh-diamond" (fun tmpDir ->
        let mk name =
            let dir = Path.Combine(tmpDir, name)
            let outDir = Path.Combine(dir, "bin", "Debug", "net10.0")
            Directory.CreateDirectory(outDir) |> ignore
            let proj = Path.Combine(dir, name + ".fsproj")
            let src = Path.Combine(dir, "Lib.fs")
            writeMinimalFsproj proj "net10.0" [ "Lib.fs" ]
            File.WriteAllText(src, "let x = 1")
            let dll = Path.Combine(outDir, name + ".dll")
            File.WriteAllText(dll, name + "-bytes")
            AbsProjectPath.create proj, AbsFilePath.create src, dll

        let libProj, libSrc, libDll = mk "Lib"
        let midProj, midSrc, midDll = mk "Mid"
        let appProj, appSrc, appDll = mk "App"

        // Both consumers hold a copy of Lib.dll, left behind by a refreshed producer.
        for consumerDll in [ midDll; appDll ] do
            let copy = Path.Combine(Path.GetDirectoryName consumerDll, "Lib.dll")
            File.WriteAllText(copy, "Lib-bytes-v1")
            File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(libDll).AddMinutes(-10.0))

        let graph = ProjectGraph()
        graph.RegisterProject(libProj, [ libSrc ], [])
        graph.RegisterProject(midProj, [ midSrc ], [ libProj ])
        graph.RegisterProject(appProj, [ appSrc ], [ libProj; midProj ])
        graph.RegisterProjectOutput(libProj, libDll)
        graph.RegisterProjectOutput(midProj, midDll)
        graph.RegisterProjectOutput(appProj, appDll)

        let libCopies =
            OutputCopyFreshness.dependencyCopies graph
            |> List.filter (fun p -> p.Producer = "Lib")

        test <@ libCopies |> List.map (fun p -> p.Consumer) |> List.sort = [ "App"; "Mid" ] @>
        test <@ libCopies |> List.filter OutputCopyFreshness.isPending |> List.length = 2 @>)
