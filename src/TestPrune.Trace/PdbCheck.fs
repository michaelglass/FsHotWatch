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
    use fs = File.OpenRead pdbPath
    use provider = MetadataReaderProvider.FromPortablePdbStream fs
    let reader = provider.GetMetadataReader()

    [ for h in reader.MethodDebugInformation do
          let info = reader.GetMethodDebugInformation h

          if not info.SequencePointsBlob.IsNil then
              let failure =
                  try
                      for _ in info.GetSequencePoints() do
                          ()

                      None
                  with ex ->
                      Some ex.Message

              match failure with
              | Some message -> yield MetadataTokens.GetRowNumber h, message
              | None -> () ]
