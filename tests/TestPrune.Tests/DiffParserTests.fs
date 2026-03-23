module TestPrune.Tests.DiffParserTests

open Xunit
open Swensen.Unquote
open TestPrune.DiffParser

[<Fact>]
let ``empty diff returns empty list`` () =
    test <@ parseChangedFiles "" |> List.isEmpty @>

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
