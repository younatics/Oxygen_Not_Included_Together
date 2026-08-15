using ONI_Together.DebugTools;
using ONI_Together.Networking;
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

		/// <summary>
		/// Announcements that ended with this peer holding the host's number, and
		/// announcements whose number was thrown away because no identity could be
		/// attached. The second was the silent hole: it read 0 forever by not existing,
		/// while the branch it describes was dropping names on every loose item the
		/// client's own simulation had also made.
		/// </summary>
		public static int NamedOnArrival => _namedOnArrival;

		/// <summary>
		/// Announcements for an object this peer already holds under that id. Non-zero is
		/// normal after a reconnect and each one is a duplicate that used to be created.
		/// </summary>
		public static int AlreadyHere => _alreadyHere;
		public static int NamesDropped => _namesDropped;

		private static int _adoptedInstead;
		private static int _refusedMinions;
		private static int _namedOnArrival;
		private static int _namesDropped;
		private static int _alreadyHere;

		public static void ResetForNewSession()
		{
			_adoptedInstead = 0;
			_refusedMinions = 0;
			_namedOnArrival = 0;
			_namesDropped = 0;
			_alreadyHere = 0;
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

			// Already holding this id? Then this is a repeat, not a spawn.
			//
			// SpawnPrefabPacket has had this check since the day announcing critters
			// started working, and this path never got it. A reconnect is where it costs:
			// the client rebuilds its world, the host announces again, and every
			// announcement for an object the client already has becomes a second copy.
			//
			// Measured across a three-run batch. The two ordinary runs end with both
			// peers holding 77 critters; the reconnect run leaves the client with 80,
			// and the extras include an adult Pacu, which no hatch can explain - only a
			// second copy of one the client already had.
			//
			// Checked before the preview match rather than after, because a client that
			// already holds the id needs neither branch.
			if (e.NetId != 0
				&& NetworkIdentityRegistry.TryGet(e.NetId, out var already)
				&& already != null && !already.gameObject.IsNullOrDestroyed())
			{
				_alreadyHere++;
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
			// AddOrGet, not TryGetComponent, and this is the whole defect.
			//
			// The object here was made a line ago with Object.Instantiate and has not
			// been activated yet, so its OnSpawn has not run - and OnSpawn is where this
			// mod attaches NetworkIdentity. TryGetComponent therefore found nothing on
			// exactly the objects that most needed naming, and the branch fell through
			// in silence: no warning, no counter, nothing in the log at all.
			//
			// Measured end to end rather than reasoned about. The host announced
			// BasicPlantFood#-558423651, PlantFiber#915634576 and
			// BasicPlantFood#-1775399734; the transport counters read sent 221 and
			// received 221, so every announcement arrived; and the client's log does not
			// contain those ids anywhere. Every other path through this method logs
			// something, which left exactly one branch that could swallow an
			// announcement without a trace - the one looking for a component that was
			// never going to be there yet.
			//
			// It produces both halves of the loose-item gap at once. The host holds a
			// name for an object the client's registry has never heard of, and the client
			// holds its own copy with no name, because when the object finally activates,
			// its OnSpawn asks for an id and a client is not allowed to mint one.
			//
			// SpawnPrefabPacket has used AddOrGet all along and does not have this
			// problem. Copying the pattern that already works instead of inventing a
			// second one is this repository's own rule, written down after three
			// compile failures spent guessing at game API.
			if (e.NetId != 0)
			{
				var identity = obj.AddOrGet<Components.NetworkIdentity>();
				if (identity == null)
				{
					_namesDropped++;
					DebugTools.ThrottledLog.Warn(
						$"[InstantiationsPacket] could not attach an identity to " +
						$"'{e.PrefabName}', so NetId {e.NetId} is unclaimed on this peer");
				}
				else
				{
					identity.OverrideNetId(e.NetId);
					_namedOnArrival++;
				}
			}

			// After the name, so the object's own OnSpawn finds an id already on it and
			// registers under the host's number instead of asking for one and being
			// refused.
			obj.SetActive(true);
		}
	}
}
