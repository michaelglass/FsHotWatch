# Analyzer evaluation helper preparation

This standalone helper is not integrated into analyzer provenance capture,
validation, packaging, or the solution build. Its net8.0 target is provisional;
no SDK compatibility is claimed until the matrix runs.

Supported preparation tasks:

- `mise run compile-evaluation-helper`: compile only this project.
- `mise run test-evaluation-helper`: compile it, then run evaluation-only smoke
  controls against installed SDK8.0.408,9.0.311,10.0.301.
- The smoke task accepts repeated `--sdk-install /path/to/dotnet/version` for
  an explicit installed matrix. It neither installs SDKs nor selects global tools.

The private request is an owner-only XML file in an owner-only directory:
`AnalyzerEvaluationRequest` version1, absolute project/sdkRoot, expected
sdkVersion/msbuildVersion, and one Globals element containing Property name/value
pairs. Values use MSBuild's escaped global representation. Environment values
are neither captured nor replayed. The request is separate from public receipts.

The parent launches the executable with the recorded SDK's MSBuild.runtimeconfig
and the helper's own BCL-only deps. The helper anchors AssemblyDependencyResolver
to the recorded SDK's MSBuild.dll/deps, loads Microsoft.Build.dll from that SDK,
and validates the evaluated SDK/MSBuild versions. MSBUILD_EXE_PATH is established
inside this disposable process before engine initialization. Other current
environment inputs remain inherited. SDK resolver behavior across8/9/10 still
requires actual qualification.

Successful JSON carries schema1, ordered Compile entries, five effective
properties, ordered resolved Imports with local content hashes, and the loaded
MSBuild assembly path. No targets execute. Raw SDK diagnostics/exceptions are
suppressed; failure returns2 with a fixed reason. Imports hashes are local
binding evidence: future integration must classify SDK/NuGet identities versus
first-party content, not put package bytes in shared semantic keys.

Still required after source review: actual compilation and matrix controls;
producer/shared-projection integration; reader protocol validation; package
ProjectReference/content/buildTransitive wiring; clean packaged-consumer proof;
full failed/malformed/timeout/drain controls at the existing ProcessHelper caller;
Windows ACL smoke preparation; and the separate per-check cohort/cost work.
Per-file evaluation remains too costly until proven otherwise by the whole-check
benchmark. No TTL or discovery-generation cache is introduced here.
