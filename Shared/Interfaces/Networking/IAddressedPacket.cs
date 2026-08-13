using ONI_Together.Networking.Packets.Architecture;

namespace Shared.Interfaces.Networking
{
    /// <summary>
    /// A packet that is about one specific object and is useless without its address.
    ///
    /// Some objects deliberately have no NetId. Gas clouds and loose liquid are refused
    /// one on purpose - they merge and split constantly and an address for them would be
    /// a lie - and the refusal is counted as refusedAsked, 659 to 1221 times a run on the
    /// host. What the refusal does not do is stop anyone sending about them:
    /// GetNetId hands back a zero, the caller writes that zero into a packet without
    /// knowing anything happened, and the packet goes out.
    ///
    /// The receiver can only throw it away. Measured on a client: "Could not resolve
    /// workable 0 for worker Humphrey", and a gate reporting 44 to 114 packets a run
    /// arriving with no id, StandardWorker_WorkingState_Packet and WorkableProgressPacket
    /// named as the senders both times.
    ///
    /// So a packet that knows it has no subject says so and is dropped before it costs
    /// bandwidth during a hard sync, which is the moment there is least of it.
    ///
    /// This does not decide which objects deserve an address - that question is open,
    /// three attempts at it are recorded in NetworkIdentity, and every Pickupable being
    /// a Workable is why "can it be worked" does not separate a gas cloud from a bottle
    /// somebody is hauling. It only stops the traffic that was already lost from being
    /// lost noisily at the far end.
    ///
    /// Because of that, the drop is counted rather than silent. A blocking fix usually
    /// moves a failure to an earlier counter instead of removing it, and this project
    /// has read that as an improvement once already.
    /// </summary>
    public interface IAddressedPacket : IPacket
    {
        /// <summary>
        /// False when the object this packet is about has no NetId, so no receiver
        /// could act on it.
        /// </summary>
        bool IsAddressable { get; }
    }
}
