module FsHotWatch.Tests.DaemonIdentityTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.DaemonIdentity
open FsHotWatch.Tests.TestHelpers

// The daemon/CLI binary-identity handshake: a new CLI must never silently talk to
// an old daemon. Detection works UNILATERALLY — an old daemon that never recorded
// an identity reads as unknown, which is stale, which restarts it.

let private ident v h : BinaryIdentity = { Version = v; ContentHash = h }

// ---------------------------------------------------------------------------
// render/parse round-trip
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``render then parse round-trips`` () =
    let id = ident "0.10.0-alpha.2+abc123" "deadbeefcafe0123"
    test <@ BinaryIdentity.parse (BinaryIdentity.render id) = Some id @>

[<Fact(Timeout = 15000)>]
let ``parse rejects garbage`` () =
    test <@ BinaryIdentity.parse null = None @>
    test <@ BinaryIdentity.parse "" = None @>
    test <@ BinaryIdentity.parse "   " = None @>
    test <@ BinaryIdentity.parse "just-one-token" = None @>
    test <@ BinaryIdentity.parse "trailing-space " = None @>

[<Fact(Timeout = 15000)>]
let ``parse takes the LAST space as separator so a spaced version still parses`` () =
    // A historic record with an unsanitized version must still parse coherently,
    // and must not read as a match for something else.
    let parsed = BinaryIdentity.parse "1.0.0 build 7 aabbccdd"
    test <@ parsed = Some(ident "1.0.0 build 7" "aabbccdd") @>

// ---------------------------------------------------------------------------
// compareIdentity — the handshake decision
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``compareIdentity matches identical identities`` () =
    let a = ident "1.0.0" "aaaa"
    test <@ compareIdentity (Some a) a = IdentityVerdict.Match @>

[<Fact(Timeout = 15000)>]
let ``compareIdentity is stale when the content hash differs even with the same version`` () =
    // A locally-repacked build shares the version string and differs in content:
    // the version label can lie, the hash cannot (AUTOMATION-123).
    let recorded = ident "1.0.0" "aaaa"
    let current = ident "1.0.0" "bbbb"
    test <@ compareIdentity (Some recorded) current = IdentityVerdict.Stale(StaleReason.DifferentBinary recorded) @>

[<Fact(Timeout = 15000)>]
let ``compareIdentity is stale when the version differs`` () =
    let recorded = ident "0.9.0" "aaaa"
    let current = ident "1.0.0" "aaaa"
    test <@ compareIdentity (Some recorded) current = IdentityVerdict.Stale(StaleReason.DifferentBinary recorded) @>

[<Fact(Timeout = 15000)>]
let ``compareIdentity treats a missing record as stale — never as a match`` () =
    // The release case: a NEW CLI meets a daemon from a build that never wrote an
    // identity. Unknown ⇒ stale ⇒ restart, with no cooperation from that daemon.
    test <@ compareIdentity None (ident "1.0.0" "aaaa") = IdentityVerdict.Stale StaleReason.NotRecorded @>

// ---------------------------------------------------------------------------
// buildIdentity — version sanitization + content-hash fallbacks
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``buildIdentity sanitizes spaces out of the version so the record stays parseable`` () =
    let id = buildIdentity (Some "1.0.0 build 7") "/nonexistent" None
    test <@ id.Version = "1.0.0_build_7" @>

[<Fact(Timeout = 15000)>]
let ``buildIdentity falls back to unknown-version when none is available`` () =
    test <@ (buildIdentity None "/nonexistent" None).Version = "unknown-version" @>

[<Fact(Timeout = 15000)>]
let ``buildIdentity hashes the entry assembly dll when it exists`` () =
    withTempDir "identity-hash" (fun tmpDir ->
        let dll = Path.Combine(tmpDir, "fake.dll")
        File.WriteAllBytes(dll, [| 1uy; 2uy; 3uy |])
        let id = buildIdentity (Some "1.0.0") "/nonexistent" (Some dll)
        test <@ id.ContentHash <> UnhashableContent @>
        test <@ id.ContentHash.Length = 16 @>

        // Content change ⇒ hash change: the property the version alone lacks.
        File.WriteAllBytes(dll, [| 9uy; 9uy; 9uy |])
        let id2 = buildIdentity (Some "1.0.0") "/nonexistent" (Some dll)
        test <@ id2.ContentHash <> id.ContentHash @>)

[<Fact(Timeout = 15000)>]
let ``buildIdentity falls back to the process path when no entry dll resolves`` () =
    withTempDir "identity-exe" (fun tmpDir ->
        let exe = Path.Combine(tmpDir, "fake-exe")
        File.WriteAllBytes(exe, [| 4uy; 5uy |])
        let id = buildIdentity (Some "1.0.0") exe None
        test <@ id.ContentHash <> UnhashableContent @>)

[<Fact(Timeout = 15000)>]
let ``buildIdentity uses the DETERMINISTIC unhashable sentinel when nothing is readable`` () =
    // Deterministic on purpose: a daemon and CLI running the same unhashable binary
    // must still AGREE, or every command restarts the daemon and thrashes the warm
    // FCS cache.
    let a = buildIdentity (Some "1.0.0") "/nonexistent" None
    let b = buildIdentity (Some "1.0.0") "/nonexistent" None
    test <@ a.ContentHash = UnhashableContent @>
    test <@ a = b @>

// ---------------------------------------------------------------------------
// record / read / verdict — the on-disk handshake
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``recordCurrent then verdictFor is Match for the same process`` () =
    withTempDir "identity-roundtrip" (fun tmpDir ->
        recordCurrent tmpDir
        test <@ verdictFor tmpDir = IdentityVerdict.Match @>)

[<Fact(Timeout = 15000)>]
let ``verdictFor with no recorded identity is Stale NotRecorded`` () =
    withTempDir "identity-absent" (fun tmpDir ->
        test <@ verdictFor tmpDir = IdentityVerdict.Stale StaleReason.NotRecorded @>)

[<Fact(Timeout = 15000)>]
let ``verdictFor with a different recorded identity is Stale DifferentBinary`` () =
    withTempDir "identity-differs" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".fshw")) |> ignore
        File.WriteAllText(identityFilePath tmpDir, "0.0.1-old cafebabe00000000")

        let expected =
            IdentityVerdict.Stale(StaleReason.DifferentBinary(ident "0.0.1-old" "cafebabe00000000"))

        test <@ verdictFor tmpDir = expected @>)

[<Fact(Timeout = 15000)>]
let ``readRecordedWith returns None for an unparseable record — fail closed`` () =
    let readRecorded content =
        readRecordedWith (fun _ -> true) (fun _ -> content) "/tmp/repo"

    test <@ readRecorded "garbage" = None @>
    test <@ readRecorded "" = None @>

// ---------------------------------------------------------------------------
// identitySourceFile / hashFile — which file IS "this binary", and the
// fallbacks when it cannot be read.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``identitySourceFile prefers the entry dll when it resolves to a real file`` () =
    withTempDir "identity-source-dll" (fun tmpDir ->
        let dll = Path.Combine(tmpDir, "app.dll")
        File.WriteAllText(dll, "x")
        let exe = Path.Combine(tmpDir, "app-exe")
        File.WriteAllText(exe, "y")

        test <@ identitySourceFile exe (Some dll) = Some dll @>)

[<Fact(Timeout = 15000)>]
let ``identitySourceFile falls back to the process path when the dll is absent, blank, or missing`` () =
    withTempDir "identity-source-exe" (fun tmpDir ->
        let exe = Path.Combine(tmpDir, "app-exe")
        File.WriteAllText(exe, "y")

        // No entry assembly at all (single-file / shim).
        test <@ identitySourceFile exe None = Some exe @>
        // A blank location (the shim case that returns "").
        test <@ identitySourceFile exe (Some "   ") = Some exe @>
        // A location that does not exist on disk.
        test <@ identitySourceFile exe (Some(Path.Combine(tmpDir, "gone.dll"))) = Some exe @>)

[<Fact(Timeout = 15000)>]
let ``identitySourceFile is None when neither the dll nor the process path exists`` () =
    test <@ identitySourceFile "/nonexistent/exe" (Some "/nonexistent/app.dll") = None @>
    test <@ identitySourceFile "" None = None @>

[<Fact(Timeout = 15000)>]
let ``hashFile returns None for an unreadable path rather than throwing`` () =
    // The caller substitutes the deterministic `UnhashableContent` sentinel; a
    // throw here would take the daemon down over a diagnostic.
    test <@ hashFile "/nonexistent/nothing-here" = None @>

[<Fact(Timeout = 15000)>]
let ``hashFile is deterministic for identical content and differs for different content`` () =
    withTempDir "identity-hashfile" (fun tmpDir ->
        let a = Path.Combine(tmpDir, "a.bin")
        let b = Path.Combine(tmpDir, "b.bin")
        let c = Path.Combine(tmpDir, "c.bin")
        File.WriteAllBytes(a, [| 1uy; 2uy; 3uy |])
        File.WriteAllBytes(b, [| 1uy; 2uy; 3uy |])
        File.WriteAllBytes(c, [| 1uy; 2uy; 4uy |])

        test <@ hashFile a = hashFile b @>
        test <@ hashFile a <> hashFile c @>
        test <@ (hashFile a).Value.Length = 16 @>)

[<Fact(Timeout = 15000)>]
let ``currentIdentity reports a usable version and a real content hash for this process`` () =
    // The live reflection path (informational version, else assembly version, else
    // the unknown-version fallback) — the same path the daemon takes at startup.
    let id = currentIdentity ()
    test <@ not (String.IsNullOrWhiteSpace id.Version) @>
    // The version must never contain the record's separator, or the record
    // could not be parsed back.
    test <@ not (id.Version.Contains " ") @>
    // The test assembly is a real file on disk, so the hash is real, not the sentinel.
    test <@ id.ContentHash <> UnhashableContent @>
    test <@ id.ContentHash.Length = 16 @>
    test <@ BinaryIdentity.parse (BinaryIdentity.render id) = Some id @>

[<Fact(Timeout = 15000)>]
let ``readRecordedWith swallows a read failure and reports no identity — fail closed`` () =
    let recorded =
        readRecordedWith (fun _ -> true) (fun _ -> raise (IOException "disk gone")) "/tmp/repo"

    test <@ recorded = None @>
    // ...which the comparison turns into a RESTART, never a reuse.
    test <@ compareIdentity recorded (currentIdentity ()) = IdentityVerdict.Stale StaleReason.NotRecorded @>

[<Fact(Timeout = 15000)>]
let ``buildIdentity treats a blank version as unknown`` () =
    test <@ (buildIdentity (Some "   ") "/nonexistent" None).Version = "unknown-version" @>
    test <@ (buildIdentity (Some "") "/nonexistent" None).Version = "unknown-version" @>

[<Fact(Timeout = 15000)>]
let ``versionOf reads a real assembly's version`` () =
    let asm = typeof<BinaryIdentity>.Assembly
    let v = versionOf asm
    test <@ v.IsSome @>
    test <@ not (String.IsNullOrWhiteSpace v.Value) @>

[<Fact(Timeout = 15000)>]
let ``identityOf with a real entry assembly hashes that assembly's file`` () =
    let asm = typeof<BinaryIdentity>.Assembly
    let id = identityOf (Some asm) "/nonexistent-process-path"
    test <@ id.Version <> "unknown-version" @>
    // Hashed the assembly's own dll — NOT the (nonexistent) process path.
    test <@ id.ContentHash <> UnhashableContent @>
    test <@ Some id.ContentHash = hashFile asm.Location @>

[<Fact(Timeout = 15000)>]
let ``identityOf with NO entry assembly falls back to the process path`` () =
    // The single-file / shim host, where GetEntryAssembly() is null: it must still
    // produce a usable, DETERMINISTIC identity — not throw, and not a random value
    // that would restart the daemon on every command.
    withTempDir "identity-noentry" (fun tmpDir ->
        let exe = Path.Combine(tmpDir, "fshw-single-file")
        File.WriteAllBytes(exe, [| 7uy; 7uy; 7uy |])

        let id = identityOf None exe
        test <@ id.Version = "unknown-version" @>
        test <@ Some id.ContentHash = hashFile exe @>
        test <@ identityOf None exe = id @>)

[<Fact(Timeout = 15000)>]
let ``identityOf with neither an assembly nor a readable exe is the deterministic sentinel`` () =
    let a = identityOf None "/nonexistent/exe"
    let b = identityOf None "/nonexistent/exe"

    test
        <@
            a = { Version = "unknown-version"
                  ContentHash = UnhashableContent }
        @>
    // Deterministic, so two such daemons AGREE and the CLI does not restart on
    // every command.
    test <@ a = b @>
    test <@ compareIdentity (Some a) b = IdentityVerdict.Match @>
