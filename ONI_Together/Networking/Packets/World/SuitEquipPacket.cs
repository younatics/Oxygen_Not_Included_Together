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

		/// <summary>
		/// Times the game's own call threw. Measured: the first run of this packet sent 7
		/// events, every one of them arrived (sent=7 recv=7, no loss), EquipTo applied 2
		/// for 2, and UnequipFrom threw 5 for 5. None of that was visible in the health
		/// row - Applied simply stopped rising, which reads the same as "the host never
		/// sent anything", and the only trace was a warning line. A failure that is not a
		/// counter is a failure the summary cannot report.
		/// </summary>
		public static int Threw { get; private set; }

		/// <summary>
		/// The two preconditions the game's own methods have, asked before calling them.
		///
		/// Read out of Assembly-CSharp rather than reasoned about. UnequipFrom opens with
		///
		///     Assignable assignable = equipment.GetAssignable(Db.Get().AssignableSlots.Suit);
		///     assignable.Unassign();
		///
		/// and has no null check, because the game only reaches it when a duplicant is
		/// actually wearing the suit. On a client that never ran the chore that put it on,
		/// the first line returns null and the second throws - measured, five times out of
		/// five, in two runs. EquipTo has the opposite shape: it starts with
		/// GetStoredOutfit() and returns quietly when the locker is empty, so calling it
		/// blind does no damage but also does nothing, and counting that as Applied
		/// reported a suit moving that never moved.
		///
		/// Neither of these forces a state. They ask what the game asks and decline where
		/// the game would have declined - the difference is that one of the two declines
		/// by crashing.
		/// </summary>
		public static int NoWornSuit { get; private set; }
		public static int NoStoredSuit { get; private set; }

		/// <summary>
		/// THE ERRAND THAT STAYS ON A CLIENT. Measured, attempted twice, reverted twice -
		/// the state of it, so a third attempt starts where the second stopped.
		///
		/// One row survives every run: "chore|Atmo_Suit#...|waiting host=0 client=1".
		/// waiting is Prioritizable.IsPrioritizable(), refCount > 0, so the client holds an
		/// errand against a suit that the host's duplicant has already taken. A player on
		/// the client sees work the host has finished.
		///
		/// A probe on Prioritizable.AddRef named the caller rather than a third guess:
		///
		///     AddRef &lt;- EquipChore..ctor &lt;- EquippableWorkable.CreateChore
		///            &lt;- EquippableWorkable.RefreshChore &lt;- Assignable...
		///
		/// It is the suit ITEM's own errand. The first attempt cancelled the LOCKER's
		/// ReturnSuitWorkable, fired every run and moved nothing, because those are two
		/// different objects.
		///
		/// RefreshChore cancels any chore and creates one only when the owner is not
		/// already wearing the item, and the assignment replicates before the suit does -
		/// so on a client it runs while the duplicant is empty-handed, makes the errand,
		/// and is never asked again once the replay puts the suit on. Calling it a second
		/// time from here was the second attempt, and three runs say why it failed:
		///
		///     suitChoreFixed   1, 2, 2      it fired
		///     refAdd/refDel    2/1, 4/2, 5/3    leftovers 1, 2, 2
		///     chore DIFFERENT  2, 2, 2      exactly the leftovers
		///
		/// refAdd rises with the reconcile, so the second call cancelled the chore and made
		/// a new one - meaning IsEquipped was still false at that instant. The shape is
		/// right and the moment is wrong: the equip has not landed when the replay returns.
		///
		/// A third attempt reconciles a frame later, or off whatever event reports the
		/// equipment changed, and proves the timing before the fix - the counter to watch
		/// is refAdd, which must NOT rise when the reconcile runs.
		/// </summary>

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

			if (Equip)
			{
				if (locker.GetStoredOutfit() == null)
				{
					NoStoredSuit++;
					return;
				}
			}
			else if (equipment.GetAssignable(Db.Get().AssignableSlots.Suit) == null)
			{
				NoWornSuit++;
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
				Threw++;

				// The whole exception, not e.Message. "Object reference not set to an
				// instance of an object" names nothing: it was logged five times for five
				// failures and did not say which dereference inside the game's method was
				// null, so the next step would have been a guess. This file already carries
				// the cost of guessing at game APIs. ToString() carries the stack, and the
				// frame below UnequipFrom is the answer.
				DebugConsole.LogWarning($"[SuitEquipPacket] {(Equip ? "EquipTo" : "UnequipFrom")} threw for locker {LockerNetId} minion {MinionNetId}: {e}");
			}
			finally
			{
				IsApplying = false;
			}
		}
	}
}
