# SyncPlay catch-up lifecycle: multi-client smoke test

This document complements the automated coverage in
`tests/Jellyfin.Server.Implementations.Tests/SyncPlay/WaitingGroupStateTests.cs`. The automated
tests exercise the public `Group` request dispatch and capture the outbound playback commands;
they are server-command evidence, not evidence that a particular client renders playback
seamlessly. The scenarios below are the reproducible manual verification for the playback
outcome.

## Status

Live playback verification is **pending**. No deployment was exercised while implementing this
change. The automated suite passes; the observed server-command outcomes are described per
scenario below.

Automated coverage that backs the scenarios:

```bash
dotnet test tests/Jellyfin.Server.Implementations.Tests \
    --filter "FullyQualifiedName~WaitingGroupStateTests"
dotnet test tests/Jellyfin.Server.Integration.Tests \
    --filter "FullyQualifiedName~SyncPlay"
```

## Prerequisites

1. Build and run the patched server from this branch:

   ```bash
   docker build -t jellyfin-syncplay:catch-up .
   docker run --rm -p 8096:8096 -v jellyfin-config:/config -v jellyfin-cache:/cache \
       -v /path/to/media:/media jellyfin-syncplay:catch-up
   ```

2. Prepare a library item long enough to buffer deliberately (for example a high-bitrate title,
   or throttle one client's network). All clients must use the same server clock.
3. Connect at least two SyncPlay clients (browser and native are a useful mix) and join the same
   group. A third client is useful for the overlapping-recording scenarios.

Useful server evidence: the server log lines from `Emby.Server.Implementations.SyncPlay.Group`
(`Session ... requested ...`, `switching from ... to ...`, recovery follow-up messages), plus a
client-side network inspector showing `SyncPlayCommand` messages with `Command`, `When` and
`PositionTicks`.

## Scenario A: recovery exceeds another member's buffer

1. Start playback in the group and let it play for a few seconds.
2. On client A, pause the network until the player reports buffering, then restore it. A resumes
   playback a few seconds behind the group.
3. Before A reaches the group position, make client B buffer and leave it loading.
4. Expected server commands: A receives a `Pause` scheduled at A's recovery deadline with the
   frozen group position, not an immediate pause, seek or resume. B receives no playback start.
   When B recovers, the group resumes no earlier than A's deadline.
5. Expected playback: A watches the missed content, stops at the group position, and waits for B.
   A must not run past the group while B is still loading.

## Scenario B: explicit pause and resume during recovery

1. Repeat step 2 of Scenario A so A is recovering.
2. Pause and immediately unpause the group from client B.
3. Make client B buffer.
4. Expected: A receives the normal group pause for the new wait. It is not exempted as a
   recovering session and the old recovery deadline is not retained; resumption uses the normal
   latency allowance once B is ready.

## Scenario C: repeated interruption before the scheduled resume

1. Repeat step 2 of Scenario A so A is recovering.
2. Make B buffer and then recover at the group position. The group schedules its start at A's
   deadline.
3. Before that start time, make B buffer again, then recover again.
4. Expected: A keeps its remaining recovery and is never paused before its deadline. The newest
   command replaces the previous resume; the group does not start while B is unready and starts
   at A's deadline when B is ready.

## Scenario D: simultaneous recoveries with different remaining times

1. Make A recover with about five seconds remaining and B recover with about one second
   remaining, both behind the same frozen position.
2. Expected: A is not repositioned before its five-second deadline. B pauses at the group
   position when its one second elapses, waits there, and resumes when A and the group start.
   If B is resumed only after its stop has taken effect, that is the deferred resume sweep; the
   delay is bounded by the sweep interval.

## Scenario E: three-member group

1. Add a third ready client C.
2. Repeat Scenario A with A recovering and B buffering.
3. Expected: C receives the immediate pause for the wait; A receives only the stop at its
   deadline; B receives no immediate pause. When B is ready, all three receive the resume at A's
   deadline and the same group position.

## Scenario F: membership and opt-out changes

1. During A's recovery, have a different member leave and then rejoin in a fresh run; have the
   recovering member leave in another run; toggle "ignore wait" from both the recovering member
   and the buffering member.
2. Expected: a departed or opted-out member never holds the group to its old deadline. Remaining
   recoveries are still respected, and no stale pause/resume fires after the change. A join
   re-plans the wait, but a recovery that still describes the frozen position stays active until
   its deadline.

## Scenario G: immediate Pause while another member is buffering

1. Repeat step 2 of Scenario A so A is recovering, then make B buffer.
2. Press Pause in the group while B is still loading.
3. Expected server commands: A receives an immediate `Pause` that replaces its future recovery
   stop; any deferred resume is cancelled. Advancing the server sweep must not unpause anyone.
4. Expected playback: A stops promptly instead of playing until its former deadline.

## Scenario H: repeated Ready and Playing-state resynchronization

1. Set up a first recovery with about five seconds remaining and a second recovery with about one
   second remaining. The second member's last command is its one-second stop.
2. Have that member report Ready again, then in a fresh run send Unpause while the group is
   already Playing, before the one-second stop.
3. Expected: the next command for the member remains the stop at the same time; the deferred
   resume for the group start follows it. Re-sending state information must not replace the stop
   with a five-second unpause.

## Scenario I: short recovery under high latency

1. Give both members pings around 1,200 ms. While one member buffers, have the other report ready
   one second behind, then have the buffering member report ready five seconds behind.
2. Expected: the one-second stop remains in effect, that member waits at the target, and resumes
   with the group at the later deadline plus the normal latency allowance.

## Scenario J: buffering report at the planned recovery stop

1. Set up a five-second recovery and a 300-millisecond recovery. Let the short stop take effect
   and its player emit a paused, in-position buffering report for the current item before the
   group start.
2. Expected: the group stays Playing, the short member is not a new blocking loader, and its
   deferred resume is delivered at the planned start without another Ready report. A behind-target
   paused report, a playing report, or a stale item must still be treated as genuine loading.

## Scenario K: unrelated departure between stop and resume delivery

1. Use three members: a five-second recovery, a 300-millisecond recovery, and a third member.
2. After the short stop and before the sweep delivers its resume, have the third member leave.
3. Expected: the group start and the short member's deferred resume are unchanged. Repeating the
   sweep must not deliver a different plan or seek the member.

## Recorded outcomes

No multi-client run has been recorded yet. Mark each scenario passed only after observing the
playback behavior above; passing `dotnet test` alone is not sufficient.
