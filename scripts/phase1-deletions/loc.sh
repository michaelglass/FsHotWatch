#!/bin/bash
# Line counter for count.sh. usage: loc.sh file[:start-end] ...   prints raw and code (non-blank, non-// , non-(* *) ) lines; totals
tr=0; tc=0
for spec in "$@"; do
  f=${spec%%:*}; r=""; [ "$spec" != "$f" ] && r=${spec#*:}
  s=1; e=999999; if [ -n "$r" ]; then s=${r%-*}; e=${r#*-}; fi
  read raw code < <(awk -v s=$s -v e=$e 'NR>=s && NR<=e { raw++; l=$0; sub(/^[ \t]+/,"",l);
     if (inb) { if (l ~ /\*\)/) inb=0; next }
     if (l=="") next; if (l ~ /^\/\//) next;
     if (l ~ /^\(\*/ && l !~ /^\(\*\)/) { if (l !~ /\*\)/) inb=1; next }
     code++ } END {print raw+0, code+0}' "$f")
  printf "%6d %6d  %s\n" $raw $code "$spec"; tr=$((tr+raw)); tc=$((tc+code))
done
printf "%6d %6d  TOTAL\n" $tr $tc
