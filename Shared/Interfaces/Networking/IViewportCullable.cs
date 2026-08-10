using System;
using System.Collections.Generic;
using System.Text;
using ONI_Together.Networking.Packets.Architecture;

namespace Shared.Interfaces.Networking
{
    /// <summary>
    /// When added to a packet, the host will only broadcast it to clients
    /// whose camera viewport contains the cell returned by <see cref="GetViewportCell"/>.
    ///
    /// Returning a negative cell means "do not cull this one" and the packet
    /// goes to every eligible peer. Some objects have to be reported whether or
    /// not anyone is looking: a duplicant off screen still needs a position, so
    /// that there is something to draw the moment the camera reaches it.
    ///
    /// The sentinel lives here because culling happens inside the sender.
    /// A caller cannot opt out by choosing a different send method - every
    /// broadcast path funnels through the same check - and one that tried to
    /// went unnoticed for a while, because the packet it handed over was
    /// dropped one layer below where it was looking.
    /// </summary>
    public interface IViewportCullable : IPacket
    {
        int GetViewportCell();
    }
}
