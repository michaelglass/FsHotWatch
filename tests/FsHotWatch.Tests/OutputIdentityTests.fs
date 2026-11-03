/// A referenced output's identity (AUTOMATION-1478): one per build of the same sources,
/// whichever checkout it was built in, and a new one whenever what a check reads of it moves.
module FsHotWatch.Tests.OutputIdentityTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

let private shortRoot = "/c"
let private longRoot = "/checkout-" + String('w', 120)

let private identityOf (spec: FakeFSharpAssembly.Spec) =
    OutputIdentity.ofBytes [ spec.Root ] (FakeFSharpAssembly.build spec)

let private both (change: FakeFSharpAssembly.Spec -> FakeFSharpAssembly.Spec) =
    identityOf (change (FakeFSharpAssembly.spec "A" shortRoot)),
    identityOf (change (FakeFSharpAssembly.spec "A" longRoot))

let private sha (bytes: byte[]) =
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

[<Fact>]
let ``one build in two checkouts has one identity, whatever length its paths are pickled with`` () =
    let short, long = both id
    // Positive control: the bytes differ, and the paths straddle the one-byte length.
    test
        <@
            FakeFSharpAssembly.build (FakeFSharpAssembly.spec "A" shortRoot)
            <> FakeFSharpAssembly.build (FakeFSharpAssembly.spec "A" longRoot)
        @>

    test
        <@
            (shortRoot + "/src/A/A1.fs").Length < 0x80
            && (longRoot + "/src/A/A1.fs").Length >= 0x80
        @>

    test <@ short.IsSome @>
    test <@ short = long @>

[<Fact>]
let ``what a check reads of an output moves its identity`` () =
    let baseline = fst (both id)

    let changes: (string * (FakeFSharpAssembly.Spec -> FakeFSharpAssembly.Spec)) list =
        [ "an entity",
          (fun s ->
              { s with
                  Entities = s.Entities @ [ "x" ] })
          "a source file",
          (fun s ->
              { s with
                  Sources = s.Sources @ [ "src/A/A3.fs" ] })
          "a method", (fun s -> { s with Methods = s.Methods @ [ "x" ] })
          "the assembly's name", (fun s -> { s with Name = "B" })
          "a resource of its own",
          (fun s ->
              { s with
                  Resources = [ "Strings.resources", [| 1uy |] ] })
          "a resource's bytes",
          (fun s ->
              { s with
                  Resources = [ "Strings.resources", [| 2uy |] ] })
          "a resource in another file", (fun s -> { s with Linked = [ "Linked" ] })
          // An F# resource the compiler left uncompressed is normalized the same way.
          "an uncompressed F# resource",
          (fun s ->
              { s with
                  Resources = [ "FSharpSignatureData.A", FakeFSharpAssembly.pickled (s.Root + "/src/A/A1.fs") ] }) ]

    for name, change in changes do
        let short, long = both change
        test <@ (name, short = long) = (name, true) @>
        test <@ (name, short <> baseline) = (name, true) @>

    test
        <@
            fst (
                both (fun s ->
                    { s with
                        Resources = [ "Strings.resources", [| 1uy |] ] })
            )
            <> fst (
                both (fun s ->
                    { s with
                        Resources = [ "Strings.resources", [| 2uy |] ] })
            )
        @>

[<Fact>]
let ``a method body, which a check never reads, does not`` () =
    test <@ fst (both (fun s -> { s with Literal = "changed" })) = fst (both id) @>

[<Fact>]
let ``bytes that are not a managed assembly have no identity`` () =
    test <@ OutputIdentity.ofBytes [ "/r" ] [| 1uy; 2uy; 3uy |] = None @>
    test <@ OutputIdentity.ofBytes [ "/r" ] (Encoding.UTF8.GetBytes "MZ but not a PE") = None @>
    // A PE whose CLI header is gone: an image with no metadata.
    let image = FakeFSharpAssembly.build (FakeFSharpAssembly.spec "A" "/r")

    let optionalHeader =
        use pe = new Reflection.PortableExecutable.PEReader(new MemoryStream(image))
        pe.PEHeaders.PEHeaderStartOffset

    // PE32: the data directories start 96 bytes in; the CLI header's is the fifteenth.
    Array.fill image (optionalHeader + 96 + 14 * 8) 8 0uy
    test <@ OutputIdentity.ofBytes [ "/r" ] image = None @>

let private restatedLength (length: int) =
    Array.append (Encoding.UTF8.GetBytes "\u0000${len}") (BitConverter.GetBytes length)

let private placeholder = Encoding.UTF8.GetBytes "\u0000${repo}\u0000"

[<Fact>]
let ``a pickled length of any width is re-stated without the root`` () =
    let rest = "/src/" + String('f', 0x4000)

    let pickledIn (root: string) =
        Array.concat [ [| 7uy |]; FakeFSharpAssembly.pickled (root + rest); [| 9uy |] ]

    let normalized (root: string) =
        OutputIdentity.normalizeResource (Encoding.UTF8.GetBytes root) (pickledIn root)

    // Positive control: this length takes the five-byte form.
    test <@ (pickledIn "/r")[1] = 0xFFuy @>
    test <@ normalized "/r" = normalized longRoot @>

    test
        <@
            normalized "/r" = Array.concat
                [ [| 7uy |]
                  restatedLength (Encoding.UTF8.GetByteCount rest)
                  placeholder
                  Encoding.UTF8.GetBytes rest
                  [| 9uy |] ]
        @>

[<Fact>]
let ``a root inside a longer string, or at the start, is replaced and no length is re-stated`` () =
    let root = Encoding.UTF8.GetBytes "/r"

    let inside =
        Array.concat [ FakeFSharpAssembly.pickled "see:"; Encoding.UTF8.GetBytes "/r/x" ]

    test
        <@
            OutputIdentity.normalizeResource root inside = Array.concat
                [ FakeFSharpAssembly.pickled "see:"; placeholder; Encoding.UTF8.GetBytes "/x" ]
        @>

    test <@ OutputIdentity.normalizeResource root root = placeholder @>
    // The second of two roots back to back has no length of its own in front of it.
    test <@ OutputIdentity.normalizeResource root (Array.append root root) = Array.append placeholder placeholder @>
    let none = [| 1uy; 2uy |]
    test <@ obj.ReferenceEquals(OutputIdentity.normalizeResource root none, none) @>
    test <@ obj.ReferenceEquals(OutputIdentity.normalizeResource [||] none, none) @>

[<Fact>]
let ``a length that cannot cover the root is not read as one`` () =
    let root = Encoding.UTF8.GetBytes "/root"
    // One byte that is shorter than the root; two bytes that run past the data, or that
    // claim a length the two-byte form never carries (under 0x80); a 0xFF
    // whose four bytes claim a length the five-byte form never carries.
    for prefix in
        [ [| 2uy |]
          [| 0x80uy; 0xFFuy |]
          [| 0x80uy; 4uy |]
          [| 0xFFuy; 5uy; 0uy; 0uy; 0uy |] ] do
        let data = Array.append prefix (Encoding.UTF8.GetBytes "/root/x")

        test
            <@
                OutputIdentity.normalizeResource root data = Array.concat
                    [ prefix; placeholder; Encoding.UTF8.GetBytes "/x" ]
            @>

[<Fact>]
let ``identity names an output by its plain hash with no root, no assembly or no bytes to read`` () =
    withTempDir "output-identity" (fun dir ->
        let dll = Path.Combine(dir, "A.dll")
        FakeFSharpAssembly.write dll (FakeFSharpAssembly.spec "A" dir)
        let plain (path: string) = sha (File.ReadAllBytes path)

        test <@ OutputIdentity.identity None plain dll = plain dll @>
        test <@ OutputIdentity.identity (Some dir) (fun _ -> "missing") dll = "missing" @>
        test <@ OutputIdentity.identity (Some dir) (fun _ -> "unreadable") dll = "unreadable" @>
        // The bytes moved since the plain hash was taken.
        test <@ OutputIdentity.identity (Some dir) (fun _ -> "0123") dll = "0123" @>
        // A directory where the output should be: nothing to read.
        test <@ OutputIdentity.identity (Some dir) (fun _ -> "4567") dir = "4567" @>
        let text = Path.Combine(dir, "A.txt")
        File.WriteAllText(text, "not an assembly")
        test <@ OutputIdentity.identity (Some dir) plain text = plain text @>
        // A path through a file: no directory to read it from.
        test <@ OutputIdentity.identity (Some dir) (fun _ -> "89ab") (Path.Combine(text, "x")) = "89ab" @>

        let named = OutputIdentity.identity (Some dir) plain dll
        test <@ named.StartsWith(OutputIdentity.Schema + ":") @>
        test <@ Some named = OutputIdentity.ofBytes [ dir ] (File.ReadAllBytes dll) @>
        // Remembered: asked again, the same answer.
        test <@ OutputIdentity.identity (Some(dir + string Path.DirectorySeparatorChar)) plain dll = named @>)

[<Fact>]
let ``remembered identities are bounded`` () =
    withTempDir "output-identity-bound" (fun dir ->
        let dll = Path.Combine(dir, "A.dll")
        FakeFSharpAssembly.write dll (FakeFSharpAssembly.spec "A" dir)
        let plain (path: string) = sha (File.ReadAllBytes path)
        let named = OutputIdentity.identity (Some dir) plain dll

        // Each root is its own entry; past the bound the memo starts again, and answers alike.
        for i in 0..4100 do
            OutputIdentity.identity (Some(Path.Combine(dir, string i))) plain dll |> ignore

        test <@ OutputIdentity.identity (Some dir) plain dll = named @>)

[<Fact>]
let ``a checkout reached through a symbolic link is named by the path its build embedded`` () =
    withTempDir "output-identity-link" (fun dir ->
        // Each checkout is a link to a real directory; a build runs in the real one, so the
        // resolved path is what it embeds, while the daemon names the checkout by the link.
        let named (name: string) =
            let real = Path.Combine(dir, "real-" + name)
            Directory.CreateDirectory real |> ignore
            let link = Path.Combine(dir, "link-" + name)
            Directory.CreateSymbolicLink(link, real) |> ignore

            let resolved =
                match RepositoryIdentity.canonicalize real with
                | Ok canonical -> canonical.Value
                | Error e -> failwith $"%A{e}"

            let dll = Path.Combine(real, "A.dll")
            FakeFSharpAssembly.write dll (FakeFSharpAssembly.spec "A" resolved)

            OutputIdentity.identity
                (Some link)
                (fun path -> sha (File.ReadAllBytes path))
                (Path.Combine(link, "A.dll"))

        let first = named "one"
        test <@ first.StartsWith(OutputIdentity.Schema + ":") @>
        test <@ first = named ("two-" + String('w', 40)) @>)

[<Fact>]
let ``of two spellings of a root, the longer is replaced first`` () =
    let image (root: string) =
        FakeFSharpAssembly.build (FakeFSharpAssembly.spec "A" root)

    let roots (root: string) = [ root; "/private" + root ]
    // Built under the resolved spelling, read with the shorter one listed first.
    test
        <@
            OutputIdentity.ofBytes (roots "/var/a") (image "/private/var/a") = OutputIdentity.ofBytes
                (roots "/var/bb")
                (image "/private/var/bb")
        @>

let private withAttribute (typeName: string) (value: string) =
    identityOf
        { FakeFSharpAssembly.spec "A" shortRoot with
            Attributes = [ typeName, value ] }

[<Fact>]
let ``a commit id in the informational version does not move the identity, and the version does`` () =
    // AUTOMATION-1480: on a git checkout SourceLink appends `+<commit id>` to it.
    let version (text: string) =
        withAttribute "AssemblyInformationalVersionAttribute" text

    test <@ version ("1.0.0+" + String('a', 40)) = version ("1.0.0+" + String('b', 40)) @>
    test <@ version ("1.0.0+" + String('a', 40)) <> version ("1.1.0+" + String('a', 40)) @>
    test <@ version "1.0.0" <> version "1.1.0" @>

[<Fact>]
let ``every other attribute's value moves the identity, a plus sign or not`` () =
    let description (text: string) =
        withAttribute "AssemblyDescriptionAttribute" text

    test <@ description "one" <> description "two" @>

    test
        <@
            description ("1.0.0+" + String('a', 40))
            <> description ("1.0.0+" + String('b', 40))
        @>

[<Fact>]
let ``an informational version with no string is hashed as it is`` () =
    test <@ (withAttribute "AssemblyInformationalVersionAttribute" null).IsSome @>

[<Fact>]
let ``a generated item is named without a commit id, and by its plain hash otherwise`` () =
    withTempDir "generated-source" (fun dir ->
        let plain (path: string) = sha (File.ReadAllBytes path)

        let named (name: string) (lines: string list) =
            let path = Path.Combine(dir, name)
            File.WriteAllLines(path, lines)
            path, OutputIdentity.generatedSource plain path

        let info (version: string) =
            [ "namespace FSharp"
              $"[<assembly: System.Reflection.AssemblyInformationalVersionAttribute(\"%s{version}\")>]"
              "do ()" ]

        let _, first = named "A.fs" (info ("1.0.0+" + String('a', 40)))
        test <@ first.StartsWith "generated-v1:" @>
        test <@ snd (named "B.fs" (info ("1.0.0+" + String('b', 40)))) = first @>
        test <@ snd (named "C.fs" (info ("1.1.0+" + String('a', 40)))) <> first @>
        // The short attribute name, as hand-written source spells it.
        let _, short =
            named "D.fs" [ "[<assembly: AssemblyInformationalVersion(\"2.0+abc\")>]" ]

        test <@ short = snd (named "E.fs" [ "[<assembly: AssemblyInformationalVersion(\"2.0+def\")>]" ]) @>
        // Anything else in the file still counts.
        test <@ snd (named "F.fs" (info ("1.0.0+" + String('a', 40)) @ [ "// more" ])) <> first @>
        // No build metadata: the plain hash.
        let path, none = named "G.fs" (info "1.0.0")
        test <@ none = plain path @>
        // Moved since the plain hash, or nothing to read: the plain hash.
        test <@ OutputIdentity.generatedSource (fun _ -> "0123") path = "0123" @>
        test <@ OutputIdentity.generatedSource (fun _ -> "4567") dir = "4567" @>
        // Remembered past its bound, the same answers.
        for i in 0..4100 do
            named "H.fs" (info $"1.0.%d{i}+x") |> ignore

        test <@ snd (named "A.fs" (info ("1.0.0+" + String('a', 40)))) = first @>)
