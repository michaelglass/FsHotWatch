# ADR-033: A green is minted from earned evidence, not from a reported status

Status: Accepted (2026-09-17). Builds on ADR-018, ADR-021, ADR-022, ADR-029 and ADR-032.

## Context

`PluginStatus.Completed` is a value any plugin can report, and the verdict read the last one
reported. Every false green on record has that shape: a stale `Completed` replayed during a
live run, a green a second after start, a status that outlived the work it described.

ADR-032 bound a check result to the project model it was captured against. What was still
missing is the other half: the thing a green is read from. Before this change:

- Nothing joined a completion's actual outcomes, the debt it left, the report it produced,
  the tree it ran on and the model it was selected under. Each was checked somewhere, by
  someone, against a different reading.
- An analysis-only daemon (no test projects) had nothing to offer at all. Its green rested
  on "no plugin reported a failure".
- The CLI's green asked for a baseline and an available model, but never for proof that the
  run it graded had earned anything.

## Decision

- **Evidence is minted by the owner fold, never reported.** `Events.EarnedEvidence` is a
  private record built only by `EarnedEvidence.fromCompletion`, from the launch identity,
  the launch's model generation, the current model generation, the runnable projects, the
  count of obligations still pending, the previous evidence (as baseline) and the actual
  completion. It answers `None` when the launch named no model, when the model was
  replaced, or when the completion belongs to a different launch.
- **A proof can refuse.** Evidence carries `FailureReasons`: an abort, remaining
  obligations, a project with no result and no baseline, a filtered execution with no
  whole-project baseline, a failure or timeout, a deferred or errored sibling, a run that
  executed nothing. Evidence of what ran is not a claim that it passed. This is what makes
  a red full suite usable as a baseline (ADR-021) without laundering what it left owed.
- **Analysis-only daemons earn their own evidence.** `AnalysisFileEvidence.fromResult`
  records what one file's completed analysis concluded (compiler errors and a failed symbol
  analysis are refusals; warnings are not). At a cohort seal, `AnalysisEvidence.fromCompleted`
  accounts for every checkable file of the current model — a file with no completed analysis
  is a refusal, not an absence. It mints nothing when test projects are configured.
- **Evidence is published with the work it belongs to.** `TestPruneState` implements
  `IEarnedEvidenceState` and `IAnalysisEvidenceState`; `HostSnapshot.Evidence` and
  `.AnalysisEvidence` read those rows from the same publication as `IsBusy`, so a reader
  can never see retirement without the evidence or the reverse.
- **The model's checkable membership reaches the plugin.** `ProjectGraphAccessor.ObserveCheckableFiles`
  returns the available model's files with their generation, from the host's own store
  (ADR-032). A membership that does not belong to the current generation is not used.
- **A green requires the graded run's receipt for the current model.** The daemon serves
  `modelReceipts` on `GetDiagnostics` (additive on `fshw-verdict-v2`, per the ticket's Q6):
  `runId` (null for the analysis-only receipt), `modelGeneration`, `refusals`. A `Clean`
  outcome is downgraded to incomplete (exit 2), with the reason recorded in the verdict,
  unless a receipt exists for the graded run at the current generation with no refusals.
  A receipt that cannot be parsed is dropped, so it cannot vouch for anything.
- **An absent receipts property means the rule is silent; an empty array means a receipt is
  owed and missing.** A host whose registered plugins mint no evidence at all — an embedder
  without TestPrune — offers no receipts (`HostSnapshot.OffersEvidence` is false, the daemon
  omits `modelReceipts`, the CLI reads `ReceiptLedger.NotOffered`) and is graded exactly as
  before. A host that does offer them and holds none for the graded run sends `[]`, and that
  refuses the green. Inferring the first from the second would make a third-party embedder
  ungatable, or make every such daemon permanently un-green.
- **A scan seals its cohort, empty or not.** The seal is the scan's answer about the whole
  model, and a model with no checkable files has that answer as much as one with two
  thousand. Without it an analysis-only repository earns no receipt and can never be green.
- **Zero-selection keeps what verified it.** A completion that selected nothing because
  everything was already verified mints no new evidence: it retains the previous evidence
  while that belongs to the current model. Otherwise "nothing needed to run" would read as
  "nothing was verified".

## Rejected

- **Gating on `PluginStatus`.** A status is a report. The whole ticket is that a report is
  not evidence.
- **Dropping a receipt that refuses.** Then a red run would leave no trace, and the next
  reading could not tell "refused" from "never ran".
- **Refusing a green when the daemon serves no receipts at all.** That is a daemon older
  than this rule, not a daemon whose evidence failed. The other refusals still apply to it.
- **Minting analysis evidence per file.** A file is not a cohort: only the seal knows the
  membership the model expects.

## Fixed alongside

The publisher downgrades an otherwise-clean reading in two places: this receipt rule, and
the older check that the working tree did not move while the verdict was produced. Both
recorded their reason in `verdict.json` and said nothing at the terminal, because the
caller explains the outcome it handed in — which is `Clean`, and `Clean` explains nothing.
An operator saw exit 2 and silence. Both now print the sentence they record.

## Consequences

- `check` exits 2, not 0, when the daemon holds no receipt for the run it graded.
- Every scan now emits one `BatchChecked`, including a scan that dispatched no file: one
  extra seal per empty scan, and one per scan of an empty model. A scan that dispatched
  files still emits exactly one (`DaemonTests` "a scan seals its cohort exactly once, with
  files or without"). Change batches are unchanged: they seal only what they dispatched.
- An analysis-only daemon can be green on its own evidence, and says which model earned it.
- Plugin API changes: `TestPruneState` gains `Earned`, `AnalysisFiles` and `AnalysisReceipt`;
  `ProjectGraphAccessor` gains `ObserveCheckableFiles`.
- Wire change (additive): `GetDiagnostics` carries `modelReceipts`;
  `IpcParsing.DaemonEvidence.Served` carries the parsed receipts beside the phases.

## Not in this change

- `waitForAllTerminalCore` still decides rest from statuses plus the quiescence window.
  `requireVerdict` and `activeVerdictWaits` remain; the wait does not yet consume this
  evidence. That is the second half of this slice.
- A completed build failure mints no evidence yet, so an evidence wait on a red build still
  rests on the status.
