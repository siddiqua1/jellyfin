#nullable disable

using System;
using System.Threading;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Controller.SyncPlay.GroupStates
{
    /// <summary>
    /// Class PlayingGroupState.
    /// </summary>
    /// <remarks>
    /// Class is not thread-safe, external locking is required when accessing methods.
    /// </remarks>
    public class PlayingGroupState : AbstractGroupState
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PlayingGroupState"/> class.
        /// </summary>
        /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
        public PlayingGroupState(ILoggerFactory loggerFactory)
            : base(loggerFactory)
        {
        }

        /// <inheritdoc />
        public override GroupStateType Type { get; } = GroupStateType.Playing;

        /// <summary>
        /// Gets or sets a value indicating whether requests for buffering should be ignored.
        /// </summary>
        public bool IgnoreBuffering { get; set; }

        /// <inheritdoc />
        public override void SessionJoined(IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Wait for session to be ready.
            var waitingState = new WaitingGroupState(LoggerFactory);
            context.SetState(waitingState);
            waitingState.SessionJoined(context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void SessionLeaving(IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // A departing session may have been the one the scheduled start was waiting for.
            context.RefreshScheduledStart(session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(IgnoreWaitGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            base.HandleRequest(request, context, prevState, session, cancellationToken);

            // A session that stops following the group no longer defines the scheduled start.
            context.RefreshScheduledStart(session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(PlayGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Change state.
            var waitingState = new WaitingGroupState(LoggerFactory);
            context.SetState(waitingState);
            waitingState.HandleRequest(request, context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(UnpauseGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            if (!prevState.Equals(Type))
            {
                // Unpause group and set starting point in future.
                // Clients will start playback at LastActivity (datetime) from PositionTicks (playback position).
                // The added delay does not guarantee, of course, that the command will be received in time.
                // Playback synchronization will mainly happen client side.
                context.StartGroupPlayback(session, cancellationToken);

                // Notify relevant state change event.
                SendGroupStateUpdate(context, request, session, cancellationToken);
            }
            else
            {
                // Client got lost, sending the command of its current playback plan.
                context.SendCurrentPlaybackPlanTo(session, cancellationToken);
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(PauseGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Change state.
            var pausedState = new PausedGroupState(LoggerFactory);
            context.SetState(pausedState);
            pausedState.HandleRequest(request, context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(StopGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Change state.
            var idleState = new IdleGroupState(LoggerFactory);
            context.SetState(idleState);
            idleState.HandleRequest(request, context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(SeekGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Change state.
            var waitingState = new WaitingGroupState(LoggerFactory);
            context.SetState(waitingState);
            waitingState.HandleRequest(request, context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(BufferGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            if (IgnoreBuffering)
            {
                return;
            }

            // A session that already stopped at the target as part of the current plan is paused
            // there on purpose. The seek that applies the stop can make the player emit another
            // buffering event; that report must not restart the wait and cancel the resume that
            // is owed to the session. The playlist item stays validated below, so a report for
            // another timeline still follows the normal path.
            if (!request.IsPlaying
                && request.PlaylistItemId.Equals(context.PlayQueue.GetPlayingItemPlaylistId())
                && context.IsAtPlannedRecoveryStop(session, request.PositionTicks))
            {
                return;
            }

            // Change state.
            var waitingState = new WaitingGroupState(LoggerFactory);
            context.SetState(waitingState);
            waitingState.HandleRequest(request, context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(ReadyGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            if (prevState.Equals(Type))
            {
                // Group was not waiting, make sure the client has the command of its current
                // playback plan without cancelling a pending recovery stop.
                context.SendCurrentPlaybackPlanTo(session, cancellationToken);
            }
            else if (prevState.Equals(GroupStateType.Waiting))
            {
                // Notify relevant state change event.
                SendGroupStateUpdate(context, request, session, cancellationToken);
            }
        }

        /// <inheritdoc />
        public override void HandleRequest(NextItemGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Change state.
            var waitingState = new WaitingGroupState(LoggerFactory);
            context.SetState(waitingState);
            waitingState.HandleRequest(request, context, Type, session, cancellationToken);
        }

        /// <inheritdoc />
        public override void HandleRequest(PreviousItemGroupRequest request, IGroupStateContext context, GroupStateType prevState, SessionInfo session, CancellationToken cancellationToken)
        {
            // Change state.
            var waitingState = new WaitingGroupState(LoggerFactory);
            context.SetState(waitingState);
            waitingState.HandleRequest(request, context, Type, session, cancellationToken);
        }
    }
}
