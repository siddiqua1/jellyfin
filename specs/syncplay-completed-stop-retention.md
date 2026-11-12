# Preserve completed recovery stops across Waiting reports

## Problem Statement

A viewer who has finished catching up can still restart the group's wait by reporting that completion before another viewer finishes loading. Their first paused Buffer or Ready report is accepted while Waiting, but deletes the recognition of their completed recovery stop. If they repeat a pause-induced Buffer report after the group schedules playback, they become a blocking loader again and cancel the valid start. A player that remains silent while paused may then leave everyone waiting until the group-wait timeout.

Review of commit 634eacab2c60865af70036895242cba977db564a reproduced this remaining defect with both initial report types. The commit fixes the earlier resume-delivery race and asymmetric position tolerance. It also records stops completed while Waiting, but the natural-completion path still invalidates those records before resume preparation can carry them forward.

## Solution

Distinguish finishing a session's advancing recovery from invalidating its recovery-stop plan. A valid completion report should remove the obsolete advancing deadline without discarding recognition of the stop at which the group still expects that viewer to wait.

Carry that recognition through the next scheduled start. Repeated reports from the same valid stop must preserve the viewer's nonblocking readiness and effective resume, regardless of whether their first report arrived while Waiting or after the start was scheduled. Explicit controls, invalid timelines, departure, and genuine new loading retain their existing invalidation and wait behavior.

## User Stories

1. As a recovering viewer, I want to finish watching the content I missed before stopping at the target, so that recovery preserves continuity without letting me watch ahead.
2. As a viewer who has reached a planned stop, I want my completed advancing deadline removed, so that a finished recovery does not unnecessarily delay the group.
3. As a paused viewer at that stop, I want my Buffer report accepted while another viewer is still loading, so that reporting completion does not erase my valid playback plan.
4. As a paused viewer at that stop, I want my Ready report accepted with the same lifecycle behavior, so that a different player event does not make synchronization less reliable.
5. As a viewer whose Buffer report is handled through synthetic Ready processing, I want that forwarding to preserve my stop recognition, so that an internal dispatch choice cannot invalidate a successful recovery.
6. As a group member waiting for another loader, I want a completed recovering viewer to remain nonblocking, so that only genuinely unfinished loading controls the wait.
7. As a loading viewer becoming ready last, I want stopped members included in the next scheduled resume, so that everyone who should follow the group starts together.
8. As a stopped viewer whose player repeats Buffer before that start, I want the existing resume retained, so that the duplicate does not create a new group wait.
9. As a stopped viewer whose player repeats Ready before that start, I want the current playback plan respected, so that resynchronization does not undo a successful recovery.
10. As a viewer whose player emits several pause-induced reports, I want the effective result to remain stable, so that event duplication does not create repeated pauses or seeks.
11. As a group member, I want repeated reports accepted without another Ready being required afterward, so that a silent paused player cannot stall the group.
12. As a viewer with a future resume already delivered, I want the periodic sweep to preserve the same recognition, so that delivery bookkeeping and natural completion cannot invalidate each other.
13. As a group member joining during an existing wait, I want valid completed stops carried into the resulting start, so that waiting for a joiner does not strand an already-ready viewer.
14. As a member of a group with several loading viewers, I want each completed stop tracked independently, so that one member's completion cannot alter another member's recovery.
15. As a viewer with network delay, I want the existing latency allowance retained when the group starts, so that this lifecycle fix does not reduce synchronization safety.
16. As a user of a client with one pending playback command, I want my valid resume to remain effective, so that a later unnecessary Pause cannot replace it.
17. As a group member, I want current-item and symmetric position validation retained, so that stale or off-target reports cannot masquerade as valid completed stops.
18. As a viewer genuinely loading after resumption, I want ordinary buffering behavior restored, so that historical recognition cannot hide a new stall.
19. As a group member pressing Pause or Stop, I want the old recovery plan superseded, so that retaining completion context cannot resurrect unwanted playback.
20. As a group member seeking or changing the playing item, I want old target recognition invalidated, so that completed stops from another timeline cannot affect the new one.
21. As a viewer leaving or changing ignore-wait behavior, I want the resulting membership and wait policy respected, so that retained recognition does not preserve obsolete playback obligations.
22. As a group member, I want genuine loading timeout behavior preserved, so that avoiding a false wait does not create an unbounded real wait.
23. As a user of an existing SyncPlay client, I want this correction within the established command contract, so that a coordinated client upgrade is unnecessary.
24. As a maintainer, I want both Buffer-first and Ready-first sequences tested through real group transitions, so that internal record assertions cannot conceal an incorrect user-visible start.

## Implementation Decisions

- Keep lifecycle ownership in the existing SyncPlay Group module. The Waiting state identifies a successful completion; the Group implementation decides which advancing deadline, stop recognition, and resume obligations remain valid. Keep that policy local rather than having callers manipulate bookkeeping independently.
- Make natural completion distinct from cancellation or invalidation at the module's interface. A completion operation, or an equivalently explicit lifecycle intent, must remove the advancing recovery without automatically removing a matching valid stop. Choose the smallest interface change that expresses this distinction.
- Apply this distinction to both the accepted paused Buffer path and direct paused Ready processing. Buffer processing forwards through Ready, so preserving recognition in only the outer handler is insufficient if the inner handler still invalidates it.
- Preserve stop recognition only when it still belongs to the session's valid current playback plan. Existing playlist-item, target-position, accepted-tolerance, membership, and wait-policy checks remain authoritative. An arbitrary paused Ready must not manufacture recognition without a matching stop plan.
- While other members remain buffering, the completed member stays nonblocking and its advancing deadline no longer delays the group. Existing necessary commands that hold it at the target may continue; successful completion must not cause it to run ahead while others load.
- When the group schedules playback, carry the retained stop into the new start using the existing resume preparation. Deliver or retain the correct resume for that session. A valid duplicate report before the start must preserve the Playing state, scheduled time, readiness, and effective per-session command plan.
- Continue to distinguish command delivery from expected execution. Natural completion, resume delivery, repeated reports, and repeated sweep processing must not discard recognition while the current plan still expects the member paused at the stop.
- Preserve full invalidation for actual supersession: explicit playback controls, incompatible target/item changes, departure, relevant wait-policy changes, and genuine recovery replacement. The fix must not weaken every retirement call or indefinitely retain stale recognition.
- Once the current plan no longer expects the session paused at that stop, preserve normal handling of new loading. Keep symmetric inclusive position tolerance, stale-item validation, correction-loop protections, timestamp compensation, latency allowance, and group-wait timeout behavior unchanged.
- Retain existing client command replacement semantics and request/command formats. No new acknowledgment, persistent storage, protocol version, or client upgrade is needed.

## Testing Decisions

### Confirmed seam and prior art

Reuse the previously user-confirmed primary seam: public Group actions and Group.HandleRequest dispatch, real state transitions, and per-recipient commands captured through the mocked ISessionManager adapter. Use the existing harness's production-sweep driver where relevant. No new caller-facing test seam is proposed.

Prior art is WaitingGroupStateTests with GroupHarness, StartPlayingGroup, and PausedBufferAtAStopCompletedWhileWaiting_PreservesTheStartPlan. That test currently sends only the delayed first Buffer after playback has been scheduled. Extend the behavior it covers to include the initial accepted completion report while Waiting, followed by another report before the new start.

Good tests assert the group's observable state and buffering behavior, the agreed start, and the effective commands for each recipient. Exercise the full request route, including Buffer-to-Ready forwarding. Internal dictionaries, helper call counts, and field names are not acceptance criteria. A retained flag alone is insufficient if a later command still replaces the valid resume.

### Required regression cases

Parameterize the following sequence by the initial completion report, producing two mandatory cases:

1. **C1 — Buffer completion while Waiting.** The first completion report is paused Buffer at the current target and playlist item; it follows the real synthetic Ready route.
2. **C2 — Ready completion while Waiting.** The first completion report is direct paused Ready at the same target and playlist item.

For each case:

1. Establish a Playing group with sessions A and B through public actions.
2. Make A report buffering and then Ready while still playing approximately 300 milliseconds behind the frozen target, creating an advancing recovery.
3. Before A completes, make B report genuine buffering. Verify the group enters Waiting and A receives its scheduled stop at the target.
4. Let that stop deadline pass while B remains buffering. The review reproductions used a bounded 450-millisecond delay and checked that the stop time had passed; these durations reproduce the defect rather than define new product constants.
5. Dispatch A's initial paused completion report according to C1 or C2. Verify the group still waits for B and A is not itself a blocking loader. Completing A's advancing recovery must not destroy its valid stop context.
6. Dispatch B's Ready report at the target. Verify the group enters Playing, schedules a future start with the established latency allowance, and includes A in the effective resume plan. A's already completed deadline must not unnecessarily delay the start.
7. Before that start, dispatch another paused Buffer from A at the same target and playlist item. Supply no additional Ready after this duplicate.
8. Verify the group remains Playing and unbuffered with the same scheduled start. A's resume remains effective; no replacement Pause, Seek, or new wait cancels it. Other ready members' commands also remain consistent with that start.

At the reviewed revision, both variants fail step 8 with expected Playing and actual Waiting. The first completion report removes the stop record, so the later report cannot be recognized during Playing.

### Supporting verification

- Repeat the valid completion report while still Waiting, and repeat valid Buffer/Ready reports after scheduling the start. Verify stable readiness and effective playback commands rather than a single successful first report.
- Exercise the existing sweep around resume delivery and repeat it to preserve the earlier fix: sending a future Unpause must not end recognition prematurely.
- Check a third member or joiner delaying/replanning the group start after A's accepted completion. Carry forward only the stop recognition valid under that plan; keep other sessions' recoveries independent.
- Retain negative cases for playing Buffer reports, no matching stop plan, wrong playlist item, and positions outside either side of the tolerance. Preserve inclusive tolerance edges and safe handling of extreme positions.
- Verify a new genuine Buffer after expected resumption follows normal group-wait handling. Preserve behavior when Pause, Stop, Seek, playing-item/queue replacement, departure, or ignore-wait changes supersede the plan.
- Retain the earlier before/after-sweep, natural-expiry, high-ping, command-resynchronization, overlapping-recovery, membership, and timeout regression coverage.
- Extend the existing multi-client smoke procedure with completion reported while another viewer still loads, followed by a repeated pause-induced report after the new start is scheduled. Record observed playback separately from simulated server request ordering.

### Completion criteria

- C1 and C2 fail against the reviewed behavior and pass after the implementation through the confirmed Group seam.
- Both completion routes remove the obsolete advancing deadline while preserving a valid stopped/awaiting-resume plan; the assertions demonstrate the resulting state and effective command delivery.
- Repeated reports before the scheduled start do not restart Waiting, require another Ready, alter the start, or cancel a valid resume.
- Invalid or superseded plans and genuine loading retain their normal handling. Earlier recognition, tolerance, scheduler, and lifecycle regression coverage remains green.
- Run the relevant implementation and SyncPlay integration suites; report executed tests, skips, and environmental limitations. Record live multi-client outcomes if available, otherwise leave that verification explicitly pending.

## Out of Scope

- Replacing the recovery scheduler, redesigning the entire state machine, or changing synchronization tolerances.
- Reopening already-fixed resume-delivery and position-range defects except to preserve their regression coverage.
- New client messages, acknowledgments, settings, or coordinated client upgrades.
- Mandatory cleanup of the review's three nonblocking naming, sentinel, and test-duplication judgements.
- Unrelated historical playback-control behavior, deployment changes, dependency upgrades, or repository settings.
- Declaring playback seamless across clients based solely on server tests.

## Further Notes

- Baseline: [634eacab2c60865af70036895242cba977db564a](https://github.com/siddiqua1/jellyfin/commit/634eacab2c60865af70036895242cba977db564a), reviewed against its parent [1eda314cd23c30f5f90e01384434523a943aa067](https://github.com/siddiqua1/jellyfin/commit/1eda314cd23c30f5f90e01384434523a943aa067).
- This specification supplements the [recovery-stop recognition requirements](<specs/syncplay-recovery-stop-recognition.md>). The earlier [command-plan requirements](<specs/syncplay-recovery-command-plan.md>) and [catch-up lifecycle requirements](<specs/syncplay-catch-up-lifecycle.md>) remain applicable; this follow-up narrows the remaining natural-completion gap.
- Review evidence: the existing implementation suite and selected SyncPlay integration tests passed. Both additional completion-report variants failed at the final state assertion: expected Playing, actual Waiting. These are recorded review results, not tests rerun while drafting this specification.
- The optional [reviewer regression patch](<.tmp-web/review-634e-regressions.patch>) contains both variants. It is an untracked local artifact; the complete reproduction and expected behavior above make the handoff independent of that patch and this conversation.
- No live multi-client playback was observed during the review. Use the established Group, session, recovery, readiness, playlist-item, and scheduled-command vocabulary; no project glossary or relevant ADR was found during the preceding exploration.
- Intended tracker: siddiqua1/jellyfin on GitHub. Intended triage label: ready-for-agent. Issues remain disabled. Per the user's established choice, save this issue-ready specification locally and leave repository settings and labels unchanged.
