using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ONI_Together.Networking.Packets;
using ONI_Together.Networking.Packets.Architecture;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Packet types are addressed on the wire by a hash of their type. Two types
    /// that hash alike is not a degraded case - the loser is unroutable for the
    /// whole session, and every packet it should have carried is decoded as the
    /// winner, which corrupts whatever that handler touches.
    ///
    /// It has never happened in a live session here. It is checked because the
    /// cost of finding out the hard way is a desync with no plausible cause, and
    /// because adding a packet type is the most routine change anyone makes.
    /// </summary>
    public static class PacketRegistryTests
    {
        [UnitTest(name: "No two packet types share an id", category: "Packets")]
        public static UnitTestResult NoPacketIdCollisions()
        {
            var byId = new Dictionary<int, List<Type>>();

            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(IPacket).IsAssignableFrom(type)) continue;

                int id = API_Helper.GetHashCode(type);
                if (!byId.TryGetValue(id, out var list))
                    byId[id] = list = new List<Type>();
                list.Add(type);
            }

            if (byId.Count == 0)
                return UnitTestResult.Skip("no packet types found");

            var clash = byId.FirstOrDefault(kvp => kvp.Value.Count > 1);
            if (clash.Value != null)
            {
                return UnitTestResult.Fail(
                    $"id {clash.Key} is shared by {string.Join(", ", clash.Value.Select(t => t.Name))}. " +
                    "One of these cannot be sent or received at all.");
            }

            return UnitTestResult.Pass($"{byId.Count} packet types, all distinct");
        }

        [UnitTest(name: "Every packet type is registered", category: "Packets")]
        public static UnitTestResult EveryPacketIsRegistered()
        {
            // A packet class that never reaches the registry is silently
            // undeliverable: the sender serializes it happily and the receiver
            // rejects the type as unknown.
            var missing = new List<string>();

            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(IPacket).IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;

                if (!PacketRegistry.HasRegisteredPacket(type))
                    missing.Add(type.Name);
            }

            if (missing.Count > 0)
                return UnitTestResult.Fail(
                    $"{missing.Count} packet type(s) are not registered and cannot be received: " +
                    string.Join(", ", missing.Take(8)));

            return UnitTestResult.Pass("every packet type with a default constructor is registered");
        }
    }
}
