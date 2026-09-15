# AUTOMATION-474 regression preparation

Eligible at read: Todo/high, Claude Code, no blocker. Ticket still has repo/intelligence label although diagnosed source is FsHotWatch. No workflow state mutation during this preparation.

Workspace: /Users/michaelglass/Developer/opensource/FsHotWatch/.workspaces/a474-receipt-loss
Base: main 082954c7da02d757138d6ee6936ac56e4c3c5a7a
Working change: lsquxyyz (WIP; use current jj snapshot)
Test edits: TestPruneRunScopeTests.fs and IpcOutputTests.fs. Plan persisted here; no production edits.

Initial tracer-bullet test and bounded controls prepared, not compiled or executed. It uses existing create/Update/Commands APIs and the existing local recording fixture. It seeds an executed full-suite completion, handles a real ordinary idle BuildCompleted BuildSucceeded, captures and executes the scheduled shared-resource work with Ready, asserts it actually returned empty NoProjectsSelected/AlreadyVerified, delivers that completion, then reads test-scope. Expected failure after a valid harness run: returned runId/scope no longer identify prior executed FullSuite1. Latest LastCoverage must still honestly say none. No new production API and no production changes. The test's command is a deliberately nonexistent sentinel, so accidental widening cannot fabricate a green fixture result.

Next vertical slices, only once root releases capacity:
1. Compile test using repository-supported toolchain; resolve any fixture/compiler defect first. Run canonical gate and observe this test fail at receipt assertions, not setup/no-op assertions. No implementation before this RED proof.
2. Repair the ordinary-build boundary around TestPrunePlugin7202 and selection/completion. Do NOT simply retain receipts across every event: transfer only valid evidence across proved AlreadyVerified/no-new-debt selection. Respect new edit/generation, changed-tree and failure boundaries. Rerun test until green.
3. Add one control at a time for real outstanding/new work (selection must execute, cannot silently reuse); manual explicit filtered launch keeps existing reset contract; artifacts unavailable/test-host unavailable invalidates receipt; failing narrowed run keeps failure ledger red even if scope includes a prior full receipt.
4. Integrate with CLI same-invocation/current-tree evidence tests in IpcOutputTests: existing changed-tree test at95, no prior execution test at47, command-convergence around206 and durable different-tree control1726 are mandatory controls. Add plugin-to-CLI reading regression if needed: prior pass, ordinary-build no-op before first settled read, final identity/scope uses earned receipt; changing candidate hash between evidence and final read withholds green.
5. Run canonical verification and inspect exact-tree verdict only after tests compile and allowed capacity exists. No confirm substitution for merge. Private Intelligence validation is a separate later release prerequisite, not satisfied here.

Unresolved design constraint: TestEvidenceReceipt currently has run identity/coverage/seeds/zero reason, not candidate hash; CLI RetainedTestRun binds hashes only when observed after settle. Keeping an old plugin receipt across a genuinely changed source tree could attach old evidence to a new hash on its first CLI read. Determine a proven generation/tree validity boundary using existing state before implementing retention. The one prepared unchanged-tree test is necessary, not sufficient ticket acceptance.

Historical audit still outstanding: original e6801d9a /1554f70c logs/snapshots must be located or explicitly reported unavailable; this fresh trace does not prove every original hypothesis.

Overlap: A572 edits TestPrunePlugin and CLI verdict-cause/reporting files; coordinate exact hunk ownership before production work, avoid its workspace. A564 identity/cache work is untouched and must remain independent. A782 runtime-debt lifecycle shares TestPrunePlugin but is separate behavior; new-debt control must preserve its generation retirement semantics. A104/106 supervision/work-ledger architecture is not required for this narrow regression.

No build, lint, test, daemon start, bootstrap, publish, QA, or closure was performed. This is source-only preparation; neither RED nor GREEN is claimed.

## Expanded source-only controls (not run)

The handler tracer is now a two-case theory: unchanged full receipt, and unchanged impacted receipt after the existing explicit manual-run boundary. It drives the ordinary idle BuildSucceeded and actual scheduled AlreadyVerified completion, then reads test-scope. This also catches completion preserving only FullSuite and discarding an otherwise valid impacted receipt.

Added a two-case unavailable-execution control with a previously earned receipt (artifacts and test host) and a dependency-debt control that reaches the shared-resource scheduler before receiving Invalid artifacts. No test subprocess is invoked in that refusal case. Existing queued narrow failure test, manual filtered FIFO reset test and CLI missing-run/unreadable-scope controls are reused instead of copied.

Added a two-case CLI command regression using REAL TreeHash.compute: a comment-only source edit and a declared root policy-input edit happen during the rescan between an executed reading and the quiet reading. Assert hashes actually differ, exactly one rescan, exit3, and no old run identity in the published non-evidence receipt. This exercises more than the existing string-hash reconciliation unit test.

## Minimal implementation design — pending demonstrated RED

The early reset at ordinary BuildSucceeded must not decide retention before selection proves AlreadyVerified. However simply removing EvidenceReceipt=None is unsafe. Bind receipt validity to candidate identity at launch/earn/reuse boundaries; exact tree includes configured exclusions, tool-known inputs and declared verdict inputs, not only changed symbols. A comment-only edit is absent from semantic ChangedFiles, and the plugin does not subscribe to raw FileChanged, so ChangedFiles/ChangedSymbols cannot prove byte identity.

Inject a small candidate-identity reader using the EXISTING TreeHash scheme and caller configuration. Current create/createWithLaunchDeadline receives no exclusion policy, so do not call compute root [] in production or invent a second recipe. Resolve wiring at plugin creation and carry the earned identity through test-scope/CLI report parsing, so first settled observation cannot assign old evidence the current hash. Capture before execution and validate after it and before reuse; mismatch/unreadability denies retention. Review the cost: hash at evidence boundaries, never once per FileChecked.

Retain provisionally across ordinary selection; publish prior valid receipt only for AlreadyVerified with matching identity and no pending debt/fanout. ChangesUncovered is not equivalent. New execution supplies new evidence. Preserve manual command resets and artifact/host failure invalidation. Failure ledger/status remains authoritative even if coverage is retained. No A104/A106 WorkLedger/supervisor replacement is needed.

Unwritten acceptance slice until that identity API exists: actual candidate edit AFTER a plugin receipt is earned but BEFORE the CLI FIRST settled reading must be refused, including comment-only and declared input. Existing API report carries no earned tree identity; inventing a test-only field would not exercise production. Add this test against the introduced production wire seam BEFORE implementing its behavior. Also add edit-during-execution, unreadable identity and genuine new runtime-generation positive controls. The current CLI edit theory covers changes after first observation, not that first-observation gap; do not confuse them.

## Coordination and next verification

A572 edits TestPrunePlugin and CLI verdict/reporting. Coordinate exact ownership before modifying either; do not merge its workspace or conflate its escalation cause with receipt validity. A564 repository/cache identity remains untouched. A782 runtime obligation generations must continue to retire only launched/current matching debt. No source production edits, no state changes or QA.

Next when root explicitly releases capacity: inspect tool setup read-only, then repository-supported compile check for tests, followed by canonical fshw check in THIS workspace. The intended invocation from an approved initialized tool environment is `dotnet fshw -a check` (FsHotWatch AGENTS workflow, not the private Intelligence wrapper); inspect `.fshw/verdict.json`. Confirm the ordinary-build theory fails at run identity/scope, not at baseline setup, helper typing or unexpected execution. All commands are deliberately unrun. Do not issue raw dotnet build/test, start a daemon, bootstrap, lint, gate or publish before capacity release. The private Intelligence ./build.fsx check/release validation remains a separate later obligation.
