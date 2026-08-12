using System.IO;
using ONI_Together.DebugTools;

namespace ONI_Together.Networking
{
	/// <summary>
	/// The one place a chore priority crosses the wire.
	///
	/// Six packets carried a priority and all six did it the same way: read
	/// ToolMenu.Instance.PriorityScreen if it happens to exist, write the two fields,
	/// read them back on the other side and hand the result to the game. When the tool
	/// menu was not there the sender skipped the assignment, so the struct kept its
	/// default - class 0, value 0 - and the receiver pushed that into the local priority
	/// screen and ran the tool with it. ONI answered "Priority Value Out Of Range: 0".
	///
	/// Measured once per run, only in the run where the client places build and
	/// deconstruct orders, which is why it survived: the two runs that never had the
	/// client act reported a clean error gate.
	///
	/// Fixing the one packet that was caught would have left the same hole in five
	/// others. So the read and the write live here and neither can produce an invalid
	/// priority: an unset sender sends a real default, and an out-of-range value on
	/// arrival is corrected and counted rather than passed on.
	/// </summary>
	public static class PriorityWire
	{
		/// <summary>ONI's valid range. Nine is the highest a player can set.</summary>
		private const int Lowest = 1;
		private const int Highest = 9;

		/// <summary>
		/// What to send when the tool menu cannot be asked. Five is the middle of the
		/// range and what ONI itself starts a colony with.
		/// </summary>
		private const int Fallback = 5;

		/// <summary>Priorities corrected on arrival. Every one was about to be refused by the game.</summary>
		public static int Corrected { get; private set; }

		/// <summary>Sends where the tool menu had nothing to say.</summary>
		public static int SentDefault { get; private set; }

		/// <summary>
		/// The priority to send: whatever the player last chose, or a valid default.
		/// </summary>
		public static PrioritySetting Sample()
		{
			var screen = ToolMenu.Instance?.PriorityScreen;
			if (screen == null)
			{
				SentDefault++;
				return new PrioritySetting(PriorityScreen.PriorityClass.basic, Fallback);
			}

			var chosen = screen.GetLastSelectedPriority();

			// Even the screen can hand back an unset value - it does before the player
			// has touched it - and sending that is what produced the error.
			if (chosen.priority_value < Lowest || chosen.priority_value > Highest)
			{
				SentDefault++;
				return new PrioritySetting(PriorityScreen.PriorityClass.basic, Fallback);
			}

			return chosen;
		}

		/// <summary>
		/// The same, for build orders, which read the plan screen instead.
		///
		/// The two build packets had no null guard at all - PlanScreen.Instance was
		/// dereferenced directly - so a build order placed by anything other than a
		/// player clicking the UI either threw or left the priority unset. The scenario
		/// runner places them exactly that way, which is how this was caught.
		/// </summary>
		public static PrioritySetting SampleBuilding()
		{
			var screen = PlanScreen.Instance;
			if (screen == null)
			{
				SentDefault++;
				return new PrioritySetting(PriorityScreen.PriorityClass.basic, Fallback);
			}

			var chosen = screen.GetBuildingPriority();
			if (chosen.priority_value < Lowest || chosen.priority_value > Highest)
			{
				SentDefault++;
				return new PrioritySetting(PriorityScreen.PriorityClass.basic, Fallback);
			}

			return chosen;
		}

		public static void Write(BinaryWriter writer, PrioritySetting priority)
		{
			writer.Write((int)priority.priority_class);
			writer.Write(priority.priority_value);
		}

		/// <summary>
		/// Read a priority that the game will accept, whatever arrived.
		/// </summary>
		public static PrioritySetting Read(BinaryReader reader)
		{
			var priorityClass = (PriorityScreen.PriorityClass)reader.ReadInt32();
			int value = reader.ReadInt32();

			if (value < Lowest || value > Highest)
			{
				Corrected++;
				// Named once and then counted. A malformed priority arrives in bursts
				// when it arrives at all - one tool drag is many cells - and a line each
				// is how per-cell logging once froze a host.
				if (Corrected <= 3)
				{
					DebugConsole.LogWarning(
						$"[PriorityWire] a priority of {value} arrived, which the game refuses " +
						$"(valid {Lowest}-{Highest}) - using {Fallback}. A sender is not filling it in.");
				}
				return new PrioritySetting(PriorityScreen.PriorityClass.basic, Fallback);
			}

			return new PrioritySetting(priorityClass, value);
		}
	}
}
