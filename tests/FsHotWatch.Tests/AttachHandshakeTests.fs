/// The attach handshake: every claim a client makes is checked against what the host
/// derives itself, and every incompatibility is a loud refusal — never a restart that
/// would take sibling worktrees down with it.
module FsHotWatch.Tests.AttachHandshakeTests

open System.IO
open System.Text.Json.Nodes
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.AttachHandshake
open FsHotWatch.DaemonIdentity
open FsHotWatch.RepositoryIdentity
open FsHotWatch.SessionScope
open FsHotWatch.Tests.TestHelpers

let private binary: BinaryIdentity =
    { Version = "1.2.3"
      ContentHash = "0123456789abcdef" }

let private protocol =
    { Version = ProtocolVersion
      Binary = binary }

let private configA = ConfigDigest.ofText "{ \"a\": 1 }"
let private configB = ConfigDigest.ofText "{ \"a\": 2 }"

let private resolved (root: string) =
    match resolveWorktree root with
    | Ok r -> r
    | Error e -> failwith (IdentityError.describe e)

/// A jj primary at `<dir>/repo` and a secondary at `<dir>/ws`, resolved.
let private withRepository (body: ResolvedWorktree -> ResolvedWorktree -> string -> unit) =
    withTempDir "attach" (fun dir ->
        let primary = Path.Combine(dir, "repo")
        Directory.CreateDirectory(Path.Combine(primary, ".jj", "repo")) |> ignore
        let ws = Path.Combine(dir, "ws")
        Directory.CreateDirectory(Path.Combine(ws, ".jj")) |> ignore
        File.WriteAllText(Path.Combine(ws, ".jj", "repo"), "../../repo/.jj/repo")
        body (resolved primary) (resolved ws) dir)

let private hostFor (worktree: ResolvedWorktree) : HostIdentity =
    { Protocol = protocol
      Repository = worktree.Repository }

let private noSessions (_: WorktreeId) : LiveSession option = None

let private fixedIncarnation = SessionIncarnation.mint ()
let private mintFixed () = fixedIncarnation

let private sessionOf (worktree: ResolvedWorktree) =
    { Repository = worktree.Repository
      Worktree = worktree.Worktree
      Incarnation = SessionIncarnation.mint () }

/// Decide as a host would: deriving the identity itself from the claimed root.
let private decideFor host live (request: AttachRequest) =
    decide host (resolveWorktree request.ClaimedRoot) live mintFixed request

let private refusalOf (response: AttachResponse) =
    match response with
    | Refused(refusal, _) -> refusal
    | Attached _ -> failwith $"expected a refusal, got %A{response}"

// ---------------------------------------------------------------------------
// Attaching
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``a fresh attach with no live session mints a new incarnation for the derived worktree`` () =
    withRepository (fun primary _ _ ->
        let host = hostFor primary
        let request = requestFor protocol primary IncarnationExpectation.Fresh configA

        let expected =
            { Repository = primary.Repository
              Worktree = primary.Worktree
              Incarnation = fixedIncarnation }

        test <@ decideFor host noSessions request = Attached(expected, AttachDisposition.NewSession, host) @>)

[<Fact(Timeout = 15000)>]
let ``two worktrees of one repository attach to one host as two sessions`` () =
    withRepository (fun primary secondary _ ->
        let host = hostFor primary

        let sessionFor (w: ResolvedWorktree) =
            match
                decide
                    host
                    (Ok w)
                    noSessions
                    SessionIncarnation.mint
                    (requestFor protocol w IncarnationExpectation.Fresh configA)
            with
            | Attached(session, AttachDisposition.NewSession, _) -> session
            | other -> failwith $"%A{other}"

        let a = sessionFor primary
        let b = sessionFor secondary

        test <@ a.Repository = b.Repository @>
        test <@ a.Worktree <> b.Worktree @>
        test <@ a.Incarnation <> b.Incarnation @>)

[<Fact(Timeout = 15000)>]
let ``a fresh attach joins the worktree's live session, flagging a changed configuration`` () =
    withRepository (fun primary _ _ ->
        let host = hostFor primary
        let live = sessionOf primary

        let lookup cfg =
            fun (w: WorktreeId) ->
                if w = primary.Worktree then
                    Some { Session = live; Config = cfg }
                else
                    None

        let request = requestFor protocol primary IncarnationExpectation.Fresh configA

        test <@ decideFor host (lookup configA) request = Attached(live, AttachDisposition.Rejoined, host) @>

        let changed = decideFor host (lookup configB) request
        test <@ changed = Attached(live, AttachDisposition.RejoinedConfigChanged, host) @>)

[<Fact(Timeout = 15000)>]
let ``resuming the live incarnation rejoins it`` () =
    withRepository (fun primary _ _ ->
        let host = hostFor primary
        let live = sessionOf primary

        let lookup (_: WorktreeId) =
            Some { Session = live; Config = configA }

        let request =
            requestFor protocol primary (IncarnationExpectation.Resume live) configA

        test <@ decideFor host lookup request = Attached(live, AttachDisposition.Rejoined, host) @>)

[<Fact(Timeout = 15000)>]
let ``resuming a replaced incarnation is refused as stale`` () =
    withRepository (fun primary _ _ ->
        let host = hostFor primary
        let old = sessionOf primary
        let current = sessionOf primary

        let lookup (_: WorktreeId) =
            Some { Session = current; Config = configA }

        let request =
            requestFor protocol primary (IncarnationExpectation.Resume old) configA

        test <@ refusalOf (decideFor host lookup request) = AttachRefusal.StaleIncarnation(old, current) @>)

[<Fact(Timeout = 15000)>]
let ``a worktree deleted and recreated keeps its WorktreeId but its old session is void`` () =
    withRepository (fun _ secondary dir ->
        let host = hostFor secondary
        let before = sessionOf secondary

        // Delete and recreate the workspace at the same path; the host detached it.
        let ws = Path.Combine(dir, "ws")
        Directory.Delete(ws, true)
        Directory.CreateDirectory(Path.Combine(ws, ".jj")) |> ignore
        File.WriteAllText(Path.Combine(ws, ".jj", "repo"), "../../repo/.jj/repo")
        let recreated = resolved ws

        test <@ recreated.Worktree = secondary.Worktree @>

        let resume =
            requestFor protocol recreated (IncarnationExpectation.Resume before) configA

        test <@ refusalOf (decideFor host noSessions resume) = AttachRefusal.UnknownSession before @>

        match
            decide
                host
                (Ok recreated)
                noSessions
                SessionIncarnation.mint
                (requestFor protocol recreated IncarnationExpectation.Fresh configA)
        with
        | Attached(session, AttachDisposition.NewSession, _) ->
            test <@ session.Worktree = before.Worktree @>
            test <@ session.Incarnation <> before.Incarnation @>
        | other -> failwith $"%A{other}")

// ---------------------------------------------------------------------------
// Mixed incompatible clients fail loudly
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``a client of another protocol version is refused before anything else is trusted`` () =
    withRepository (fun primary _ _ ->
        let host = hostFor primary

        let request =
            { requestFor protocol primary IncarnationExpectation.Fresh configA with
                Protocol =
                    { protocol with
                        Version = ProtocolVersion + 1 }
                ClaimedRoot = "/nowhere" }

        test
            <@
                refusalOf (decideFor host noSessions request) = AttachRefusal.ProtocolVersionMismatch(
                    ProtocolVersion + 1,
                    ProtocolVersion
                )
            @>)

[<Fact(Timeout = 15000)>]
let ``a client of another binary is refused, and told the host is NOT restarted`` () =
    withRepository (fun primary _ _ ->
        let host = hostFor primary

        let other =
            { binary with
                ContentHash = "fedcba9876543210" }

        let request =
            { requestFor protocol primary IncarnationExpectation.Fresh configA with
                Protocol = { protocol with Binary = other } }

        let refusal = refusalOf (decideFor host noSessions request)
        test <@ refusal = AttachRefusal.BinaryMismatch(other, binary) @>
        test <@ (AttachRefusal.describe refusal).Contains "NOT be restarted" @>)

[<Fact(Timeout = 15000)>]
let ``a worktree of another repository is refused`` () =
    withRepository (fun primary _ dir ->
        let otherRoot = Path.Combine(dir, "other")
        Directory.CreateDirectory(Path.Combine(otherRoot, ".jj", "repo")) |> ignore
        let other = resolved otherRoot
        let host = hostFor primary
        let request = requestFor protocol other IncarnationExpectation.Fresh configA

        test
            <@
                refusalOf (decideFor host noSessions request) = AttachRefusal.WrongRepository(
                    other.Repository,
                    primary.Repository
                )
            @>)

[<Fact(Timeout = 15000)>]
let ``a client claiming its sibling's WorktreeId is refused`` () =
    withRepository (fun primary secondary _ ->
        let host = hostFor primary

        let request =
            { requestFor protocol secondary IncarnationExpectation.Fresh configA with
                Worktree = primary.Worktree }

        test
            <@
                refusalOf (decideFor host noSessions request) = AttachRefusal.ClaimMismatch(
                    "worktree",
                    primary.Worktree.Value,
                    secondary.Worktree.Value
                )
            @>)

[<Fact(Timeout = 15000)>]
let ``a claimed root that is not canonical is refused`` () =
    withRepository (fun primary _ dir ->
        let link = Path.Combine(dir, "link")
        Directory.CreateSymbolicLink(link, primary.Root.Value) |> ignore
        let host = hostFor primary

        let request =
            { requestFor protocol primary IncarnationExpectation.Fresh configA with
                ClaimedRoot = link }

        test
            <@
                refusalOf (decideFor host noSessions request) = AttachRefusal.ClaimMismatch(
                    "worktree root",
                    link,
                    primary.Root.Value
                )
            @>)

[<Fact(Timeout = 15000)>]
let ``a repository claim the host's derivation contradicts is refused`` () =
    withRepository (fun primary _ dir ->
        // The client says it is in the host's repository, but its root derives elsewhere.
        let otherRoot = Path.Combine(dir, "other")
        Directory.CreateDirectory(Path.Combine(otherRoot, ".jj", "repo")) |> ignore
        let other = resolved otherRoot
        let host = hostFor primary

        let request =
            { requestFor protocol other IncarnationExpectation.Fresh configA with
                Repository = primary.Repository }

        test
            <@
                refusalOf (decideFor host noSessions request) = AttachRefusal.ClaimMismatch(
                    "repository",
                    primary.Repository.Value,
                    other.Repository.Value
                )
            @>)

[<Fact(Timeout = 15000)>]
let ``a root the host cannot resolve is refused with the identity error`` () =
    withRepository (fun primary _ dir ->
        let host = hostFor primary
        let missing = Path.Combine(dir, "gone")

        let request =
            { requestFor protocol primary IncarnationExpectation.Fresh configA with
                ClaimedRoot = missing }

        test
            <@
                refusalOf (decideFor host noSessions request) = AttachRefusal.UnresolvableWorktree(
                    IdentityError.PathNotFound missing
                )
            @>)

[<Fact(Timeout = 15000)>]
let ``every refusal has a stable kind and a message`` () =
    withRepository (fun primary _ _ ->
        let s = sessionOf primary

        let refusals =
            [ AttachRefusal.MalformedRequest "x"
              AttachRefusal.ProtocolVersionMismatch(1, 2)
              AttachRefusal.BinaryMismatch(binary, binary)
              AttachRefusal.WrongRepository(primary.Repository, primary.Repository)
              AttachRefusal.UnresolvableWorktree(IdentityError.PathNotFound "/p")
              AttachRefusal.ClaimMismatch("f", "a", "b")
              AttachRefusal.UnknownSession s
              AttachRefusal.StaleIncarnation(s, s)
              AttachRefusal.WorktreeOwnedByDaemon(Some 42)
              AttachRefusal.ToolchainMismatch("10.0.100", "10.0.200")
              AttachRefusal.EnvironmentMismatch
                  [ { Name = "Configuration"
                      Host = Some "Debug"
                      Client = None } ]
              AttachRefusal.SessionStartFailed "no projects" ]

        test <@ refusals |> List.map AttachRefusal.kind |> List.distinct |> List.length = refusals.Length @>

        test
            <@
                refusals
                |> List.forall (fun r -> not (System.String.IsNullOrWhiteSpace(AttachRefusal.describe r)))
            @>)

// ---------------------------------------------------------------------------
// Wire format
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``requests round-trip through the wire, fresh and resuming`` () =
    withRepository (fun primary _ _ ->
        let fresh = requestFor protocol primary IncarnationExpectation.Fresh configA

        let resume =
            requestFor protocol primary (IncarnationExpectation.Resume(sessionOf primary)) configB

        test <@ decodeRequest (encodeRequest fresh) = Ok fresh @>
        test <@ decodeRequest (encodeRequest resume) = Ok resume @>)

[<Fact(Timeout = 15000)>]
let ``responses round-trip through the wire`` () =
    withRepository (fun primary _ _ ->
        let host = hostFor primary
        let s = sessionOf primary

        for d in
            [ AttachDisposition.NewSession
              AttachDisposition.Rejoined
              AttachDisposition.RejoinedConfigChanged ] do
            test <@ decodeResponse (encodeResponse (Attached(s, d, host))) = Ok(AttachedReply(s, d, host)) @>

        let refusal = AttachRefusal.UnknownSession s

        test
            <@
                decodeResponse (encodeResponse (Refused(refusal, host))) = Ok(
                    RefusedReply(AttachRefusal.kind refusal, AttachRefusal.describe refusal, host)
                )
            @>)

let private withField (json: string) (name: string) (value: JsonNode) =
    let node = JsonNode.Parse(json).AsObject()
    node[name] <- value
    node.ToJsonString()

let private withoutField (json: string) (name: string) =
    let node = JsonNode.Parse(json).AsObject()
    node.Remove name |> ignore
    node.ToJsonString()

[<Fact(Timeout = 15000)>]
let ``another protocol version is recognised as such and answered with a version refusal`` () =
    withRepository (fun primary _ _ ->
        let host = hostFor primary

        let future =
            withField
                (encodeRequest (requestFor protocol primary IncarnationExpectation.Fresh configA))
                "protocol"
                (JsonValue.Create(ProtocolVersion + 1))

        test <@ decodeRequest future = Error(DecodeError.UnsupportedProtocol(ProtocolVersion + 1)) @>

        test
            <@
                respond host resolveWorktree noSessions mintFixed future = Refused(
                    AttachRefusal.ProtocolVersionMismatch(ProtocolVersion + 1, ProtocolVersion),
                    host
                )
            @>

        // And a client reading a newer host's reply fails loudly too.
        let reply =
            withField
                (encodeResponse (Refused(AttachRefusal.MalformedRequest "x", host)))
                "protocol"
                (JsonValue.Create(ProtocolVersion + 1))

        test <@ decodeResponse reply = Error(DecodeError.UnsupportedProtocol(ProtocolVersion + 1)) @>)

[<Fact(Timeout = 15000)>]
let ``malformed requests are answered with a refusal, never dropped`` () =
    withRepository (fun primary _ _ ->
        let host = hostFor primary

        let good =
            encodeRequest (requestFor protocol primary IncarnationExpectation.Fresh configA)

        let malformed =
            [ "not json"
              "[]"
              "{}"
              withField good "schema" (JsonValue.Create "something.else")
              withField good "schema" (JsonValue.Create 7)
              withField good "expect" (JsonObject())
              withField good "protocol" (JsonValue.Create "one")
              withoutField good "protocol"
              withoutField good "binary"
              withField good "repository" (JsonValue.Create "XYZ")
              withField good "worktree" (JsonValue.Create 7)
              withoutField good "root"
              withField good "root" (JsonValue.Create "   ")
              withField
                  good
                  "binary"
                  (JsonObject(
                      dict
                          [ "version", JsonValue.Create "" :> JsonNode
                            "contentHash", JsonValue.Create "abc" :> JsonNode ]
                  ))
              withField good "config" (JsonValue.Create "short")
              withField good "expect" (JsonObject(dict [ "kind", JsonValue.Create "sideways" :> JsonNode ]))
              withField good "expect" (JsonObject(dict [ "kind", JsonValue.Create "resume" :> JsonNode ])) ]

        for json in malformed do
            test
                <@
                    match respond host resolveWorktree noSessions mintFixed json with
                    | Refused(AttachRefusal.MalformedRequest _, h) -> h = host
                    | _ -> false
                @>)

[<Fact(Timeout = 15000)>]
let ``respond attaches a well-formed request end to end`` () =
    withRepository (fun primary _ _ ->
        let host = hostFor primary

        let json =
            encodeRequest (requestFor protocol primary IncarnationExpectation.Fresh configA)

        match decodeResponse (encodeResponse (respond host resolveWorktree noSessions mintFixed json)) with
        | Ok(AttachedReply(session, AttachDisposition.NewSession, h)) ->
            test <@ session.Worktree = primary.Worktree @>
            test <@ session.Incarnation = fixedIncarnation @>
            test <@ h = host @>
        | other -> failwith $"%A{other}")

[<Fact(Timeout = 15000)>]
let ``a reply with an unknown outcome or disposition is malformed`` () =
    withRepository (fun primary _ _ ->
        let host = hostFor primary

        let attached =
            encodeResponse (Attached(sessionOf primary, AttachDisposition.Rejoined, host))

        for json in
            [ withField attached "outcome" (JsonValue.Create "maybe")
              withField attached "disposition" (JsonValue.Create "sideways")
              withoutField attached "host"
              withoutField attached "outcome" ] do
            test
                <@
                    match decodeResponse json with
                    | Error(DecodeError.Malformed _) -> true
                    | _ -> false
                @>)

[<Fact(Timeout = 15000)>]
let ``config digests are content digests and parse strictly`` () =
    test <@ configA <> configB @>
    test <@ ConfigDigest.ofText "x" = ConfigDigest.ofText "x" @>
    test <@ ConfigDigest.tryParse configA.Value = Some configA @>
    test <@ ConfigDigest.tryParse (configA.Value.ToUpperInvariant()) = None @>
    test <@ ConfigDigest.tryParse null = None @>
    test <@ ConfigDigest.ofText null = ConfigDigest.ofText "" @>
    test <@ string configA = configA.Value @>

[<Fact(Timeout = 15000)>]
let ``this process has a protocol identity`` () =
    let current = ProtocolIdentity.current ()
    test <@ current.Version = ProtocolVersion @>
    test <@ current.Binary = DaemonIdentity.currentIdentity () @>

[<Fact(Timeout = 15000)>]
let ``the client's environment travels with its request`` () =
    withRepository (fun primary _ _ ->
        let request =
            { requestFor protocol primary IncarnationExpectation.Fresh configA with
                Environment = Map.ofList [ "PATH", "/w/bin:/usr/bin"; "EMPTY", "" ] }

        test <@ decodeRequest (encodeRequest request) = Ok request @>)

[<Fact(Timeout = 15000)>]
let ``a request without an environment, or with a non-string value in it, is malformed`` () =
    withRepository (fun primary _ _ ->
        let json =
            encodeRequest (requestFor protocol primary IncarnationExpectation.Fresh configA)

        let number = JsonObject()
        number["PATH"] <- JsonValue.Create 3
        let nested = JsonObject()
        nested["PATH"] <- JsonObject()

        for broken in
            [ withoutField json "environment"
              withField json "environment" number
              withField json "environment" nested ] do
            test
                <@
                    match decodeRequest broken with
                    | Error(DecodeError.Malformed _) -> true
                    | _ -> false
                @>)

[<Fact(Timeout = 15000)>]
let ``refusals that leave the worktree on its own daemon are exactly the ones a shared process cannot serve`` () =
    let fallsBack refusal =
        AttachRefusal.fallsBackToOwnDaemon (AttachRefusal.kind refusal)

    test <@ fallsBack (AttachRefusal.WorktreeOwnedByDaemon None) @>
    test <@ fallsBack (AttachRefusal.ToolchainMismatch("a", "b")) @>
    test <@ fallsBack (AttachRefusal.EnvironmentMismatch []) @>

    // Everything else is a stop: a client that disagrees with the host must never
    // quietly go its own way, and a session that cannot start would not start alone
    // either.
    for refusal in
        [ AttachRefusal.MalformedRequest "x"
          AttachRefusal.ProtocolVersionMismatch(1, 2)
          AttachRefusal.BinaryMismatch(binary, binary)
          AttachRefusal.SessionStartFailed "no projects" ] do
        test <@ not (fallsBack refusal) @>

[<Fact(Timeout = 15000)>]
let ``each host-side refusal names what differed and what to do`` () =
    let owned = AttachRefusal.describe (AttachRefusal.WorktreeOwnedByDaemon(Some 42))
    test <@ owned.Contains "42" && owned.Contains "fshw stop" @>
    let ownedNoPid = AttachRefusal.describe (AttachRefusal.WorktreeOwnedByDaemon None)
    test <@ ownedNoPid.Contains "fshw stop" @>

    let toolchain =
        AttachRefusal.describe (AttachRefusal.ToolchainMismatch("10.0.100", "10.0.200"))

    test
        <@
            toolchain.Contains "10.0.100"
            && toolchain.Contains "10.0.200"
            && toolchain.Contains "global.json"
        @>

    let env =
        AttachRefusal.describe (
            AttachRefusal.EnvironmentMismatch
                [ { Name = "Configuration"
                    Host = Some "Debug"
                    Client = Some "Release" }
                  { Name = "NUGET_PACKAGES"
                    Host = None
                    Client = Some "/c" } ]
        )

    test
        <@
            env.Contains "Configuration"
            && env.Contains "Release"
            && env.Contains "NUGET_PACKAGES"
        @>

    let failed =
        AttachRefusal.describe (AttachRefusal.SessionStartFailed "no projects discovered")

    test <@ failed.Contains "no projects discovered" @>
