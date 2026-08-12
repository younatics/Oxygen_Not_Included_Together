using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.DuplicantActions
{
	/// <summary>
	/// A duplicant died on the host; kill the copy here.
	///
	/// Nothing replicated death. There is no death packet, no patch on the death
	/// path, and no handling anywhere - the only thing that could ever kill a
	/// client's duplicant was its own health reaching zero, carried by
	/// VitalStatsPacket. So the two peers were left to arrive at a death
	/// independently, on a client whose AI is switched off.
	///
	/// It does not arrive. A real session shows the host burying a colonist - the
	/// carry and bury animations replicate, so the client draws the funeral - while
	/// in the same twenty seconds VitalStatsPacket threw 39 times writing amounts
	/// onto a duplicant that no longer had them. The health that would have killed
	/// the client's copy is exactly what failed to apply. The host ends up with one
	/// fewer colonist than the client, permanently, which is the reported symptom:
	/// the duplicant totals do not match.
	///
	/// Death is stated rather than inferred. A cause travels with it so the client
	/// shows the same one, and an unknown cause still kills - the count matters more
	/// than the label on the tombstone.
	/// </summary>
	public class DuplicantDeathPacket : IPacket
	{
		public int NetId;
		public string DeathId;

		public DuplicantDeathPacket() { }

		public DuplicantDeathPacket(int netId, string deathId)
		{
			NetId = netId;
			DeathId = deathId ?? string.Empty;
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(NetId);
			writer.Write(DeathId ?? string.Empty);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			NetId = reader.ReadInt32();
			DeathId = reader.ReadString();
		}

		public static int Applied { get; private set; }
		public static int AlreadyDead { get; private set; }
		public static int Unresolved { get; private set; }
		public static int NotADuplicant { get; private set; }

		/// <summary>
		/// Set while a received death is being applied, so the host-side patch that
		/// announces deaths cannot echo one that came from the network. The client
		/// does not send these, but a host that ever applied one would announce its
		/// own echo forever.
		/// </summary>
		public static bool Applying { get; private set; }

		public static void ResetForNewSession()
		{
			Applied = 0;
			AlreadyDead = 0;
			Unresolved = 0;
			NotADuplicant = 0;
			Applying = false;
		}

		public static string Describe() =>
			$"applied={Applied} alreadyDead={AlreadyDead} unresolved={Unresolved} " +
			$"notADuplicant={NotADuplicant}";

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost)
				return;

			if (!NetworkIdentityRegistry.TryGet(NetId, out var identity) || identity == null
				|| identity.gameObject.IsNullOrDestroyed())
			{
				Unresolved++;
				ThrottledLog.Warn(
					$"[DuplicantDeath] NetId {NetId} could not be resolved, so a duplicant that died " +
					"on the host stays alive here and the colonist counts will disagree");
				return;
			}

			var go = identity.gameObject;

			// Only duplicants. The same discipline as the building packets: an id
			// that resolves to something else is a wrong address, and killing
			// whatever came back would be worse than the divergence.
			if (!go.HasTag(GameTags.BaseMinion))
			{
				NotADuplicant++;
				ThrottledLog.Warn(
					$"[DuplicantDeath] NetId {NetId} resolves to '{go.PrefabID()}', which is not a " +
					"duplicant - refusing to kill it");
				return;
			}

			if (go.HasTag(GameTags.Dead))
			{
				AlreadyDead++;
				return;
			}

			var death = Db.Get().Deaths.TryGet(DeathId) ?? Db.Get().Deaths.Generic;
			if (death == null)
			{
				Unresolved++;
				ThrottledLog.Warn($"[DuplicantDeath] no death cause resolved for '{DeathId}'");
				return;
			}

			var monitor = go.GetSMI<DeathMonitor.Instance>();
			if (monitor == null)
			{
				Unresolved++;
				ThrottledLog.Warn(
					$"[DuplicantDeath] '{go.PrefabID()}' has no DeathMonitor, so it cannot be killed here");
				return;
			}

			Applying = true;
			try
			{
				monitor.Kill(death);
				Applied++;
				DebugConsole.Log(
					$"[DuplicantDeath] killed NetId {NetId} ('{go.GetProperName()}') from '{death.Id}' " +
					"because the host did");
			}
			finally { Applying = false; }
		}
	}
}
