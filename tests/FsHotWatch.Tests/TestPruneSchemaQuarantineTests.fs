/// A daemon whose own `test-impact.db` was written by a NEWER TestPrune.Core.
///
/// A land that bumps the TestPrune pin and then fails leaves the land worktree's index
/// forward-migrated. TestPrune.Core rightly refuses to open it
/// (`SchemaNewerThanConsumerException`), and before this the refusal escaped plugin
/// registration, so the older daemon never started and every later land failed. The
/// index is a cache: the daemon sets the newer file aside, keeps it under a name that
/// says which schema wrote it, starts on a fresh index, and says so.
module FsHotWatch.Tests.TestPruneSchemaQuarantineTests

open System.Collections.Concurrent
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.TestHelpers

/// One schema beyond what this TestPrune.Core supports: what a newer pin leaves behind.
let private newerSchema = TestPrune.Database.SchemaVersion + 1

let private stampUserVersion (dbPath: string) (version: int) =
    use conn = new Microsoft.Data.Sqlite.SqliteConnection(sqliteConnectionString dbPath)

    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- $"CREATE TABLE IF NOT EXISTS newer_marker (x INTEGER); PRAGMA user_version = %d{version};"
    cmd.ExecuteNonQuery() |> ignore

let private readUserVersion (dbPath: string) =
    use conn = new Microsoft.Data.Sqlite.SqliteConnection(sqliteConnectionString dbPath)

    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "PRAGMA user_version;"
    let version = cmd.ExecuteScalar() |> System.Convert.ToInt32
    conn.Close()
    clearSqlitePool dbPath
    version

[<Fact(Timeout = 30000)>]
let ``a daemon whose test-impact db has a newer schema quarantines it and starts on a fresh one`` () =
    withTempDir "tp-schema-quarantine" (fun tmpDir ->
        let dbPath = FsHotWatch.Cli.DaemonConfig.testImpactDbPath tmpDir
        Directory.CreateDirectory(Path.GetDirectoryName dbPath) |> ignore
        stampUserVersion dbPath newerSchema
        clearSqlitePool dbPath

        // PositiveControl: the file really is one this TestPrune.Core refuses.
        raises<TestPrune.Database.SchemaNewerThanConsumerException> <@ TestPrune.Database.Database.create dbPath @>

        let config =
            FsHotWatch.Cli.DaemonConfig.parseConfig
                """{"tests":{"projects":[{"project":"P","command":"true","args":"","coverage":false,"timeoutSec":10}]}}"""
                (FsHotWatch.Cli.DaemonConfig.stripConfig (defaultTestConfig ()))

        let config =
            { config with
                Format = FsHotWatch.Cli.DaemonConfig.FormatMode.Off }

        let daemon =
            FsHotWatch.Daemon.Daemon.createWith (Unchecked.defaultof<_>) tmpDir oneShotDaemonOptions

        let lines = ConcurrentQueue<string>()

        let sink: FsHotWatch.Logging.LogSink =
            { Write = lines.Enqueue
              Level = FsHotWatch.Logging.LogLevel.Info
              File = None }

        try
            do
                use _ = FsHotWatch.Logging.installSink sink
                FsHotWatch.Cli.DaemonConfig.registerPlugins daemon tmpDir config

            let aside =
                FsHotWatch.TestPrune.TestPrunePlugin.quarantinedIndexPath dbPath newerSchema

            // The newer file is kept, untouched, under a name a newer consumer can find.
            test <@ File.Exists aside @>
            test <@ readUserVersion aside = newerSchema @>
            // The daemon's own index is a fresh one at this consumer's schema.
            test <@ readUserVersion dbPath = TestPrune.Database.SchemaVersion @>

            let logged = lines |> Seq.toList

            let mentions (text: string) =
                logged |> List.exists (fun l -> l.Contains text)

            test <@ mentions aside @>
            test <@ mentions $"schema v%d{newerSchema}" @>
        finally
            daemon.Host.Teardown())

[<Fact(Timeout = 30000)>]
let ``quarantine drops a stale sidecar left by an earlier quarantine of the same schema`` () =
    withTempDir "tp-schema-quarantine-sidecars" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test-impact.db")
        stampUserVersion dbPath newerSchema
        clearSqlitePool dbPath

        let aside =
            FsHotWatch.TestPrune.TestPrunePlugin.quarantinedIndexPath dbPath newerSchema

        // A journal from an EARLIER quarantine, which must not pair with this file.
        File.WriteAllText(aside + "-wal", "stale wal")

        let db =
            FsHotWatch.TestPrune.TestPrunePlugin.openOwnIndex dbPath TestPrune.Domain.WalkPolicy.FollowAll

        test <@ db.WasRecreated @>
        clearSqlitePool dbPath
        test <@ not (File.Exists(aside + "-wal")) @>
        test <@ readUserVersion aside = newerSchema @>)
