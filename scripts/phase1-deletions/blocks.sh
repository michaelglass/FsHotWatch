#!/bin/bash
# Test-block counter for count.sh. usage: blocks.sh REGEX file...   -- top-level blocks (a `[<Fact>]`/`[<Theory>]`-attributed or plain top-level `let`)
# whose text matches REGEX; prints raw and code lines per file and total. Only blocks that are tests ([<Fact|[<Theory|[<Property) are counted.
re="$1"; shift; tr=0; tc=0; tn=0
for f in "$@"; do
  read raw code n < <(awk -v re="$re" '
    function flush() { if (buf != "" && istest && buf ~ re) { raw += nraw; code += ncode; n++ } buf=""; nraw=0; ncode=0; istest=0 }
    /^\[</ { if (!inattr) flush(); inattr=1; if ($0 ~ /Fact|Theory|Property/) istest=1; buf=buf $0 "\n"; nraw++; ncode++; next }
    /^(let|type|module|\/\/\/|\[<|#)/ && !inattr { flush() }
    { inattr = 0; buf = buf $0 "\n"; nraw++; l=$0; sub(/^[ \t]+/,"",l); if (l != "" && l !~ /^\/\//) ncode++ }
    END { flush(); print raw+0, code+0, n+0 }' "$f")
  [ "$n" -gt 0 ] && printf "%6d %6d %4d tests  %s\n" $raw $code $n "$f"
  tr=$((tr+raw)); tc=$((tc+code)); tn=$((tn+n))
done
printf "%6d %6d %4d tests  TOTAL\n" $tr $tc $tn
