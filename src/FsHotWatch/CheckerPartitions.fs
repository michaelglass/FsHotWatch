/// A repository host's checkers, one per checker configuration.
///
/// Sessions whose configuration is equal check through one checker. FCS keys its
/// framework imports and `TcGlobals` by the framework references, not by the project,
/// so sessions on one SDK then hold one copy of both.
module FsHotWatch.CheckerPartitions

// TransparentCompiler.CacheSizes is marked experimental; it is the checker's configuration.
#nowarn "57"

open System.Collections.Concurrent
open FSharp.Compiler.CodeAnalysis

/// The checkers built so far, by configuration. `make` builds the checker for a
/// configuration the first time any session asks for it.
type Partitions<'Checker>(make: TransparentCompiler.CacheSizes -> 'Checker) =
    let checkers =
        ConcurrentDictionary<TransparentCompiler.CacheSizes, System.Lazy<'Checker>>()

    /// The checker for `sizes`, built once however many sessions ask at once.
    member _.For(sizes: TransparentCompiler.CacheSizes) : 'Checker =
        checkers.GetOrAdd(sizes, fun key -> lazy (make key)).Value

    /// How many partitions exist.
    member _.Count: int = checkers.Count
