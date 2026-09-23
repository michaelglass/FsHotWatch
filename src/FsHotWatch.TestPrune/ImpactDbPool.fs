/// The pooled SQLite connections of one test-impact database.
///
/// `TestPrune.Core` opens its database through Microsoft.Data.Sqlite's pool, which is
/// keyed by connection string and outlives every `Database` value. A connection left in
/// it keeps the file it was opened on, even after that file is deleted or replaced.
module FsHotWatch.TestPrune.ImpactDbPool

open Microsoft.Data.Sqlite

/// The connection string `TestPrune.Core` opens `dbPath` with, and so the key its pooled
/// connections live under. A key that differs from the library's names a different,
/// empty pool, and clearing it does nothing.
let connectionString (dbPath: string) : string = $"Data Source=%s{dbPath}"

/// Drop the pooled connections of the database at `dbPath`, and of no other, so its
/// next open is a new connection to whatever file is there now. Never clear every pool
/// in the process instead: that lands between another database's open and its read.
let clear (dbPath: string) : unit =
    let key = new SqliteConnection(connectionString dbPath)

    try
        SqliteConnection.ClearPool key
    finally
        key.Dispose()
