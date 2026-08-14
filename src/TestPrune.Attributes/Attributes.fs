namespace TestPrune

open System

/// Declares an explicit dependency from the annotated symbol to the given type.
///
/// Use this to tell TestPrune about edges it can't infer from the static graph:
/// reflection, DI registration by type, runtime plug-in loading, or any other
/// cross-cutting reference. The annotated symbol will be treated as depending on
/// the given type for impact analysis — editing the type (or anything the type
/// aggregates via Layer 1) invalidates tests downstream of the annotated symbol.
///
/// Implementation note: TestPrune captures this edge through the same mechanism
/// that handles `typeof&lt;T&gt;` in any attribute argument — FCS emits a symbol use
/// for T at the typeof site, and the main dependency pass turns it into an edge
/// from the annotated symbol to T. So this attribute is a plain marker with no
/// runtime behavior and no special-case handling in the analyzer.
///
/// Example:
///   [&lt;TestPrune.DependsOn(typeof&lt;MyReflectionTarget&gt;)&gt;]
///   let registerHandlers () = ...
[<AttributeUsage(AttributeTargets.Method
                 ||| AttributeTargets.Property
                 ||| AttributeTargets.Class
                 ||| AttributeTargets.Struct
                 ||| AttributeTargets.Interface,
                 AllowMultiple = true,
                 Inherited = false)>]
type DependsOnAttribute(target: Type) =
    inherit Attribute()
    member _.Target = target

/// Declares that the annotated symbol depends on the contents of a specific file.
/// When the given repo-relative path appears in a change set, the annotated symbol is
/// treated as changed for impact analysis, pulling its downstream tests.
///
/// Use for snapshot/golden files, config files, SQL migrations, test data, schema
/// definitions, or any other non-F# input that drives the symbol's observable behavior.
///
/// The path is repo-relative (same normalization TestPrune applies to source files) and
/// matched exactly. For patterns, use `DependsOnGlobAttribute`.
///
/// Example:
///   [&lt;TestPrune.DependsOnFile("tests/snapshots/api.snap.json")&gt;]
///   let ``api snapshot`` () = ...
[<AttributeUsage(AttributeTargets.Method
                 ||| AttributeTargets.Property
                 ||| AttributeTargets.Class
                 ||| AttributeTargets.Struct
                 ||| AttributeTargets.Interface,
                 AllowMultiple = true,
                 Inherited = false)>]
type DependsOnFileAttribute(path: string) =
    inherit Attribute()
    member _.Path = path

/// Declares that the annotated symbol depends on any file matching the given glob pattern.
/// When any file in the change set matches, the annotated symbol is treated as changed.
///
/// Pattern dialect (deliberately small):
///   - `*` matches any sequence of characters except `/` (stays within one segment)
///   - `**` matches any number of path segments (including zero)
///   - `?` matches any single character except `/`
/// All other characters are literal. No negation, character classes, or brace
/// expansion. Paths are repo-relative forward-slash strings and case-sensitive.
///
/// Example:
///   [&lt;TestPrune.DependsOnGlob("tests/fixtures/**/*.yaml")&gt;]
///   type FixtureDrivenTests() = ...
[<AttributeUsage(AttributeTargets.Method
                 ||| AttributeTargets.Property
                 ||| AttributeTargets.Class
                 ||| AttributeTargets.Struct
                 ||| AttributeTargets.Interface,
                 AllowMultiple = true,
                 Inherited = false)>]
type DependsOnGlobAttribute(pattern: string) =
    inherit Attribute()
    member _.Pattern = pattern

/// Declares the annotated symbol an APPLICATION COMPOSITION ROOT: a symbol that
/// names the whole application rather than using any part of it.
///
/// The archetype is a routing table — `let handler route = match route with
/// | Home -> Handlers.home | Admin -> Handlers.admin | ...` — which references
/// every handler in the codebase for the sole purpose of wiring them up. DI
/// container registration and a top-level `composeApp` are the same shape.
///
/// Why TestPrune needs to be told. Such a symbol is a genuine dependency edge, so
/// the reverse-walk is right to traverse it, and that is exactly the problem: an
/// integration-test fixture that boots the app depends on the composition root,
/// so EVERY handler transitively reaches EVERY fixture-using test. One edited
/// handler selects the whole suite. The edge is true; the relevance it implies is
/// not. Nothing in the graph distinguishes "wires X up" from "calls X", so the
/// application author has to say which symbol is the wiring.
///
/// SEMANTICS — read both halves, they are not symmetric:
///
///   * Relevance does not propagate THROUGH the annotated symbol. When the walk
///     REACHES it from something it aggregates, the symbol is still reported
///     affected, but the walk stops there: tests reached only by continuing past
///     it are not selected on that basis.
///   * Relevance does propagate FROM a change TO it. When the annotated symbol is
///     itself in the change set, the walk proceeds normally and reaches
///     everything downstream. "The app is wired up differently now" is precisely
///     what host-booting tests verify, and they must run.
///
/// SAFETY. This narrows selection, so it is the direction that can silently skip
/// a failing test. It is opt-in and applies to nothing else: an un-annotated
/// codebase selects exactly what it selected before. Annotate ONLY a symbol whose
/// references are pure composition — if callers depend on what it COMPUTES rather
/// than on which parts it wires together, annotating it will drop real tests.
/// Before annotating, make sure the coupling it carries is covered another way
/// (TestPrune.Falco's route→test edges are the worked example: they attribute
/// each route to its own tests directly, so the composition edge is redundant).
///
/// TestPrune matches this attribute BY NAME, exactly as it does `DependsOnFile`.
/// Referencing this package is the convenient way to get it, not the only one — a
/// codebase that would rather not take the dependency in production code can
/// declare its own three-line equivalent and TestPrune will honour it:
///
///   type CompositionRootAttribute() =
///       inherit System.Attribute()
///
/// Example:
///   [&lt;TestPrune.CompositionRoot&gt;]
///   let endpointsFor (route: Route) : HttpHandler = ...
[<AttributeUsage(AttributeTargets.Method
                 ||| AttributeTargets.Property
                 ||| AttributeTargets.Class
                 ||| AttributeTargets.Struct
                 ||| AttributeTargets.Interface,
                 AllowMultiple = false,
                 Inherited = false)>]
type CompositionRootAttribute() =
    inherit Attribute()
