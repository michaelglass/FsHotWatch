/// FSHW-WALK-001 — no recursive directory walk that can follow a directory symlink.
///
/// WHY. `.devenv/profile` links into /nix/store, where ncurses ships
/// `include/{ncurses,ncursesw} -> .` — two self-loops in one directory, so the path
/// count DOUBLES per level and a walk that follows directory symlinks never finishes.
/// It silently wedged `fshw check` before `SafeWalk` existed, and every repo-scale
/// walk in src/ now goes through it. Nothing stopped a new raw walk coming back, and
/// tests and tooling still had them; this rule is that stop.
///
/// WHAT IS FLAGGED, in every source (tests, bench and tooling included: a test that
/// walks the repository root wedges the suite exactly as a daemon walk wedges the gate):
///   1. `SearchOption.AllDirectories`, at any qualification, and
///      `RecurseSubdirectories = true` / `<- true` on an `EnumerationOptions`. Those
///      overloads use `EnumerationOptions.Compatible`, which descends hidden
///      directories (`.devenv` included) AND follows directory symlinks.
///   2. A `let rec` group that lists subdirectories (`GetDirectories` /
///      `EnumerateDirectories` / `GetFileSystemEntries` / `EnumerateFileSystemEntries`,
///      static or `DirectoryInfo`) and recurses OVER that listing: the listing (or a
///      name bound to it) feeds a `for` whose body recurses, or sits on one side of an
///      application whose other side does.
///
/// NOT FLAGGED: a one-level listing; recursion that lists no directories (walking UP
/// a parent chain terminates); a rec function that lists one level and recurses on
/// something else.
///
/// Class members are in scope. The SDK's `walkAst` reads a type's object-model body,
/// and the positive control pins that: xUnit class fixtures are written as
/// `type FooTests() = member …`, and a walker that read only augmentation members
/// would be blind in exactly the files this rule is mostly about.
///
/// SUPPRESS: `// FSHW-WALK-001 ok: <reason>` on the flagged line or in the contiguous
/// comment block directly above it. The reason is required — a bare marker does not
/// suppress — so `rg "FSHW-WALK-001 ok"` lists every exception WITH its justification.
module FsHotWatch.Rules.WalkAnalyzer

open FSharp.Analyzers.SDK
open FSharp.Analyzers.SDK.ASTCollecting
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

/// Pure syntactic detection, testable from a bare `ParsedInput`.
module Detect =

    [<Literal>]
    let OptOutMarker = "FSHW-WALK-001 ok:"

    /// Calls that list a directory's children. Recursing over any of them is a walk.
    let private listingCalls =
        set
            [ "GetDirectories"
              "EnumerateDirectories"
              "GetFileSystemEntries"
              "EnumerateFileSystemEntries" ]

    let private texts (ids: Ident list) = ids |> List.map (fun i -> i.idText)

    /// The dotted name an identifier-shaped expression spells, or [] for anything else.
    let rec private identChain (e: SynExpr) : string list =
        match e with
        | SynExpr.Ident id -> [ id.idText ]
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> texts ids
        | SynExpr.DotGet(expr = receiver; longDotId = SynLongIdent(id = ids)) -> identChain receiver @ texts ids
        | SynExpr.Paren(expr = inner) -> identChain inner
        | _ -> []

    let private lastIs (name: string) (chain: string list) = List.tryLast chain = Some name

    let private isTrue (e: SynExpr) =
        match e with
        | SynExpr.Const(SynConst.Bool true, _) -> true
        | _ -> false

    /// Pattern 1: `SearchOption.AllDirectories`, or `RecurseSubdirectories` set true.
    let private recursiveOption (e: SynExpr) : bool =
        match e with
        | SynExpr.LongIdent _ ->
            match List.rev (identChain e) with
            | "AllDirectories" :: "SearchOption" :: _ -> true
            | _ -> false
        // `RecurseSubdirectories = true`: a named constructor argument or an initialiser.
        | SynExpr.App(funcExpr = SynExpr.App(isInfix = true; funcExpr = op; argExpr = lhs); argExpr = rhs) ->
            lastIs "op_Equality" (identChain op)
            && lastIs "RecurseSubdirectories" (identChain lhs)
            && isTrue rhs
        // `o.RecurseSubdirectories <- true`
        | SynExpr.LongIdentSet(longDotId = SynLongIdent(id = ids); expr = rhs)
        | SynExpr.DotSet(longDotId = SynLongIdent(id = ids); rhsExpr = rhs) ->
            lastIs "RecurseSubdirectories" (texts ids) && isTrue rhs
        | SynExpr.Set(targetExpr = lhs; rhsExpr = rhs) -> lastIs "RecurseSubdirectories" (identChain lhs) && isTrue rhs
        | _ -> false

    let rec private patName (p: SynPat) : string option =
        match p with
        | SynPat.Named(ident = SynIdent(ident, _)) -> Some ident.idText
        | SynPat.LongIdent(longDotId = SynLongIdent(id = [ ident ])) -> Some ident.idText
        | SynPat.Typed(pat = inner)
        | SynPat.Paren(pat = inner)
        | SynPat.Attrib(pat = inner) -> patName inner
        | _ -> None

    let private posLeq (aLine: int, aCol: int) (bLine: int, bCol: int) =
        aLine < bLine || (aLine = bLine && aCol <= bCol)

    let private contains (outer: range) (inner: range) =
        posLeq (outer.StartLine, outer.StartColumn) (inner.StartLine, inner.StartColumn)
        && posLeq (inner.EndLine, inner.EndColumn) (outer.EndLine, outer.EndColumn)

    /// One `let rec` group: the names it binds and the ranges of their bodies.
    type private RecGroup =
        { Names: Set<string>
          Bodies: range list }

    /// Everything the rule needs, gathered in ONE walk as ranges. Expression nesting
    /// is range nesting, so "does this sub-expression mention X" becomes containment
    /// and no second expression walker is needed.
    type private Collector() =
        inherit SyntaxCollectorBase()

        member val Options = ResizeArray<range>()
        member val Listings = ResizeArray<range>()
        /// Every identifier read: (name, where).
        member val References = ResizeArray<string * range>()
        /// Every simple `let name = <body>`: (name, body).
        member val Aliases = ResizeArray<string * range>()
        /// Every `for x in source do body` and `f a`: (one side, the other side).
        member val Pairs = ResizeArray<range * range>()
        member val Groups = ResizeArray<RecGroup>()

        member this.AddGroup(bindings: SynBinding list) =
            let names =
                bindings
                |> List.choose (fun (SynBinding(headPat = p)) -> patName p)
                |> Set.ofList

            if not (Set.isEmpty names) then
                this.Groups.Add
                    { Names = names
                      Bodies = bindings |> List.map (fun (SynBinding(expr = body)) -> body.Range) }

        override this.WalkExpr(_path, expr) =
            if recursiveOption expr then
                this.Options.Add expr.Range

            match expr with
            | SynExpr.Ident _
            | SynExpr.LongIdent _
            | SynExpr.DotGet _ ->
                match List.tryLast (identChain expr) with
                | Some last when listingCalls.Contains last -> this.Listings.Add expr.Range
                | _ -> ()
            | _ -> ()

            match expr with
            | SynExpr.Ident id -> this.References.Add(id.idText, expr.Range)
            | SynExpr.LongIdent(longDotId = SynLongIdent(id = [ id ])) -> this.References.Add(id.idText, expr.Range)
            | SynExpr.ForEach(enumExpr = source; bodyExpr = body) -> this.Pairs.Add(source.Range, body.Range)
            | SynExpr.App(funcExpr = f; argExpr = a) ->
                this.Pairs.Add(a.Range, f.Range)
                this.Pairs.Add(f.Range, a.Range)
            | SynExpr.LetOrUse letOrUse ->
                if letOrUse.IsRecursive then
                    this.AddGroup letOrUse.Bindings

                for SynBinding(headPat = p; expr = body) in letOrUse.Bindings do
                    match p with
                    | SynPat.Named(ident = SynIdent(ident, _)) -> this.Aliases.Add(ident.idText, body.Range)
                    | _ -> ()
            | _ -> ()

        override this.WalkSynModuleDecl(_path, decl) =
            match decl with
            | SynModuleDecl.Let(isRecursive = true; bindings = bindings) -> this.AddGroup bindings
            | _ -> ()

        override this.WalkMember(_path, memberDefn) =
            match memberDefn with
            | SynMemberDefn.LetBindings(isRecursive = true; bindings = bindings) -> this.AddGroup bindings
            | _ -> ()

    /// Pattern 2 for one group: the listings whose result the recursion consumes.
    let private consumedListings (c: Collector) (group: RecGroup) : range list =
        let inGroup (r: range) =
            group.Bodies |> List.exists (fun b -> contains b r)

        let listingsWithin (r: range) =
            c.Listings |> Seq.filter (contains r) |> List.ofSeq

        let mentions (names: Set<string>) (r: range) =
            c.References |> Seq.exists (fun (n, at) -> names.Contains n && contains r at)

        // Names bound, inside the group, to an expression containing a listing.
        let aliases =
            c.Aliases
            |> Seq.filter (fun (_, body) -> inGroup body)
            |> Seq.choose (fun (name, body) ->
                match listingsWithin body with
                | [] -> None
                | ls -> Some(name, ls))
            |> List.ofSeq

        let listingsReadBy (side: range) =
            listingsWithin side
            @ (aliases
               |> List.filter (fun (name, _) -> mentions (set [ name ]) side)
               |> List.collect snd)

        c.Pairs
        |> Seq.filter (fun (listingSide, recursingSide) -> inGroup listingSide && mentions group.Names recursingSide)
        |> Seq.collect (fun (listingSide, _) -> listingsReadBy listingSide)
        |> List.ofSeq

    /// Ranges of every unguarded recursive walk in `input`, before suppression.
    let unguardedWalks (input: ParsedInput) : range list =
        let c = Collector()
        walkAst c input

        List.ofSeq c.Options
        @ (c.Groups |> Seq.collect (consumedListings c) |> List.ofSeq)
        |> List.distinct
        |> List.sortBy (fun r -> r.StartLine, r.StartColumn)

    /// True when the line carries the marker AND a reason after it.
    let private reasoned (text: string) =
        match text.IndexOf(OptOutMarker, System.StringComparison.Ordinal) with
        | -1 -> false
        | at -> text.Substring(at + OptOutMarker.Length).Trim().Length > 0

    /// `unguardedWalks` minus the ones opted out with a stated reason: on the walk's
    /// own line, or anywhere in the contiguous comment block directly above it.
    /// `getLine` is 1-based and returns "" out of range.
    let findings (getLine: int -> string) (input: ParsedInput) : range list =
        let optedOut (walk: range) =
            let rec scan (line: int) =
                if line < 1 then
                    false
                else
                    let text = getLine line

                    // Above the walk, only a COMMENT line counts: a code line's trailing
                    // marker belongs to that line's own walk, not to this one.
                    if line = walk.StartLine || text.TrimStart().StartsWith("//") then
                        reasoned text || scan (line - 1)
                    else
                        false

            scan walk.StartLine

        unguardedWalks input |> List.filter (optedOut >> not)

[<CliAnalyzer("UnguardedWalkAnalyzer",
              "Forbids SearchOption.AllDirectories / RecurseSubdirectories and hand-rolled let-rec recursion over GetDirectories — walks that follow directory symlinks into the .devenv -> /nix/store self-loop and never terminate")>]
let unguardedWalkAnalyzer: Analyzer<CliContext> =
    fun (context: CliContext) ->
        async {
            // Same defensive shape as the other line-reading rules: an analyzer that
            // throws takes the whole run's findings with it.
            let getLine (line: int) =
                try
                    if line >= 1 && line <= context.SourceText.GetLineCount() then
                        context.SourceText.GetLineString(line - 1)
                    else
                        ""
                with _ ->
                    ""

            return
                Detect.findings getLine context.ParseFileResults.ParseTree
                |> List.map (fun range ->
                    { Type = "Unguarded directory walk"
                      Message =
                        "Unguarded recursive directory walk. `SearchOption.AllDirectories` (and `RecurseSubdirectories = true`) uses \
                         `EnumerationOptions.Compatible`, which descends hidden directories and FOLLOWS DIRECTORY SYMLINKS; a hand-rolled \
                         `let rec` over `GetDirectories`/`EnumerateDirectories` does the same unless it checks for reparse points itself. \
                         `.devenv/profile -> /nix/store` sits one hop off the repository root, and its ncurses `include/{ncurses,ncursesw} -> .` \
                         self-loops double the path count per level: such a walk never terminates. Use FsHotWatch.SafeWalk \
                         (`bestEffortFilePaths`, `walk`, `enumerateEntriesPruning`, or `subdirectories` for one level) — it never enters a \
                         symlinked directory and is depth-capped. A genuine exception states its reason: \
                         `// FSHW-WALK-001 ok: <reason>` on this line or the comment block above it."
                      Code = "FSHW-WALK-001"
                      Severity = Severity.Error
                      Range = range
                      Fixes = [] })
        }
