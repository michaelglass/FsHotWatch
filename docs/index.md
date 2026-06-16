<!-- sync:intro -->
# FsHotWatch

Trying to speed up the F# development feedback loop.

FsHotWatch is a background daemon that watches your source files and aims to
keep the F# compiler warm, so saving a file re-checks just what changed and
hands the results to your tools (linters, analyzers, test runners) — instead of
each tool restarting the compiler from scratch every time.
<!-- sync:intro:end -->
