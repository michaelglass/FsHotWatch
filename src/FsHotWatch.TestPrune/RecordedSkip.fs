/// The zero-affected skip with no session baseline, answered from the box-wide test records
/// (ADR-067, the T' = T step of the AUTOMATION-1587 design).
///
/// A restarted daemon (or any daemon before its first completed run) refuses the
/// zero-affected skip because no run has completed in this session. That refusal was
/// standing in for a second question: has anything verified THIS tree. When every
/// runnable project holds a full-scope PASS record at its K_P, keyed over the tree as
/// `beforeRun` settles it, the records answer that question, and the cycle launches
/// nothing: every project's green is filed from the record that earned it, cited by run
/// and key.
///
/// Every rule here can only WIDEN. No store, a project with no record, a record that does
/// not hold once `beforeRun` has settled the tree, or a hit the shared reuse check
/// (`TestEvidenceRun.reuseRefusal`) refuses, such as a newer fail at a project's K_P: the
/// skip is refused and the cycle runs as it did before.
module FsHotWatch.TestPrune.RecordedSkip

/// A record a skip cites: the project, the run that earned its green, where, and the K_P.
type Citation =
    { Project: string
      RunId: string
      Worktree: string
      Key: string }

module Citation =
    let ofProvenance (project: string) (p: TestEvidenceRun.Provenance) : Citation =
        { Project = project
          RunId = p.RunId
          Worktree = p.Worktree
          Key = p.Key }

    let describe (c: Citation) =
        let key = if c.Key.Length > 12 then c.Key.Substring(0, 12) else c.Key
        $"%s{c.Project} <- run %s{c.RunId} in %s{c.Worktree} under key %s{key}"

    let describeMany (citations: Citation list) =
        citations |> List.map describe |> String.concat "; "

/// Whether every one of `runnable` holds a full-pass record at `plan`'s keys that `plan`
/// reuses: the records the skip would cite, or why there is no skip on them.
///
/// `plan.Reused` already holds only the hits `TestEvidenceRun.reuseRefusal` lets stand
/// (ADR-059 §34): a newer full or impact fail at a project's K_P, or a record there that
/// cannot be read, moved that project to `plan.Misses` with the reason. The skip asks no
/// question of its own, so it can never be laxer than an ordinary reuse.
let decide (runnable: string list) (plan: TestEvidenceRun.Plan option) : Result<Citation list, string> =
    match plan with
    | None -> Error "no test-evidence store is wired, or the project model publishes no identities to key by"
    | Some plan ->
        let missing =
            runnable |> List.filter (fun project -> not (plan.Reused.ContainsKey project))

        if List.isEmpty runnable then
            Error "no runnable project"
        elif not (List.isEmpty missing) then
            missing
            |> List.map (fun project ->
                let why = plan.Misses |> Map.tryFind project |> Option.defaultValue "not looked up"
                $"%s{project}: %s{why}")
            |> String.concat "; "
            |> Error
        else
            runnable
            |> List.map (fun project ->
                Citation.ofProvenance project (TestEvidenceRun.Provenance.ofEntry plan.Reused[project]))
            |> Ok

/// The run's verdict on the skip once it completed: every runnable project's green held
/// by its record through `beforeRun` and the completion re-check (`held`), so nothing
/// launched, or the projects that ran instead.
let settle
    (runnable: string list)
    (held: Map<string, TestEvidenceRun.Provenance>)
    : Result<Citation list, string list> =
    match runnable |> List.filter (fun project -> not (held.ContainsKey project)) with
    | [] ->
        runnable
        |> List.map (fun project -> Citation.ofProvenance project held[project])
        |> Ok
    | ran -> Error ran
