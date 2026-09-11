# Declared exclusions bound verification debt

The final A104/A106 candidate at `4f5a5c09` passed 3,440 unit tests and all coverage floors, then its actual confirmation returned Red. The configured unit suite passed, but 2,193 symbols remained owed to integration or runner-fixture projects. The verifier had retained every indexed covering project without receiving the explicit solution-scope exclusions already validated and recorded by the CLI. Mixed symbols therefore could not retire even after all required tests passed.

The verification owner now applies one declared-exclusion policy to initial debt classification, captured launch coverers, later boot-scan coverers, and residual diagnostics. Only a nonblank reason for a resolved project removes that project from this claim. Configured projects remain required, and unexplained covering projects remain owed. Existing construction APIs supply no exclusions. Excluding a project is never evidence that its tests passed.

The CLI resolves declarations against actual solution project files and the discovered project inventory. Directory names cannot substitute for project filenames: the symbol database uses the project-file stem. Ambiguous identities must be refused rather than accidentally exempting a different discovered project. This resolution is reviewed and tested independently of debt retirement.

The xUnit v4 runner fixture is now explicitly listed in the solution and excluded from the top-level daemon test scope with a reason identifying its real runner/CTRF harness in the configured unit suite. Integration tests keep their existing explicit exclusion and separate required integration gate. No blanket exemption is introduced for projects outside a solution or for tests that the daemon cannot run.

The original confirmation at `4f5a5c09` was red despite its passing unit suite. Subsequent controls and complete candidate acceptance must be recorded separately; that unit pass did not establish a passing canonical confirmation.
