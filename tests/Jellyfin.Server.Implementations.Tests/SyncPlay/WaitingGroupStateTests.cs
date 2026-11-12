using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay.GroupStates;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using SyncPlayGroup = Emby.Server.Implementations.SyncPlay.Group;

namespace Jellyfin.Server.Implementations.Tests.SyncPlay;

public class WaitingGroupStateTests
{
    [Fact]
    public void Ready_PlayingSessionReportsPositionFromBeforeSeek_IsCorrected()
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        group.PositionTicks = TimeSpan.FromMinutes(10).Ticks;
        group.LastActivity = DateTime.UtcNow;

        var state = new WaitingGroupState(NullLoggerFactory.Instance) { ResumePlaying = true };

        // One member seeks half an hour in.
        state.HandleRequest(
            new SeekGroupRequest(TimeSpan.FromMinutes(40).Ticks),
            group,
            GroupStateType.Playing,
            harness.Second,
            CancellationToken.None);

        harness.Commands.Clear();

        // The other member has not applied the seek yet and reports the old position, still playing.
        state.HandleRequest(
            new ReadyGroupRequest(DateTime.UtcNow, TimeSpan.FromMinutes(10).Ticks, true, harness.PlaylistItemId),
            group,
            GroupStateType.Waiting,
            harness.First,
            CancellationToken.None);

        // It must be seeked into position, not accepted as ready and handed a pause command
        // scheduled the length of the seek into the future.
        Assert.Contains(harness.Commands, c => c.Command == SendCommandType.Seek);
        Assert.DoesNotContain(harness.Commands, c => c.Command == SendCommandType.Pause);
        Assert.True(group.IsBuffering(), "session should still be considered buffering");
    }

    [Fact]
    public void Ready_PlayingSessionRecoveringFromALongStall_IsNotSeeked()
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        group.PositionTicks = TimeSpan.FromMinutes(10).Ticks;
        group.LastActivity = DateTime.UtcNow;

        var state = new WaitingGroupState(NullLoggerFactory.Instance) { ResumePlaying = true };

        // The session reports it is buffering. No seek happens, so the group position stays put.
        state.HandleRequest(
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, true, harness.PlaylistItemId),
            group,
            GroupStateType.Playing,
            harness.First,
            CancellationToken.None);

        harness.Commands.Clear();

        // It recovers 45 seconds later, still behind, and must be waited for rather than seeked
        // forward past content it already buffered.
        var behind = group.PositionTicks - TimeSpan.FromSeconds(45).Ticks;
        state.HandleRequest(
            new ReadyGroupRequest(DateTime.UtcNow, behind, true, harness.PlaylistItemId),
            group,
            GroupStateType.Waiting,
            harness.First,
            CancellationToken.None);

        Assert.DoesNotContain(harness.Commands, c => c.Command == SendCommandType.Seek);
    }

    [Fact]
    public void Ready_PlayingSessionSlightlyBehindGroup_IsStillTreatedAsCatchingUp()
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        // A session that is a couple of seconds behind is genuinely recovering, and the group
        // is expected to wait for it rather than seek it around.
        group.PositionTicks = TimeSpan.FromMinutes(30).Ticks;
        group.LastActivity = DateTime.UtcNow;
        group.SetBuffering(harness.First, true);
        group.SetBuffering(harness.Second, true);

        var state = new WaitingGroupState(NullLoggerFactory.Instance) { ResumePlaying = true };
        harness.Commands.Clear();

        var clientPosition = group.PositionTicks - TimeSpan.FromSeconds(2).Ticks;
        state.HandleRequest(
            new ReadyGroupRequest(DateTime.UtcNow, clientPosition, true, harness.PlaylistItemId),
            group,
            GroupStateType.Waiting,
            harness.First,
            CancellationToken.None);

        Assert.DoesNotContain(harness.Commands, c => c.Command == SendCommandType.Seek);
        Assert.Contains(harness.Commands, c => c.Command == SendCommandType.Pause);
    }

    [Fact]
    public void Ready_PlayingSessionAheadOfGroup_IsCorrectedAtMostOnce()
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        group.PositionTicks = TimeSpan.FromMinutes(10).Ticks;
        group.LastActivity = DateTime.UtcNow;

        var state = new WaitingGroupState(NullLoggerFactory.Instance) { ResumePlaying = true };
        group.SetState(state);

        // Another member seeks, freezing the group position until everyone is ready.
        state.HandleRequest(
            new SeekGroupRequest(TimeSpan.FromMinutes(20).Ticks),
            group,
            GroupStateType.Playing,
            harness.Second,
            CancellationToken.None);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // This session applied the seek and kept playing while the group waited for the other
        // member, so it is now ten seconds ahead of the frozen group position. It must be
        // corrected, but only once: a seek makes the client report ready again while it is
        // still ahead, and correcting every report loops until the group wait timeout.
        var ahead = group.PositionTicks + TimeSpan.FromSeconds(10).Ticks;
        for (var i = 0; i < 5; i++)
        {
            group.HandleRequest(
                harness.First,
                new ReadyGroupRequest(DateTime.UtcNow, ahead, true, harness.PlaylistItemId),
                CancellationToken.None);
        }

        var seeks = harness.CommandsTo(harness.First.Id, SendCommandType.Seek);
        Assert.Single(seeks);
    }

    [Fact]
    public void Ready_LastSessionSlightlyAhead_GroupResumesAtTheGroupPosition()
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        group.PositionTicks = TimeSpan.FromMinutes(10).Ticks;
        group.LastActivity = DateTime.UtcNow;

        var state = new WaitingGroupState(NullLoggerFactory.Instance) { ResumePlaying = true };
        group.SetState(state);

        state.HandleRequest(
            new SeekGroupRequest(TimeSpan.FromMinutes(20).Ticks),
            group,
            GroupStateType.Playing,
            harness.Second,
            CancellationToken.None);

        harness.Commands.Clear();

        // Within the tolerated offset, the last session is accepted as ready.
        var ahead = group.PositionTicks + TimeSpan.FromMilliseconds(200).Ticks;
        state.HandleRequest(
            new ReadyGroupRequest(DateTime.UtcNow, ahead, true, harness.PlaylistItemId),
            group,
            GroupStateType.Waiting,
            harness.First,
            CancellationToken.None);

        // The unpause command carries the group position and brings every client back in sync.
        state.HandleRequest(
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, true, harness.PlaylistItemId),
            group,
            GroupStateType.Waiting,
            harness.Second,
            CancellationToken.None);

        Assert.DoesNotContain(harness.Commands, c => c.Command == SendCommandType.Seek);
        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        var unpauses = harness.Commands.Where(c => c.Command == SendCommandType.Unpause).ToList();
        Assert.NotEmpty(unpauses);
        Assert.All(unpauses, command => Assert.Equal(group.PositionTicks, command.PositionTicks));
    }

    [Fact]
    public void Buffer_FromPausedSessionAtGroupPosition_DoesNotBlockTheGroup()
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        group.PositionTicks = TimeSpan.FromMinutes(10).Ticks;
        group.LastActivity = DateTime.UtcNow;

        var state = new WaitingGroupState(NullLoggerFactory.Instance) { ResumePlaying = true };
        group.SetState(state);

        state.HandleRequest(
            new SeekGroupRequest(TimeSpan.FromMinutes(20).Ticks),
            group,
            GroupStateType.Playing,
            harness.Second,
            CancellationToken.None);

        harness.Commands.Clear();

        // The group paused this member while waiting for another session. The pause issued a
        // seek, which made the paused player emit a buffering event at the group position.
        // The member must not be waited for, since a paused player never reports ready on its
        // own; otherwise the group stays stuck until the wait timeout.
        state.HandleRequest(
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, harness.PlaylistItemId),
            group,
            GroupStateType.Waiting,
            harness.First,
            CancellationToken.None);

        state.HandleRequest(
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, true, harness.PlaylistItemId),
            group,
            GroupStateType.Waiting,
            harness.Second,
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);
        Assert.DoesNotContain(harness.Commands, c => c.Command == SendCommandType.Seek);
    }

    [Fact]
    public void Buffer_FromPausedSessionBehindGroupPosition_StillBlocksTheGroup()
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        group.PositionTicks = TimeSpan.FromMinutes(10).Ticks;
        group.LastActivity = DateTime.UtcNow;

        var state = new WaitingGroupState(NullLoggerFactory.Instance) { ResumePlaying = true };
        group.SetState(state);

        state.HandleRequest(
            new SeekGroupRequest(TimeSpan.FromMinutes(20).Ticks),
            group,
            GroupStateType.Playing,
            harness.Second,
            CancellationToken.None);

        harness.Commands.Clear();

        // This session has not reached the group position yet, so it is really still loading
        // and must keep the group waiting.
        state.HandleRequest(
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks - TimeSpan.FromSeconds(10).Ticks, false, harness.PlaylistItemId),
            group,
            GroupStateType.Waiting,
            harness.First,
            CancellationToken.None);

        state.HandleRequest(
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, true, harness.PlaylistItemId),
            group,
            GroupStateType.Waiting,
            harness.Second,
            CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);
    }

    [Fact]
    public void Ready_PausedSessionOutOfPosition_IsStillCorrected()
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        group.PositionTicks = TimeSpan.FromMinutes(30).Ticks;
        group.LastActivity = DateTime.UtcNow;
        group.SetBuffering(harness.First, true);
        group.SetBuffering(harness.Second, true);

        var state = new WaitingGroupState(NullLoggerFactory.Instance) { ResumePlaying = true };
        harness.Commands.Clear();

        state.HandleRequest(
            new ReadyGroupRequest(DateTime.UtcNow, 0, false, harness.PlaylistItemId),
            group,
            GroupStateType.Waiting,
            harness.First,
            CancellationToken.None);

        Assert.Contains(harness.Commands, c => c.Command == SendCommandType.Seek);
    }

    [Fact]
    public void Ready_ClientResumedWithLowPing_AppliesTheDefaultPingFloorInMilliseconds()
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        // Both members report a ping well under the default, so the floor is what decides the delay.
        group.UpdatePing(harness.First, 10);
        group.UpdatePing(harness.Second, 10);

        group.PositionTicks = TimeSpan.FromMinutes(5).Ticks;
        group.LastActivity = DateTime.UtcNow;
        group.SetBuffering(harness.First, true);
        group.SetBuffering(harness.Second, false);

        var state = new WaitingGroupState(NullLoggerFactory.Instance) { ResumePlaying = true };

        var before = DateTime.UtcNow;
        state.HandleRequest(
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, true, harness.PlaylistItemId),
            group,
            GroupStateType.Waiting,
            harness.First,
            CancellationToken.None);

        // DefaultPing is expressed in milliseconds, so the floor must be converted before being
        // compared against a tick count. Without the conversion the floor is 500 ticks (0.05 ms)
        // and never applies.
        var scheduledDelay = group.LastActivity - before;
        Assert.True(
            scheduledDelay >= TimeSpan.FromMilliseconds(group.DefaultPing),
            $"expected a resume delay of at least {group.DefaultPing} ms, got {scheduledDelay.TotalMilliseconds} ms");
    }

    [Theory]
    [InlineData(4_000_000_000L)]
    [InlineData(1_000_000_000_000_000L)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    public void UpdatePing_ClientReportsAnUnusablePing_IsClampedAndCannotStallTheGroup(long reportedPing)
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        group.UpdatePing(harness.First, reportedPing);

        Assert.InRange(group.GetHighestPing(), 0, group.MaxPing);

        // The reported ping is scaled into the group's resume point, so an unclamped value either
        // pushes playback months out or overflows the arithmetic outright.
        var state = new PlayingGroupState(NullLoggerFactory.Instance);
        var before = DateTime.UtcNow;
        state.HandleRequest(
            new UnpauseGroupRequest(),
            group,
            GroupStateType.Paused,
            harness.First,
            CancellationToken.None);

        Assert.InRange(group.LastActivity - before, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task SessionJoined_JoinerNeverReportsReady_GroupResumesWithoutIt()
    {
        var harness = new GroupHarness(groupWaitTimeout: 200);
        var group = harness.Group;

        group.PositionTicks = TimeSpan.FromMinutes(5).Ticks;
        group.LastActivity = DateTime.UtcNow;
        group.SetState(new PlayingGroupState(NullLoggerFactory.Instance));

        // A session joins while the group is playing: the group pauses and waits for it.
        var joiner = harness.NewSession("joiner");
        group.SessionJoin(joiner, new JoinGroupRequest(group.GroupId), CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        // The joiner's player aborts and never reports ready. Without a bounded wait the whole
        // group stays paused forever.
        await harness.WaitForState(GroupStateType.Playing);

        // Late buffer reports from the session that missed the deadline must not drag the group
        // back into waiting.
        group.HandleRequest(
            joiner,
            new BufferGroupRequest(DateTime.UtcNow, 0, false, harness.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);
    }

    [Fact]
    public async Task SessionJoined_GroupWasPaused_TimeoutLeavesTheGroupPaused()
    {
        var harness = new GroupHarness(groupWaitTimeout: 200);
        var group = harness.Group;

        group.PositionTicks = TimeSpan.FromMinutes(5).Ticks;

        // The group has been sitting paused for a while before anyone joins.
        group.LastActivity = DateTime.UtcNow.AddMinutes(-2);
        group.SetState(new PausedGroupState(NullLoggerFactory.Instance));

        var joiner = harness.NewSession("joiner");
        group.SessionJoin(joiner, new JoinGroupRequest(group.GroupId), CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        // A group that was paused must not start playing because a member failed to report ready.
        await harness.WaitForState(GroupStateType.Paused);

        // Giving up on the joiner must not move the playback position of an already paused group.
        Assert.Equal(TimeSpan.FromMinutes(5).Ticks, group.PositionTicks);

        // Every member has to be told the group is no longer waiting.
        var recipients = harness.StateUpdates
            .Where(update => update.Update.State == GroupStateType.Paused)
            .Select(update => update.SessionId)
            .ToList();
        Assert.Contains(harness.First.Id, recipients);
        Assert.Contains(harness.Second.Id, recipients);
        Assert.Contains(joiner.Id, recipients);
    }

    [Fact]
    public async Task Ready_ReportedBeforeTheDeadline_GroupDoesNotGiveUpOnAnyone()
    {
        var harness = new GroupHarness(groupWaitTimeout: 200);
        var group = harness.Group;

        group.PositionTicks = TimeSpan.FromMinutes(5).Ticks;
        group.LastActivity = DateTime.UtcNow;
        group.SetState(new PlayingGroupState(NullLoggerFactory.Instance));

        var joiner = harness.NewSession("joiner");
        group.SessionJoin(joiner, new JoinGroupRequest(group.GroupId), CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        group.HandleRequest(
            joiner,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, true, harness.PlaylistItemId),
            CancellationToken.None);

        // Everyone reported ready, so no deadline is left to trip and force a spurious unpause.
        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);
        Assert.Null(group.GroupWaitDeadline);

        var until = DateTime.UtcNow.AddMilliseconds(3 * 200);
        while (DateTime.UtcNow < until)
        {
            harness.PumpGroupWaitTimeout();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);
    }

    [Fact]
    public async Task SetPlaylistItem_AfterATimeout_GroupWaitsForEveryoneAgain()
    {
        var harness = new GroupHarness(groupWaitTimeout: 200);
        var group = harness.Group;

        group.LastActivity = DateTime.UtcNow;
        group.SetState(new PlayingGroupState(NullLoggerFactory.Instance));

        var joiner = harness.NewSession("joiner");
        group.SessionJoin(joiner, new JoinGroupRequest(group.GroupId), CancellationToken.None);
        await harness.WaitForState(GroupStateType.Playing);

        // Giving up on a session lasts only until the group changes what it is playing.
        group.HandleRequest(
            harness.First,
            new SetPlaylistItemGroupRequest(harness.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);
        Assert.NotNull(group.GroupWaitDeadline);
    }

    [Fact]
    public void Buffer_DuringCatchUp_KeepsTheRecoveryAndStopsItAtTheGroupPosition()
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        group.UpdatePing(harness.First, 10);
        group.UpdatePing(harness.Second, 10);
        group.PositionTicks = TimeSpan.FromMinutes(10).Ticks;
        group.LastActivity = DateTime.UtcNow;

        var state = new WaitingGroupState(NullLoggerFactory.Instance) { ResumePlaying = true };
        group.SetState(state);

        // First buffers and comes back two seconds behind while still playing.
        state.HandleRequest(
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, true, harness.PlaylistItemId),
            group,
            GroupStateType.Playing,
            harness.First,
            CancellationToken.None);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks - TimeSpan.FromSeconds(2).Ticks, true, harness.PlaylistItemId),
            CancellationToken.None);

        var catchUpUntil = group.LastActivity;
        Assert.InRange(catchUpUntil - DateTime.UtcNow, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));

        var scheduledUnpause = Assert.Single(harness.CommandsTo(harness.Second.Id, SendCommandType.Unpause));
        Assert.True(scheduledUnpause.When >= catchUpUntil.AddMilliseconds(-100), "the other session must wait for the catch-up");

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // Second starts buffering during the catch-up. It must not pause or seek First: First
        // is still playing towards the group position, and pausing it would seek it forward and
        // skip the content it is catching up on.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, harness.PlaylistItemId),
            CancellationToken.None);

        var pauses = harness.CommandsTo(harness.First.Id, SendCommandType.Pause);
        Assert.DoesNotContain(pauses, pause => pause.When < catchUpUntil.AddMilliseconds(-100));
        Assert.Contains(pauses, pause => pause.When <= catchUpUntil.AddMilliseconds(100));
        Assert.Empty(harness.CommandsTo(harness.First.Id, SendCommandType.Seek));

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // Second finishes loading before First reaches the group position. The group must
        // resume when First gets there, not immediately.
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, harness.PlaylistItemId),
            CancellationToken.None);

        var unpauses = harness.DeliveredCommands
            .Where(delivery => delivery.Command.Command == SendCommandType.Unpause)
            .ToList();
        Assert.NotEmpty(unpauses);
        Assert.All(unpauses, delivery => Assert.Equal(group.PositionTicks, delivery.Command.PositionTicks));
        Assert.All(
            unpauses,
            delivery => Assert.True(
                delivery.Command.When >= catchUpUntil.AddMilliseconds(-100),
                $"group resumed at {delivery.Command.When:O}, before the catch-up deadline {catchUpUntil:O}"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    [InlineData(800)]
    [InlineData(1500)]
    [InlineData(2500)]
    [InlineData(4000)]
    public void Ready_PlayingSessionReportsAStalePosition_VaryingLatencyIsCompensated(int latencyMilliseconds)
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        group.UpdatePing(harness.First, 10);
        group.UpdatePing(harness.Second, 10);
        group.PositionTicks = TimeSpan.FromMinutes(10).Ticks;
        group.LastActivity = DateTime.UtcNow;

        // Both sessions are buffering, so a ready report pauses the session instead of resuming
        // the group. This keeps the scheduled command observable.
        group.SetBuffering(harness.First, true);
        group.SetBuffering(harness.Second, true);

        var state = new WaitingGroupState(NullLoggerFactory.Instance) { ResumePlaying = true };

        // The session was at the group position when it sent the report, but the report takes
        // the given stream latency to arrive. Time sync compensates it only up to TimeSyncOffset.
        var latency = TimeSpan.FromMilliseconds(latencyMilliseconds);
        var reportTime = DateTime.UtcNow - latency;
        var positionWhenSent = group.PositionTicks - latency.Ticks;

        var before = DateTime.UtcNow;
        state.HandleRequest(
            new ReadyGroupRequest(reportTime, positionWhenSent, true, harness.PlaylistItemId),
            group,
            GroupStateType.Waiting,
            harness.First,
            CancellationToken.None);

        // A session that is behind after a buffer is catching up, not lost. It must never be
        // seeked (that would skip content), regardless of the transport latency.
        Assert.Empty(harness.CommandsTo(harness.First.Id, SendCommandType.Seek));

        var pause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Pause));
        var scheduledDelay = (pause.When - before).TotalMilliseconds;

        if (latencyMilliseconds <= group.TimeSyncOffset)
        {
            // The elapsed time is used, so the latency is compensated and the pause is due now.
            Assert.InRange(scheduledDelay, -100.0, 300.0);
        }
        else
        {
            // The report is older than TimeSyncOffset: the elapsed time is ignored, the session
            // is treated as behind by the latency, and the pause is scheduled for when it arrives.
            Assert.InRange(scheduledDelay, latencyMilliseconds - 250.0, latencyMilliseconds + 250.0);
        }
    }

    private static CatchUpScenario StartCatchUpRecovery(int extraMembers = 0, long? groupWaitTimeout = null)
    {
        var harness = new GroupHarness(groupWaitTimeout);
        var group = harness.Group;

        var extras = new List<SessionInfo>();
        for (var i = 0; i < extraMembers; i++)
        {
            extras.Add(harness.AddSession($"extra{i}"));
        }

        group.UpdatePing(harness.First, 10);
        group.UpdatePing(harness.Second, 10);
        foreach (var extra in extras)
        {
            group.UpdatePing(extra, 10);
        }

        var startPosition = TimeSpan.FromMinutes(10).Ticks;

        // Reach the playing state through the public request dispatch rather than by installing
        // a state directly.
        group.HandleRequest(
            harness.First,
            new PlayGroupRequest(new[] { Guid.NewGuid() }, 0, startPosition),
            CancellationToken.None);
        var playlistItemId = group.PlayQueue.GetPlayingItemPlaylistId();

        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, startPosition, true, playlistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, startPosition, true, playlistItemId),
            CancellationToken.None);
        foreach (var extra in extras)
        {
            group.HandleRequest(
                extra,
                new ReadyGroupRequest(DateTime.UtcNow, startPosition, true, playlistItemId),
                CancellationToken.None);
        }

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        // First buffers and comes back five seconds behind the frozen group position. With
        // nothing else loading, the group stays playing and waits for First to recover.
        group.HandleRequest(
            harness.First,
            new BufferGroupRequest(DateTime.UtcNow, startPosition, true, playlistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, startPosition - TimeSpan.FromSeconds(5).Ticks, true, playlistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);
        Assert.Equal(startPosition, group.PositionTicks);

        var deadline = group.LastActivity;
        Assert.InRange(deadline - DateTime.UtcNow, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6));

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        return new CatchUpScenario(harness, playlistItemId, deadline, extras);
    }

    [Fact]
    public void Buffer_DuringCatchUp_RecoveringSessionIsScheduledToStopAtTheGroupPosition()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        // Another member starts buffering before the recovering member reaches the position.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        // The recovering member keeps playing towards the frozen position and must stop there
        // when its recovery finishes, while the group is still waiting for the other member.
        var pause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Pause));
        Assert.InRange(pause.When, scenario.Deadline.AddMilliseconds(-100), scenario.Deadline.AddMilliseconds(100));
        Assert.Equal(group.PositionTicks, pause.PositionTicks);

        // The playback scheduled before the buffering event is superseded: nothing resumes the
        // group merely because the recovery deadline arrives while it is still waiting.
        Assert.DoesNotContain(harness.DeliveredCommands, delivery => delivery.Command.Command == SendCommandType.Unpause);
    }

    [Fact]
    public void PauseThenUnpause_DuringCatchUp_DoesNotLeaveAnExemptRecoveringSession()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        // The user pauses and resumes the group before the original recovery deadline.
        group.HandleRequest(harness.Second, new PauseGroupRequest(), CancellationToken.None);
        Assert.Equal(GroupStateType.Paused, group.GetInfo().State);

        group.HandleRequest(harness.Second, new UnpauseGroupRequest(), CancellationToken.None);
        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // Another member buffers on the new timeline. First stopped following its recovery when
        // the user paused, so it must receive the pause for the new wait immediately.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        var pause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Pause));
        Assert.True(
            pause.When <= DateTime.UtcNow.AddMilliseconds(300),
            $"the former recovering session was paused at {pause.When:O}, not for the new wait");

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // Once the other member is ready, playback resumes with the normal latency allowance;
        // no obsolete recovery delay is retained.
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.True(
            unpause.When < scenario.Deadline.AddMilliseconds(-100),
            $"the group kept the superseded recovery deadline: resumed at {unpause.When:O}");
        Assert.InRange(unpause.When - DateTime.UtcNow, TimeSpan.FromMilliseconds(-100), TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public void RepeatedBuffer_BeforeScheduledResume_KeepsTheRecoveryAndReplacesTheResume()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        // Another member buffers and becomes ready at the group position, so the group is
        // scheduled to resume when First finishes recovering.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);
        var scheduledResume = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.True(scheduledResume.When >= scenario.Deadline.AddMilliseconds(-100));

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // Before the scheduled start, the other member buffers again. First's valid recovery
        // must be preserved and the stale resume replaced.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        var pauses = harness.CommandsTo(harness.First.Id, SendCommandType.Pause);
        Assert.DoesNotContain(pauses, pause => pause.When < scenario.Deadline.AddMilliseconds(-100));
        Assert.Contains(pauses, pause => pause.When <= scenario.Deadline.AddMilliseconds(100));

        // The newest command for the recovering session replaces the resume that was scheduled
        // before this interruption.
        var superseding = harness.DeliveredCommands
            .Where(delivery => delivery.SessionId == harness.First.Id)
            .Where(delivery => delivery.Command.When >= scenario.Deadline.AddMilliseconds(-100))
            .ToList();
        Assert.NotEmpty(superseding);
        Assert.Equal(SendCommandType.Pause, superseding[^1].Command.Command);

        // The group is still waiting, so nothing resumes it now.
        Assert.DoesNotContain(harness.DeliveredCommands, delivery => delivery.Command.Command == SendCommandType.Unpause);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // When the other member is ready again, the group still resumes at First's deadline.
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);
        var resume = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.True(
            resume.When >= scenario.Deadline.AddMilliseconds(-100),
            $"group resumed at {resume.When:O}, before the recovery deadline {scenario.Deadline:O}");
    }

    [Fact]
    public void SecondRecovery_WithShorterDelay_KeepsTheLongerDeadline()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;
        var bufferTime = DateTime.UtcNow;

        // Another member buffers, then reports ready one second behind the same frozen position.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(bufferTime, group.PositionTicks, true, scenario.PlaylistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks - TimeSpan.FromSeconds(1).Ticks, true, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        // First's longer recovery must not be cut short by the shorter one.
        var firstUnpauses = harness.CommandsTo(harness.First.Id, SendCommandType.Unpause);
        Assert.NotEmpty(firstUnpauses);
        Assert.All(
            firstUnpauses,
            unpause => Assert.True(
                unpause.When >= scenario.Deadline.AddMilliseconds(-100),
                $"First was repositioned at {unpause.When:O}, before its recovery deadline {scenario.Deadline:O}"));

        // Second must stop at the frozen position when it finishes recovering, instead of
        // running past the group while it waits for First.
        // The newest pause is the one the client keeps; it is due when Second finishes
        // recovering, one second after it reported ready.
        var secondPause = harness.CommandsTo(harness.Second.Id, SendCommandType.Pause)[^1];
        Assert.InRange(
            secondPause.When,
            bufferTime.AddMilliseconds(700),
            bufferTime.AddMilliseconds(1500));
        Assert.Equal(group.PositionTicks, secondPause.PositionTicks);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // Once Second has stopped at the group position, it is resumed with the group at the
        // later deadline rather than left paused.
        harness.PumpScheduledRecoveryResumes(secondPause.When.AddMilliseconds(50));

        var secondUnpause = Assert.Single(harness.CommandsTo(harness.Second.Id, SendCommandType.Unpause));
        Assert.InRange(
            secondUnpause.When,
            scenario.Deadline.AddMilliseconds(-100),
            scenario.Deadline.AddMilliseconds(100));
    }

    [Fact]
    public void ThirdMember_DuringOverlappingCatchUp_ReceivesTheCommandsForItsRole()
    {
        var scenario = StartCatchUpRecovery(extraMembers: 1);
        var harness = scenario.Harness;
        var group = harness.Group;
        var third = scenario.Extras[0];

        // A third member is ready and not recovering when another member buffers.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        // It pauses with the rest of the waiting group.
        var thirdPause = Assert.Single(harness.CommandsTo(third.Id, SendCommandType.Pause));
        Assert.True(
            thirdPause.When <= DateTime.UtcNow.AddMilliseconds(300),
            $"the ready member was paused at {thirdPause.When:O}, not for the current wait");

        // The recovering member is only stopped at the group position when it gets there.
        var firstPause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Pause));
        Assert.InRange(firstPause.When, scenario.Deadline.AddMilliseconds(-100), scenario.Deadline.AddMilliseconds(100));

        // The buffering member is not paused now.
        Assert.DoesNotContain(
            harness.CommandsTo(harness.Second.Id, SendCommandType.Pause),
            pause => pause.When < scenario.Deadline.AddMilliseconds(-100));

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // When the buffering member becomes ready, every member resumes at the recovery deadline.
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        foreach (var member in new[] { harness.First, harness.Second, third })
        {
            var unpause = Assert.Single(harness.CommandsTo(member.Id, SendCommandType.Unpause));
            Assert.InRange(unpause.When, scenario.Deadline.AddMilliseconds(-100), scenario.Deadline.AddMilliseconds(100));
            Assert.Equal(group.PositionTicks, unpause.PositionTicks);
        }
    }

    [Fact]
    public void SecondRecovery_LongerThanTheFirst_StopsTheFirstAndResumesTogether()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;
        var bufferTime = DateTime.UtcNow;

        // Another member buffers, then reports ready seven seconds behind the same frozen
        // position: its recovery is longer than the first member's.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(bufferTime, group.PositionTicks, true, scenario.PlaylistItemId),
            CancellationToken.None);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks - TimeSpan.FromSeconds(7).Ticks, true, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        // The first member finishes before the group starts: it must stop at the position.
        var firstPause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Pause));
        Assert.InRange(firstPause.When, scenario.Deadline.AddMilliseconds(-100), scenario.Deadline.AddMilliseconds(100));
        Assert.Equal(group.PositionTicks, firstPause.PositionTicks);

        // It is not told to resume with the group yet; the longer recovery decides the start.
        Assert.Empty(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        var secondUnpause = Assert.Single(harness.CommandsTo(harness.Second.Id, SendCommandType.Unpause));
        Assert.InRange(secondUnpause.When, bufferTime.AddSeconds(6.5), bufferTime.AddSeconds(7.5));

        // Once the first member has stopped at the position, it resumes with the group.
        harness.PumpScheduledRecoveryResumes(firstPause.When.AddMilliseconds(50));

        var firstUnpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.Equal(secondUnpause.When, firstUnpause.When);
        Assert.Equal(group.PositionTicks, firstUnpause.PositionTicks);
    }

    [Fact]
    public void Stop_DuringCatchUp_SupersedesTheRecoveryPlan()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        // A shorter recovery schedules a deferred resume for the other member.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, true, scenario.PlaylistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks - TimeSpan.FromSeconds(1).Ticks, true, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        group.HandleRequest(harness.Second, new StopGroupRequest(), CancellationToken.None);

        Assert.Equal(GroupStateType.Idle, group.GetInfo().State);

        // No deferred recovery work survives the stop.
        harness.PumpScheduledRecoveryResumes(DateTime.UtcNow.AddSeconds(10));
        harness.PumpGroupWaitTimeout();

        Assert.DoesNotContain(harness.DeliveredCommands, delivery => delivery.Command.Command == SendCommandType.Unpause);
        Assert.DoesNotContain(harness.DeliveredCommands, delivery => delivery.Command.Command == SendCommandType.Pause);
    }

    [Fact]
    public void Seek_DuringCatchUp_SupersedesTheRecoveryPlan()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        // A shorter recovery schedules a deferred resume for the other member.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, true, scenario.PlaylistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks - TimeSpan.FromSeconds(1).Ticks, true, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        var newPosition = group.PositionTicks + TimeSpan.FromMinutes(5).Ticks;
        group.HandleRequest(harness.Second, new SeekGroupRequest(newPosition), CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);
        Assert.Equal(newPosition, group.PositionTicks);

        // The old recovery windows cannot fire anymore.
        harness.PumpScheduledRecoveryResumes(DateTime.UtcNow.AddSeconds(10));
        Assert.DoesNotContain(harness.DeliveredCommands, delivery => delivery.Command.Command == SendCommandType.Unpause);

        // The group resumes at the sought position with the normal latency allowance.
        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, newPosition, false, scenario.PlaylistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, newPosition, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.True(unpause.When <= DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(newPosition, unpause.PositionTicks);
    }

    [Fact]
    public void Join_DuringCatchUp_PreservesTheValidRecoveryAndWaitsForTheJoiner()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        // A new member joins while the group is playing and a recovery is in progress.
        var joiner = harness.NewSession("joiner");
        group.SessionJoin(joiner, new JoinGroupRequest(group.GroupId), CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        // The recovery still describes the frozen position: the recovering member keeps playing
        // towards it and stops there rather than being repositioned by the join.
        var pauses = harness.CommandsTo(harness.First.Id, SendCommandType.Pause);
        Assert.DoesNotContain(pauses, pause => pause.When < scenario.Deadline.AddMilliseconds(-100));
        Assert.Contains(pauses, pause => pause.When <= scenario.Deadline.AddMilliseconds(100));

        // The ready member is paused for the wait caused by the join.
        var readyPause = harness.CommandsTo(harness.Second.Id, SendCommandType.Pause)[^1];
        Assert.True(
            readyPause.When <= DateTime.UtcNow.AddMilliseconds(300),
            $"the ready member was paused at {readyPause.When:O}, not for the new wait");

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // Once the joiner is ready, playback resumes at the still-valid recovery deadline.
        group.HandleRequest(
            joiner,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.InRange(unpause.When, scenario.Deadline.AddMilliseconds(-100), scenario.Deadline.AddMilliseconds(100));
    }

    [Fact]
    public void BufferingMemberLeaves_DuringCatchUp_GroupResumesAtTheRecoveryDeadline()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // The member that was keeping the group waiting leaves.
        group.SessionLeave(harness.Second, new LeaveGroupRequest(), CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        // The remaining recoverer is still respected.
        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.InRange(unpause.When, scenario.Deadline.AddMilliseconds(-100), scenario.Deadline.AddMilliseconds(100));
    }

    [Fact]
    public void RecoveringMemberLeaves_DuringCatchUp_DoesNotHoldTheGroup()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        group.SessionLeave(harness.First, new LeaveGroupRequest(), CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        // The other member becomes ready; the departed member's deadline does not delay it.
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        var unpause = Assert.Single(harness.CommandsTo(harness.Second.Id, SendCommandType.Unpause));
        Assert.True(
            unpause.When < scenario.Deadline.AddMilliseconds(-100),
            $"the departed member's recovery still delayed the group: resumed at {unpause.When:O}");
        Assert.Empty(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
    }

    [Fact]
    public void RecoveringMemberIgnoresWait_DuringCatchUp_DoesNotDelayTheGroup()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        // The recovering member opts out of the group wait.
        group.HandleRequest(harness.First, new IgnoreWaitGroupRequest(true), CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        var unpause = Assert.Single(harness.CommandsTo(harness.Second.Id, SendCommandType.Unpause));
        Assert.True(
            unpause.When < scenario.Deadline.AddMilliseconds(-100),
            $"the opted-out recovery still delayed the group: resumed at {unpause.When:O}");
    }

    [Fact]
    public void BufferingMemberIgnoresWait_DuringCatchUp_GroupResumesAtTheRecoveryDeadline()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // The buffering member stops holding up the group.
        group.HandleRequest(harness.Second, new IgnoreWaitGroupRequest(true), CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.InRange(unpause.When, scenario.Deadline.AddMilliseconds(-100), scenario.Deadline.AddMilliseconds(100));
    }

    [Fact]
    public void RecoveringSessionBuffersAgain_DuringCatchUp_ReevaluatesItsDeadline()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        // The recovering session buffers again before its deadline.
        group.HandleRequest(
            harness.First,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, true, scenario.PlaylistItemId),
            CancellationToken.None);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        var readyTime = DateTime.UtcNow;

        // It comes back two seconds behind instead of five: its new deadline is shorter.
        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks - TimeSpan.FromSeconds(2).Ticks, true, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        var pause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Pause));
        Assert.InRange(pause.When, readyTime.AddSeconds(1.5), readyTime.AddSeconds(2.5));
        Assert.Equal(group.PositionTicks, pause.PositionTicks);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.InRange(unpause.When, readyTime.AddSeconds(1.5), readyTime.AddSeconds(2.5));
        Assert.True(
            unpause.When < scenario.Deadline.AddMilliseconds(-100),
            $"the superseded recovery deadline was retained: resumed at {unpause.When:O}");
    }

    [Fact]
    public void DuplicateReadyReports_DoNotExtendTheRecovery()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // The same readiness report arrives twice.
        var reportTime = DateTime.UtcNow;
        var report = new ReadyGroupRequest(
            reportTime,
            group.PositionTicks - TimeSpan.FromSeconds(5).Ticks,
            true,
            scenario.PlaylistItemId);
        group.HandleRequest(harness.First, report, CancellationToken.None);
        group.HandleRequest(harness.First, report, CancellationToken.None);

        // Both reports describe the same five-second recovery, so the deadline must not move.
        var pauses = harness.CommandsTo(harness.First.Id, SendCommandType.Pause);
        Assert.InRange(pauses.Count, 1, 2);
        Assert.All(
            pauses,
            pause => Assert.InRange(
                pause.When,
                reportTime.AddSeconds(4.5),
                reportTime.AddSeconds(5.5)));
        Assert.True(
            (pauses[^1].When - pauses[0].When).Duration() < TimeSpan.FromMilliseconds(200),
            "the duplicate report moved the recovery deadline");

        Assert.Empty(harness.CommandsTo(harness.First.Id, SendCommandType.Seek));
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);
    }

    [Fact]
    public async Task ExpiredRecovery_IsNotAppliedToANewResume()
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        group.UpdatePing(harness.First, 10);
        group.UpdatePing(harness.Second, 10);

        var startPosition = TimeSpan.FromMinutes(10).Ticks;
        group.HandleRequest(
            harness.First,
            new PlayGroupRequest(new[] { Guid.NewGuid() }, 0, startPosition),
            CancellationToken.None);
        var playlistItemId = group.PlayQueue.GetPlayingItemPlaylistId();
        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, startPosition, true, playlistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, startPosition, true, playlistItemId),
            CancellationToken.None);

        // Both members buffer; the first comes back with a recovery that will expire almost
        // immediately.
        group.HandleRequest(
            harness.First,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, true, playlistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, true, playlistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks - TimeSpan.FromMilliseconds(100).Ticks, true, playlistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        // Let the short recovery deadline pass with the group still waiting.
        await Task.Delay(300, TestContext.Current.CancellationToken);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, playlistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        // The expired recovery is not replayed: the member resumes with the group.
        Assert.Empty(harness.CommandsTo(harness.First.Id, SendCommandType.Pause));
        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.True(unpause.When <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void StalePlaylistItemReport_RetiresTheRecoveryForThePreviousItem()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // The recovering member reports readiness for the item of the previous queue.
        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks - TimeSpan.FromSeconds(5).Ticks, true, harness.PlaylistItemId),
            CancellationToken.None);

        Assert.True(group.IsBuffering(), "a stale report must not mark the member ready");

        // The other member becomes ready; the group keeps waiting for the stale reporter.
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);
        Assert.DoesNotContain(harness.DeliveredCommands, delivery => delivery.Command.Command == SendCommandType.Unpause);

        // The member is ready for the current item: the group resumes with the normal latency.
        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);
        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.True(
            unpause.When < scenario.Deadline.AddMilliseconds(-100),
            $"the previous item's recovery still delayed the group: resumed at {unpause.When:O}");
    }

    [Fact]
    public void RecoveringSessionReachesTheGroupPosition_RetiresItsRecovery()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // The recovering member reaches the group position before its deadline.
        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, true, scenario.PlaylistItemId),
            CancellationToken.None);

        // It is now a ready member and is paused with the rest of the waiting group.
        var pause = harness.CommandsTo(harness.First.Id, SendCommandType.Pause)[^1];
        Assert.True(
            pause.When <= DateTime.UtcNow.AddMilliseconds(300),
            $"the arrived member was still treated as recovering until {pause.When:O}");

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // The group does not wait for the fulfilled recovery when the other member is ready.
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.True(
            unpause.When < scenario.Deadline.AddMilliseconds(-100),
            $"the fulfilled recovery still delayed the group: resumed at {unpause.When:O}");
    }

    [Fact]
    public void Resume_AfterADeferredStop_SupersedesThePause()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        // The other member buffers, scheduling a stop for the recovering member at its deadline.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        // The other member becomes ready: the group resumes at the deadline, and that resume is
        // delivered after the stop it replaces.
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        var relevant = harness.DeliveredCommands
            .Where(delivery => delivery.SessionId == harness.First.Id)
            .Where(delivery => delivery.Command.When >= scenario.Deadline.AddMilliseconds(-100))
            .ToList();
        Assert.Contains(relevant, delivery => delivery.Command.Command == SendCommandType.Pause);
        Assert.Equal(SendCommandType.Unpause, relevant[^1].Command.Command);
    }

    [Fact]
    public void SetPlaylistItem_DuringCatchUp_SupersedesTheRecoveryPlan()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        // A shorter recovery schedules a deferred resume for the other member.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, true, scenario.PlaylistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks - TimeSpan.FromSeconds(1).Ticks, true, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // The group switches to the playing item again, restarting the timeline.
        group.HandleRequest(harness.First, new SetPlaylistItemGroupRequest(scenario.PlaylistItemId), CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);
        Assert.Equal(0, group.PositionTicks);

        harness.PumpScheduledRecoveryResumes(DateTime.UtcNow.AddSeconds(10));
        Assert.DoesNotContain(harness.DeliveredCommands, delivery => delivery.Command.Command == SendCommandType.Unpause);

        // The group starts the new timeline with the normal latency allowance.
        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, 0, false, scenario.PlaylistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, 0, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);
        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.True(unpause.When <= DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(0, unpause.PositionTicks);
    }

    [Fact]
    public void PlayNewQueue_DuringCatchUp_SupersedesTheRecoveryPlan()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        // A shorter recovery schedules a deferred resume for the other member.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, true, scenario.PlaylistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks - TimeSpan.FromSeconds(1).Ticks, true, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        var newPosition = TimeSpan.FromMinutes(3).Ticks;
        group.HandleRequest(
            harness.First,
            new PlayGroupRequest(new[] { Guid.NewGuid() }, 0, newPosition),
            CancellationToken.None);
        var newPlaylistItemId = group.PlayQueue.GetPlayingItemPlaylistId();

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);
        Assert.Equal(newPosition, group.PositionTicks);

        harness.PumpScheduledRecoveryResumes(DateTime.UtcNow.AddSeconds(10));
        Assert.DoesNotContain(harness.DeliveredCommands, delivery => delivery.Command.Command == SendCommandType.Unpause);

        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, newPosition, false, newPlaylistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, newPosition, false, newPlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);
        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.True(unpause.When <= DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(newPosition, unpause.PositionTicks);
    }

    [Fact]
    public void Resume_WithHighAcceptedPing_KeepsTheLatencyAllowance()
    {
        var harness = new GroupHarness();
        var group = harness.Group;

        group.UpdatePing(harness.First, 1200);
        group.UpdatePing(harness.Second, 1200);

        var startPosition = TimeSpan.FromMinutes(10).Ticks;
        group.HandleRequest(
            harness.First,
            new PlayGroupRequest(new[] { Guid.NewGuid() }, 0, startPosition),
            CancellationToken.None);
        var playlistItemId = group.PlayQueue.GetPlayingItemPlaylistId();
        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, startPosition, true, playlistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, startPosition, true, playlistItemId),
            CancellationToken.None);

        // The first member buffers and comes back at the group position while the other member
        // is ready, so the group resumes with the latency allowance implied by the ping.
        group.HandleRequest(
            harness.First,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, true, playlistItemId),
            CancellationToken.None);

        var before = DateTime.UtcNow;
        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, true, playlistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        var scheduledDelay = group.LastActivity - before;
        Assert.InRange(scheduledDelay, TimeSpan.FromMilliseconds(2200), TimeSpan.FromMilliseconds(2700));
    }

    [Fact]
    public void RecoveringMemberLeaves_DuringScheduledResume_StartsPlaybackWithoutItsDeadline()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // The recovering member leaves before playback starts.
        group.SessionLeave(harness.First, new LeaveGroupRequest(), CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        // The remaining member starts after the normal latency allowance, not the departed
        // member's deadline.
        var unpause = Assert.Single(harness.CommandsTo(harness.Second.Id, SendCommandType.Unpause));
        Assert.True(
            unpause.When < scenario.Deadline.AddMilliseconds(-100),
            $"the departed member's recovery still delayed the start: {unpause.When:O}");
        Assert.True(unpause.When <= DateTime.UtcNow.AddSeconds(1));

        // Nothing scheduled for the departed member still points at its old deadline.
        Assert.All(
            harness.CommandsTo(harness.First.Id, SendCommandType.Unpause),
            command => Assert.True(command.When < scenario.Deadline.AddMilliseconds(-100)));
    }

    [Fact]
    public void RecoveringMemberIgnoresWait_DuringScheduledResume_StartsPlaybackWithoutItsDeadline()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        group.HandleRequest(harness.First, new IgnoreWaitGroupRequest(true), CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        var unpause = Assert.Single(harness.CommandsTo(harness.Second.Id, SendCommandType.Unpause));
        Assert.True(
            unpause.When < scenario.Deadline.AddMilliseconds(-100),
            $"the opted-out recovery still delayed the start: {unpause.When:O}");
        Assert.True(unpause.When <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task GroupWaitTimeout_DuringCatchUp_ResumesAtTheRecoveryDeadline()
    {
        var scenario = StartCatchUpRecovery(groupWaitTimeout: 200);
        var harness = scenario.Harness;
        var group = harness.Group;

        // The other member buffers and never reports ready.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // The group gives up on the stalled member, but does not cut the active recovery short.
        await harness.WaitForState(GroupStateType.Playing);

        var firstUnpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.InRange(
            firstUnpause.When,
            scenario.Deadline.AddMilliseconds(-100),
            scenario.Deadline.AddMilliseconds(100));

        var secondUnpause = Assert.Single(harness.CommandsTo(harness.Second.Id, SendCommandType.Unpause));
        Assert.Equal(firstUnpause.When, secondUnpause.When);
    }

    [Fact]
    public void UnpauseDuringCatchUp_StartsTheGroupWithoutWaitingForTheRecovery()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // The user forces playback to start while the group is waiting.
        group.HandleRequest(harness.Second, new UnpauseGroupRequest(), CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        // The forced start uses the normal latency allowance and does not retain the recovery.
        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.True(
            unpause.When < scenario.Deadline.AddMilliseconds(-100),
            $"the forced start kept the recovery deadline: {unpause.When:O}");
        Assert.True(unpause.When <= DateTime.UtcNow.AddSeconds(1));

        // The last command for the recovering member is the forced start, replacing its pending stop.
        var relevant = harness.DeliveredCommands
            .Where(delivery => delivery.SessionId == harness.First.Id)
            .ToList();
        Assert.Equal(SendCommandType.Unpause, relevant[^1].Command.Command);
    }

    [Fact]
    public void ShortRemainingRecovery_StopsAtThePositionAndResumesAfterTheLatencyAllowance()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        // The first member buffers and recovers again with a much shorter remaining recovery.
        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        group.HandleRequest(
            harness.First,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, true, scenario.PlaylistItemId),
            CancellationToken.None);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        var readyTime = DateTime.UtcNow;
        group.HandleRequest(
            harness.First,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks - TimeSpan.FromMilliseconds(300).Ticks, true, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        var pause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Pause));
        Assert.InRange(pause.When, readyTime.AddMilliseconds(100), readyTime.AddMilliseconds(500));

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // The other member is ready: the group starts after the latency allowance, not before the
        // member has stopped at the position.
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        // The recovering member is deferred until it has stopped, so it is not part of the resume.
        Assert.Empty(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));

        harness.PumpScheduledRecoveryResumes(pause.When.AddMilliseconds(50));

        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.InRange(unpause.When, readyTime.AddMilliseconds(400), readyTime.AddMilliseconds(1000));
        Assert.Equal(group.PositionTicks, unpause.PositionTicks);
    }

    [Fact]
    public void IgnoredMemberReportingARecovery_DoesNotDelayTheGroup()
    {
        var scenario = StartCatchUpRecovery(extraMembers: 1);
        var harness = scenario.Harness;
        var group = harness.Group;
        var third = scenario.Extras[0];

        // The third member opts out of the group wait before reporting its own recovery.
        group.HandleRequest(third, new IgnoreWaitGroupRequest(true), CancellationToken.None);

        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // The ignored member reports ready seven seconds behind: longer than the first recovery.
        group.HandleRequest(
            third,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks - TimeSpan.FromSeconds(7).Ticks, true, scenario.PlaylistItemId),
            CancellationToken.None);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        // When the buffering member is ready, the group resumes at the first member's recovery
        // deadline; the ignored member's recovery does not delay it.
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.InRange(unpause.When, scenario.Deadline.AddMilliseconds(-100), scenario.Deadline.AddMilliseconds(100));
    }

    [Fact]
    public void Join_WhileWaitingWithACatchUp_PreservesTheRecovery()
    {
        var scenario = StartCatchUpRecovery();
        var harness = scenario.Harness;
        var group = harness.Group;

        group.HandleRequest(
            harness.Second,
            new BufferGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        harness.Commands.Clear();
        harness.DeliveredCommands.Clear();

        var joiner = harness.NewSession("joiner");
        group.SessionJoin(joiner, new JoinGroupRequest(group.GroupId), CancellationToken.None);

        Assert.Equal(GroupStateType.Waiting, group.GetInfo().State);

        // The recovering member is still only stopped at the group position when it gets there.
        var pause = harness.CommandsTo(harness.First.Id, SendCommandType.Pause)[^1];
        Assert.InRange(pause.When, scenario.Deadline.AddMilliseconds(-100), scenario.Deadline.AddMilliseconds(100));

        // Both the buffering member and the joiner report ready at the group position.
        group.HandleRequest(
            harness.Second,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);
        group.HandleRequest(
            joiner,
            new ReadyGroupRequest(DateTime.UtcNow, group.PositionTicks, false, scenario.PlaylistItemId),
            CancellationToken.None);

        Assert.Equal(GroupStateType.Playing, group.GetInfo().State);

        // The preserved recovery still defines the start.
        var unpause = Assert.Single(harness.CommandsTo(harness.First.Id, SendCommandType.Unpause));
        Assert.InRange(unpause.When, scenario.Deadline.AddMilliseconds(-100), scenario.Deadline.AddMilliseconds(100));
    }

    private sealed class GroupHarness
    {
        private readonly ISessionManager _sessionManager;
        private readonly Guid _userId;

        public GroupHarness(long? groupWaitTimeout = null)
        {
            var userManager = new Mock<IUserManager>();
            var sessionManager = new Mock<ISessionManager>();
            var libraryManager = new Mock<ILibraryManager>();

            var user = new User("tester", "auth-provider", "pwdreset-provider");
            userManager.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns(user);

            var item = new Mock<BaseItem>();
            item.Setup(i => i.IsVisibleStandalone(It.IsAny<User>())).Returns(true);
            item.Object.RunTimeTicks = TimeSpan.FromHours(2).Ticks;
            libraryManager.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns(item.Object);

            sessionManager
                .Setup(m => m.SendSyncPlayCommand(It.IsAny<string>(), It.IsAny<SendCommand>(), It.IsAny<CancellationToken>()))
                .Callback<string, SendCommand, CancellationToken>((sessionId, command, _) =>
                {
                    Commands.Add(command);
                    DeliveredCommands.Add((sessionId, command));
                })
                .Returns(Task.CompletedTask);

            sessionManager
                .Setup(m => m.SendSyncPlayGroupUpdate(It.IsAny<string>(), It.IsAny<GroupUpdate<GroupStateUpdate>>(), It.IsAny<CancellationToken>()))
                .Callback((string sessionId, GroupUpdate<GroupStateUpdate> update, CancellationToken _) => StateUpdates.Add((sessionId, update.Data)))
                .Returns(Task.CompletedTask);

            Group = new SyncPlayGroup(
                NullLoggerFactory.Instance,
                userManager.Object,
                sessionManager.Object,
                libraryManager.Object)
            {
                GroupWaitTimeout = groupWaitTimeout ?? SyncPlayGroup.DefaultGroupWaitTimeout
            };

            _sessionManager = sessionManager.Object;
            _userId = user.Id;

            First = NewSession("first");
            Second = NewSession("second");

            Group.CreateGroup(First, new NewGroupRequest("group"), CancellationToken.None);
            Group.SessionJoin(Second, new JoinGroupRequest(Group.GroupId), CancellationToken.None);
            Group.SetPlayQueue(new List<Guid> { Guid.NewGuid() }, 0, 0);
            PlaylistItemId = Group.PlayQueue.GetPlayingItemPlaylistId();
        }

        public SyncPlayGroup Group { get; }

        public List<(string SessionId, GroupStateUpdate Update)> StateUpdates { get; } = new();

        public SessionInfo First { get; }

        public SessionInfo Second { get; }

        public Guid PlaylistItemId { get; }

        public List<SendCommand> Commands { get; } = new List<SendCommand>();

        /// <summary>
        /// Gets the commands as delivered, with the session that received each one.
        /// </summary>
        public List<(string SessionId, SendCommand Command)> DeliveredCommands { get; } = new();

        /// <summary>
        /// Gets the commands of the given type delivered to a session.
        /// </summary>
        public List<SendCommand> CommandsTo(string sessionId, SendCommandType type)
        {
            return DeliveredCommands
                .Where(delivery => delivery.SessionId == sessionId && delivery.Command.Command == type)
                .Select(delivery => delivery.Command)
                .ToList();
        }

        // Mirrors the sweep SyncPlayManager runs on a timer.
        public void PumpGroupWaitTimeout()
        {
            var group = Group;

            // Group lock required as Group is not thread-safe.
            lock (group)
            {
                group.HandleGroupWaitTimeout(CancellationToken.None);
            }
        }

        // Mirrors the sweep SyncPlayManager runs on a timer.
        public void PumpScheduledRecoveryResumes(DateTime utcNow)
        {
            var group = Group;

            // Group lock required as Group is not thread-safe.
            lock (group)
            {
                group.HandleScheduledRecoveryResumes(utcNow, CancellationToken.None);
            }
        }

        public async Task WaitForState(GroupStateType expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Group.GetInfo().State != expected && DateTime.UtcNow < deadline)
            {
                PumpGroupWaitTimeout();
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.Equal(expected, Group.GetInfo().State);
        }

        public SessionInfo NewSession(string id)
        {
            return new SessionInfo(_sessionManager, NullLogger.Instance)
            {
                Id = id,
                UserId = _userId,
                UserName = id
            };
        }

        public SessionInfo AddSession(string id)
        {
            var session = NewSession(id);
            Group.SessionJoin(session, new JoinGroupRequest(Group.GroupId), CancellationToken.None);
            return session;
        }
    }

    /// <summary>
    /// A playing group whose first member is recovering from a buffer five seconds behind the
    /// frozen group position.
    /// </summary>
    private sealed class CatchUpScenario
    {
        public CatchUpScenario(GroupHarness harness, Guid playlistItemId, DateTime deadline, IReadOnlyList<SessionInfo> extras)
        {
            Harness = harness;
            PlaylistItemId = playlistItemId;
            Deadline = deadline;
            Extras = extras;
        }

        public GroupHarness Harness { get; }

        public Guid PlaylistItemId { get; }

        public DateTime Deadline { get; }

        public IReadOnlyList<SessionInfo> Extras { get; }
    }
}
