#nullable disable

using System;

namespace MediaBrowser.Controller.SyncPlay
{
    /// <summary>
    /// Describes a session that is catching up to the group position after buffering.
    /// </summary>
    /// <remarks>
    /// A recovery is a single coherent value: the session, the playback timeline it belongs to,
    /// the position it is heading to and the time it is expected to get there. Keeping it
    /// immutable means playback decisions never observe a half-updated recovery.
    /// </remarks>
    public sealed class PlaybackRecovery
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PlaybackRecovery"/> class.
        /// </summary>
        /// <param name="sessionId">The identifier of the recovering session.</param>
        /// <param name="playlistItemId">The playlist identifier of the playing item.</param>
        /// <param name="targetPositionTicks">The position the session is catching up to.</param>
        /// <param name="expectedCompletion">The UTC time at which the session is expected to reach the target position.</param>
        public PlaybackRecovery(string sessionId, Guid playlistItemId, long targetPositionTicks, DateTime expectedCompletion)
        {
            SessionId = sessionId;
            PlaylistItemId = playlistItemId;
            TargetPositionTicks = targetPositionTicks;
            ExpectedCompletion = expectedCompletion;
        }

        /// <summary>
        /// Gets the identifier of the recovering session.
        /// </summary>
        /// <value>The session identifier.</value>
        public string SessionId { get; }

        /// <summary>
        /// Gets the playlist identifier of the playing item the recovery belongs to.
        /// </summary>
        /// <value>The playlist item identifier.</value>
        public Guid PlaylistItemId { get; }

        /// <summary>
        /// Gets the position ticks the session is catching up to.
        /// </summary>
        /// <value>The target position ticks.</value>
        public long TargetPositionTicks { get; }

        /// <summary>
        /// Gets the UTC time at which the session is expected to reach the target position.
        /// </summary>
        /// <value>The expected completion time.</value>
        public DateTime ExpectedCompletion { get; }
    }
}
