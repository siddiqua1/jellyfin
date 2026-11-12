# Fix SyncPlay recovery command-plan consistency

## Problem Statement

Viewers can still watch ahead, remain paused while everyone else resumes, or wait for the group timeout after a normal recovery stop. A user pressing Pause while another member buffers can also find that a recovering viewer keeps playing for several seconds.

The catch-up lifecycle implementation improves the original four regression scenarios, but its recovery records, deferred resumes, and client commands do not remain consistent across all production event handlers. Review of commit d38bed5b576642c39843e42ad4f64f8d64b520c5 verified five remaining defects:

1. Explicit Pause while Waiting clears recovery records without sending an immediate replacement for the recovering session's future stop.
2. Duplicate Ready reports and Playing-state Unpause requests send a future Unpause that replaces a shorter recovery's pending Pause.
3. A recovery shorter than twice the highest ping is not tracked, so its necessary stop can be replaced when another recovery takes longer.
4. A scheduled recovery Pause can cause a paused, in-position Buffer report while the group is logically Playing before its scheduled start. That report restarts Waiting and cancels the valid resume plan.
5. Natural recovery expiry can prune the deferred resume still owed to a session that has stopped at the target. An unrelated member leaving before the next sweep can trigger this deletion and leave that session paused.

Six added test cases reproduce these five defects; both Ready and Unpause variants of defect 2 fail. Existing passing tests do not cover these combinations of events. This is a corrective follow-up, not a replacement for the original catch-up lifecycle requirements.

## Solution

Maintain one coherent playback command plan for each affected session throughout recovery, waiting at the target, and scheduled resumption. Every normal request, playback control, membership change, and periodic sweep must respect that plan.

A viewer finishing recovery early should stop at the frozen group position and resume with the group. Repeated reports should preserve that stop, natural completion should preserve any resume still owed, and a pause-induced Buffer report should not create a new loading stall. Explicit Pause must interrupt recovery immediately, including while the group is Waiting. Keep the existing clients and command contract compatible, preserve valid latency allowances, and retain genuine buffering and timeout behavior.

## User Stories

1. As a group member, I want Pause to stop recovering viewers while another member is buffering, so that my playback control takes effect immediately.
2. As a recovering viewer, I want an explicit Pause to replace my previously scheduled recovery stop, so that I do not keep playing until an obsolete deadline.
3. As a group member, I want pending recovery resumes cancelled when I explicitly pause, so that playback does not restart against my intent.
4. As a group member, I want Stop to supersede every outstanding recovery action, so that stopped playback stays stopped.
5. As a group member, I want Seek and playing-item changes to replace the previous timeline's command plan, so that old recovery work cannot move me to the wrong position or item.
6. As a recovering viewer, I want repeated Ready reports to preserve my necessary stop at the group position, so that reporting readiness twice does not let me run ahead.
7. As a viewer requesting the current Playing state, I want the response to respect my recovery phase, so that resynchronization does not cancel a pending stop.
8. As a viewer whose recovery finishes first, I want to wait at the group position for longer recoveries, so that everyone resumes aligned.
9. As a viewer whose recovery takes longer, I want shorter recoveries to retain their stops, so that they do not watch the next several seconds without me.
10. As a viewer with higher network latency, I want a short recovery to remain represented in the playback plan, so that the transport allowance does not erase my need to stop at the target.
11. As a group member, I want the normal latency allowance retained when playback resumes, so that commands have time to reach participants.
12. As a viewer stopped at the recovery target, I want pause-induced buffering to be recognized in the context of that planned stop, so that the group does not restart a loading wait unnecessarily.
13. As a paused viewer at the recovery target, I want my valid resume to remain scheduled without another Ready report, so that a player that stays silent while paused cannot stall the group.
14. As a viewer genuinely loading behind the group, I want the server to continue waiting for me according to group policy, so that the paused-buffer workaround does not incorrectly declare me ready.
15. As a viewer who has reached the target, I want natural recovery expiry to preserve any resume still owed to me, so that I am not left paused while others continue.
16. As a group member, I want an unrelated member leaving between my recovery stop and the next sweep to leave my valid resume intact, so that membership bookkeeping cannot strand me.
17. As a joining viewer, I want the group to replan any necessary wait while retaining valid playback obligations, so that joining does not lose another member's eventual resume.
18. As a group member changing ignore-wait behavior, I want only obsolete wait and recovery obligations removed, so that other members' valid plans survive the change.
19. As a remaining viewer, I want departed sessions' deferred actions retired, so that stale work is not delivered after they leave.
20. As a group member, I want overlapping recovery to work with three or more sessions, so that correctness is not limited to a two-person sequence.
21. As a viewer interrupted repeatedly by buffering, I want each replacement plan to preserve the content I still need to recover, so that intermittent loading does not repeatedly skip content.
22. As a group member, I want stale reports for an old playlist item rejected, so that they cannot overwrite the current timeline's command plan.
23. As a viewer whose resume is handled by a periodic sweep, I want delivery to remain bounded by the scheduling mechanism, so that finishing recovery does not introduce an indefinite pause.
24. As a group member, I want duplicate sweep processing to leave the effective playback plan unchanged, so that background processing does not cause repeated seeks or restarts.
25. As a group member, I want a permanently stalled session to remain subject to the existing group-wait timeout, so that preserving recovery does not create an unbounded wait.
26. As a user of an existing SyncPlay client, I want this fix to use the established command contract, so that I do not need a coordinated client upgrade.
27. As a maintainer, I want tests that model the client's single pending playback command, so that individually reasonable timestamps cannot hide an invalid command sequence.
28. As a viewer evaluating this fix, I want server-test results distinguished from observed multi-client playback, so that a seamless-experience claim reflects actual verification.

## Implementation Decisions

- Keep command-plan behavior within the existing SyncPlay Group module and its state-machine implementation. Reuse the existing request-dispatch interface, session-manager adapter, and periodic sweep. Keep recovery bookkeeping and command replacement localized rather than adding new caller responsibilities.
- Treat advancing toward the target, having stopped at the target, and still being owed a resume as distinct facts. Natural completion of the first phase does not discharge the remaining command-delivery obligation. Internal types and storage are implementation choices; dictionary contents are not part of the acceptance contract.
- Respect existing client semantics: receiving a new playback command replaces the previously scheduled command immediately, irrespective of which command has the later execution time. Sending a future Unpause before a necessary Pause has taken effect can erase that stop. Preserve the effective sequence, not merely individually valid timestamps.
- Route normal Playing-state responses through the same command-plan decisions as initial recovery scheduling. Ready repeats and state-resynchronizing Unpause requests must retain or safely refresh a pending recovery stop. Preserve the distinction between those requests and the existing explicit force-start behavior while Waiting.
- An explicit Pause while Waiting must issue the playback command needed to stop recovering sessions promptly, supersede their old future stops, and retire incompatible deferred resumes. It must not wait for a buffering member to report Ready before affecting sessions that are still playing.
- Keep recovery progress and transport allowance independent. Ping determines sufficient command-delivery allowance; it must not erase a shorter recovery's required stop when another session still determines a later group start. Preserve existing position tolerance and timestamp compensation without allowing them to invalidate the effective command sequence.
- Recognize paused, in-position Buffer reports associated with the current plan's scheduled recovery stops, including before a future start while group state is Playing. Such a report must not by itself restart Waiting, discard a valid resume, or require a silent paused player to produce another Ready report. Preserve validation and real waiting for genuine loading, behind-target positions, and incorrect playlist items; a blanket acceptance of all paused Buffer reports is not the intended fix.
- Retain a valid deferred resume when its recovery deadline naturally passes. Queries or pruning triggered by unrelated membership or wait-policy events must not silently delete that delivery obligation. Continue to cancel obsolete work when a session leaves or the playback timeline, user intent, or wait policy genuinely invalidates it.
- Recompute affected plans consistently across Playing, Waiting, Paused, and Idle, including membership changes, ignore-wait changes, user controls, readiness reports, and sweeps. Keep the original protections for multiple active recoveries, bounded correction loops, late reports, stale playlist items, and group-wait timeout.
- Keep sweep-driven delivery bounded by the existing scheduling mechanism and safe when an entry is processed late or more than once. A sweep must not restart a paused/stopped group or apply a resume belonging to an obsolete timeline.
- Keep the Group module deep. Consolidate shared recovery-validity and wait-command-plan decisions where the fixes require them. Expose operations to state handlers only when they genuinely need them; Group-only helpers remain implementation details. Broad style cleanup is not a completion requirement.

## Testing Decisions

### Confirmed seam and prior art

Reuse the user-confirmed primary seam: public Group actions and Group.HandleRequest dispatch, with real group states and captured outbound commands through the existing mocked ISessionManager adapter. The seam has not changed, so this follow-up requires no new caller-facing test interface.

Use existing WaitingGroupStateTests, GroupHarness, StartCatchUpRecovery, and the harness's production-sweep driver as prior art. Add committed behavioral regression tests alongside the existing coverage. Exercise actual request routing rather than choosing a state handler and inventing its previous state.

Good tests assert observable group state and the effective per-recipient command sequence: command type, playlist item, target position, scheduling time, replacement order, and eventual delivery. For each session, account for the fact that a newly delivered command replaces its pending playback command. Assertions on private recovery fields, collection sizes, or helper invocation counts are not acceptance evidence.

### Required regressions: five scenarios, six cases

Use a valid shared playlist item and a frozen group position. Small accepted pings can be used except where higher ping is the subject. Timing values below are reproduction examples, not new product constants.

1. **T1 — Explicit Pause while Waiting.** Establish A's five-second recovery. Before it completes, B buffers, causing a future stop for A while the group waits. Issue user Pause while B remains unready. Verify A receives an immediate replacement Pause rather than continuing to its old deadline. Advancing the sweep must not subsequently unpause A against the user's intent. At the reviewed revision, the immediate replacement command is missing.
2. **T2a/T2b — Playing-state responses preserve a pending stop.** Establish A's five-second recovery and B's one-second recovery, with group playback scheduled at A's deadline. B's effective next command is its one-second Pause. Before that stop, dispatch an additional B Ready report in one case and a B Unpause request in the other. Verify the next effective command still stops B at its target, then B receives the valid deferred resume at the group start. At the reviewed revision, both requests replace the stop with a five-second Unpause.
3. **T3 — Short recovery under higher accepted ping.** Set both sessions' pings to 1,200 milliseconds. While B is still buffering, A reports ready and playing one second behind the frozen position. B then reports ready and playing five seconds behind. Verify A's one-second stop remains effective, A waits at the target, and its eventual resume honors B's later deadline and the normal latency allowance. At the reviewed revision, A is untracked because one second is below the 2.4-second transport threshold, and its stop is replaced by a later Unpause.
4. **T4 — Buffer report from a planned recovery stop.** Establish A's five-second recovery and B's 300-millisecond recovery. After B's planned stop takes effect but before the group start, dispatch B's paused, in-position Buffer report for the current playlist item. Verify the valid start plan remains in force, B does not become a new blocking loader solely because of that report, and B eventually resumes without another Ready report. At the reviewed revision, this event changes the group from Playing to Waiting and cancels the resume plan.
5. **T5 — Natural expiry followed by unrelated membership change.** Establish A's five-second recovery, B's 300-millisecond recovery, and a third member C. After B has stopped, but before the sweep delivers B's deferred resume, have C leave. A still determines the same future group start. Run the normal sweep and verify B receives its resume for that start and is not left paused. At the reviewed revision, pruning B's expired recovery deletes the pending resume, so the captured delivery is absent.

### Supporting verification

- Cover duplicates both before a required stop and after the session has reached the target. Confirm state resynchronization does not prematurely replace a stop or indefinitely postpone recovery.
- Exercise ping/remaining-recovery combinations below, at, and above the existing transport threshold, with a separate session requiring a longer recovery. Retain the default ping floor and timestamp-compensation behavior.
- Pair T4 with negative cases: genuinely buffering while playing, paused but behind the target, and a mismatching playlist item. These must retain the existing readiness and playlist validation behavior.
- Exercise membership and ignore-wait changes immediately before and after a short recovery completes, with two and three members. Distinguish a departing recovering member from an unrelated member leaving; preserve valid remaining resumes and cancel obsolete ones.
- Exercise explicit Pause, Stop, Seek, force-start, and playing-item/queue changes with a deferred resume outstanding. After cancellation, advance the sweep past the former deadlines and verify obsolete work produces no playback action.
- Drive the periodic sweep before a stop is due, after it is due but before group start, after group start, and repeatedly. Verify stops are not replaced early, resumes are not lost, late processing remains bounded, and duplicate processing is safe.
- Keep the original four catch-up regression scenarios and existing correction-loop, paused-buffering, stale-report, and timeout tests green. Add assertions for supersession order where a timestamp-only assertion previously concealed the defect.
- Prefer bounded scheduled-time assertions and existing sweep controls. If deterministic passage of time requires an internal clock/scheduler adapter, keep it narrow and shared rather than adding separate seams to every state. The existing short-delay reproductions are evidence, not a requirement to retain wall-clock sleeps in permanent tests.
- Extend the existing multi-client smoke-test scenarios to cover immediate Pause while Waiting, repeated Ready/state-resync requests, high-ping short recovery, the pause/seek Buffer report at a scheduled stop, and a third member leaving between stop and resume delivery. Record what was actually observed; identify unperformed playback checks explicitly.

### Completion criteria

- All six required regression cases fail for the reviewed behavior and pass for the implementation through the confirmed test seam.
- The original catch-up regressions and existing implementation/integration suites remain green. Account for every supporting scenario with automated coverage or a specific verification note.
- Tests establish both preservation of the necessary stop and eventual resumption; neither timestamps alone nor absence of an early command is sufficient.
- Stop, Pause, timeline changes, member departure, and ignore-wait changes leave no obsolete recovery action capable of affecting later playback.
- Report the exact tests run, skipped checks, and environmental failures. Distinguish automated server-command evidence from live playback evidence. If live checks cannot run, retain an explicit pending status rather than describing the experience as seamless.

## Out of Scope

- Replacing the SyncPlay client protocol, adding new client request types, or introducing a coordinated client upgrade.
- A generic scheduling framework or a broad state-machine rewrite when localized command-plan changes suffice.
- Unrelated style refactoring. The review's interface-expansion and duplication findings are nonblocking design guidance, not a separate cleanup project.
- Replacing the bundled-web users-query guard, fixing that underlying query, or changing Docker/Compose deployment configuration.
- Repository settings, dependency/version upgrades, and enabling GitHub Issues.
- Guaranteeing zero buffering or identical playback behavior across all clients without corresponding client verification.

## Further Notes

- This follow-up targets [d38bed5b576642c39843e42ad4f64f8d64b520c5](https://github.com/siddiqua1/jellyfin/commit/d38bed5b576642c39843e42ad4f64f8d64b520c5) on syncplay-patched, reviewed against [b5de3bf8ce67a90018b0c743d67e92f69bdd7523](https://github.com/siddiqua1/jellyfin/commit/b5de3bf8ce67a90018b0c743d67e92f69bdd7523). Preserve the original catch-up lifecycle specification's behavior; this document addresses the remaining command-plan gaps.
- Review verification: 1,114 implementation tests passed, 12 were skipped, and all three selected SyncPlay integration tests passed after supplying the disposable container's missing native fontconfig dependency. All six added regression cases failed for the behavioral reasons described above. These are recorded review results, not a new test run performed while writing this specification.
- The optional [reviewer regression patch](<.tmp-web/review-d38-regressions.patch>) contains the six reproductions. It is an untracked local artifact; this specification includes their complete scenarios so another model does not depend on that artifact or this conversation. Incorporate permanent tests into the normal suite rather than relying on scratch files.
- No live multi-client playback run was performed. Client-command replacement semantics and server event traces informed the review; client rendering and convergence still require observation.
- No project glossary or relevant ADR was found. Vocabulary follows the existing Group, session, group state, readiness, buffering, recovery, playlist item, and scheduled-command model.
- Intended tracker: siddiqua1/jellyfin on GitHub. Intended triage label: ready-for-agent. Issues remain disabled. Following the user's earlier choice, save this issue-ready specification locally and leave repository settings and labels unchanged.
