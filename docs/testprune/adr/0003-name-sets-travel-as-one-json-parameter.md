# Name sets travel as one JSON parameter, not one placeholder per name

Every query in `Database.fs` that tests membership against a caller-supplied
list of names built `IN (@p0, @p1, ..., @pN)`. That spends one SQLite host
parameter per name, and SQLite caps host parameters per prepared statement at
`SQLITE_MAX_VARIABLE_NUMBER` — 32766 in the `SQLitePCLRaw.lib.e_sqlite3` build
this repository pins, confirmed with `sqlite_compileoption_get` and by binding
parameters until the library refused.

The ceiling was therefore set by repository size, not by anything a caller could
see, and nothing in the suite approached it. The impact-filtered path passes a
handful of changed symbols; only a full unfiltered pass passes the whole graph.
One over a 64,913-symbol index did, and `flushAndQueryAffected` failed the run
with `SQLite Error 1: 'too many SQL variables'`.

Name sets now travel as a single bound parameter holding a JSON array, matched
with `IN (SELECT value FROM json_each(@p))`. No list length changes the
parameter count, so there is no longer a length at which these queries begin to
fail. `SqliteVariableLimitTests` crosses the measured ceiling on every affected
query and asserts the real result back, with a positive control proving the
ceiling exists in the loaded library.

Chunking the list was considered and rejected. It raises the ceiling without
removing it — a later column or a larger repository reaches the new one — and it
is wrong for `QueryAffectedTestsCore` in particular, whose `barriers` CTE is
defined by exclusion from the seed set: a per-chunk walk treats seeds outside
its own chunk as barriers, stops expanding through them, and silently selects
fewer tests. A failure that becomes a quiet under-selection is worse than the
failure.

A per-connection temporary table was also considered. It is equally immune, but
it adds DDL, transaction plumbing at each call site, and connection state to
clean up, for no benefit over one parameter.

The JSON form is not a performance trade. Measured on this machine against an
80,000-row `symbols` table, 2000 iterations per arm: a 5-name list resolved in
0.009 ms/call versus 0.017 ms/call for the placeholder list, because the SQL
text no longer grows with the list; a 64,913-name list resolved in 115 ms, where
the placeholder form cannot run at all. The change stands on removing the
failure mode; the measurement records only that nothing was given up for it.
