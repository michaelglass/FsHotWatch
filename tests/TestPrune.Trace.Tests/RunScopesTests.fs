/// Which static-init scopes a test inherits: those of the types it touched, transitively.
module TestPrune.Trace.Tests.RunScopesTests

open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model

let private row id kind typeName (doc: string option) : ManifestRow =
    { Id = id
      Kind = kind
      Assembly = "A"
      TypeName = typeName
      Member = ""
      Document = doc
      FirstLine = 1
      LastLine = 1 }

/// Ids 0-1: `N.Config`, a module whose values initialize in a startup class declared in
/// Config.fs. Ids 2-3: `N.Colour`, a type with its own initializer. Id 4: `N.Other`, in a
/// file with no initializer. Id 5: `N.Late`'s initializer, in Late.fs; id 6: a member there.
/// Id 7: the getter of a `N.Config` value, which has no sequence points and so no document.
/// Ids 8-9: `N.Solo`, a module whose one member is a value: its startup class's initializer
/// in Solo.fs, and the value's getter, with no document and no documented sibling.
let private manifest =
    { Rows =
        [| row 0 StaticCtor "<StartupCode$A>.$N.Config" (Some "/r/Config.fs")
           row 1 UserMethod "N.Config" (Some "/r/Config.fs")
           row 2 StaticCtor "N.Colour" None
           row 3 GeneratedMethod "N.Colour" None
           row 4 UserMethod "N.Other" (Some "/r/Other.fs")
           row 5 StaticCtor "N.Late" (Some "/r/Late.fs")
           row 6 UserMethod "N.Late" (Some "/r/Late.fs")
           row 7 UserMethod "N.Config" None
           row 8 StaticCtor "<StartupCode$A>.$N.Solo" (Some "/r/Solo.fs")
           row 9 UserMethod "N.Solo" None |]
      Documents = Map.empty
      IdCount = 10 }

let private scope key (ids: int list) : RecordedScope =
    { Key = key
      Test = None
      Parents = []
      Links = []
      Ids = Array.ofList ids
      Inputs = []
      Children = [] }

let private testScope key cls ids =
    { scope key ids with
        Test =
            Some
                { Class = cls
                  Method = "m"
                  Display = cls + ".m" } }

let private inherits (scopes: RecordedScope list) (held: string list) =
    let byKey = scopes |> List.map (fun s -> s.Key, s) |> Map.ofList
    RunScopes.staticInheritance manifest byKey (Set.ofList held)

[<Fact>]
let ``a test inherits the initializer of a type it touched, and of a file it ran code in`` () =
    let scopes =
        [ testScope "T:config" "N.Tests" [ 1 ]
          testScope "T:colour" "N.Tests" [ 3 ]
          testScope "T:other" "N.Tests" [ 4 ]
          scope "S:<StartupCode$A>.$N.Config" [ 0 ]
          scope "S:N.Colour" [ 2 ] ]

    test <@ inherits scopes [ "T:config" ] = set [ "S:<StartupCode$A>.$N.Config" ] @>
    test <@ inherits scopes [ "T:colour" ] = set [ "S:N.Colour" ] @>
    test <@ inherits scopes [ "T:other" ] = Set.empty @>

[<Fact>]
let ``a member with no document touches the files of its type's other members`` () =
    // Config's startup class was initialized elsewhere: reading its value from another file
    // runs only the getter, and the getter names no file.
    let scopes =
        [ testScope "T:1" "N.Tests" [ 7 ]; scope "S:<StartupCode$A>.$N.Config" [ 0 ] ]

    test <@ inherits scopes [ "T:1" ] = set [ "S:<StartupCode$A>.$N.Config" ] @>

[<Fact>]
let ``a module member touches the initializer of the module's startup class`` () =
    // In an executable a module has no type initializer of its own: reading a value runs its
    // file's startup class initializer, which F# names `<StartupCode$…>.$` + the module.
    let scopes =
        [ testScope "T:1" "N.Tests" [ 9 ]; scope "S:<StartupCode$A>.$N.Solo" [ 8 ] ]

    test <@ inherits scopes [ "T:1" ] = set [ "S:<StartupCode$A>.$N.Solo" ] @>

[<Fact>]
let ``a test inherits the initializer of its own class`` () =
    let scopes = [ testScope "T:1" "N.Colour" []; scope "S:N.Colour" [ 2 ] ]
    test <@ inherits scopes [ "T:1" ] = set [ "S:N.Colour" ] @>

[<Fact>]
let ``what an inherited initializer touched or ran inside it is inherited too`` () =
    // Config's initializer ran Late's member (Late was initialized earlier, elsewhere) and
    // triggered Colour's initializer inside its own.
    let scopes =
        [ testScope "T:1" "N.Tests" [ 1 ]
          { scope "S:<StartupCode$A>.$N.Config" [ 0; 6 ] with
              Links = [ "S:N.Colour"; "S:never-recorded" ] }
          scope "S:N.Colour" [ 2 ]
          scope "S:N.Late" [ 5 ] ]

    test <@ inherits scopes [ "T:1" ] = set [ "S:<StartupCode$A>.$N.Config"; "S:N.Colour"; "S:N.Late" ] @>

[<Fact>]
let ``an initializer that cannot be placed is inherited by every test`` () =
    // No manifest row names its type, or the recorder could not name it at all.
    let scopes =
        [ testScope "T:1" "N.Tests" [ 4 ]
          scope "S:N.Unwoven" []
          scope RunScopes.UnnamedStaticInit []
          scope "S:N.Colour" [ 2 ] ]

    test <@ inherits scopes [ "T:1" ] = set [ "S:N.Unwoven"; RunScopes.UnnamedStaticInit ] @>

[<Fact>]
let ``an id with no manifest row, or past the manifest, touches nothing`` () =
    let unrowed = { manifest with IdCount = 11 }

    let scopes =
        [ testScope "T:1" "N.Tests" [ 10; 99; -1 ]; scope "S:N.Colour" [ 2 ] ]
        |> List.map (fun s -> s.Key, s)
        |> Map.ofList

    test <@ RunScopes.staticInheritance unrowed scopes (set [ "T:1" ]) = Set.empty @>

[<Fact>]
let ``a child process's static init belongs to the scope that started it`` () =
    let dump pid parent scopes : ProcessDump =
        { Pid = pid
          ParentScope = parent
          Runtime = ""
          Os = ""
          Arch = ""
          IdCount = 7
          CpuMs = 0L
          Counters =
            { Test = 0L
              Class = 0L
              Collection = 0L
              Assembly = 0L
              Override = 0L
              StaticInit = 0L
              Ambient = 0L
              Overflow = 0L }
          Scopes = scopes }

    let merged =
        RunScopes.merged
            [ dump 1 None [ scope "T:1" [ 1 ]; scope "S:N.Colour" [ 2 ] ]
              dump 2 (Some "T:1") [ scope "S:N.Late" [ 5 ] ] ]

    test <@ merged |> Map.keys |> List.ofSeq = [ "S:N.Colour"; "T:1" ] @>
    test <@ merged.["T:1"].Ids = [| 1; 5 |] @>
