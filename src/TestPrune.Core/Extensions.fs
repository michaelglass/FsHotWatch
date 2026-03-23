module TestPrune.Extensions

open TestPrune.Database

/// Result from an extension's test selection.
type AffectedTest =
    { TestProject: string
      TestClass: string }

/// Extension interface for custom dependency sources beyond AST analysis.
/// Implement this to add framework-specific test selection (e.g., route-based,
/// coverage-based, or manual hints).
type ITestPruneExtension =
    /// Unique name for this extension (used in logging).
    abstract Name: string

    /// Given a list of changed source files (repo-relative paths),
    /// return test classes that should be re-run.
    abstract FindAffectedTests: db: Database -> changedFiles: string list -> repoRoot: string -> AffectedTest list
