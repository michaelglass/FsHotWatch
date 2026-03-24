<!-- sync:intro -->
# FsHotWatch

Speed up your F# development feedback loop.

FsHotWatch is a background daemon that watches your source files and
keeps the F# compiler warm. When you save a file, it instantly re-checks
it and tells your tools (linters, analyzers, test runners) what changed
— without restarting the compiler from scratch each time.
<!-- sync:intro:end -->
