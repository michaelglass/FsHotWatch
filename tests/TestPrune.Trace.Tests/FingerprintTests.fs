module TestPrune.Trace.Tests.FingerprintTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model

let private inputs: Fingerprint.Inputs =
    { Runtime = ".NET 10.0.0"
      Os = "OSX"
      Arch = "Arm64"
      DepsJsonSha256 = "d"
      RecorderVersion = "0.1.0"
      WeaverVersion = "0.1.0"
      HashScheme = 16
      ConfigFiles = [ "global.json", "h" ]
      ConfigEnv = [] }

[<Fact>]
let ``the fingerprint changes with any input and is stable otherwise`` () =
    let i = inputs
    test <@ Fingerprint.compute i = Fingerprint.compute i @>
    test <@ Fingerprint.compute i <> Fingerprint.compute { i with Runtime = ".NET 10.0.1" } @>
    test <@ Fingerprint.compute i <> Fingerprint.compute { i with Os = "Linux" } @>
    test <@ Fingerprint.compute i <> Fingerprint.compute { i with Arch = "X64" } @>
    test <@ Fingerprint.compute i <> Fingerprint.compute { i with DepsJsonSha256 = "e" } @>

    test
        <@
            Fingerprint.compute i
            <> Fingerprint.compute { i with RecorderVersion = "0.2.0" }
        @>

    test <@ Fingerprint.compute i <> Fingerprint.compute { i with WeaverVersion = "0.2.0" } @>
    test <@ Fingerprint.compute i <> Fingerprint.compute { i with HashScheme = 17 } @>

    test
        <@
            Fingerprint.compute i
            <> Fingerprint.compute
                { i with
                    ConfigFiles = [ "global.json", "h2" ] }
        @>

    test
        <@
            Fingerprint.compute i
            <> Fingerprint.compute
                { i with
                    ConfigEnv = [ "APP_ENV", "x" ] }
        @>

[<Fact>]
let ``the fingerprint ignores the order configured files and variables are listed in`` () =
    let a =
        { inputs with
            ConfigFiles = [ "a.json", "1"; "b.json", "2" ]
            ConfigEnv = [ "X", "1"; "Y", "2" ] }

    let b =
        { a with
            ConfigFiles = List.rev a.ConfigFiles
            ConfigEnv = List.rev a.ConfigEnv }

    test <@ Fingerprint.compute a = Fingerprint.compute b @>

[<Fact>]
let ``the fingerprint is a pinned lowercase sha256 of canonical json`` () =
    // Pinned: a change to the canonical form silently invalidates every stored trace, so it
    // must be a deliberate edit of this value (and a note in the changelog). The value is
    // sha256 of the compact, key-sorted JSON:
    // {"arch":"Arm64","deps":"d","env":[],"files":["global.json=h"],"hashScheme":16,
    //  "os":"OSX","recorder":"0.1.0","runtime":".NET 10.0.0","weaver":"0.1.0"}
    test <@ Fingerprint.compute inputs = "4d69f1872f10cdaaaa3ebbf3da64a9b173dc3c8143e30783c28e643ec3974bc1" @>

[<Fact>]
let ``hashFile hashes a repo file's bytes and marks a missing file`` () =
    let root = Directory.CreateTempSubdirectory().FullName
    File.WriteAllText(Path.Combine(root, "global.json"), "abc")

    test
        <@ Fingerprint.hashFile root "global.json" = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" @>

    test <@ Fingerprint.hashFile root "absent.json" = "missing" @>

[<Fact>]
let ``gather folds in the dump, versions, core's schema as hash scheme and hashed config`` () =
    let root = Directory.CreateTempSubdirectory().FullName
    File.WriteAllText(Path.Combine(root, "global.json"), "abc")
    let setVar = $"TESTPRUNE_FP_SET_{Guid.NewGuid():N}"
    let unsetVar = $"TESTPRUNE_FP_UNSET_{Guid.NewGuid():N}"
    Environment.SetEnvironmentVariable(setVar, "abc")

    let dump: ProcessDump =
        { Pid = 1
          ParentScope = None
          Runtime = ".NET 10.0.0"
          Os = "OSX"
          Arch = "Arm64"
          IdCount = 0
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
          Scopes = [] }

    try
        let i =
            Fingerprint.gather root [ "global.json"; "absent.json" ] [ setVar; unsetVar ] dump "deps-sha"

        let abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        test <@ i.Runtime = ".NET 10.0.0" && i.Os = "OSX" && i.Arch = "Arm64" @>
        test <@ i.DepsJsonSha256 = "deps-sha" @>
        test <@ i.HashScheme = TestPrune.Database.SchemaVersion @>
        test <@ i.RecorderVersion.Split('.').Length = 3 && i.WeaverVersion.Split('.').Length = 3 @>
        test <@ i.ConfigFiles = [ "global.json", abc; "absent.json", "missing" ] @>
        // The value is never stored, only its hash.
        test <@ i.ConfigEnv = [ setVar, abc; unsetVar, "unset" ] @>
    finally
        Environment.SetEnvironmentVariable(setVar, null)
