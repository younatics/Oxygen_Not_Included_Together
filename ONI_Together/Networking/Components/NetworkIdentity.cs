using KSerialization;
using ONI_Together.DebugTools;
using System.Collections.Generic;
using System.IO;
using Shared.Profiling;

namespace ONI_Together.Networking.Components
{
	[SerializationConfig(MemberSerialization.OptIn)]
	public class NetworkIdentity : KMonoBehaviour
	{
		[Serialize]
		public int NetId = 0;

		[SkipSaveFileSerialization]
		private bool IsRegistered = false;

		/// <summary>
		/// Drawn on a client before the host has named it. Visible, not
		/// addressable, and not in the registry until OverrideNetId arrives.
		/// </summary>
		[SkipSaveFileSerialization]
		public bool IsClientPreview { get; private set; }

		/// <summary>
		/// How many previews were drawn and how many were later named by the
		/// host. A gap between them is the count of objects a client can see but
		/// cannot address, which is otherwise only visible as failed lookups
		/// somewhere else entirely.
		/// </summary>
		public static int PreviewsCreated { get; private set; }
		public static int PreviewsAdopted { get; private set; }

		/// <summary>
		/// Previews broken down by prefab. The totals said 38 previews for 20
		/// dig orders without saying what the other eighteen were, and the set
		/// of prefabs that actually need a network identity is what decides how
		/// expensive host-authoritative spawning would be.
		/// </summary>
		private static readonly Dictionary<string, int> _previewsByPrefab = new();

		/// <summary>
		/// Id reserved for the very next NetworkIdentity to spawn.
		///
		/// A handler that creates an object the host has already named used to
		/// let it mint its own id first and replace it a moment later. For that
		/// moment the object sat in the registry under a foreign id, which is a
		/// breakoff slot every other object in that cell then had to step over -
		/// an Iron pile came out differing from the host's by exactly 2. Claiming
		/// the id up front means it is registered once, under the right name.
		/// </summary>
		private static int _reservedNetId;

		public static void ReserveNextNetId(int netId) => _reservedNetId = netId;

		public static void ResetPreviewCounters()
		{
			PreviewsCreated = 0;
			PreviewsAdopted = 0;
			_previewsByPrefab.Clear();
		}

		public static void DumpPreviewBreakdown(string tag)
		{
			foreach (var kvp in _previewsByPrefab)
				DebugConsole.Log($"{tag} preview|{kvp.Key}|{kvp.Value}");
		}

		public override void OnSpawn()
		{
			using var _ = Profiler.Scope();

			base.OnSpawn();

			// Read here rather than in the patch: OnSpawn runs inside the
			// KInstantiate call that set it, and only objects that carry a
			// NetworkIdentity care.
			if (_reservedNetId != 0)
			{
				NetId = _reservedNetId;
				_reservedNetId = 0;
			}

			if (KInstantiatePatch.ConsumeClientPreviewFlag())
			{
				IsClientPreview = true;
				PreviewsCreated++;

				string prefab = gameObject.name ?? "?";
				_previewsByPrefab.TryGetValue(prefab, out int n);
				_previewsByPrefab[prefab] = n + 1;
			}

			RegisterIdentity();
		}

		public void RegisterIdentity()
		{
			using var _ = Profiler.Scope();

			if (IsRegistered)
				return;

			// A client-side preview draws immediately so the game stays
			// responsive, but it must not mint its own id: the host has not
			// issued one, so anything the client registered under it would be
			// an address the host cannot use. It stays unregistered until
			// OverrideNetId hands it the host's id.
			if (IsClientPreview)
				return;

			if (Grid.WidthInCells == 0)
			{
				// DebugConsole.LogWarning($"[NetworkIdentity] Skipping registration for {gameObject.name} - Grid not ready");
				return;
			}

			// Try to handle deterministic ID for buildings first
			if (NetId == 0)
			{
				if (TryGetComponent<Building>(out var building))
				{
					int detId = NetIdHelper.GetDeterministicBuildingId(gameObject);
					if (detId != 0)
					{
						NetId = detId;
						// DebugConsole.Log($"[NetworkIdentity] Generated Deterministic NetId {detId} for building {gameObject.name}");
					}
				}
				else if(TryGetComponent<Workable>(out var workable))
				{
					int detId = NetIdHelper.GetDeterministicWorkableId(gameObject);
					if (detId != 0)
					{
						NetId = detId;
					}
				}
				else
				{
					int detId = NetIdHelper.GetDeterministicEntityId(gameObject);
					if (detId != 0)
					{
						NetId = detId;
						// DebugConsole.Log($"[NetworkIdentity] Generated Deterministic NetId {detId} for building {gameObject.name}");
					}
				}
				//DebugConsole.Log($"[NetworkIdentity] Generated Deterministic NetId {NetId} for {gameObject.name}");
			}

			if (NetId == 0)
			{
				NetId = NetworkIdentityRegistry.Register(this);
				//DebugConsole.Log($"[NetworkIdentity] Generated Random NetId {NetId} for {gameObject.name}");
			}
			else if (!NetworkIdentityRegistry.RegisterExisting(this, NetId))
			{
				// Somebody else holds this id. Leaving it here is what made the
				// damage permanent: the object kept an id that resolves to
				// another object, was marked registered anyway, and then got
				// SAVED that way - so the duplicate came back on every load and
				// the loser never appeared in the registry at all. Two
				// hydroponic farms, two swamp lilies and three hatches were in
				// exactly that state in a live colony, and nothing addressed to
				// them could ever arrive.
				//
				// NetId is [Serialize]d, so repairing it here also repairs the
				// save the next time the world is written.
				if (!TryRehouse())
					return;
			}
			IsRegistered = true;

			AnnounceSpawnIfHost();
		}

		/// <summary>
		/// Find this object a free id after its own turned out to be taken.
		///
		/// The replacement is derived, not allocated: recomputing the
		/// deterministic id gives a value that is a pure function of what this
		/// object is and where it is, so two peers loading the same save reach
		/// the same answer without talking. Only if that is taken too does it
		/// walk upward, and a host that is in session announces the result so a
		/// client cannot be left guessing.
		///
		/// Returns false if no id could be found at all, in which case the
		/// object stays unregistered and unmarked - it will try again the next
		/// time RegisterIdentity runs, rather than pretending it succeeded.
		/// </summary>
		private bool TryRehouse()
		{
			using var _ = Profiler.Scope();

			int taken = NetId;
			int candidate = ComputeDeterministicId();

			// The saved id may itself be the deterministic one, in which case
			// recomputing changes nothing and the walk below does the work.
			if (candidate == 0 || candidate == taken || NetworkIdentityRegistry.Exists(candidate))
				candidate = NetworkIdentityRegistry.FindFreeId(candidate != 0 ? candidate : taken + 1);

			if (candidate == 0)
			{
				DebugConsole.LogWarning(
					$"[NetworkIdentity] '{gameObject.name}' could not be rehoused off NetId {taken}; " +
					"it stays unregistered and will retry");
				return false;
			}

			NetId = candidate;
			if (!NetworkIdentityRegistry.RegisterExisting(this, NetId))
			{
				NetId = taken;
				return false;
			}

			DebugConsole.Log(
				$"[NetworkIdentity] '{gameObject.name}' rehoused from duplicate NetId {taken} to {NetId}");
			return true;
		}

		/// <summary>
		/// The id this object would be given if it had none, by the same branch
		/// order RegisterIdentity uses. Kept as one method so the repair path
		/// and the first-registration path cannot drift apart.
		/// </summary>
		private int ComputeDeterministicId()
		{
			if (TryGetComponent<Building>(out _))
				return NetIdHelper.GetDeterministicBuildingId(gameObject);
			if (TryGetComponent<Workable>(out _))
				return NetIdHelper.GetDeterministicWorkableId(gameObject);
			return NetIdHelper.GetDeterministicEntityId(gameObject);
		}

		/// <summary>
		/// Tell clients about an object the host just named.
		///
		/// Announcing from KInstantiate only caught two spawns in a whole run,
		/// because most objects never go through Util.KInstantiate -
		/// SpawnResource, which produces the element piles that are 71% of what
		/// a client draws unnamed, is one of them. Announcing here instead
		/// catches every creation path, because they all end up needing an id.
		/// </summary>
		private void AnnounceSpawnIfHost()
		{
			if (NetId == 0) return;
			if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession) return;
			if (!NeedsReplication()) return;

			Misc.World.InstantiationBatcher.Queue(new Packets.InstantiationsPacket.InstantiationEntry
			{
				NetId = NetId,
				PrefabName = TryGetComponent<KPrefabID>(out var kpid) ? kpid.PrefabTag.Name : gameObject.name,
				Position = transform.position,
				Rotation = transform.rotation,
				ObjectName = gameObject.name,
				InitializeId = true,
				GameLayer = gameObject.layer
			});
		}

		/// <summary>
		/// Objects the peers must agree on by name: anything that can be picked
		/// up, hauled or interacted with across the link. Buildings and conduits
		/// are excluded - they already replicate through their own paths, and
		/// including them would make this scale with the whole colony.
		/// </summary>
		private bool NeedsReplication()
		{
			if (TryGetComponent<Building>(out _)) return false;
			return TryGetComponent<Pickupable>(out _) || TryGetComponent<Navigator>(out _);
		}

		/// <summary>
		/// This will be primarily used when the host spawns in an object and the client and host need to sync the netid
		/// </summary>
		/// <param name="netIdOverride"></param>
		/// <summary>Sequence of the packet that last named this object.</summary>
		[SkipSaveFileSerialization]
		private int _namedBySequence = int.MinValue;

		public void OverrideNetId(int netIdOverride)
		{
			using var _ = Profiler.Scope();

			// Refuse a name older than the one already applied.
			//
			// Without this the last packet to arrive wins whatever order it was
			// sent in, and the same object could be named twice by a spawn
			// announcement and a periodic sweep racing each other. That is why
			// every attempt to announce spawns faster - 500 ms, 100 ms,
			// immediately - turned netid_compare from agreeing to disagreeing
			// while the 2 s interval happened to keep them apart.
			int sequence = Packets.Architecture.PacketHandler.CurrentSequence;
			if (_namedBySequence != int.MinValue && sequence - _namedBySequence < 0)
			{
				DebugConsole.Log(
					$"[NetworkIdentity] ignoring stale naming of {gameObject.name}: " +
					$"packet {sequence} is older than {_namedBySequence}");
				return;
			}
			_namedBySequence = sequence;

			// The host has named this object, so it is no longer a preview.
			if (IsClientPreview)
			{
				IsClientPreview = false;
				PreviewsAdopted++;
			}

			// Unregister old NetId
			NetworkIdentityRegistry.Unregister(NetId, this);

			// Override internal value
			NetId = netIdOverride;

			// Re-register with new NetId
			NetworkIdentityRegistry.RegisterOverride(this, netIdOverride);

			//DebugConsole.Log($"[NetworkIdentity] Overridden NetId. New NetId = {NetId} for {gameObject.name}");
		}


		public override void OnCleanUp()
		{
			using var _ = Profiler.Scope();

			RemoteProgressRegistry.Clear(NetId);
			NetworkIdentityRegistry.Unregister(NetId, this);
			//DebugConsole.Log($"[NetworkIdentity] Unregistered NetId {NetId} for {gameObject.name}");
			base.OnCleanUp();
		}
	}
}
