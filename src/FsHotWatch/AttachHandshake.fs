/// The versioned handshake every connection to a repository host performs before any
/// other request: the client states which repository and worktree it is, which session
/// incarnation it expects, which configuration it loaded and which binary/protocol it
/// speaks; the host re-derives the worktree's identity itself, checks every claim, and
/// either attaches the worktree (issuing the `SessionId` every later RPC carries) or
/// refuses with a reason.
///
/// Mixed incompatible clients FAIL LOUDLY. A binary or protocol mismatch is a refusal,
/// never a restart: with one host serving many worktrees, a client restarting the host
/// it disagrees with would tear down every sibling session. (The per-worktree daemon's
/// `DaemonIdentity` handshake restarts a stale daemon instead — that daemon has no
/// siblings to hurt.)
///
/// Pure: the host supplies the derived identity, its live session for that worktree
/// and an incarnation minter, so every rule here is a unit-testable function.
module FsHotWatch.AttachHandshake

open System
open System.Text.Json.Nodes
open FsHotWatch.DaemonIdentity
open FsHotWatch.RepositoryIdentity
open FsHotWatch.SessionScope

/// The wire schema name. A message without it is not an attach message.
[<Literal>]
let Schema = "fshw.attach"

/// The attach protocol this build speaks. Bump on ANY change to the message shape or
/// its meaning; peers of different versions refuse each other.
///
/// 2: the request carries the client's environment.
[<Literal>]
let ProtocolVersion = 2

/// Digest of the configuration a client loaded (SHA-256, lowercase hex).
type ConfigDigest =
    private
    | ConfigDigest of string

    member this.Value =
        let (ConfigDigest v) = this
        v

    override this.ToString() = this.Value

module ConfigDigest =
    [<Literal>]
    let Length = 64

    /// Digest of a configuration's text, as loaded.
    let ofText (config: string) : ConfigDigest =
        ConfigDigest(ContentHash.ofText (if isNull config then "" else config))

    let tryParse (s: string) : ConfigDigest option =
        if isLowerHex Length s then Some(ConfigDigest s) else None

/// Which protocol, spoken by which binary.
type ProtocolIdentity =
    { Version: int; Binary: BinaryIdentity }

module ProtocolIdentity =
    /// This process's protocol identity.
    let current () : ProtocolIdentity =
        { Version = ProtocolVersion
          Binary = DaemonIdentity.currentIdentity () }

/// What the client expects of the worktree's session.
[<RequireQualifiedAccess>]
type IncarnationExpectation =
    /// No prior session: attach to whatever session the worktree has, or a new one.
    | Fresh
    /// The client holds this session and expects it to still be the live one.
    | Resume of SessionId

type AttachRequest =
    {
        Protocol: ProtocolIdentity
        Repository: RepositoryId
        Worktree: WorktreeId
        /// The client's canonical spelling of its worktree root. A CLAIM: the host
        /// re-derives the identity from it and never trusts the ids alongside.
        ClaimedRoot: string
        Expectation: IncarnationExpectation
        Config: ConfigDigest
        /// The client's environment. The session's children start from it, and the
        /// host refuses a client whose MSBuild-relevant variables differ from its own.
        Environment: Map<string, string>
    }

/// Who the host is, stated in every reply so a refused client can say what it met.
type HostIdentity =
    { Protocol: ProtocolIdentity
      Repository: RepositoryId }

/// The host's current session for a worktree, if it has one.
type LiveSession =
    { Session: SessionId
      Config: ConfigDigest }

[<RequireQualifiedAccess>]
type AttachDisposition =
    /// No session existed; the host minted a new incarnation.
    | NewSession
    /// The worktree's live session, unchanged.
    | Rejoined
    /// The worktree's live session, but it was loaded from a different configuration
    /// than the client's — the host must reload it before serving this client.
    | RejoinedConfigChanged

/// Why a host refused to attach. Each is a reason to stop and tell the user — never to
/// restart the host.
[<RequireQualifiedAccess>]
type AttachRefusal =
    /// The message was not a well-formed attach request.
    | MalformedRequest of reason: string
    | ProtocolVersionMismatch of client: int * host: int
    | BinaryMismatch of client: BinaryIdentity * host: BinaryIdentity
    /// The client belongs to a different repository than this host serves.
    | WrongRepository of client: RepositoryId * host: RepositoryId
    /// The host could not derive an identity for the claimed root.
    | UnresolvableWorktree of IdentityError
    /// A client claim disagrees with what the host derived from the root.
    | ClaimMismatch of field: string * claimed: string * derived: string
    /// The client expects a session the worktree no longer has (the worktree was
    /// detached, or the host restarted).
    | UnknownSession of expected: SessionId
    /// The worktree has a live session, but not the incarnation the client expects.
    | StaleIncarnation of expected: SessionId * live: SessionId
    /// The worktree's own per-worktree daemon holds its lock (pid, when it recorded one).
    | WorktreeOwnedByDaemon of pid: int option
    /// The worktree resolves a different .NET SDK than the one this host loaded.
    | ToolchainMismatch of client: string * host: string
    /// The client's MSBuild-relevant environment differs from the host's.
    | EnvironmentMismatch of EnvironmentMismatch list
    /// The session could not be built (a configuration error, no projects, ...).
    | SessionStartFailed of reason: string

module AttachRefusal =
    /// A stable, payload-free name for each refusal — what goes on the wire as `kind`.
    let kind (refusal: AttachRefusal) : string =
        match refusal with
        | AttachRefusal.MalformedRequest _ -> "malformed-request"
        | AttachRefusal.ProtocolVersionMismatch _ -> "protocol-version-mismatch"
        | AttachRefusal.BinaryMismatch _ -> "binary-mismatch"
        | AttachRefusal.WrongRepository _ -> "wrong-repository"
        | AttachRefusal.UnresolvableWorktree _ -> "unresolvable-worktree"
        | AttachRefusal.ClaimMismatch _ -> "claim-mismatch"
        | AttachRefusal.UnknownSession _ -> "unknown-session"
        | AttachRefusal.StaleIncarnation _ -> "stale-incarnation"
        | AttachRefusal.WorktreeOwnedByDaemon _ -> "worktree-owned-by-daemon"
        | AttachRefusal.ToolchainMismatch _ -> "toolchain-mismatch"
        | AttachRefusal.EnvironmentMismatch _ -> "environment-mismatch"
        | AttachRefusal.SessionStartFailed _ -> "session-start-failed"

    /// True when a refusal of `kind` means "this worktree keeps its own per-worktree
    /// daemon": the host is healthy but cannot serve THIS worktree in its process.
    /// Every other refusal stops the client, which must never quietly go its own way.
    let fallsBackToOwnDaemon (kind: string) : bool =
        kind = "worktree-owned-by-daemon"
        || kind = "toolchain-mismatch"
        || kind = "environment-mismatch"

    /// One human-readable explanation, including what to do.
    let describe (refusal: AttachRefusal) : string =
        match refusal with
        | AttachRefusal.MalformedRequest reason -> $"the host could not read the attach request: %s{reason}"
        | AttachRefusal.ProtocolVersionMismatch(client, host) ->
            $"this fshw speaks attach protocol %d{client} but the repository host speaks %d{host}; \
              run the same fshw build as the host, or stop the host (`fshw stop --repository`) \
              once no other worktree is using it"
        | AttachRefusal.BinaryMismatch(client, host) ->
            $"this fshw is %s{BinaryIdentity.render client} but the repository host is \
              %s{BinaryIdentity.render host}; it will NOT be restarted from here because other \
              worktrees may be attached to it — run the host's build, or stop the host deliberately"
        | AttachRefusal.WrongRepository(client, host) ->
            $"this worktree belongs to repository %s{client.Value}, but the host serves %s{host.Value}"
        | AttachRefusal.UnresolvableWorktree error ->
            $"the host could not identify this worktree: %s{IdentityError.describe error}"
        | AttachRefusal.ClaimMismatch(field, claimed, derived) ->
            $"the client claimed %s{field} %s{claimed}, but the host derived %s{derived}"
        | AttachRefusal.UnknownSession expected ->
            $"session %s{SessionId.render expected} is not attached (the worktree was detached or \
              the host restarted); attach afresh"
        | AttachRefusal.StaleIncarnation(expected, live) ->
            $"session %s{SessionId.render expected} has been replaced by %s{SessionId.render live}; \
              results from the old incarnation are void — attach afresh"
        | AttachRefusal.WorktreeOwnedByDaemon pid ->
            let who =
                match pid with
                | Some pid -> $"its own daemon (pid %d{pid})"
                | None -> "its own daemon"

            $"this worktree is already served by %s{who}, which keeps serving it; to move it into \
              the repository host, `fshw stop` that daemon first"
        | AttachRefusal.ToolchainMismatch(client, host) ->
            $"this worktree resolves .NET SDK %s{client} but the repository host loaded %s{host}, and \
              one process can load only one MSBuild; this worktree keeps its own daemon — align its \
              global.json with the host's to share it"
        | AttachRefusal.EnvironmentMismatch mismatches ->
            let each =
                mismatches |> List.map SessionEnvironment.describeMismatch |> String.concat "; "

            $"this shell's MSBuild-relevant environment differs from the repository host's (%s{each}), \
              and in-process MSBuild evaluation reads the host's; this worktree keeps its own daemon — \
              run from a shell that matches, or stop the host"
        | AttachRefusal.SessionStartFailed reason ->
            $"the repository host could not start this worktree's session: %s{reason}"

type AttachResponse =
    | Attached of session: SessionId * disposition: AttachDisposition * host: HostIdentity
    | Refused of refusal: AttachRefusal * host: HostIdentity

/// The host's decision. `derived` is the host's OWN resolution of the claimed root;
/// `liveSession` looks up its current session for a worktree; `mint` issues a fresh
/// incarnation. The checks run cheapest-and-most-fundamental first, so a peer from a
/// different protocol is refused before any of its fields are trusted.
let decide
    (host: HostIdentity)
    (derived: Result<ResolvedWorktree, IdentityError>)
    (liveSession: WorktreeId -> LiveSession option)
    (mint: unit -> SessionIncarnation)
    (request: AttachRequest)
    : AttachResponse =
    let refuse reason = Refused(reason, host)

    if request.Protocol.Version <> host.Protocol.Version then
        refuse (AttachRefusal.ProtocolVersionMismatch(request.Protocol.Version, host.Protocol.Version))
    elif request.Protocol.Binary <> host.Protocol.Binary then
        refuse (AttachRefusal.BinaryMismatch(request.Protocol.Binary, host.Protocol.Binary))
    elif request.Repository <> host.Repository then
        refuse (AttachRefusal.WrongRepository(request.Repository, host.Repository))
    else
        match derived with
        | Error error -> refuse (AttachRefusal.UnresolvableWorktree error)
        | Ok resolved when resolved.Repository <> request.Repository ->
            refuse (AttachRefusal.ClaimMismatch("repository", request.Repository.Value, resolved.Repository.Value))
        | Ok resolved when resolved.Root.Value <> request.ClaimedRoot ->
            refuse (AttachRefusal.ClaimMismatch("worktree root", request.ClaimedRoot, resolved.Root.Value))
        | Ok resolved when resolved.Worktree <> request.Worktree ->
            refuse (AttachRefusal.ClaimMismatch("worktree", request.Worktree.Value, resolved.Worktree.Value))
        | Ok resolved ->
            let rejoin (live: LiveSession) =
                let disposition =
                    if live.Config = request.Config then
                        AttachDisposition.Rejoined
                    else
                        AttachDisposition.RejoinedConfigChanged

                Attached(live.Session, disposition, host)

            match request.Expectation, liveSession resolved.Worktree with
            | IncarnationExpectation.Fresh, Some live -> rejoin live
            | IncarnationExpectation.Fresh, None ->
                let session =
                    { Repository = resolved.Repository
                      Worktree = resolved.Worktree
                      Incarnation = mint () }

                Attached(session, AttachDisposition.NewSession, host)
            | IncarnationExpectation.Resume expected, Some live when live.Session = expected -> rejoin live
            | IncarnationExpectation.Resume expected, Some live ->
                refuse (AttachRefusal.StaleIncarnation(expected, live.Session))
            | IncarnationExpectation.Resume expected, None -> refuse (AttachRefusal.UnknownSession expected)

/// The request a client builds for its own resolved worktree, with an empty
/// environment; a client sets `Environment` to its own.
let requestFor
    (protocol: ProtocolIdentity)
    (worktree: ResolvedWorktree)
    (expectation: IncarnationExpectation)
    (config: ConfigDigest)
    : AttachRequest =
    { Protocol = protocol
      Repository = worktree.Repository
      Worktree = worktree.Worktree
      ClaimedRoot = worktree.Root.Value
      Expectation = expectation
      Config = config
      Environment = Map.empty }

// ---------------------------------------------------------------------------
// Wire format
// ---------------------------------------------------------------------------

/// Why a message could not be decoded. A different protocol version is its own case:
/// the rest of such a message is not read, because its shape is not this build's to
/// assume.
[<RequireQualifiedAccess>]
type DecodeError =
    | Malformed of reason: string
    | UnsupportedProtocol of version: int

let private binaryNode (binary: BinaryIdentity) =
    let node = JsonObject()
    node["version"] <- JsonValue.Create binary.Version
    node["contentHash"] <- JsonValue.Create binary.ContentHash
    node

let private envelope () =
    let node = JsonObject()
    node["schema"] <- JsonValue.Create Schema
    node["protocol"] <- JsonValue.Create ProtocolVersion
    node

let private hostNode (host: HostIdentity) =
    let node = JsonObject()
    node["binary"] <- binaryNode host.Protocol.Binary
    node["repository"] <- JsonValue.Create host.Repository.Value
    node

let encodeRequest (request: AttachRequest) : string =
    let node = envelope ()
    node["protocol"] <- JsonValue.Create request.Protocol.Version
    node["binary"] <- binaryNode request.Protocol.Binary
    node["repository"] <- JsonValue.Create request.Repository.Value
    node["worktree"] <- JsonValue.Create request.Worktree.Value
    node["root"] <- JsonValue.Create request.ClaimedRoot
    node["config"] <- JsonValue.Create request.Config.Value

    let environment = JsonObject()

    request.Environment
    |> Map.iter (fun name value -> environment[name] <- JsonValue.Create value)

    node["environment"] <- environment

    let expect = JsonObject()

    match request.Expectation with
    | IncarnationExpectation.Fresh -> expect["kind"] <- JsonValue.Create "fresh"
    | IncarnationExpectation.Resume session ->
        expect["kind"] <- JsonValue.Create "resume"
        expect["session"] <- JsonValue.Create(SessionId.render session)

    node["expect"] <- expect
    node.ToJsonString()

let private field (name: string) (parse: string -> 'a option) (node: JsonObject) : Result<'a, DecodeError> =
    match node[name] with
    | :? JsonValue as v ->
        match v.TryGetValue<string>() with
        | true, s ->
            match parse s with
            | Some parsed -> Ok parsed
            | None -> Error(DecodeError.Malformed $"`%s{name}` is not valid: %A{s}")
        | _ -> Error(DecodeError.Malformed $"`%s{name}` is not a string")
    | _ -> Error(DecodeError.Malformed $"`%s{name}` is missing")

let private nonEmpty (s: string) =
    if String.IsNullOrWhiteSpace s then None else Some s

let private objectField (name: string) (node: JsonObject) : Result<JsonObject, DecodeError> =
    match node[name] with
    | :? JsonObject as o -> Ok o
    | _ -> Error(DecodeError.Malformed $"`%s{name}` is missing or not an object")

let private binaryOf (node: JsonObject) : Result<BinaryIdentity, DecodeError> =
    objectField "binary" node
    |> Result.bind (fun b ->
        field "version" nonEmpty b
        |> Result.bind (fun version ->
            field "contentHash" nonEmpty b
            |> Result.map (fun hash ->
                { Version = version
                  ContentHash = hash })))

/// Parse the envelope: valid JSON, the attach schema, and THIS protocol version.
let private openEnvelope (json: string) : Result<JsonObject, DecodeError> =
    let parsed =
        try
            match JsonNode.Parse(json) with
            | :? JsonObject as o -> Ok o
            | _ -> Error(DecodeError.Malformed "not a JSON object")
        with ex ->
            Error(DecodeError.Malformed $"not JSON: %s{ex.Message}")

    parsed
    |> Result.bind (fun node ->
        match node["schema"] with
        | :? JsonValue as v when
            (match v.TryGetValue<string>() with
             | true, s -> s = Schema
             | _ -> false)
            ->
            match node["protocol"] with
            | :? JsonValue as p ->
                match p.TryGetValue<int>() with
                | true, version when version = ProtocolVersion -> Ok node
                | true, version -> Error(DecodeError.UnsupportedProtocol version)
                | _ -> Error(DecodeError.Malformed "`protocol` is not an integer")
            | _ -> Error(DecodeError.Malformed "`protocol` is missing")
        | _ -> Error(DecodeError.Malformed $"not an `%s{Schema}` message"))

type private ResultBuilder() =
    member _.Bind(r, f) = Result.bind f r
    member _.Return v = Ok v

let private result = ResultBuilder()

let decodeRequest (json: string) : Result<AttachRequest, DecodeError> =
    result {
        let! node = openEnvelope json
        let! binary = binaryOf node
        let! repository = field "repository" RepositoryId.tryParse node
        let! worktree = field "worktree" WorktreeId.tryParse node
        let! root = field "root" nonEmpty node
        let! config = field "config" ConfigDigest.tryParse node
        let! expect = objectField "expect" node
        let! environment = objectField "environment" node

        let! variables =
            environment
            |> Seq.fold
                (fun acc (KeyValue(name, value)) ->
                    acc
                    |> Result.bind (fun (vars: Map<string, string>) ->
                        match value with
                        | :? JsonValue as v ->
                            match v.TryGetValue<string>() with
                            | true, s -> Ok(vars.Add(name, s))
                            | _ -> Error(DecodeError.Malformed $"environment `%s{name}` is not a string")
                        | _ -> Error(DecodeError.Malformed $"environment `%s{name}` is not a string")))
                (Ok Map.empty)

        let! expectation =
            match field "kind" Some expect with
            | Ok "fresh" -> Ok IncarnationExpectation.Fresh
            | Ok "resume" ->
                field "session" SessionId.tryParse expect
                |> Result.map IncarnationExpectation.Resume
            | Ok other -> Error(DecodeError.Malformed $"unknown expectation %A{other}")
            | Error e -> Error e

        return
            { Protocol =
                { Version = ProtocolVersion
                  Binary = binary }
              Repository = repository
              Worktree = worktree
              ClaimedRoot = root
              Expectation = expectation
              Config = config
              Environment = variables }
    }

/// The host's side of the wire: decode, then decide. An undecodable request is still
/// ANSWERED — with a refusal naming why — so the client fails loudly instead of hanging
/// or guessing.
let respond
    (host: HostIdentity)
    (derive: string -> Result<ResolvedWorktree, IdentityError>)
    (liveSession: WorktreeId -> LiveSession option)
    (mint: unit -> SessionIncarnation)
    (requestJson: string)
    : AttachResponse =
    match decodeRequest requestJson with
    | Error(DecodeError.UnsupportedProtocol version) ->
        Refused(AttachRefusal.ProtocolVersionMismatch(version, host.Protocol.Version), host)
    | Error(DecodeError.Malformed reason) -> Refused(AttachRefusal.MalformedRequest reason, host)
    | Ok request -> decide host (derive request.ClaimedRoot) liveSession mint request

let private dispositionName (d: AttachDisposition) =
    match d with
    | AttachDisposition.NewSession -> "new-session"
    | AttachDisposition.Rejoined -> "rejoined"
    | AttachDisposition.RejoinedConfigChanged -> "rejoined-config-changed"

let encodeResponse (response: AttachResponse) : string =
    let node = envelope ()

    match response with
    | Attached(session, disposition, host) ->
        node["host"] <- hostNode host
        node["outcome"] <- JsonValue.Create "attached"
        node["session"] <- JsonValue.Create(SessionId.render session)
        node["disposition"] <- JsonValue.Create(dispositionName disposition)
    | Refused(refusal, host) ->
        node["host"] <- hostNode host
        node["outcome"] <- JsonValue.Create "refused"
        node["kind"] <- JsonValue.Create(AttachRefusal.kind refusal)
        node["message"] <- JsonValue.Create(AttachRefusal.describe refusal)

    node.ToJsonString()

/// What a client reads back. A refusal arrives as its stable `kind` plus the host's
/// explanation — the client's job is to stop and show it, not to act on its payload.
type AttachReply =
    | AttachedReply of session: SessionId * disposition: AttachDisposition * host: HostIdentity
    | RefusedReply of kind: string * message: string * host: HostIdentity

let decodeResponse (json: string) : Result<AttachReply, DecodeError> =
    result {
        let! node = openEnvelope json
        let! hostNode = objectField "host" node
        let! binary = binaryOf hostNode
        let! repository = field "repository" RepositoryId.tryParse hostNode

        let host =
            { Protocol =
                { Version = ProtocolVersion
                  Binary = binary }
              Repository = repository }

        let! reply =
            match field "outcome" Some node with
            | Ok "attached" ->
                result {
                    let! session = field "session" SessionId.tryParse node

                    let! disposition =
                        field
                            "disposition"
                            (fun s ->
                                [ AttachDisposition.NewSession
                                  AttachDisposition.Rejoined
                                  AttachDisposition.RejoinedConfigChanged ]
                                |> List.tryFind (fun d -> dispositionName d = s))
                            node

                    return AttachedReply(session, disposition, host)
                }
            | Ok "refused" ->
                result {
                    let! kind = field "kind" nonEmpty node
                    let! message = field "message" nonEmpty node
                    return RefusedReply(kind, message, host)
                }
            | Ok other -> Error(DecodeError.Malformed $"unknown outcome %A{other}")
            | Error e -> Error e

        return reply
    }
