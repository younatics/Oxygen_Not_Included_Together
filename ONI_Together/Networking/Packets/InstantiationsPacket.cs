using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets
{
	public class InstantiationsPacket : IPacket
	{
		private const int MaxCompressedBytes = 16 * 1024 * 1024;
		private const int MaxInstantiationCount = 8192;

		public List<InstantiationEntry> Entries = new List<InstantiationEntry>();

		public struct InstantiationEntry
		{
			/// <summary>
			/// The id the host gave this object.
			///
			/// Without it the client instantiates the object and then names it
			/// itself, which is the divergence this packet exists to prevent -
			/// and is very likely why announcing spawns was left commented out:
			/// it could not have worked.
			/// </summary>
			public int NetId;

			public string PrefabName;
			public Vector3 Position;
			public Quaternion Rotation;
			public string ObjectName;
			public bool InitializeId;
			public int GameLayer;
		}

		public void Serialize(BinaryWriter w)
		{
			using var _ = Profiler.Scope();

			using (var ms = new MemoryStream())
			{
				using (var deflate = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
				{
					using (var tempWriter = new BinaryWriter(deflate))
					{
						tempWriter.Write(Entries.Count);
						foreach (var e in Entries)
						{
							tempWriter.Write(e.NetId);
							tempWriter.Write(e.PrefabName ?? "");
							tempWriter.Write(e.Position.x); tempWriter.Write(e.Position.y); tempWriter.Write(e.Position.z);
							tempWriter.Write(e.Rotation.x); tempWriter.Write(e.Rotation.y); tempWriter.Write(e.Rotation.z); tempWriter.Write(e.Rotation.w);
							tempWriter.Write(e.ObjectName ?? "");
							tempWriter.Write(e.InitializeId);
							tempWriter.Write(e.GameLayer);
						}
					}
				}

				byte[] compressed = ms.ToArray();
				w.Write(compressed.Length);
				w.Write(compressed);
			}
		}

		public void Deserialize(BinaryReader r)
		{
			using var _ = Profiler.Scope();

			int compressedLength = r.ReadInt32();
			if (compressedLength < 0 || compressedLength > MaxCompressedBytes)
			{
				DebugConsole.LogWarning($"[InstantiationsPacket] Invalid compressed payload length: {compressedLength}");
				Entries = [];
				return;
			}
			byte[] compressedData = r.ReadBytes(compressedLength);

			using (var ms = new MemoryStream(compressedData))
			{
				using (var deflate = new DeflateStream(ms, CompressionMode.Decompress))
				{
					using (var tempReader = new BinaryReader(deflate))
					{
						int count = tempReader.ReadInt32();
						if (count < 0 || count > MaxInstantiationCount)
						{
							DebugConsole.LogWarning($"[InstantiationsPacket] Invalid instantiation count: {count}");
							Entries = [];
							return;
						}
						Entries = new List<InstantiationEntry>(count);

						for (int i = 0; i < count; i++)
						{
							Entries.Add(new InstantiationEntry
							{
								NetId = tempReader.ReadInt32(),
								PrefabName = tempReader.ReadString(),
								Position = new Vector3(tempReader.ReadSingle(), tempReader.ReadSingle(), tempReader.ReadSingle()),
								Rotation = new Quaternion(tempReader.ReadSingle(), tempReader.ReadSingle(), tempReader.ReadSingle(), tempReader.ReadSingle()),
								ObjectName = tempReader.ReadString(),
								InitializeId = tempReader.ReadBoolean(),
								GameLayer = tempReader.ReadInt32()
							});
						}
					}
				}
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost) return;

			// A joining client enables packet processing while still in the menu
			// so the save transfer can run, and the host starts sending spawns
			// straight away. Instantiating with no world runs OnPrefabInit
			// against an empty Grid, and Pickupable divides by Grid.WidthInCells
			// - zero until a world exists.
			if (Grid.WidthInCells == 0)
			{
				DebugConsole.LogWarning(
					$"[InstantiationsPacket] ignoring {Entries.Count} spawns: no world loaded yet");
				return;
			}

			foreach (var e in Entries)
				Instantiate(e);
		}

		private void Instantiate(InstantiationEntry e)
		{
			using var _ = Profiler.Scope();

			GameObject prefab = Assets.GetPrefab(e.PrefabName);
			if (prefab == null)
			{
				DebugConsole.LogWarning($"[InstantiationsPacket] Missing prefab '{e.PrefabName}'");
				return;
			}

			GameObject obj = Object.Instantiate(prefab, e.Position, e.Rotation);
			if (obj == null)
			{
				DebugConsole.LogWarning($"[InstantiationsPacket] Failed to instantiate prefab '{e.PrefabName}'");
				return;
			}

			if (e.GameLayer != 0)
				obj.SetLayerRecursively(e.GameLayer);

			obj.name = e.ObjectName ?? prefab.name;

			KPrefabID id = obj.GetComponent<KPrefabID>();
			if (id != null)
			{
				if (e.InitializeId)
				{
					id.InstanceID = KPrefabID.GetUniqueID();
					KPrefabIDTracker.Get().Register(id);
				}

				id.InitializeTags(force_initialize: true);

				KPrefabID source = prefab.GetComponent<KPrefabID>();
				if (source != null)
				{
					id.CopyTags(source);
					id.CopyInitFunctions(source);
				}

				id.RunInstantiateFn();
			}

			// Take the host's name for it. Instantiating without this leaves the
			// client holding an object the host cannot address, which is the
			// divergence this packet is meant to close rather than widen.
			if (e.NetId != 0 && obj.TryGetComponent<Components.NetworkIdentity>(out var identity))
				identity.OverrideNetId(e.NetId);

			obj.SetActive(true);
		}
	}
}
