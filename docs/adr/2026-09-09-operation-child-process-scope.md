# Operation child process scopes

Status: compiled; operation deadline integration and plugin owner-context controls pass. Full qualification remains pending.

The tracked issue requires a deadline to account for actual child-process lifetime. The integration control on parent revision 6c20b2d8 demonstrated that cancellation alone leaves a noncooperative callback's child alive beyond seven seconds. The stronger launcher control passed independently. Exact report identities and source hashes are in ../evidence/operation-scope-reds.json.

Each supervised callback receives a narrower process registry, installed inside the captured owner execution context. It forwards admission and untracking to its parent so daemon shutdown retains visibility. The local registry owns operation cancellation and normal callback cleanup. Parent admission occurs first; a closed parent or local registry terminates the exact newly admitted handle. Registry failure records propagate to the parent.

Cancellation registration lifetime encloses teardown. Retirement waits for a concurrently running cancellation callback, and successful work cannot retire successfully when child termination remained uncertain. No foreign PID is resolved or killed. The parent is never closed merely to cancel one operation, because that would terminate siblings and refuse unrelated future work.

This is one part of the planned common supervisor. It does not complete the restart-policy migration, plugin/preprocessor adoption, or private earned-verdict work. The full unit suite still has the deliberately reproduced UI-status evidence failure. The parent-visibility assertions passed in the corrected deadline integration. Full CI and consumer qualification are still required before any merge claim.

## Verification exposed a triggering-context lifetime bug

The deadline integration passed after the child-scope change. A cold daemon then killed a test host as its scan completed and refused later preflights through that closed scope. Two direct controls demonstrated that ordinary and shared exclusive plugin work inherited the triggering caller's AsyncLocal context rather than its registered owner's context. Plugin registration now captures the owner execution context and launches both paths in that context. Both controls passed in full run 65360ed562d24d6b835461d5ef2412f7.

The scope includes completion notification, with child teardown before retirement publication. Parent-attribution coverage now injects a child-scope failure and checks that it reaches the owner, preserves the caller scope, and refuses a successful result. Shutdown coverage holds real preprocessor work open so daemon-wide reaping is observed while the child remains active; children are no longer expected to outlive successful operation completion. The first execution passed those ownership assertions but cleanup surfaced an AggregateException containing only the expected cancellation; the narrow cancellation-only adjustment passed in full run abdb77a165e044ba85ce573b94de7a95 (3160 passed, only the separate VerdictEvidence regression failed). A formatter test now waits for the owned state fold, not UI completion alone, before reading its command result.

## Disposed tracked handles require positive termination evidence

Independent review identified a callback that disposes its tracked Process before returning while the OS child remains alive. The added integration control reproduced successful retirement with that live child. Cleanup now preserves the PID captured at admission for diagnostics and treats an unobservable handle as termination uncertainty. A raced kill is benign only when HasExited positively establishes exit. Uncertainty propagates to the parent leak ledger and faults the original receipt; cleanup never re-resolves an old PID. This deliberately refuses success rather than claiming the disposed handle was reaped. The fixture retains an independent observer acquired while its child is positively live, and reaps that exact child during cleanup. All 70 integration controls passed in session89435 after compile4676; broader qualification remains pending.
