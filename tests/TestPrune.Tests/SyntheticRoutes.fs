namespace TestPrune.Tests.SyntheticRoutes

open Falco.UnionRoutes

// A synthetic Falco.UnionRoutes route DU exercising the hard patterns for the AST-vs-reflection
// head-to-head. Compiled here for the reflection resolver; the IDENTICAL
// source text is read back off disk (copied next to the test dll) for the AST resolver, so there
// is zero drift between the two inputs.
//
// Pattern coverage per case:
//   List         explicit path, no param                      (expect AGREE)
//   Show         explicit path with a BARE {id} + Guid field  (reflection adds :guid -> DIFFER)
//   ByNumber     explicit path with an EXPLICIT {id:int}       (AST keeps it verbatim -> AGREE)
//   ProfileCard  NO path, Guid param -> field-inferred segment (reflection adds :guid -> DIFFER)
//   Settings     explicit path, no param                      (AGREE)
//   HealthScores NO path, no field -> kebab fallback           (AGREE)
//   Members      empty-segment convention name (List) + param  (reflection adds :guid -> DIFFER)
//   Health       top-level leaf, explicit path                (AGREE)
// Plus deep nesting (Route -> Admin -> AdminPages -> Users -> AdminUsers -> leaf) and a
// multi-parent shared leaf (AdminUsers reachable under both Admin.Users and SharedUsers).

type AdminUsers =
    | [<Route(RouteMethod.Get, Path = "users")>] List
    | [<Route(RouteMethod.Get, Path = "users/{id}")>] Show of id: System.Guid
    | [<Route(RouteMethod.Get, Path = "users/{id:int}")>] ByNumber of id: int
    | ProfileCard of cardId: System.Guid

type AdminPages =
    | [<Route(RouteMethod.Get, Path = "settings")>] Settings
    | HealthScores
    | [<Route(RouteMethod.Get, Path = "")>] Users of AdminUsers
    | Show of id: System.Guid

type Route =
    | [<Route(Path = "admin")>] Admin of PreCondition<System.Guid> * AdminPages
    | [<Route(Path = "shared")>] SharedUsers of AdminUsers
    | [<Route(RouteMethod.Get, Path = "health")>] Health
