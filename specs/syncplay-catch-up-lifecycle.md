# Fix SyncPlay catch-up lifecycle during overlapping buffering and playback transitions

## Problem Statement

People watching together can still lose their place, watch ahead of the rest of the group, or wait unnecessarily when buffering overlaps with another member's recovery. The fork's SyncPlay fixes address correction loops and one overlapping-buffer sequence, but four verified server-command defects remain:

1. A catching-up session is exempted from the group pause without being scheduled to stop when it reaches the frozen group position. If another session remains buffering, the recovering viewer can run ahead and later be rewound.
2. Catch-up tracking survives a normal Playing-to-Paused transition. After a user pauses and resumes, a subsequent buffering event can wrongly exempt a session that is no longer catching up and retain an obsolete resume deadline.
3. Tracking is cleared when future resumption is scheduled rather than when recovery finishes. Another buffering event before that deadline pauses/repositions the recovering session early, potentially skipping its remaining content.
4. A second recovering session can replace a longer existing catch-up deadline with its own shorter deadline, causing the first session to receive an early reposition-to-group command.

The first two are introduced regressions; the latter two are incomplete fixes for the existing catch-up cancellation problem. All four were reproduced through public Group request dispatch and captured outbound commands. Existing tests pass because they do not cover these event sequences.

## Solution

Make catch-up a coherent part of the group playback lifecycle. A viewer recovering from buffering should be allowed to reach the group's frozen position without an unsolicited forward seek. If the group is still waiting, that viewer must stop there. When all relevant viewers can resume, schedule playback at a time that preserves every still-valid recovery and the existing latency allowance.

Explicit playback controls and changes to the active playback timeline must supersede obsolete recovery consistently, regardless of the current group state. Repeated buffering must preserve valid recovery rather than accidentally cancel it. Keep this behavior compatible with existing SyncPlay clients and preserve the fork's correction-loop protections.

## User Stories

1. As a viewer recovering from buffering, I want to watch the content I missed, so that recovery does not silently skip part of the video.
2. As a viewer recovering from buffering, I want to continue toward the frozen group position while another viewer loads, so that another buffering event does not cancel my recovery.
3. As a recovering viewer, I want to stop at the group position if someone is still loading, so that I do not watch ahead alone.
4. As a viewer waiting at the group position, I want recovered viewers to remain aligned with me, so that resumption does not require a disruptive rewind.
5. As a group member, I want playback to resume after everyone who should be waited for can participate, so that nobody's active recovery is cut short.
6. As a group member, I want resume scheduling to retain the normal latency allowance, so that low-latency recovery does not remove the time needed to deliver commands.
7. As a recovering viewer, I want repeated buffering from another member to preserve my original valid recovery, so that intermittent loading does not repeatedly skip content.
8. As a recovering viewer, I want my recovery deadline to remain valid after a future group resume is scheduled, so that another interruption before that time is handled correctly.
9. As a viewer whose recovery takes longer than another member's, I want my deadline to remain respected, so that the shorter recovery does not force me forward.
10. As a viewer whose recovery finishes first, I want to wait at the group position for a longer recovery, so that simultaneous recovery remains synchronized.
11. As a group member, I want overlapping recovery to work with three or more sessions, so that correctness is not limited to a two-person group.
12. As a group member, I want an explicit Pause to take effect during recovery, so that my playback control is not overridden by an old deadline.
13. As a group member, I want Pause followed by Unpause to establish the correct current playback state, so that a later buffer does not exempt an incorrectly tracked session.
14. As a group member, I want Stop to supersede pending recovery actions, so that stopped playback cannot be restarted by stale recovery work.
15. As a group member, I want an explicit Seek to supersede the previous recovery target, so that everyone follows the requested position.
16. As a group member, I want queue and playing-item changes to retire recovery for the previous timeline, so that an old position is not applied to a different item.
17. As a joining viewer, I want to receive the current valid group timeline, so that recovery information from before my join cannot corrupt synchronization.
18. As a remaining viewer, I want membership changes to update which recoveries matter, so that departed sessions do not hold the group to obsolete deadlines.
19. As a viewer opting out of group wait, I want that choice respected during recovery, so that I no longer hold up members who continue following the group.
20. As a viewer whose player reports another buffer while recovering, I want my readiness to be reevaluated, so that the group does not rely on a recovery that is no longer progressing.
21. As a viewer with delayed readiness reports, I want existing timestamp and position compensation to remain effective, so that network delay does not unnecessarily seek me or resume the group early.
22. As a viewer receiving repeated readiness reports, I want correction loops to remain bounded, so that the group does not stall until its wait timeout.
23. As a paused viewer already at the group position, I want the existing pause-induced buffering workaround preserved, so that joining or seeking does not reintroduce the original wait stall.
24. As a viewer whose player is still loading behind the group, I want the group to continue treating me as unready, so that the paused-buffer workaround does not accept genuinely incomplete recovery.
25. As a group member, I want silent or permanently stalled members to remain subject to the existing group-wait timeout, so that this fix does not introduce an unbounded wait.
26. As a user of an existing SyncPlay client, I want these corrections to use the established playback-command contract, so that I do not need a coordinated client upgrade.

## Implementation Decisions

- Keep recovery behavior within the existing SyncPlay Group module and its state-machine implementation. Preserve the current request-dispatch interface and command-delivery adapter; no new public request types, persistent schema, or client protocol are required.
- Treat a recovery as one coherent operation associated with a session, the current playback timeline, a target position, and its expected completion time. The internal representation is an implementation choice. Independently mutable fields must not leave partially valid recovery information visible to playback decisions.
- Retain enough information to preserve all simultaneously relevant recoveries. A second session's report must not silently replace another session's still-valid, later deadline.
- A future scheduled resume does not mean recovery has already completed. Recovery remains relevant until it completes, is superseded, or is otherwise deliberately retired. Group state may already be Playing before the scheduled playback start; lifecycle decisions must account for this distinction.
- When another session starts buffering, allow a valid recovering session to reach the frozen group position, then ensure it is paused there if the group is still waiting. Use the existing command-scheduling contract. When readiness or user action changes the plan, commands must supersede earlier scheduled work according to existing client semantics, without leaving a stale pause or resume to fire later.
- Schedule group resumption no earlier than both the normal latency-adjusted start time and every relevant recovery deadline. A session finishing sooner must remain aligned while another session finishes later. When a later event supersedes the playback timeline, recompute from the current timeline rather than retaining an obsolete deadline.
- Apply cancellation and recomputation consistently across Playing, Waiting, Paused, and Idle. Explicit Pause, Unpause/force-play, Stop, Seek, queue/item changes, joins, leaves, and ignore-wait changes must not leave obsolete recovery exemptions or delayed actions. Preserve recoveries that remain valid under the resulting group timeline and wait policy; retire those that do not.
- If the recovering session itself buffers again, reevaluate its recovery and readiness rather than assuming its previous deadline is still achievable.
- Preserve position sanitization, playlist-item validation, timestamp compensation, ping-based scheduling, paused-in-position handling, bounded correction behavior, and the existing group-wait timeout. Repeated or stale reports must not create endless correction or recovery rescheduling loops.
- Keep the Group module deep: callers and tests should exercise the existing interface, while recovery coherence, scheduling, and cancellation remain localized in its implementation rather than becoming new caller responsibilities.

## Testing Decisions

### Confirmed test seam

Use the existing public Group actions and Group.HandleRequest dispatch as the single primary test seam. The user confirmed this choice. Drive real group states, queue handling, and production request routing; use the existing mocked ISessionManager adapter to capture outbound commands per recipient. Group creation, join, leave, and ignore-wait changes should use their normal public entry points.

Existing WaitingGroupStateTests and its GroupHarness provide the prior art. Extend that harness where useful, but regression tests must exercise actual dispatch transitions rather than invoke a chosen state's handler with an invented previous state. Assert command type, recipient, target position, scheduled time, and public group information. Private recovery fields, collection shapes, and helper-call counts are implementation details, not acceptance criteria.

### Required regression scenarios

1. **A recovers while B remains buffering beyond A's deadline.** Start a playing group. A buffers, then reports ready while playing five seconds behind the frozen position. Before A catches up, B buffers and remains unready. A must not be paused/repositioned before its valid recovery deadline; its command plan must stop it at the frozen position when that deadline arrives if B is still unready. The group must not resume solely because A's deadline passed.
2. **Pause, resume, then buffer before the old deadline.** Establish A's five-second recovery. Perform a user Pause, then Unpause, then have B buffer before the superseded recovery deadline. A must receive the pause appropriate to the new timeline; it must not be exempted as an old recovering session. Later resumption must not retain an obsolete recovery delay.
3. **Repeated interruption before scheduled resume.** Establish A's five-second recovery. B buffers, then reports ready at the group position, causing a future group resume to be scheduled. Before that time, B buffers again. A's remaining valid recovery must still be preserved. Validate that the replacement command plan neither pauses A early nor leaves a stale resume active while B remains unready.
4. **A second session has a shorter recovery.** Establish A's five-second recovery. B buffers and reports ready while playing one second behind the same frozen position. No resume/reposition command may force A to the target before A's original valid deadline. B must also be prevented from running past the frozen position while waiting for A.

For the reviewed head, four temporary public-dispatch reproductions failed respectively with a missing deferred pause, a missing pause after user pause/resume, an early pause on repeated buffering, and a resume approximately four seconds earlier than A's existing deadline. Recreate these as behavioral tests rather than copying assumptions about internal storage.

### Supporting coverage

- Exercise a third session joining the buffering/recovery sequence, both shorter and longer subsequent recovery deadlines, and repeated interruption before the same scheduled start. Check recipients individually, including members that should remain paused.
- Cover cancellation or recomputation on user Pause, Stop, Seek, queue/item changes, join/leave, and ignore-wait changes from the real states reached during recovery. Include both the recovering member and a different member leaving. Verify obsolete work cannot affect later playback, and valid remaining work is respected.
- Cover the recovering session buffering again, duplicate readiness reports, an already-passed recovery deadline, and stale reports for a previous playlist item. Assert bounded commands and correct wait/resume behavior.
- Preserve existing varying-latency, out-of-position correction, paused-buffering, genuinely-behind buffering, and group-wait-timeout coverage. Include low and higher accepted ping values, zero/short remaining recovery, and reports on either side of the existing timestamp-compensation threshold.
- Validate command replacement as well as scheduling: a pending pause must not stop successfully resumed playback, and a superseded unpause must not restart a waiting or explicitly paused/stopped group. If the mocked delivery adapter cannot establish client execution semantics, distinguish server-command evidence from client playback evidence.
- Prefer assertions on captured scheduled times with bounded tolerances, not long sleeps. Introduce a single narrow internal clock/scheduler seam only if deterministic execution of time-dependent work requires it; reuse an existing time facility if one is available. Keep callers on the confirmed Group seam.

### Completion criteria

- The four regression scenarios fail against the reviewed behavior and pass after the implementation.
- All existing SyncPlay tests continue to pass; run relevant broader tests for any modified shared scheduling or lifecycle code.
- All supporting scenarios above are accounted for by automated behavioral coverage or an explicit, justified verification note. Report exactly what ran and distinguish test failures from unavailable infrastructure.
- Provide reproducible multi-client smoke-test steps for overlapping buffering, recovery exceeding another member's buffer, repeated interruptions, simultaneous recoveries, and pause/resume during recovery. Where a runnable deployment is available, record observed playback outcomes. Otherwise mark live playback verification pending; passing server-command tests alone is not evidence of seamless browser/native playback.

## Out of Scope

- Replacing the overall SyncPlay protocol, adding a new client coordination protocol, or rewriting client drift correction.
- Repairing the underlying users-query response or replacing the bundled-web menu guard. Preserve that existing workaround; it is independent of these four server defects.
- Docker/Compose portability, hardware-device configuration, deployment changes, repository settings, and dependency/version upgrades.
- Unrelated naming/style cleanup, broad state-machine rewrites, and generic scheduling abstractions without a demonstrated need in these scenarios.
- Promising zero buffering, identical decoder behavior across clients, or seamless support for every client without corresponding playback verification.

## Further Notes

- Reviewed branch: syncplay-patched. Reviewed head: [b5de3bf8ce67a90018b0c743d67e92f69bdd7523](https://github.com/siddiqua1/jellyfin/commit/b5de3bf8ce67a90018b0c743d67e92f69bdd7523), compared with merge-base baseline [83d0d0e7f76e23e2e085c8ff67d28b772d35967d](https://github.com/siddiqua1/jellyfin/commit/83d0d0e7f76e23e2e085c8ff67d28b772d35967d).
- The relevant intended behavior is recorded in the catch-up fix commit: preserve recovery when another member buffers, retain the later of normal resumption and recovery, and cancel obsolete recovery on playback/membership changes.
- Review verification used an isolated .NET 10 SDK container. All 47 existing SyncPlay tests passed; all four added regression reproductions failed. No live multi-client playback verification was performed. No production code was changed during the review.
- No project domain glossary or relevant ADR was found during specification discovery. Vocabulary here follows the existing Group, session, group state, buffering, readiness, playlist item, and scheduled-command model.
- Intended tracker: the siddiqua1/jellyfin GitHub repository. Intended triage label: ready-for-agent. Issues are currently disabled and that label does not exist. The user chose a local specification instead of enabling Issues; repository settings and labels remain unchanged. This document is ready to use as an issue body or direct implementation handoff.
