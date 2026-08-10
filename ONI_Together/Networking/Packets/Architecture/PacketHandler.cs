using System;
using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Architecture
{

	public static class PacketHandler
	{
		private static bool _readyToProcess = true;
		private static float _notReadySince = float.MaxValue;
		private const float NOT_READY_TIMEOUT = 60f;

		public static bool readyToProcess
		{
			get => _readyToProcess;
			set
			{
				if (!value)
					_notReadySince = Time.unscaledTime;
				_readyToProcess = value;
			}
		}

		/// <summary>
		/// Sequence number of the packet being dispatched right now.
		///
		/// Valid only inside OnDispatched. It exists so a handler can refuse to
		/// apply something older than what it already has - without it, the last
		/// message to arrive always wins, whatever order they were sent in.
		/// </summary>
		public static int CurrentSequence { get; private set; }

		/// <summary>
		/// readyToProcess is turned off before a client loads a world and turned
		/// back on when it reconnects. A load that aborts leaves it off, and
		/// every inbound packet of the next session is dropped until a 60 second
		/// self-heal notices. The self-heal was masking this, not fixing it.
		/// </summary>
		public static void ResetForNewSession()
		{
			_readyToProcess = true;
			_notReadySince = float.MaxValue;
			CurrentSequence = 0;
		}

		public static void HandleIncoming(byte[] data)
		{
			using var _ = Profiler.Scope();

			if (!_readyToProcess)
			{
				if (Time.unscaledTime - _notReadySince > NOT_READY_TIMEOUT)
				{
					DebugConsole.LogWarning($"[PacketHandler] readyToProcess was false for >{NOT_READY_TIMEOUT}s — force-recovering");
					_readyToProcess = true;
				}
				else
				{
					return;
				}
			}

			using (var ms = new MemoryStream(data))
			{
				using (var reader = new BinaryReader(ms))
				{
					int type = (int)reader.ReadInt32();
                    if (!PacketRegistry.HasRegisteredPacket(type))
                    {
                        DebugConsole.LogError($"Invalid PacketType received: {type}", false);
                        return;
                    }

                    using var scope = Profiler.Scope();

                    // Read before deserializing: the sequence sits in the header,
                    // and handlers read it through CurrentSequence to decide
                    // whether what they are being told is newer than what they
                    // already applied.
                    CurrentSequence = reader.ReadInt32();

                    var packet = PacketRegistry.Create(type);
					packet.Deserialize(reader);
					Dispatch(packet);

                    scope.End(packet.GetType().Name, data.Length);

                    PacketTracker.TrackIncoming(new PacketTracker.PacketTrackData
                    {
						packet = packet,
						size = data.Length
                    });
                }
			}
		}

		private static void Dispatch(IPacket packet)
		{
			using var _ = Profiler.Scope();

			packet.OnDispatched();
		}
	}

}