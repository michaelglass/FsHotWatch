# Changelog — TestPrune.Analyzers

## Unreleased

- fix: re-publish the initial release. The `analyzers-v0.1.0-alpha.1` tag was created
  but its NuGet publish never landed (orphan tag), so this is the first version of the
  package actually available on the feed. No code changes since 0.1.0-alpha.1.

## 0.1.0-alpha.1 - 2026-06-02
- feat: initial release — opt-in `FSharp.Analyzers.SDK` analyzer `TP001`
  (`TestPrune.AnonymousRecord`) that flags anonymous-record expressions and type
  annotations, which are invisible to TestPrune impact analysis.
