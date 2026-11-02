/// Each plugin command's typed reply: written as the JSON the command always sent, read back
/// into the same reply, and rendered by the CLI exactly as its shape was rendered before the
/// reply carried a schema.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.PluginReplyTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli
open FsHotWatch.Cli.IpcOutput
open FsHotWatch.Build.BuildReplies

/// The exit code and everything written (stdout, stderr and the verdict terminal) when the
/// CLI renders `reply`.
let private rendered (noWarnFail: bool) (reply: string) : int * string =
    let priorOut, priorErr = Console.Out, Console.Error
    use writer = new StringWriter()
    Console.SetOut writer
    Console.SetError writer

    try
        use _ = VerdictTerminal.install writer

        let exitCode =
            renderIpcResult ProgressRenderer.Verbose (fun _ -> [ "statuses" ]) noWarnFail reply

        writer.Flush()
        exitCode, writer.ToString()
    finally
        Console.SetOut priorOut
        Console.SetError priorErr

/// The CLI renders `payload` under its declared schema exactly as it renders the same bytes
/// read by their shape.
let private rendersAsSniffed (command: string) (schema: string) (payload: string) =
    let typed =
        FsHotWatch.Ipc.declaredOutputReply command (FsHotWatch.Plugin.ReplySchema.Typed schema) payload

    let sniffed =
        FsHotWatch.Ipc.declaredOutputReply command (FsHotWatch.Plugin.ReplySchema.Raw "read by its shape") payload

    for noWarnFail in [ false; true ] do
        test <@ rendered noWarnFail typed = rendered noWarnFail sniffed @>

/// A payload its declared schema does not describe is no verdict, naming the schema.
let private refusesUndescribed (command: string) (schema: string) (payload: string) =
    let exitCode, text =
        rendered false (FsHotWatch.Ipc.declaredOutputReply command (FsHotWatch.Plugin.ReplySchema.Typed schema) payload)

    test <@ exitCode = NoVerdictExitCode @>
    test <@ text.Contains schema @>

let private buildStatuses =
    [ BuildStatus.Passed "Build succeeded."
      BuildStatus.Failed "error FS0001: boom"
      BuildStatus.NotRun ]

[<Fact(Timeout = 15000)>]
let ``build-status is written as the JSON it always was, and read back as itself`` () =
    test <@ BuildStatus.serialize (BuildStatus.Passed "ok") = """{"output":"ok","status":"passed"}""" @>
    test <@ BuildStatus.serialize (BuildStatus.Failed "no") = """{"output":"no","status":"failed"}""" @>
    test <@ BuildStatus.serialize BuildStatus.NotRun = """{"status":"not run"}""" @>

    for status in buildStatuses do
        test <@ BuildStatus.tryParse (BuildStatus.serialize status) = Some status @>

[<Fact(Timeout = 15000)>]
let ``force-rebuild is written as the JSON it always was, and read back as itself`` () =
    test <@ ForceRebuild.serialize ForceRebuild.Forced = """{"forced":true,"status":"ok"}""" @>
    test <@ ForceRebuild.tryParse (ForceRebuild.serialize ForceRebuild.Forced) = Some ForceRebuild.Forced @>

[<Fact(Timeout = 15000)>]
let ``a typed build reply renders exactly as its shape did`` () =
    for status in buildStatuses do
        rendersAsSniffed "build-status" BuildStatusSchema (BuildStatus.serialize status)

    rendersAsSniffed "force-rebuild" ForceRebuildSchema (ForceRebuild.serialize ForceRebuild.Forced)

[<Theory(Timeout = 15000)>]
[<InlineData("""{"status":"passed"}""")>]
[<InlineData("""{"status":"exploded","output":"x"}""")>]
[<InlineData("""{"count":3}""")>]
[<InlineData("not json")>]
let ``a build-status payload its schema does not describe is no verdict`` (payload: string) =
    refusesUndescribed "build-status" BuildStatusSchema payload

[<Theory(Timeout = 15000)>]
[<InlineData("""{"status":"ok"}""")>]
[<InlineData("""{"status":"ok","forced":false}""")>]
let ``a force-rebuild payload its schema does not describe is no verdict`` (payload: string) =
    refusesUndescribed "force-rebuild" ForceRebuildSchema payload
