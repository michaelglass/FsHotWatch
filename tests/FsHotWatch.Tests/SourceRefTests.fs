module FsHotWatch.Tests.SourceRefTests

// AUTOMATION-123 — `fshw --version` must report the source ref the binary was
// built from. SourceRef.parse reads it out of the assembly informational
// version (RefStamp `-ref.` stamp, SourceLink `+sha` metadata, or honestly
// Unknown); SourceRef.describe renders the one human line.

open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.SourceRef

// --- parse: RefStamp stamps ---------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``parse reads a clean jj ref stamp`` () =
    test <@ parse "0.14.0-alpha.6-ref.xqknxxtq.gda8793b6ca64" = SourceRef.RefStamped("xqknxxtq.gda8793b6ca64", false) @>

[<Fact(Timeout = 15000)>]
let ``parse reads a dirty jj ref stamp`` () =
    test
        <@
            parse "0.14.0-alpha.6-ref.xqknxxtq.gda8793b6ca64.dirty" = SourceRef.RefStamped(
                "xqknxxtq.gda8793b6ca64.dirty",
                true
            )
        @>

[<Fact(Timeout = 15000)>]
let ``parse reads a git ref stamp with a dirty-state hash`` () =
    // git-dirty shape: the dirty marker sits mid-body, followed by the stash
    // hash — it still counts as dirty.
    test
        <@
            parse "1.2.3-ref.gaaaabbbbcccc.dirty.gddddeeeeffff" = SourceRef.RefStamped(
                "gaaaabbbbcccc.dirty.gddddeeeeffff",
                true
            )
        @>

[<Fact(Timeout = 15000)>]
let ``parse prefers the ref stamp over commit metadata when both are present`` () =
    // A colocated jj+git repo can have SourceLink active during a local pack:
    // the stamp names the exact tree, so it wins; the metadata is dropped.
    test
        <@
            parse "1.2.3-ref.xqknxxtq.gda8793b6ca64+0123456789abcdef" = SourceRef.RefStamped(
                "xqknxxtq.gda8793b6ca64",
                false
            )
        @>

[<Fact(Timeout = 15000)>]
let ``parse counts a leading dirty segment as dirty`` () =
    // Defensive: no stamper emits this shape today, but a dirty marker is a
    // dirty marker wherever it sits in the body.
    test <@ parse "1.2.3-ref.dirty.gddddeeeeffff" = SourceRef.RefStamped("dirty.gddddeeeeffff", true) @>

[<Fact(Timeout = 15000)>]
let ``parse of a bare ref marker with no body is Unknown`` () =
    test <@ parse "1.2.3-ref." = SourceRef.Unknown @>

// --- parse: commit metadata (release/CI builds) -------------------------------

[<Fact(Timeout = 15000)>]
let ``parse reads commit metadata from a release build`` () =
    test
        <@
            parse "0.14.0-alpha.6+da8793b6ca64527765827eacd49efe5b7994341a" = SourceRef.CommitMetadata(
                "da8793b6ca64527765827eacd49efe5b7994341a"
            )
        @>

[<Fact(Timeout = 15000)>]
let ``parse of empty metadata is Unknown`` () =
    test <@ parse "0.14.0-alpha.6+" = SourceRef.Unknown @>

// --- parse: nothing recorded --------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``parse of a plain version is Unknown`` () =
    test <@ parse "0.14.0-alpha.6" = SourceRef.Unknown @>

[<Fact(Timeout = 15000)>]
let ``parse of null is Unknown`` () =
    test <@ parse null = SourceRef.Unknown @>

// --- describe -----------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``describe names a clean local pack's ref`` () =
    let line = describe (SourceRef.RefStamped("xqknxxtq.gda8793b6ca64", false))

    test <@ line.Contains "xqknxxtq.gda8793b6ca64" @>
    test <@ line.Contains "source ref" @>
    test <@ not (line.Contains "dirty") @>

[<Fact(Timeout = 15000)>]
let ``describe flags a dirty local pack`` () =
    let line = describe (SourceRef.RefStamped("xqknxxtq.gda8793b6ca64.dirty", true))

    test <@ line.Contains "dirty" @>

[<Fact(Timeout = 15000)>]
let ``describe names a release build's sha`` () =
    let line = describe (SourceRef.CommitMetadata "da8793b6ca64")

    test <@ line.Contains "da8793b6ca64" @>

[<Fact(Timeout = 15000)>]
let ``describe is honest about an unknown ref`` () =
    let line = describe SourceRef.Unknown

    test <@ line.Contains "unknown" @>

// --- line ---------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``line composes parse and describe`` () =
    let output = line "0.14.0-alpha.6-ref.xqknxxtq.gda8793b6ca64.dirty"

    test <@ output.Contains "xqknxxtq.gda8793b6ca64" @>
    test <@ output.Contains "dirty" @>
