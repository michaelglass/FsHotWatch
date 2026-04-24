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
