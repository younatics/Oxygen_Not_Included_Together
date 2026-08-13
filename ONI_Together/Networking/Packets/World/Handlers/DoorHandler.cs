using UnityEngine;
using ONI_Together.DebugTools;
using ONI_Together.Misc;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World.Handlers
{
	/// <summary>
	/// Handles Door state changes.
	/// </summary>
	public class DoorHandler : IBuildingConfigHandler
	{
		private static readonly int[] _hashes = new int[]
		{
			"DoorState".GetHashCode(),
		};

		public int[] SupportedConfigHashes => _hashes;

		public bool TryApplyConfig(GameObject go, BuildingConfigPacket packet)
		{
			using var _ = Profiler.Scope();

			if (packet.ConfigHash != "DoorState".GetHashCode()) return false;

			var door = go.GetComponent<Door>();
			if (door == null) return false;

			Door.ControlState state = (Door.ControlState)(int)packet.Value;

			// Through DoorControl, which holds both rules this needed.
			//
			// The first was already here: the host relay sends a client its own change
			// back, and QueueStateChange with requestedState == nextState takes the
			// cancel path - requestedState = controlState - resetting the door to the
			// state it was leaving. That is still handled, inside Converge.
			//
			// The second was not. Returning early on RequestedState == state leaves a
			// door whose request matches and whose control state never followed, and on
			// a client that is the normal outcome rather than an edge case: the queue
			// ends in a Toggle chore and client duplicants have no chores. So the event
			// path could not converge a door either, and the periodic syncer had the
			// same defect in its own words. Two places, one shape - now one place.
			DoorControl.Converge(door, state);
			return true;
		}
	}
}
