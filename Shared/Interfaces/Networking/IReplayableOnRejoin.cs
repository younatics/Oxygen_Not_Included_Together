using ONI_Together.Networking.Packets.Architecture;

namespace Shared.Interfaces.Networking
{
    /// <summary>
    /// Marks a packet the host should keep briefly and resend to a client that was
    /// disconnected while it went out.
    ///
    /// For packets that create or complete a thing and are never repeated. A build order
    /// is announced once; if the client is not there to hear it, nothing says it again,
    /// and the building simply does not exist on that peer for the rest of the session.
    /// State that is resent periodically - storage contents, building flags, vitals -
    /// does not want this, because its next keyframe already repairs the gap.
    ///
    /// Not for anything whose reapplication is destructive. Replays are bounded to the
    /// window the client was away, but that window is judged from the host's clock, and
    /// a packet that must not be applied twice should not rely on that being exact.
    /// </summary>
    public interface IReplayableOnRejoin : IPacket
    {
    }
}
