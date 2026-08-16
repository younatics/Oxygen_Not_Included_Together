using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using System.IO;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// Moving a suit onto or off a duplicant on the client, through the game's own call.
	///
	/// The assignment already replicates - the client agrees about who owns the suit -
	/// but the suit itself does not move. Taking it is a chore, and the client runs no
	/// chores, so the host wears a suit whose client copy is still docked in the locker.
	/// The state dump reports it as a cell mismatch on the checkpoint:
	///
	///     host    Atmo_Suit|cell 0        - carried
	///     client  Atmo_Suit|cell 51570    - still in the locker
	///
	/// What is sent is the event, not the resulting position, and the client replays it
	/// through SuitLocker.EquipTo / UnequipFrom - the same methods the game calls when a
	/// duplicant finishes the chore. Nothing here moves an item by hand. Every attempt in
	/// this repository that placed an object itself has been reverted; the ones that
	/// handed the work to the game's own entry point (Artable.SetStage, Clearable.
	/// MarkForClear) are the ones still in the tree.
	///
	/// Both peers can therefore refuse the same way for the same reason. If the client's
	/// locker has no suit, or the duplicant already wears one, EquipTo declines exactly as
	/// it would in a single-player game, and the counters below say so rather than the
	/// packet forcing a state the game would not have produced.
	/// </summary>
	public class SuitEquipPacket : IPacket
	{
		public int LockerNetId;
		public int MinionNetId;
		public bool Equip;

		/// <summary>
		/// Set while the client is replaying an event, so the patch that broadcasts does
		/// not broadcast the replay back. The host is the only sender, but a client that
		/// is later promoted keeps this correct rather than relying on role alone.
		/// </summary>
		public static bool IsApplying { get; private set; }

		public static int Applied { get; private set; }
		public static int NoLocker { get; private set; }
		public static int NoEquipment { get; private set; }

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(LockerNetId);
			writer.Write(MinionNetId);
			writer.Write(Equip);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			LockerNetId = reader.ReadInt32();
			MinionNetId = reader.ReadInt32();
			Equip = reader.ReadBoolean();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost || IsApplying)
				return;

			if (!NetworkIdentityRegistry.TryGet(LockerNetId, out var lockerIdentity) || lockerIdentity == null)
			{
				NoLocker++;
				return;
			}

			var locker = lockerIdentity.GetComponent<SuitLocker>();
			if (locker == null)
			{
				NoLocker++;
				return;
			}

			if (!NetworkIdentityRegistry.TryGet(MinionNetId, out var minionIdentity) || minionIdentity == null)
			{
				NoEquipment++;
				return;
			}

			var equipment = minionIdentity.GetComponent<MinionIdentity>()?.GetEquipment();
			if (equipment == null)
			{
				NoEquipment++;
				return;
			}

			IsApplying = true;
			try
			{
				if (Equip)
					locker.EquipTo(equipment);
				else
					locker.UnequipFrom(equipment);

				Applied++;
			}
			catch (System.Exception e)
			{
				// Counted rather than swallowed. A suit that cannot be moved is the defect
				// this packet exists to close, and a silent catch would leave it looking
				// closed. The session survives either way; the number does not lie about it.
				DebugConsole.LogWarning($"[SuitEquipPacket] {(Equip ? "EquipTo" : "UnequipFrom")} threw for locker {LockerNetId}: {e.Message}");
			}
			finally
			{
				IsApplying = false;
			}
		}
	}
}
