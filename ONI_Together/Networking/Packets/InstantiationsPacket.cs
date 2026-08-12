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

		/// <summary>
		/// Announcements satisfied by naming an object the client had already drawn,
		/// rather than adding a second one. The pair of numbers - this against the
		/// preview count - is how to tell whether the matching is working.
		/// </summary>
		public static int AdoptedInstead => _adoptedInstead;

		/// <summary>
		/// Duplicant prefabs refused. Zero is the expected reading once the sender is
		/// fixed; a non-zero one means another sender is still announcing colonists.
		/// </summary>
		public static int RefusedMinions => _refusedMinions;

		private static int _adoptedInstead;
		private static int _refusedMinions;

		public static void ResetForNewSession()
		{
			_adoptedInstead = 0;
			_refusedMinions = 0;
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

			// Refuse duplicants here as well as at the sender.
			//
			// A colonist cannot be rebuilt from a prefab name: personality, traits,
			// aptitudes and name are not in the prefab, and the result is a duplicant
			// with personality 0x0. The renderer reads the personality inside
			// World.LateUpdate, so it throws every frame and the game closes itself -
			// three client sessions ended that way, each one with "Could not find
			// Personality: 0x0" repeating in the last seconds.
			//
			// The sender no longer announces them. This is the second layer, because
			// the cost of one more sender ever reaching this line is a dead client,
			// and refusing it only costs a colonist the telepad path can deliver
			// properly.
			if (prefab.HasTag(GameTags.BaseMinion))
			{
				_refusedMinions++;
				DebugTools.ThrottledLog.Warn(
					$"[InstantiationsPacket] refusing to build a duplicant from the prefab name " +
					$"'{e.PrefabName}' (NetId {e.NetId}): it would have no personality and drawing it " +
					"throws every frame. Duplicants replicate through the telepad path.");
				return;
			}

			// Name what is already here before building another one.
			//
			// The client drew this object itself the moment it was ordered, and left
			// it unnamed on purpose. Instantiating regardless produced two objects
			// for one thing: the named copy from this packet and the client's own
			// with no id. 577 previews created against 38 adopted in eleven minutes,
			// with failed lookups rising from 270 to 3925 over the same period, is
			// what that costs - the host talks about its item, the client is holding
			// a different one, and every packet about it misses.
			int cell = Grid.IsValidCell(Grid.PosToCell(e.Position)) ? Grid.PosToCell(e.Position) : -1;
			if (cell >= 0 && Components.NetworkIdentity.TryAdoptPreview(cell, e.PrefabName, e.NetId))
			{
				_adoptedInstead++;
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
