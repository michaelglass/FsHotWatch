# Changelog — TestPrune.Analyzers

## Unreleased

## 0.1.0-alpha.4 - 2026-07-15

- **TP001 is now dogfooded: TestPrune runs this analyzer against TestPrune**.
  The package was published but never loaded against its own repo, so nothing proved the rule
  still fired — an analyzer that reports nothing and an analyzer that never loaded produce
  byte-identical output. It is now loaded by the `fshw` gate (`.fshw.json` `analyzers.paths`)
  in `mise run ci` and in GitHub Actions, and a violation fails both. Turning it on found 6
  real TP001 sites in TestPrune's own source (fixed; see the TestPrune.Core and TestPrune
  changelogs). No behavior change to the analyzer itself.

## 0.1.0-alpha.3 - 2026-06-25

- chore(deps): recompile against FSharp.Analyzers.SDK 0.37.2 (FCS 43.12.201). TP001 (`TestPrune.AnonymousRecord`) diagnostic behavior unchanged.

## 0.1.0-alpha.2 - 2026-06-12

- fix: re-publish the initial release. The `analyzers-v0.1.0-alpha.1` tag was created
  but its NuGet publish never landed (orphan tag), so this is the first version of the
  package actually available on the feed. No code changes since 0.1.0-alpha.1.

## 0.1.0-alpha.1 - 2026-06-02
- feat: initial release — opt-in `FSharp.Analyzers.SDK` analyzer `TP001`
  (`TestPrune.AnonymousRecord`) that flags anonymous-record expressions and type
  annotations, which are invisible to TestPrune impact analysis.
