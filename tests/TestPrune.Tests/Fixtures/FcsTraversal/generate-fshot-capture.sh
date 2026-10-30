#!/bin/sh
set -eu

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <capture-directory> <output.zip>" >&2
  exit 2
fi

capture_dir=$1
output_dir=$(cd "$(dirname "$2")" && pwd)
output_zip="$output_dir/$(basename "$2")"

test -f "$capture_dir/MANIFEST.txt"
test -f "$capture_dir/SHA256SUMS"
test -f "$capture_dir/LICENSE.FsHotWatch"
test -f "$capture_dir/LICENSE.xunit"

stage_dir=$(mktemp -d "${TMPDIR:-/tmp}/testprune-capture.XXXXXX")
trap 'rm -rf "$stage_dir"' EXIT HUP INT TERM
cp -R "$capture_dir" "$stage_dir/capture"

# ZIP's portable timestamp floor is 1980. Normalize every entry and sort the
# input list so the same audited payload always produces the same bytes.
find "$stage_dir/capture" -exec touch -t 198001010000 {} +
rm -f "$output_zip"
(
  cd "$stage_dir"
  find capture -print | LC_ALL=C sort | zip -X -q "$output_zip" -@
)
