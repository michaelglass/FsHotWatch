module FsHotWatch.Tests.TaskCacheTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.TaskCache

let private fixedTime = DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)

let private makeResult cacheKey =
    { CacheKey = cacheKey
      Errors = []
      Status = Completed(at = fixedTime)
      EmittedEvents = [] }

[<Fact>]
let ``TryGet returns None for unknown key`` () =
    let cache = InMemoryTaskCache()
    let result = cache.TryGet("build--Foo.fs", "hash1")
    test <@ result = None @>

[<Fact>]
let ``Set then TryGet roundtrip`` () =
    let cache = InMemoryTaskCache()
    let expected = makeResult "hash1"
    cache.Set("build--Foo.fs", "hash1", expected)
    let result = cache.TryGet("build--Foo.fs", "hash1")
    test <@ result = Some expected @>

[<Fact>]
let ``TryGet returns None when cacheKey does not match`` () =
    let cache = InMemoryTaskCache()
    let entry = makeResult "hash1"
    cache.Set("build--Foo.fs", "hash1", entry)
    let result = cache.TryGet("build--Foo.fs", "hash2")
    test <@ result = None @>

[<Fact>]
let ``Clear removes all entries`` () =
    let cache = InMemoryTaskCache()
    cache.Set("build--Foo.fs", "h1", makeResult "h1")
    cache.Set("lint--Bar.fs", "h2", makeResult "h2")
    cache.Clear()
    test <@ cache.TryGet("build--Foo.fs", "h1") = None @>
    test <@ cache.TryGet("lint--Bar.fs", "h2") = None @>

[<Fact>]
let ``ClearPlugin removes only that plugin's entries`` () =
    let cache = InMemoryTaskCache()
    let lintResult = makeResult "h3"
    cache.Set("build--Foo.fs", "h1", makeResult "h1")
    cache.Set("build--Bar.fs", "h2", makeResult "h2")
    cache.Set("lint--Foo.fs", "h3", lintResult)
    cache.Set("build", "h4", makeResult "h4")
    cache.ClearPlugin("build")
    test <@ cache.TryGet("build--Foo.fs", "h1") = None @>
    test <@ cache.TryGet("build--Bar.fs", "h2") = None @>
    test <@ cache.TryGet("build", "h4") = None @>
    test <@ cache.TryGet("lint--Foo.fs", "h3") = Some lintResult @>

[<Fact>]
let ``ClearFile removes entries matching the file`` () =
    let cache = InMemoryTaskCache()
    let barResult = makeResult "h3"
    cache.Set("build--Foo.fs", "h1", makeResult "h1")
    cache.Set("lint--Foo.fs", "h2", makeResult "h2")
    cache.Set("build--Bar.fs", "h3", barResult)
    cache.ClearFile("Foo.fs")
    test <@ cache.TryGet("build--Foo.fs", "h1") = None @>
    test <@ cache.TryGet("lint--Foo.fs", "h2") = None @>
    test <@ cache.TryGet("build--Bar.fs", "h3") = Some barResult @>

[<Fact>]
let ``ClearPluginFile removes specific entry`` () =
    let cache = InMemoryTaskCache()
    let barResult = makeResult "h2"
    let lintResult = makeResult "h3"
    cache.Set("build--Foo.fs", "h1", makeResult "h1")
    cache.Set("build--Bar.fs", "h2", barResult)
    cache.Set("lint--Foo.fs", "h3", lintResult)
    cache.ClearPluginFile("build", "Foo.fs")
    test <@ cache.TryGet("build--Foo.fs", "h1") = None @>
    test <@ cache.TryGet("build--Bar.fs", "h2") = Some barResult @>
    test <@ cache.TryGet("lint--Foo.fs", "h3") = Some lintResult @>
