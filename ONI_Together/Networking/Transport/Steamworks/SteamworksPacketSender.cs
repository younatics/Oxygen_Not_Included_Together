using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ClipperLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using Steamworks;

namespace ONI_Together.Networking.Transport.Steam
{
    public class SteamworksPacketSender : TransportPacketSender
    {
        // Steam fragments an unreliable message past roughly one MTU and drops
        // the entire message if any fragment is lost - the same failure Riptide
        // has, at a different threshold. Steam is not the safe transport.
        public const int STEAM_UNRELIABLE_MTU_BYTES = 1200;

        // k_cbMaxSteamNetworkingSocketsMessageSizeSend
        public const int STEAM_MAX_MESSAGE_BYTES = 512 * 1024;

        public override int MaxUnfragmentedPayloadBytes => STEAM_UNRELIABLE_MTU_BYTES;

        public override int MaxMessageBytes => STEAM_MAX_MESSAGE_BYTES;

        protected override bool SendSerialized(object conn, byte[] bytes, IPacket packet, PacketSendMode sendType)
        {
            using var _ = Profiler.Scope();

            if (conn is not HSteamNetConnection)
                return false;

            HSteamNetConnection s_conn = (HSteamNetConnection)conn;

            // The shared sender splits anything over MaxUnfragmentedPayloadBytes,
            // so reaching this with an oversized payload means a single packet
            // genuinely cannot be carried. Steam would answer
            // k_EResultLimitExceeded and, with the log below commented out, the
            // packet used to vanish with no trace at all.
            if (bytes.Length > MaxMessageBytes)
            {
                DebugConsole.LogError(
                    $"[Sockets] refusing {packet.GetType().Name}: {bytes.Length} B exceeds Steam's " +
                    $"{MaxMessageBytes} B message limit.", false);
                return false;
            }

            var _sendType = ConvertSendType(sendType); //(int)sendType;

            IntPtr unmanagedPointer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, unmanagedPointer, bytes.Length);

                var result = SteamNetworkingSockets.SendMessageToConnection(s_conn, unmanagedPointer, (uint)bytes.Length, _sendType, out long msgNum);

                bool sent = result == EResult.k_EResultOK;

                if (!sent)
                {
                    // Was commented out, so every rejected send was invisible.
                    // Rate-limited per packet type rather than silenced: a
                    // failing syncer would otherwise flood the log at its own
                    // tick rate and drown everything else.
                    WarnSendFailed(packet, bytes.Length, result);
                }
                else
                {
                    PacketTracker.TrackSent(new PacketTracker.PacketTrackData
                    {
                        packet = packet,
                        size = bytes.Length
                    });
                    //DebugConsole.Log($"[Sockets] Sent {packet.Type} to conn {conn} ({Utils.FormatBytes(bytes.Length)})");
                }
                return sent;
            }
            finally
            {
                Marshal.FreeHGlobal(unmanagedPointer);
            }
        }

        private static readonly Dictionary<string, int> _failuresByType = new Dictionary<string, int>();
        private static readonly HashSet<string> _oversizeWarned = new HashSet<string>();

        /// <summary>First failure of a type is logged, then every 100th.</summary>
        private static void WarnSendFailed(IPacket packet, int bytes, EResult result)
        {
            string name = packet.GetType().Name;
            _failuresByType.TryGetValue(name, out int count);
            _failuresByType[name] = ++count;

            if (count == 1 || count % 100 == 0)
            {
                DebugConsole.LogError(
                    $"[Sockets] failed to send {name} ({bytes} B, result {result}) - {count} failure(s) of this type",
                    false);
            }
        }

        private static void WarnOversizedUnreliable(IPacket packet, int bytes)
        {
            string name = packet.GetType().Name;
            if (!_oversizeWarned.Add(name)) return;

            DebugConsole.LogWarning(
                $"[Sockets] {name} is {bytes} B sent unreliably, over the {STEAM_UNRELIABLE_MTU_BYTES} B " +
                "unfragmented limit. Steam splits it and discards the whole message if any fragment is lost.");
        }

        public int ConvertSendType(PacketSendMode mode)
        {
            int result = 0;

            // Reliable / Unreliable
            if ((mode & PacketSendMode.Reliable) == PacketSendMode.Reliable)
                result |= 8;  // k_nSteamNetworkingSend_Reliable
            else
                result |= 0;  // k_nSteamNetworkingSend_Unreliable (implicitly 0)

            // Immediate (flush) corresponds to NoNagle behavior
            if ((mode & PacketSendMode.Immediate) == PacketSendMode.Immediate)
                result |= 1;  // k_nSteamNetworkingSend_NoNagle

            // NoDelay (drop if can't send soon)
            if ((mode & PacketSendMode.NoDelay) == PacketSendMode.NoDelay)
                result |= 4;  // k_nSteamNetworkingSend_NoDelay

            return result;
        }
    }
}