# Schedule dependency checks from registered FCS inputs

The daemon keeps raw MSBuild source lists in its project graph, including generated
sources under `obj` and `bin`. CheckPipeline removes those generated sources from
its registered FCS options. Ordinary source-change dependency fanout previously
scheduled raw graph inputs against those filtered options. Real generated files
therefore reached FCS despite not belonging to the supplied project configuration.

Both project refresh and ordinary source-change fanout now use the registered
pipeline source list. When no options are registered, the existing graph fallback
retains the generated-path exclusion. Dependency graph edges and transitive
authored checks remain intact.

A watcher-to-batch regression uses three projects with transitive dependency
edges, real generated files in both output directories, and actual FCS checking.
It observes the transitive authored file and cohort completion before rejecting
generated entries in the scheduled SourceChanged event. The actual failing report
is recorded in `../evidence/automation-104-generated-input-fanout.json`.

Suppressing FCS's exception would conceal invalid scheduling. Restarting discovery
would recreate the same raw/filtered membership mismatch. Dropping dependent
project checks would miss authored breakage; the positive check witness prevents
that shortcut. This correction does not establish private earned-verdict proof or
complete the broader A104/A106 owner migration.
