module TestPrune.Tests.DiffParserTests

open Xunit
open Swensen.Unquote
open TestPrune.DiffParser

[<Fact>]
let ``empty diff returns empty list`` () =
    test <@ parseChangedFiles "" |> List.isEmpty @>
    test <@ parseChangedPaths "" |> List.isEmpty @>

[<Fact>]
let ``single modified file`` () =
    let diff =
        """diff --git a/src/Foo.fs b/src/Foo.fs
--- a/src/Foo.fs
+++ b/src/Foo.fs
@@ -10,6 +10,7 @@
 unchanged
+new line
 unchanged"""

    test <@ parseChangedFiles diff = [ "src/Foo.fs" ] @>

[<Fact>]
let ``multiple changed files`` () =
    let diff =
        """diff --git a/src/Foo.fs b/src/Foo.fs
--- a/src/Foo.fs
+++ b/src/Foo.fs
@@ -1,3 +1,4 @@
+added
diff --git a/src/Bar.fs b/src/Bar.fs
--- a/src/Bar.fs
+++ b/src/Bar.fs
@@ -1,3 +1,4 @@
+added"""

    test <@ parseChangedFiles diff = [ "src/Foo.fs"; "src/Bar.fs" ] @>

[<Fact>]
let ``new file included`` () =
    let diff =
        """diff --git a/src/New.fs b/src/New.fs
new file mode 100644
--- /dev/null
+++ b/src/New.fs
@@ -0,0 +1,5 @@
+module New"""

    test <@ parseChangedFiles diff = [ "src/New.fs" ] @>
    test <@ parseChangedPaths diff = [ "src/New.fs" ] @>

[<Fact>]
let ``deleted file included`` () =
    let diff =
        """diff --git a/src/Old.fs b/src/Old.fs
deleted file mode 100644
--- a/src/Old.fs
+++ /dev/null
@@ -1,5 +0,0 @@
-module Old"""

    test <@ parseChangedFiles diff = [ "src/Old.fs" ] @>
    test <@ parseChangedPaths diff = [ "src/Old.fs" ] @>

[<Fact>]
let ``non-code files filtered out`` () =
    let diff =
        """diff --git a/README.md b/README.md
--- a/README.md
+++ b/README.md
@@ -1 +1 @@
-old
+new
diff --git a/package.json b/package.json
--- a/package.json
+++ b/package.json
@@ -1 +1 @@
-old
+new
diff --git a/ci.yml b/ci.yml
--- a/ci.yml
+++ b/ci.yml
@@ -1 +1 @@
-old
+new
diff --git a/src/Real.fs b/src/Real.fs
--- a/src/Real.fs
+++ b/src/Real.fs
@@ -1 +1 @@
-old
+new"""

    test <@ parseChangedFiles diff = [ "src/Real.fs" ] @>

[<Fact>]
let ``fsproj files included`` () =
    let diff =
        """diff --git a/src/MyProject/MyProject.fsproj b/src/MyProject/MyProject.fsproj
--- a/src/MyProject/MyProject.fsproj
+++ b/src/MyProject/MyProject.fsproj
@@ -1 +1 @@
-old
+new"""

    test <@ parseChangedFiles diff = [ "src/MyProject/MyProject.fsproj" ] @>

[<Fact>]
let ``hasFsprojChanges detects fsproj`` () =
    let files = [ "src/Foo.fs"; "src/MyProject/MyProject.fsproj"; "src/Bar.fs" ]
    test <@ hasFsprojChanges files = true @>

[<Fact>]
let ``hasFsprojChanges returns false when no fsproj`` () =
    let files = [ "src/Foo.fs"; "src/Bar.fsx" ]
    test <@ hasFsprojChanges files = false @>

[<Fact>]
let ``renamed file uses new path`` () =
    let diff =
        """diff --git a/src/Old.fs b/src/New.fs
rename from src/Old.fs
rename to src/New.fs"""

    test <@ parseChangedFiles diff = [ "src/New.fs" ] @>

[<Fact>]
let ``changed paths keep non-code files without changing changed-files filtering`` () =
    let diff =
        """diff --git a/tests/snapshots/api.snap.json b/tests/snapshots/api.snap.json
--- a/tests/snapshots/api.snap.json
+++ b/tests/snapshots/api.snap.json
@@ -1 +1 @@
-old
+new
diff --git a/src/Real.fs b/src/Real.fs
--- a/src/Real.fs
+++ b/src/Real.fs"""

    test <@ parseChangedPaths diff = [ "tests/snapshots/api.snap.json"; "src/Real.fs" ] @>
    test <@ parseChangedFiles diff = [ "src/Real.fs" ] @>

[<Fact>]
let ``changed paths include both sides of a rename`` () =
    let diff =
        """diff --git a/migrations/001-old.sql b/migrations/001-new.sql
similarity index 100%
rename from migrations/001-old.sql
rename to migrations/001-new.sql"""

    test <@ parseChangedPaths diff = [ "migrations/001-old.sql"; "migrations/001-new.sql" ] @>

[<Fact>]
let ``changed paths decode git C-quoted UTF-8 paths`` () =
    let diff =
        "diff --git \"a/tests/snapshots/caf\\303\\251 old.snap.json\" "
        + "\"b/tests/snapshots/caf\\303\\251 new.snap.json\"\n"
        + "similarity index 100%\n"
        + "rename from \"tests/snapshots/caf\\303\\251 old.snap.json\"\n"
        + "rename to \"tests/snapshots/caf\\303\\251 new.snap.json\""

    test <@ parseChangedPaths diff = [ "tests/snapshots/café old.snap.json"; "tests/snapshots/café new.snap.json" ] @>

[<Fact>]
let ``changed paths decode short octal and escaped characters`` () =
    let diff =
        "diff --git \"a/data/control\\1x-space\\40x-tab\\t-quote\\\"-slash\\\\.json\" "
        + "\"b/data/control\\1x-space\\40x-tab\\t-quote\\\"-slash\\\\.json\""

    test <@ parseChangedPaths diff = [ "data/control\u0001x-space x-tab\t-quote\"-slash\\.json" ] @>

[<Fact>]
let ``changed paths decode every Git C-style control escape`` () =
    let diff =
        "diff --git \"a/data/alert\\a-backspace\\b-newline\\n-vertical\\v-form\\f-return\\r.json\" "
        + "\"b/data/alert\\a-backspace\\b-newline\\n-vertical\\v-form\\f-return\\r.json\""

    test <@ parseChangedPaths diff = [ "data/alert\a-backspace\b-newline\n-vertical\v-form\f-return\r.json" ] @>

[<Fact>]
let ``malformed diff headers are ignored instead of producing partial paths`` () =
    let diff =
        "not a diff header\n"
        + "diff --git \n"
        + "diff --git a/no-new-path\n"
        + "diff --git \"a/unclosed b/data/new.json\n"
        + "diff --git a/data/old.json \"b/unclosed\n"
        + "diff --git x/data/old.json b/data/new.json\n"
        + "diff --git a/data/old.json c/data/new.json\n"

    test <@ parseChangedPaths diff |> List.isEmpty @>
    test <@ parseChangedFiles diff |> List.isEmpty @>

[<Fact>]
let ``malformed rename metadata falls through to valid patch metadata`` () =
    let diff =
        """diff --git a/data/old.json b/data/new.json
similarity index 90%
rename from "unterminated
rename to data/new.json
--- a/data/old.json
+++ b/data/new.json"""

    test <@ parseChangedPaths diff = [ "data/old.json"; "data/new.json" ] @>

[<Fact>]
let ``wrong-prefix patch metadata falls through to the header path`` () =
    let diff =
        """diff --git a/data/old.json b/data/new.json
--- c/data/not-the-old-path.json
+++ b/data/new.json"""

    test <@ parseChangedPaths diff = [ "data/old.json"; "data/new.json" ] @>

[<Fact>]
let ``quoted header tokens require separating whitespace`` () =
    let diff = "diff --git \"a/data/file.json\"\"b/data/file.json\"\n"

    test <@ parseChangedPaths diff |> List.isEmpty @>

[<Fact>]
let ``changed paths decode an octal escape at the end of a quoted path`` () =
    let diff = "diff --git \"a/data/name-\\141\" \"b/data/name-\\141\"\n"

    test <@ parseChangedPaths diff = [ "data/name-a" ] @>

[<Fact>]
let ``header ending in whitespace without a new token is ignored`` () =
    let diff = "diff --git a/data/old.json   \n"

    test <@ parseChangedPaths diff |> List.isEmpty @>

[<Fact>]
let ``quoted old header ending in whitespace without a new token is ignored`` () =
    let diff = "diff --git \"a/data/old.json\"   \n"

    test <@ parseChangedPaths diff |> List.isEmpty @>

[<Fact>]
let ``quoted old header ending immediately without a new token is ignored`` () =
    let diff = "diff --git \"a/data/old.json\"\n"

    test <@ parseChangedPaths diff |> List.isEmpty @>

[<Fact>]
let ``quoted new header token rejects trailing non-whitespace content`` () =
    let diff = "diff --git \"a/data/old.json\" \"b/data/new.json\"junk\n"

    test <@ parseChangedPaths diff |> List.isEmpty @>

[<Fact>]
let ``quoted new header token permits trailing whitespace`` () =
    let diff = "diff --git \"a/data/old.json\" \"b/data/new.json\"   \n"

    test <@ parseChangedPaths diff = [ "data/old.json"; "data/new.json" ] @>

[<Fact>]
let ``changed paths handle CRLF and preserve first-seen order while deduplicating`` () =
    let diff =
        "diff --git a/data/first.json b/data/first.json\r\n"
        + "diff --git a/data/first.json b/data/renamed.json\r\n"
        + "diff --git a/data/second.json b/data/second.json\r\n"

    test <@ parseChangedPaths diff = [ "data/first.json"; "data/renamed.json"; "data/second.json" ] @>

[<Fact>]
let ``changed paths preserve unquoted spaces in diff header paths`` () =
    let diff =
        "diff --git a/tests/snapshots/api old.json b/tests/snapshots/api new.json\n"

    test <@ parseChangedPaths diff = [ "tests/snapshots/api old.json"; "tests/snapshots/api new.json" ] @>

[<Fact>]
let ``changed paths parse an unquoted old path and C-quoted new path`` () =
    let diff = "diff --git a/data/plain old.json \"b/data/caf\\303\\251 new.json\"\n"

    test <@ parseChangedPaths diff = [ "data/plain old.json"; "data/café new.json" ] @>

[<Fact>]
let ``changed paths parse a C-quoted old path and unquoted new path`` () =
    let diff = "diff --git \"a/data/caf\\303\\251 old.json\" b/data/plain new.json\n"

    test <@ parseChangedPaths diff = [ "data/café old.json"; "data/plain new.json" ] @>

[<Fact>]
let ``unquoted old path may contain a new-path-like segment`` () =
    let diff = "diff --git a/data/x b/y.fs b/data/z.fs\n"

    test <@ parseChangedPaths diff = [ "data/x b/y.fs"; "data/z.fs" ] @>
    test <@ parseChangedFiles diff = [ "data/z.fs" ] @>

[<Fact>]
let ``diff metadata disambiguates a new path containing a new-path-like segment`` () =
    let diff =
        """diff --git a/data/old.fs b/data/x b/y.fs
--- a/data/old.fs
+++ b/data/x b/y.fs"""

    test <@ parseChangedPaths diff = [ "data/old.fs"; "data/x b/y.fs" ] @>
    test <@ parseChangedFiles diff = [ "data/x b/y.fs" ] @>

[<Fact>]
let ``diff metadata disambiguates identical paths containing a new-path-like segment`` () =
    let diff =
        """diff --git a/data/x b/y.fs b/data/x b/y.fs
--- a/data/x b/y.fs
+++ b/data/x b/y.fs"""

    test <@ parseChangedPaths diff = [ "data/x b/y.fs" ] @>
    test <@ parseChangedFiles diff = [ "data/x b/y.fs" ] @>

[<Fact>]
let ``changed files keeps its code-only new-side contract for quoted rename paths`` () =
    let diff =
        "diff --git \"a/src/caf\\303\\251 old.fs\" \"b/src/caf\\303\\251 new.fs\"\n"
        + "similarity index 100%\n"

    test <@ parseChangedFiles diff = [ "src/café new.fs" ] @>
