using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Transport.Lan;

namespace ONI_Together.DebugTools.UnitTests
{
    public static class NetworkingTests
    {
        [UnitTest(name: "Server is running", category: "Networking")]
        public static UnitTestResult ServerStarts()
        {
            if (!MultiplayerSession.IsHost && !MultiplayerSession.IsClient)
                return UnitTestResult.Skip("not in a session");

            if (NetworkConfig.TransportServer == null)
                return UnitTestResult.Fail("in a session but TransportServer is null");

            return UnitTestResult.Pass("Server is running");
        }

        /// <summary>
        /// Replaces a pair of tests that asserted the transport was Steamworks
        /// and that it was Riptide. Being mutually exclusive, one always failed,
        /// so a healthy run never looked healthy.
        /// </summary>
        [UnitTest(name: "Active transport matches configuration", category: "Networking")]
        public static UnitTestResult TransportMatchesConfig()
        {
            if (!MultiplayerSession.IsHost && !MultiplayerSession.IsClient)
                return UnitTestResult.Skip($"not in a session; transport is {NetworkConfig.transport}");

            var configured = (NetworkConfig.NetworkTransport)Configuration.Instance.Host.NetworkTransport;
            if (MultiplayerSession.IsHost && NetworkConfig.transport != configured)
                return UnitTestResult.Fail($"host is on {NetworkConfig.transport} but configured for {configured}");

            return UnitTestResult.Pass($"running on {NetworkConfig.transport}");
        }

        [UnitTest(name: "Check for duplicate network identities", category: "Networking")]
        public static UnitTestResult CheckForDuplicateNetworkIdentities()
        {
            // Report the key, the object and the component, not just the count.
            //
            // "NetId X has 2 identities" covers two different bugs that need
            // different fixes: one object filed under two keys, and one key
            // claimed by two objects. A count cannot tell them apart, and a
            // colony carrying a collision baked into its save produced exactly
            // this line for several runs with no way to say which it was.
            var byId = new Dictionary<int, List<string>>();

            foreach (var entry in NetworkIdentityRegistry.AllEntries)
            {
                var identity = entry.Value;
                if (identity.IsNullOrDestroyed()) continue;

                string where;
                try
                {
                    var go = identity.gameObject;
                    where = $"{go.PrefabID()}@{Grid.PosToCell(go)} obj#{go.GetInstanceID()} cmp#{identity.GetInstanceID()} filedUnder={entry.Key}";
                }
                catch
                {
                    where = $"unreadable filedUnder={entry.Key}";
                }

                if (!byId.TryGetValue(identity.NetId, out var list))
                {
                    list = new List<string>();
                    byId[identity.NetId] = list;
                }
                list.Add(where);
            }

            foreach (var kv in byId)
            {
                if (kv.Value.Count <= 1) continue;

                // Same component twice means one object filed under two keys;
                // different components mean two objects sharing an id.
                bool sameComponent = kv.Value
                    .Select(s => s.Substring(s.IndexOf("cmp#", StringComparison.Ordinal)))
                    .Distinct().Count() == 1;

                return UnitTestResult.Fail(
                    $"NetId {kv.Key} has {kv.Value.Count} identities " +
                    $"({(sameComponent ? "one object filed under two keys" : "two objects sharing one id")}): " +
                    string.Join(" | ", kv.Value));
            }

            return UnitTestResult.Pass("No duplicate network identities found");
        }

        [UnitTest(name: "TCP file transfer server ready (host, LAN)", category: "Networking")]
        public static UnitTestResult TcpTransferServerReady()
        {
            if (!MultiplayerSession.IsHost)
                return UnitTestResult.Skip("only the host runs the TCP transfer server");

            if (!NetworkConfig.IsLanConfig())
                return UnitTestResult.Skip("not on the Riptide/LAN transport");

            if (NetworkConfig.TransportServer is not RiptideServer server)
                return UnitTestResult.Fail("TransportServer is not a RiptideServer");

            if (server.TcpTransfer == null)
                return UnitTestResult.Fail("TcpFileTransfer is null (listener failed to start, UDP fallback in use)");

            int riptidePort = Configuration.Instance.Host.LanSettings.Port;
            return UnitTestResult.Pass($"TcpFileTransferServer running on port {riptidePort + 1}");
        }

        [UnitTest(name: "UDP save-transfer fallback pipeline registered", category: "Networking")]
        public static UnitTestResult UdpFallbackAvailable()
        {
            if (!PacketRegistry.HasRegisteredPacket(typeof(SaveFileRequestPacket)))
                return UnitTestResult.Fail("SaveFileRequestPacket not registered");

            if (!PacketRegistry.HasRegisteredPacket(typeof(SaveFileChunkPacket)))
                return UnitTestResult.Fail("SaveFileChunkPacket not registered");

            return UnitTestResult.Pass("Save-transfer UDP fallback packets are registered");
        }

        [UnitTest(name: "Auto chunking: split and reassemble roundtrip", category: "Networking")]
        public static UnitTestResult AutoChunkingWorks()
        {
            const int payloadSize = 2500;
            const int chunkSize = 900;

            byte[] payload = new byte[payloadSize];
            var rnd = new Random(42);
            rnd.NextBytes(payload);

            int totalChunks = (payloadSize + chunkSize - 1) / chunkSize;
            int sequence = ChunkedPacket.GetNextSequenceId();

            var roundtripped = new List<byte[]>(totalChunks);
            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * chunkSize;
                int length = Math.Min(chunkSize, payloadSize - offset);
                byte[] slice = new byte[length];
                Array.Copy(payload, offset, slice, 0, length);

                var chunk = new ChunkedPacket
                {
                    SequenceId = sequence,
                    ChunkIndex = i,
                    TotalChunks = totalChunks,
                    ChunkData = slice
                };

                using var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                    chunk.Serialize(w);
                ms.Position = 0;
                var copy = new ChunkedPacket();
                using (var r = new BinaryReader(ms, Encoding.UTF8, true))
                    copy.Deserialize(r);

                if (copy.SequenceId != sequence || copy.ChunkIndex != i || copy.TotalChunks != totalChunks)
                    return UnitTestResult.Fail($"Chunk {i} header did not roundtrip");

                if (copy.ChunkData.Length != length)
                    return UnitTestResult.Fail($"Chunk {i} data length mismatch: got {copy.ChunkData.Length}, expected {length}");

                roundtripped.Add(copy.ChunkData);
            }

            byte[] reassembled = new byte[payloadSize];
            int writeOffset = 0;
            foreach (var part in roundtripped)
            {
                Array.Copy(part, 0, reassembled, writeOffset, part.Length);
                writeOffset += part.Length;
            }

            if (writeOffset != payloadSize)
                return UnitTestResult.Fail($"Reassembled size {writeOffset} != original {payloadSize}");

            for (int i = 0; i < payloadSize; i++)
            {
                if (reassembled[i] != payload[i])
                    return UnitTestResult.Fail($"Reassembled byte {i} differs from original");
            }

            return UnitTestResult.Pass($"Chunked {payloadSize} bytes into {totalChunks} chunks and reassembled byte-identical");
        }

        [UnitTest(name: "All expected clients connected", category: "Networking")]
        public static UnitTestResult AllClientsConnected()
        {
            if (!MultiplayerSession.InSession)
                return UnitTestResult.Skip("not in a multiplayer session");

            var transportClients = NetworkConfig.GetConnectedClients();
            if (transportClients.Count == 0)
                return UnitTestResult.Fail("Transport reports zero connected clients");

            // The two lists mean different things on the two roles, and this
            // used to compare them as if they did not.
            //
            // A client's ConnectedPlayers holds the host and nothing else - that
            // is deliberate, and RiptideClient says so where it builds it - while
            // its transport list holds every peer including itself. So the
            // comparison below could never pass on a client, and it failed on
            // every single run with "Transport client 2 is missing from
            // ConnectedPlayers". Two is the client itself.
            //
            // A permanently failing check is worse than no check: it is noise in
            // the place where a real regression would have to appear, and by the
            // end I was reading past it.
            if (MultiplayerSession.IsClient)
            {
                if (!MultiplayerSession.ConnectedPlayers.ContainsKey(MultiplayerSession.HostUserID))
                    return UnitTestResult.Fail("a client must know the host, and this one does not");

                if (MultiplayerSession.ConnectedPlayers.Count != 1)
                {
                    return UnitTestResult.Fail(
                        $"a client's ConnectedPlayers should hold only the host, this one holds " +
                        $"{MultiplayerSession.ConnectedPlayers.Count}");
                }

                ulong self = RiptideClient.CLIENT_ID;
                if (self != 0 && !transportClients.Contains(self))
                    return UnitTestResult.Fail($"the transport does not list this client ({self}) among its peers");

                return UnitTestResult.Pass(
                    $"client knows the host; transport lists {transportClients.Count} peer(s)");
            }

            int sessionCount = MultiplayerSession.ConnectedPlayers.Count;
            if (sessionCount != transportClients.Count)
                return UnitTestResult.Fail($"Session has {sessionCount} players but transport reports {transportClients.Count}");

            foreach (var clientId in transportClients)
            {
                if (!MultiplayerSession.ConnectedPlayers.ContainsKey(clientId))
                    return UnitTestResult.Fail($"Transport client {clientId} is missing from ConnectedPlayers");
            }

            return UnitTestResult.Pass($"{transportClients.Count} clients connected and tracked in session");
        }

        [UnitTest(name: "Packet routing: host never sends to itself", category: "Networking")]
        public static UnitTestResult PacketRouting()
        {
            if (!MultiplayerSession.InSession)
                return UnitTestResult.Skip("not in a multiplayer session");

            if (!MultiplayerSession.HostUserID.IsValid())
                return UnitTestResult.Fail("HostUserID is not valid");

            if (MultiplayerSession.IsHost && MultiplayerSession.LocalUserID != MultiplayerSession.HostUserID)
                return UnitTestResult.Fail($"IsHost but LocalUserID {MultiplayerSession.LocalUserID} != HostUserID {MultiplayerSession.HostUserID}");

            foreach (var kvp in MultiplayerSession.ConnectedPlayers)
            {
                var player = kvp.Value;
                if (player == null)
                    return UnitTestResult.Fail($"Player {kvp.Key} entry is null");
                if (player.PlayerId != kvp.Key)
                    return UnitTestResult.Fail($"ConnectedPlayers key {kvp.Key} != PlayerId {player.PlayerId}");
            }

            if (MultiplayerSession.IsClient)
            {
                if (MultiplayerSession.ConnectedPlayers.ContainsKey(MultiplayerSession.LocalUserID))
                    return UnitTestResult.Fail("Client's own LocalUserID is listed in ConnectedPlayers (only host should be there)");
            }

            return UnitTestResult.Pass("Session routing state is consistent; host self-send guard can function");
        }

    }
}
