/// The invariant MS CodeCoverage depends on: every method's sequence-point blob in a
/// portable PDB decodes with System.Reflection.Metadata. Cecil tolerates blobs SRM
/// rejects, so this must be checked with SRM itself.
module TestPrune.Trace.PdbCheck

open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335

/// Every method whose sequence points fail to decode, as (method row number, message).
/// An empty list means the PDB is healthy.
let decodeFailures (pdbPath: string) : (int * string) list =
    // `using` rather than `use`, and Seq rather than a list comprehension: both compile
    // branches (a disposal null check, an enumerator dispose) no input can take.
    using (File.OpenRead pdbPath) (fun fs ->
        using (MetadataReaderProvider.FromPortablePdbStream fs) (fun provider ->
            let reader = provider.GetMetadataReader()

            reader.MethodDebugInformation
            |> Seq.choose (fun h ->
                let info = reader.GetMethodDebugInformation h

                if info.SequencePointsBlob.IsNil then
                    None
                else
                    try
                        for _ in info.GetSequencePoints() do
                            ()

                        None
                    with ex ->
                        Some(MetadataTokens.GetRowNumber h, ex.Message))
            |> Seq.toList))
