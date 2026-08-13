using ONI_Together.DebugTools;
using UnityEngine;

namespace ONI_Together.Misc
{
	/// <summary>
	/// The one way this mod makes a door's control state match the other peer's.
	///
	/// There are two senders for this - the event patch on QueueStateChange and the
	/// periodic BuildingFlagsSyncer - and before this they applied it two different ways,
	/// both of which could not work on a client. One place to be wrong in is the point.
	///
	/// What the game does, read out of Door's IL rather than assumed:
	///
	///   QueueStateChange(next) with next == requestedState takes a cancel path -
	///     requestedState = controlState, chore cancelled, status item removed. Asking
	///     twice for the state you want is how to lose it.
	///   QueueStateChange(next) otherwise sets requestedState and, unless
	///     DebugHandler.InstantBuildMode, creates a ChoreTypes.Toggle work chore. A
	///     duplicant has to walk over and operate the door before anything changes.
	///   ApplyRequestedControlState() is what runs when that work completes:
	///     controlState = requestedState, then ApplyControlState and Open/Close.
	///
	/// The third line is the one that matters here. Client duplicants have no chores at
	/// all - ChoreConsumer and MinionBrain are disabled - so a queued door change on a
	/// client waits for a worker who is never coming, and controlState never follows
	/// requestedState. Measured: PressureDoor@43122 and @43123 held Locked on the host
	/// and Opened on the client for a whole run, with the syncer's own dump agreeing
	/// (bfDoor host=2 client=1) and repairing neither.
	///
	/// So this queues the change when it is genuinely new - keeping the animation, the
	/// status item and the pathing update the game does on its own - and then completes
	/// it in place, because the host already paid the duplicant for this decision and
	/// the client is mirroring the outcome, not re-deciding it.
	/// </summary>
	public static class DoorControl
	{
		/// <summary>
		/// Doors whose control state was already right, so the counter beside this can be
		/// read. A repair count of zero means "nothing drifted" only if this is not zero.
		/// </summary>
		public static int AlreadyMatching { get; private set; }

		/// <summary>
		/// Doors finished in place because the queued change was waiting on a chore that
		/// a client will never run. This is the number that was missing entirely before.
		/// </summary>
		public static int CompletedWithoutWorker { get; private set; }

		/// <summary>
		/// Bring a door's control state to <paramref name="wanted"/>.
		/// Returns true if anything was changed, so callers can count a repair.
		/// </summary>
		public static bool Converge(Door door, Door.ControlState wanted)
		{
			if (door.IsNullOrDestroyed()) return false;

			// CurrentState, not RequestedState.
			//
			// The senders sample CurrentState, so this has to judge on CurrentState too.
			// Comparing RequestedState is what hid the bug: a door whose request already
			// matched but whose control state never followed looked correct and was
			// skipped every keyframe for the rest of the session.
			if (door.CurrentState == wanted)
			{
				AlreadyMatching++;
				return false;
			}

			// Read before anything moves, so the log says what was wrong rather than
			// what it has just been set to.
			var wasCurrent = door.CurrentState;
			var wasRequested = door.RequestedState;

			// Only when the request is genuinely different - a repeat is the cancel path.
			if (door.RequestedState != wanted)
				door.QueueStateChange(wanted);

			// Guarded because QueueStateChange can refuse: if it took the cancel path,
			// requestedState is now controlState and applying it would be a no-op that
			// still counted as a repair.
			if (door.RequestedState == wanted)
			{
				door.ApplyRequestedControlState();
				CompletedWithoutWorker++;
			}

			// Named for the first few, then counted - a keyframe arrives for every
			// watched building every fifteen seconds and a line each is how per-cell
			// logging once froze a host.
			if (CompletedWithoutWorker <= 5)
			{
				DebugConsole.LogWarning(
					$"[DoorControl] door at {Grid.PosToCell(door.gameObject)} was " +
					$"{wasCurrent} (requested {wasRequested}) and the other peer " +
					$"says {wanted}");
			}

			return true;
		}
	}
}
