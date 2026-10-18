module FsHotWatch.Tests.LintConfigShapeTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Lint.LintConfigShape
open FsHotWatch.Tests.TestHelpers

/// The default fsharplint.json FSharpLint embeds in itself: every rule it knows, in the
/// shape its loader reads. A walker that flagged any key here would be wrong about
/// FSharpLint, not about the config.
let private fsharpLintDefaultConfig () =
    let assembly = typeof<FSharpLint.Framework.Rules.Rule>.Assembly

    let name =
        assembly.GetManifestResourceNames()
        |> Array.find (fun n -> n.EndsWith("fsharplint.json", System.StringComparison.Ordinal))

    use stream = assembly.GetManifestResourceStream name
    use reader = new StreamReader(stream)
    reader.ReadToEnd()

[<Fact(Timeout = 5000)>]
let ``FSharpLint's own default config binds every key`` () =
    test <@ List.isEmpty (unboundKeys (fsharpLintDefaultConfig ())) @>

[<Fact(Timeout = 5000)>]
let ``a group wrapped in enabled and config is unbound at the wrapper`` () =
    let json =
        """{ "typography": { "enabled": true, "config": { "maxCharactersOnLine": { "enabled": true } } } }"""

    test <@ unboundKeys json = [ "typography.enabled"; "typography.config" ] @>

[<Fact(Timeout = 5000)>]
let ``FSharpLint's own grouped shape binds`` () =
    let json =
        """{ "typography": { "maxCharactersOnLine": { "enabled": true, "config": { "maxCharactersOnLine": 120 } } } }"""

    test <@ List.isEmpty (unboundKeys json) @>

[<Fact(Timeout = 5000)>]
let ``a key FSharpLint has no field for is unbound`` () =
    test
        <@ unboundKeys """{ "binding": { "enabled": true }, "numberOfItems": {} }""" = [ "binding"; "numberOfItems" ] @>

[<Fact(Timeout = 5000)>]
let ``a rule setting beside enabled instead of inside config is unbound`` () =
    let json =
        """{ "maxCharactersOnLine": { "enabled": true, "maxCharacters": 120 } }"""

    test <@ unboundKeys json = [ "maxCharactersOnLine.maxCharacters" ] @>

[<Fact(Timeout = 5000)>]
let ``a misnamed setting inside config is unbound`` () =
    let json =
        """{ "maxCharactersOnLine": { "enabled": true, "config": { "maxCharacters": 120 } } }"""

    test <@ unboundKeys json = [ "maxCharactersOnLine.config.maxCharacters" ] @>

[<Fact(Timeout = 5000)>]
let ``an enabled key under hints is unbound`` () =
    // FSharpLint's hints take `add` and `ignore`; there is no switch to turn them off.
    test <@ unboundKeys """{ "hints": { "enabled": false, "add": [] } }""" = [ "hints.enabled" ] @>

[<Fact(Timeout = 5000)>]
let ``annotation keys are allowed at any depth`` () =
    let json =
        """{ "_comment": "top", "noTabCharacters": { "enabled": false, "_comment": "off: 3 findings" } }"""

    test <@ List.isEmpty (unboundKeys json) @>

[<Fact(Timeout = 5000)>]
let ``text that is not JSON is left to FSharpLint's loader`` () =
    test <@ List.isEmpty (unboundKeys "{ not json") @>

[<Fact(Timeout = 5000)>]
let ``a root that is not an object has no keys to drop`` () =
    test <@ List.isEmpty (unboundKeys "[]") @>

[<Fact(Timeout = 5000)>]
let ``check names each unbound key and the file`` () =
    withTempDir "lint-config-shape" (fun dir ->
        let path = Path.Combine(dir, "fsharplint.json")
        File.WriteAllText(path, """{ "binding": {}, "typography": { "enabled": true } }""")

        match check path with
        | Some message ->
            test <@ message.Contains path @>
            test <@ message.Contains "binding, typography.enabled" @>
        | None -> Assert.Fail "expected the unbound keys to be reported")

[<Fact(Timeout = 5000)>]
let ``check passes a config every key of which binds`` () =
    withTempDir "lint-config-shape" (fun dir ->
        let path = Path.Combine(dir, "fsharplint.json")
        File.WriteAllText(path, """{ "noTabCharacters": { "enabled": true } }""")
        test <@ check path = None @>)

[<Fact(Timeout = 5000)>]
let ``check leaves a missing file to FSharpLint's loader`` () =
    withTempDir "lint-config-shape" (fun dir -> test <@ check (Path.Combine(dir, "absent.json")) = None @>)

[<Fact(Timeout = 5000)>]
let ``an empty object binds`` () =
    test <@ List.isEmpty (unboundKeys """{ "global": {} }""") @>

[<Fact(Timeout = 5000)>]
let ``a config object on a rule that takes no settings is converted whole`` () =
    // `noTabCharacters` takes no settings: its `config` is FSharpLint's `unit`, not a
    // record, so there are no keys under it to drop.
    test <@ List.isEmpty (unboundKeys """{ "noTabCharacters": { "enabled": true, "config": {} } }""") @>
