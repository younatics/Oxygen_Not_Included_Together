using ONI_Together.Networking.Packets.Architecture;

namespace Shared.Interfaces.Networking
{
    /// <summary>
    /// Marks a packet that addresses objects by NetId, and so cannot be applied
    /// by a peer whose world has not spawned yet. The host does not send these
    /// to a client it knows is still loading.
    ///
    /// A joining client answers every one of these with a lookup failure, because
    /// its registry is empty - and those failures are the divergence gate, so a
    /// clean join was being reported as a desync. Eight of a run's distinct
    /// failing ids arrived in one burst seven seconds before the client's world
    /// spawned, all logged against a registry holding zero objects. They were
    /// never going to land; sending them spends bandwidth during the hard sync,
    /// which is the moment there is least of it, to produce a warning.
    ///
    /// Not for anything the client needs in order to finish loading. The hard
    /// sync, chat, session and ready-state traffic must keep flowing to a
    /// loading client - block those and the join never completes. If in doubt,
    /// leave the marker off: the cost is a warning, and the cost of a wrong
    /// marker is a client that cannot join.
    /// </summary>
    public interface IRequiresLoadedWorld : IPacket
    {
    }
}
