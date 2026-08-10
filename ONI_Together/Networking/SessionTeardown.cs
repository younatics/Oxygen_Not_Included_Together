using ONI_Together.DebugTools;
using ONI_Together.Misc.World;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Trackers;
using ONI_Together.Patches.Navigation;

namespace ONI_Together.Networking
{
	/// <summary>
	/// Everything that must not outlive a session, in one place.
	///
	/// A mod like this is full of static state - registries, subscription sets,
	/// in-flight transfers, "am I busy" flags - and a session boundary is not a
	/// process boundary. A player disconnects and rejoins, and the host runs a
	/// hard sync several times an evening, tearing the world down and building
	/// it again each time. Anything left behind produces a bug that only appears
	/// on the second or third session, which is the hardest kind to reproduce
	/// and the easiest to blame on something else.
	///
	/// The worst of what was found here: a hard sync interrupted mid-flight left
	/// its "in progress" flag set, and every later hard sync in that process
	/// refused to start, logging only "already in progress". A partial save
	/// download stayed keyed by file name, so the next sync merged fresh chunks
	/// into the previous session's half-finished buffer. Subscription sets kept
	/// net ids that, after the next load, belonged to entirely different objects.
	///
	/// Each step is guarded on its own, because one type failing to resolve must
	/// not stop the rest of the cleanup.
	/// </summary>
	public static class SessionTeardown
	{
		public static void ClearAll()
		{
			Step("hard sync", GameServerHardSync.ResetForNewSession);
			Step("save assembler", SaveChunkAssembler.ResetForNewSession);
			Step("packet handler", PacketHandler.ResetForNewSession);
			Step("packet sender", PacketSender.ResetForNewSession);
			Step("identity statics", NetworkIdentity.ResetForNewSession);
			Step("anim syncers", AnimSyncCoordinator.ResetForNewSession);
			Step("status subscriptions", StatusBroadcaster.ResetForNewSession);
			Step("chore subscriptions", DuplicantChoreBroadcaster.ResetForNewSession);
			Step("navigator overrides", NavigatorExtensions.ResetForNewSession);
			Step("world trackers", () =>
			{
				MopTracker.MopPlacers.Clear();
				PlantTracker.AllPlants.Clear();
				DisinfectTracker.Disinfectables.Clear();
			});
		}

		private static void Step(string what, System.Action action)
		{
			try { action(); }
			catch (System.Exception ex)
			{
				DebugConsole.LogWarning($"[SessionTeardown] could not clear {what}: {ex.Message}");
			}
		}
	}
}
