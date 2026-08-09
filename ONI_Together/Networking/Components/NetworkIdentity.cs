using KSerialization;
using ONI_Together.DebugTools;
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

		public override void OnSpawn()
		{
			using var _ = Profiler.Scope();

			base.OnSpawn();

			// Read here rather than in the patch: OnSpawn runs inside the
			// KInstantiate call that set it, and only objects that carry a
			// NetworkIdentity care.
			if (KInstantiatePatch.ConsumeClientPreviewFlag())
				IsClientPreview = true;

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
			else
			{
				NetworkIdentityRegistry.RegisterExisting(this, NetId);
				// DebugConsole.Log($"[NetworkIdentity] Registered Existing NetId {NetId} for {gameObject.name}");
			}
			IsRegistered = true;
		}

		/// <summary>
		/// This will be primarily used when the host spawns in an object and the client and host need to sync the netid
		/// </summary>
		/// <param name="netIdOverride"></param>
		public void OverrideNetId(int netIdOverride)
		{
			using var _ = Profiler.Scope();

			// The host has named this object, so it is no longer a preview.
			IsClientPreview = false;

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
