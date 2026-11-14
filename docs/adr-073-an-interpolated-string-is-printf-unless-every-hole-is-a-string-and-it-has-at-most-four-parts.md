# ADR-073: An interpolated string is printf unless every hole is a string and it has at most four parts

FsHotWatch.Core and `fshw-lookup` build their text without FSharp.Core's printf engine, so a
Native AOT publish of them has no trim/AOT warning. Getting there showed something that is easy
to get wrong: an UNTYPED interpolated string can still compile to printf. This records the rule,
as measured, and how Core keeps to it. AUTOMATION-1695 (Phase 1, step C; experiment E5).

Status: Accepted (2026-11-14)

## Context

A Native AOT publish of `fshw-lookup` (FsHotWatch.Core only, FSharp.Core 10.1.402) listed 46
trim/AOT warning sites, all in FSharp.Core: 13 in `PrintfImpl` (`MakeGenericMethod` and
`MakeGenericType`, which Native AOT cannot guarantee for a value-type hole such as `%d{n}`), and
33 in the `%A` formatter and the F# reflection behind it. Core reached `PrintfImpl`; it could not
reach `%A`, because it compiles with `--reflectionfree`.

Converting Core's 128 typed holes (`%s{x}`, `%d{n}`) to untyped ones (`{x}`) was not enough. A
scan of Core's compiled method bodies (F# compiler of SDK 10.0.400) still found
`ExtraTopLevelOperators.PrintFormatToString` and `PrintfFormat` constructors in 16 methods.

## The rule, as measured

`$"..."` compiles to `String.Concat` only when BOTH hold:

- every hole is a `string`, and
- the string has at most four parts (literal runs and holes counted together).

Otherwise it compiles to `new PrintfFormat<...>` and `PrintFormatToString`, the printf engine,
even with no `%` in it. Examples from Core: `$"a digest is 64 characters, not {length}"` (an
`int` hole) and `$"  [{tag}] {ts} {msg}"` (six parts) were printf; `$"{id.Version} {id.ContentHash}"`
(three parts, all strings) was `String.Concat`. Typed holes (`%s{x}`), `sprintf`, `printfn`,
`eprintfn` and `failwithf` are always printf.

## Decision

1. Core and `fshw-lookup` never reach the printf engine or F# reflection.
   - Text that breaks the rule is a `+` chain: `+` on strings is `String.Concat`.
   - A number is written with `ToString(CultureInfo.InvariantCulture)`, which is what `%d`
     printed, so the output is byte-identical.
   - Output goes through `Console.Out.WriteLine` and `Console.Error.WriteLine`.
2. `CorePrintfFreeTests` enforces it on the compiled assembly, not on the source. It fails on any
   metadata reference to `PrintfModule`, `PrintfImpl`, `PrintfFormat`N`,
   `ExtraTopLevelOperators.PrintFormat*` (which covers `sprintf`, `printfn`, `eprintfn` and
   `failwithf`), `FSharpType` or `FSharpValue`. A control finds them in the daemon assembly. The
   rule above is a measurement of one compiler, and a later compiler may lower differently: the
   test, not the rule, is what keeps Core printf-free.
3. Nothing is allowlisted. With `PrintfImpl` unreachable, the trimmer drops the `%A` formatter and
   F# reflection too, and the publish reports 0 trim/AOT warnings with warnings as errors.

## Consequences

- Text in Core is wordier where a string breaks the rule. Most of Core's 80 interpolated strings
  still compile to `String.Concat` and are unchanged.
- `fshw-lookup` is 9,083,864 bytes (osx-arm64), down from 9,481,960 with printf.
- Code outside Core, the daemon and the CLI included, is unaffected and still uses printf.
