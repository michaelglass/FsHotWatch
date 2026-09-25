namespace TestPrune.Trace.Recorder

open System.Collections.Concurrent
open System.Numerics
open System.Threading

/// One attribution bucket: a bitset of probe ids plus what the host needs to name,
/// link and join it. One word per 64 ids; set with a racy read then an atomic OR, so
/// the common "already set" case is a plain load.
[<Sealed; AllowNullLiteral>]
type Scope(key: string, idCount: int) =
    let bits: uint64[] = Array.zeroCreate ((idCount + 63) >>> 6)

    /// The scope key: `T:<test uid>`, `C:<class>`, `L:<collection>`, `A:…`, `S:static-init`, `P:…`.
    member _.Key = key

    /// The test class, set when the scope is a test (or a class fixture).
    member val TestClass: string = null with get, set

    /// The test method name, set only when the scope is a test.
    member val TestMethod: string = null with get, set

    /// The test display name, set only when the scope is a test.
    member val TestDisplay: string = null with get, set

    /// Keys of the enclosing scopes, innermost first.
    member val Parents: string[] = [||] with get, set

    /// Keys of scopes this one inherits everything from.
    member val Links = ConcurrentDictionary<string, byte>()

    /// Recorded file inputs as (kind, path): kind is `read`, `exists` or `list`.
    member val Inputs = ConcurrentDictionary<struct (string * string), byte>()

    /// Child processes started under this scope as (pid, file name, recorder env injected).
    member val Children = ConcurrentQueue<struct (int * string * bool)>()

    /// Marks probe `id`. The caller guarantees `0 <= id < idCount`.
    member _.Set(id: int) =
        let w = id >>> 6
        let m = 1UL <<< (id &&& 63)

        if (Volatile.Read(&bits.[w]) &&& m) = 0UL then
            Interlocked.Or(&bits.[w], m) |> ignore

    /// Every marked id, ascending.
    member _.Ids() : int[] =
        let out = ResizeArray()

        for w in 0 .. bits.Length - 1 do
            let mutable word = bits.[w]

            while word <> 0UL do
                out.Add((w <<< 6) + BitOperations.TrailingZeroCount word)
                word <- word &&& (word - 1UL)

        out.ToArray()

    /// True when nothing at all was recorded under this scope.
    member this.IsEmpty =
        Array.forall ((=) 0UL) bits
        && this.Links.IsEmpty
        && this.Inputs.IsEmpty
        && this.Children.IsEmpty
