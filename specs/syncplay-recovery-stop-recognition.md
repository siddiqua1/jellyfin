# Preserve SyncPlay recovery-stop recognition through scheduled resumption

## Problem Statement

A viewer who has correctly stopped at the group's recovery target can still make everyone wait again simply because their pause-induced buffering report arrives after the server has sent their future resume command. The group can then stall until its wait timeout because that paused viewer may never send another Ready report.

The current implementation also treats paused buffering reports arbitrarily far ahead of the recovery target as if they were valid planned stops. A genuinely off-target session can remain marked ready instead of following normal buffering and correction handling.

Review of commit 1eda314cd23c30f5f90e01384434523a943aa067 verified two defects:

1. Planned-stop recognition depends on the deferred-resume queue entry. The periodic sweep removes that entry when it sends a future Unpause, before playback actually resumes. A delayed Buffer report from the completed pause/seek consequently restarts Waiting and cancels the valid group start.
2. The in-position check has only a lower bound. A paused report two seconds ahead of the target is accepted even though it exceeds the existing 500-millisecond position tolerance.

Both public-dispatch reproductions fail at the reviewed revision. The earlier six command-plan regression cases and the existing suites pass, so this is a focused follow-up rather than a replacement for the previous catch-up specifications.

## Solution

Recognize a session's valid recovery stop throughout the period in which its current playback plan expects it to wait at the target, including after its future resume has been sent. Sending that command is not evidence that its scheduled start has happened.

Apply the same bounded position tolerance on both sides of the target. Only a paused, in-position report belonging to the current recovery-stop plan receives the exemption from starting a new wait. Real loading, off-target reports, stale playlist items, and buffering after the plan has resumed or been superseded retain their normal handling.

## User Stories

1. As a recovering viewer, I want to watch the content I missed and stop at the group target, so that recovery does not skip content or let me watch ahead.
2. As a viewer waiting at a planned recovery stop, I want my valid pause-induced Buffer report accepted before the resume sweep, so that the existing successful event order remains reliable.
3. As a viewer waiting at a planned recovery stop, I want the same report accepted after the sweep sends my future resume, so that network and timer ordering cannot restart the group wait.
4. As a group member, I want the server to distinguish sending a resume from reaching its scheduled start, so that an already delivered command does not prematurely discard valid stop context.
5. As a paused viewer at the target, I want to resume without another Ready report, so that a player that stays silent while paused cannot stall the group.
6. As a group member, I want an accepted recovery-stop report to preserve the agreed start time, so that incidental player events do not unnecessarily delay everyone.
7. As a viewer whose player repeats the same pause-induced report, I want those repeats to preserve the current plan, so that duplicated events do not create repeated waits or commands.
8. As a viewer whose resume is delivered by a periodic sweep, I want repeated sweeps to preserve the effective playback plan, so that background processing does not cause repeated restarts or seeks.
9. As a paused viewer slightly behind the recovery target within tolerance, I want the accepted position tolerance applied consistently, so that normal player imprecision does not create an unnecessary wait.
10. As a paused viewer slightly ahead of the recovery target within tolerance, I want the same accepted tolerance applied, so that validation is symmetric around the target.
11. As a group member, I want a paused buffering report beyond the target's upper tolerance handled as off-target, so that an ahead session is not silently declared ready.
12. As a group member, I want a paused buffering report below the target's lower tolerance handled as off-target, so that the existing behind-target protection remains effective.
13. As a viewer whose report names a previous playlist item, I want the current item validated, so that an old stop cannot excuse buffering on another timeline.
14. As a viewer genuinely buffering while playing, I want normal group-wait behavior retained, so that the planned-stop exemption does not hide real loading.
15. As a group member without a matching recovery-stop plan, I want ordinary Buffer reports handled normally, so that the fix does not become a blanket exception for paused players.
16. As a viewer whose planned start has passed, I want new buffering reevaluated normally, so that historical stop context cannot indefinitely suppress a real loading event.
17. As a group member pressing Pause or Stop, I want the previous recovery-stop plan superseded consistently, so that old recognition context cannot conflict with my playback control.
18. As a group member seeking or changing the playing item, I want stop recognition tied to the new timeline, so that old target positions cannot affect later playback.
19. As a joining or remaining viewer, I want membership changes to preserve only the stop context that remains valid, so that joining or leaving does not strand another member or reuse a departed session's plan.
20. As a group member changing ignore-wait behavior, I want the resulting wait policy respected, so that recognition context does not outlive the plan it belongs to.
21. As a member of a group with three or more sessions, I want stop recognition maintained per session, so that one viewer's sweep or report cannot corrupt another viewer's recovery.
22. As a viewer with network delay, I want existing time compensation and resume delivery allowance preserved, so that this correctness fix does not change synchronization tolerances unexpectedly.
23. As a group member waiting for a genuinely stalled viewer, I want the existing group-wait timeout retained, so that preserving planned stops does not introduce an unbounded wait.
24. As a user of an existing SyncPlay client, I want this correction to use the established command contract, so that a coordinated client upgrade is unnecessary.
25. As a maintainer, I want tests that cover both report/sweep orders and both position bounds, so that a passing example cannot hide the opposite ordering or range error.
26. As a viewer evaluating the fix, I want automated server evidence distinguished from observed playback, so that a seamless-experience claim reflects actual verification.

## Implementation Decisions

- Keep recognition behavior within the existing SyncPlay Group module and its state-machine implementation. Preserve the current request-dispatch interface, session-manager adapter, scheduled-command contract, and periodic sweep.
- Separate recognition of a valid completed/planned recovery stop from the queue of resumes awaiting delivery. The latter may become empty while the former is still relevant. Choose the internal representation for locality; callers should not coordinate these facts themselves.
- Retain enough per-session context to associate recognition with the current playlist item, target position, and playback plan. A queue lookup alone does not establish that a report belongs to a valid stop.
- Keep recognition available while the current plan expects the session to be paused awaiting its scheduled resumption. The current scheduled start, rather than command transmission, is the server's reference for expected resumption. Repeated reports and sweeps must leave that valid plan intact.
- Retire or recompute recognition when the playback plan is genuinely superseded: explicit controls, target/item changes, departure, membership replanning, and ignore-wait changes must remain consistent with existing behavior. Once the plan no longer expects the session to wait at that stop, ordinary buffering must not be suppressed by historical context.
- Accept a paused position only when its distance from the matching target is within the existing position tolerance on both sides. Preserve the current inclusive tolerance edges and position-sanitization conventions; compare malformed or extreme tick values safely. No new tolerance constant or client setting is needed.
- Require the matching current playlist item and stopped/awaiting-resume context for the exemption. Playing reports, off-target positions, stale items, and reports without a valid matching plan must continue through normal buffering/validation handling.
- An accepted pause-induced report must preserve the scheduled group start, the session's nonblocking readiness, and its valid resume. It must not trigger an unsolicited seek or require another Ready report from a player that remains paused.
- Preserve existing client command replacement semantics, deferred stop/resume ordering, latency allowances, correction-loop protections, genuine buffering waits, and timeout behavior. The fix is recognition and validation, not a new playback scheduler.
- Keep the Group module deep and the recognition policy coherent in one place. The review's stop-selection, conversion, and fixture duplication concerns remain nonblocking cleanup guidance rather than a requirement for a broader refactor.

## Testing Decisions

### Confirmed seam and prior art

Reuse the user-confirmed primary seam: public Group actions and Group.HandleRequest dispatch, with real group states and captured commands through the mocked ISessionManager adapter. Use the existing harness's production-sweep driver to reproduce time-driven delivery. No new caller-facing seam is proposed.

WaitingGroupStateTests, GroupHarness, StartCatchUpRecovery, and StartShortRecovery provide the existing setup. The relevant positive prior art is the paused Buffer report processed before the sweep. Existing negative tests already cover behind-target, playing, and stale-item reports; extend them rather than creating a separate isolated predicate-test suite.

Good tests assert externally observable group state, readiness, the scheduled start, and effective per-recipient command delivery and replacement. They must establish that a necessary stop remains effective and that the valid resume is still delivered or remains scheduled. Internal dictionary membership, field names, and helper invocation counts are not acceptance criteria.

### Required regression cases

1. **R1 — Delayed pause-induced Buffer after resume delivery, before group start.** Establish A's five-second recovery and B's 300-millisecond recovery to the same frozen position. Let B's scheduled stop take effect while the group start remains in the future. Run the periodic sweep first and verify B receives its future Unpause for that start. Then dispatch B's paused Buffer report at the target for the current playlist item. Verify the group stays Playing with the same scheduled start, B does not become a blocking loader, and no replacement pause, seek, or wait cancels the valid resume. No additional Ready report is supplied. At the reviewed revision, the actual state is Waiting because sending the resume removed the recognition marker.
2. **R2 — Paused buffering ahead of the target beyond tolerance.** Establish the same overlapping recovery plan, leaving B's deferred resume outstanding. Dispatch B's paused Buffer report two seconds ahead of the target for the current playlist item. Verify it follows normal off-target buffering/validation handling, enters Waiting as the existing behind-target negative case does, and is not silently retained as a ready planned-stop session. At the reviewed revision, the group incorrectly stays Playing and unbuffered because the position predicate has no upper bound.

The time values above reproduce the defects; they are not new product constants. Permanent tests may use the existing time controls or a narrow internal clock adapter where necessary. The review reproductions used a bounded 450-millisecond delay to establish that the short stop had passed.

### Supporting verification

- Cover both event orders: Buffer before the sweep and Buffer after the sweep, always before the still-valid scheduled start. Repeat Buffer and sweep processing to establish a stable effective plan without requiring a new Ready report.
- Exercise positions at the target, exactly at each tolerance edge, immediately outside each edge, and materially ahead/behind. Check symmetric inclusive acceptance only when valid stop context exists. Include malformed/extreme positions if needed to verify safe comparison under the established sanitization rules.
- Retain negative cases for a playing Buffer report, wrong playlist item, paused behind-target loading, and a session with no matching recovery-stop plan. The new recognition state must not turn these into successful recovery reports.
- After the plan's expected resumption, verify a new genuine Buffer report uses normal wait handling. Cover Pause, Stop, Seek, item/queue replacement, member departure, and wait-policy changes so obsolete recognition cannot excuse later buffering or resurrect an old resume.
- Exercise a third member's unrelated change while B waits at the target, and separate cases where B itself leaves or changes ignore-wait behavior. Preserve only obligations valid under the resulting plan.
- Preserve the earlier six command-plan regression cases, the original catch-up regression cases, position correction/timestamp compensation, and group-wait timeout coverage.
- Extend the existing multi-client smoke procedure with a run in which the resume is sent before the pause-induced report reaches the server, and check that no timeout wait appears. Record observed playback separately from simulated request ordering. Controlled off-target reports can be verified through the behavioral test seam where a normal player cannot reproduce them safely.

### Completion criteria

- R1 and R2 fail against the reviewed behavior and pass after the implementation through the confirmed Group seam.
- Both report/sweep orders and both tolerance bounds have behavioral coverage; accepted reports preserve the actual effective resume plan, not just an internal flag or timestamp.
- Genuine buffering and superseded plans follow their normal handling. No retained recognition suppresses a later real wait or permits obsolete playback work.
- Earlier regression coverage and the relevant implementation/integration suites remain green. Report exact executed tests, skips, and any environmental failures.
- Live playback outcomes are recorded if a runnable deployment is available. Otherwise leave live multi-client verification explicitly pending; server-command tests alone do not establish a seamless experience.

## Out of Scope

- A new SyncPlay protocol, client acknowledgment requirement, new request types, or coordinated client upgrade.
- Replacing the recovery scheduler, redesigning the entire state machine, or expanding synchronization tolerance.
- Broad code-style cleanup or mandatory resolution of the review's three nonblocking duplication judgements.
- The bundled-web users-query workaround, Docker/Compose changes, dependency upgrades, and repository settings.
- Unrelated preexisting behavior not needed to correct the two reviewed defects.
- Claims of identical playback behavior across clients without corresponding observation.

## Further Notes

- Reviewed revision: [1eda314cd23c30f5f90e01384434523a943aa067](https://github.com/siddiqua1/jellyfin/commit/1eda314cd23c30f5f90e01384434523a943aa067), compared with [d38bed5b576642c39843e42ad4f64f8d64b520c5](https://github.com/siddiqua1/jellyfin/commit/d38bed5b576642c39843e42ad4f64f8d64b520c5). This follow-up preserves the earlier catch-up lifecycle and recovery command-plan requirements.
- Recorded review evidence: 1,129 implementation tests passed, 12 were skipped, and all three selected SyncPlay integration tests passed. Both added regression cases failed for the state-transition reasons described above. These are review results, not a new test run performed while drafting this document.
- The optional [reviewer regression patch](<.tmp-web/review-1eda-regressions.patch>) contains both reproductions. It is an untracked local artifact; the complete scenarios are included above so the implementation handoff does not depend on that artifact or this conversation.
- No live multi-client playback run was performed. No project glossary or relevant ADR was found; terminology follows the existing Group, session, recovery, readiness, playlist-item, and scheduled-command model.
- Intended tracker: siddiqua1/jellyfin on GitHub. Intended triage label: ready-for-agent. Issues remain disabled. Following the user's earlier choice, save this issue-ready specification locally and leave repository settings and labels unchanged.
