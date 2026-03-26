module FsHotWatch.Logging

/// When false, suppress noisy per-file status transitions
/// ([lint], [check], [test-prune], [analyzers], [format-check]).
/// Always show [discover], [scan], [build], and error/warning messages.
let mutable verbose = false
