using Database;
using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Social;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.GamePatches
{
	// Note: ImmigrantScreen logic is complex. This is a partial implementation.
	// We need to sync the containers (Care Packages / Duplicants)

	public static class ImmigrantScreenPatch
	{
		public static List<ImmigrantOptionEntry> AvailableOptions;

		// Flag to prevent re-syncing once options are locked for this cycle
		public static bool OptionsLocked = false;

		// Flag to skip ApplyOptionsToScreen when InitializeContainers has already created containers
		public static bool ContainersCreatedByPatch = false;

		// Clear the lock when the screen closes or duplicant is printed
		public static void ClearOptionsLock()
		{
			using var _ = Profiler.Scope();

			OptionsLocked = false;
			ContainersCreatedByPatch = false;
			AvailableOptions = null;

			// Also clear selectedDeliverables to prevent "add beyond limit" errors on reopen
			try
			{
				if (ImmigrantScreen.instance != null)
				{
					ImmigrantScreen.instance.selectedDeliverables.Clear();
				}
			}
			catch { }

			DebugConsole.Log("[ImmigrantScreen] Options lock cleared");
		}
		// SetMinionDelayed used to live here and must not come back.
		//
		// It waited a frame before calling container.SetMinion, and the container's
		// portrait duplicant spawns in that frame with no stats - "Could not find
		// Personality: 0x0" out of Accessorizer.OnSpawn, once per frame, until ONI
		// raises it as an error and closes the game. Three client sessions ended
		// that way and it reproduced every time the printing pod was opened.
		//
		// SetMinion is called directly now, in the same frame as the container is
		// created, which is what the base game does. If a future change here seems
		// to need a frame of delay, the delay is the bug.
		static IEnumerator SetCarePackageInfoDelayed(CarePackageContainer carePackageContainer, CarePackageInfo pkg)
		{
			using var _ = Profiler.Scope();

			// Wait for end of frame to ensure proper initialization
			yield return SequenceUtil.WaitForNextFrame;
			carePackageContainer.info = pkg;
			var packageInstance = new CarePackageContainer.CarePackageInstanceData();
			if (carePackageContainer.animController != null)
			{
				UnityEngine.Object.Destroy(carePackageContainer.animController.gameObject);
				carePackageContainer.animController = null;
			}
			packageInstance.info = pkg;
			if (pkg.facadeID == "SELECTRANDOM")
			{
				packageInstance.facadeID = Db.GetEquippableFacades().resources.FindAll((EquippableFacadeResource match) => match.DefID == pkg.id).GetRandom().Id;
			}
			else
			{
				packageInstance.facadeID = pkg.facadeID;
			}
			carePackageContainer.carePackageInstanceData = packageInstance;
			carePackageContainer.ClearEntryIcons();
			carePackageContainer.SetAnimator();
			carePackageContainer.SetInfoText();
			DebugConsole.Log($"[ImmigrantScreen] SetCarePackageInfoDelayed: Set care package '{pkg.id}' in container");
		}

		public static void ApplyOptionsToScreen(ImmigrantScreen instance)
		{
			using var _ = Profiler.Scope();

            if (instance.Telepad == null) return;

            if (AvailableOptions == null || AvailableOptions.Count == 0 || instance == null)
			{
				DebugConsole.LogWarning($"[ImmigrantScreen] ApplyOptionsToScreen: Cannot apply - Options:{AvailableOptions?.Count ?? 0}, Screen:{(instance != null ? "valid" : "null")}");
				return;
			}

			bool canRerollCarePackages = false, canRerollMinions = false;
			foreach (var cont in instance.containers)
			{
				if(cont is CharacterContainer cc)
					if(cc.reshuffleButton.gameObject.activeSelf)
						canRerollMinions = true;
				else if(cont is CarePackageContainer cpc)
					if(cpc.reshuffleButton.gameObject.activeSelf)
						canRerollCarePackages = true;
			}
			///Clearing existing containers
			instance.containers.ForEach(delegate (ITelepadDeliverableContainer cc)
			{
				Object.Destroy(cc.GetGameObject());
			});
			instance.containers.Clear();

			DebugConsole.Log($"[ImmigrantScreen] ApplyOptionsToScreen: Applying {AvailableOptions.Count} options");

			instance.selectedDeliverables.Clear();

			foreach (var option in AvailableOptions)
			{
				DebugConsole.Log($"[ImmigrantScreen]   Type={option.EntryType}, Id={(option.GetId())}");
				var deliverable = option.ToGameDeliverable();
				if(deliverable is MinionStartingStats stats)
				{
					// Active, and given its duplicant in the same frame.
					//
					// This is the crash that ends a client the moment the printing pod
					// choices are opened, and it took two attempts to get right.
					//
					// The original code created the container and handed over the stats
					// a frame later, by coroutine ("wait for end of frame to ensure
					// proper initialization"). In that frame the container's portrait
					// Minion spawns with no MinionStartingStats, so MinionIdentity and
					// Accessorizer read a personality that is not there:
					//
					//   [ERROR] Could not find Personality: 0x0
					//   at MinionStartingStats.CreateBodyData (Personality p)
					//   at Accessorizer.OnSpawn () ... at (0.00, 0.00, -25.50)
					//
					// It then repeats every frame, ONI raises it as an error, and the
					// game closes itself seconds later. No mod frame appears in those
					// stacks, which is why it read as a game bug for three sessions.
					//
					// The first attempt built the container inactive so nothing could
					// spawn before the stats were set. That traded one crash for
					// another: SetMinion goes through SetAnimator to ApplyTraits, and
					// on an inactive container the MinionSelectPreview it applies them
					// to is still the prefab, not an instance -
					//
					//   Assert failed: Tried adding a trait on a prefab ...
					//   MinionSelectPreview
					//   at Klei.AI.Modifier.AddTo (Attributes) -> NullReferenceException
					//
					// So SetMinion needs the container live, and the portrait needs the
					// stats before it spawns. Both hold if the container is created the
					// way the base game creates it and the stats are set immediately:
					// KMonoBehaviour.Spawn is scheduled, not immediate, so the same
					// frame is early enough. The deferral was the whole defect.
					// force_active: true, and this is the whole fix.
				//
				// The reasoning above was right and the code did not do it. KInstantiateUI
				// takes a third argument that activates the object, it was not passed, and
				// the default is false - so whether the container came out live depended on
				// whether its parent happened to be active at that moment. During
				// ImmigrantScreen.Initialize it is not, and an inactive container sends
				// SetMinion into ApplyTraits against a MinionSelectPreview that is still
				// the prefab. Measured on the host, from a click on the printing pod:
				//
				//   NullReferenceException at Klei.AI.Modifier.AddTo (Attributes)
				//     Klei.AI.Traits.Add <- MinionStartingStats.ApplyTraits
				//     CharacterContainer.SetAnimator <- CharacterContainer.SetMinion
				//     ImmigrantScreenPatch.ApplyOptionsToScreen
				//     ImmigrantScreenInitializePatch.Postfix
				//     ImmigrantScreen.InitializeImmigrantScreen <- TelepadSideScreen click
				//
				// ONI raised it and closed the game seconds later, taking the session with
				// it. Every other KInstantiateUI in this repository that needs a live
				// object passes true; this was the one that did not.
				//
				// The unit test missed it because it mirrors this call exactly and runs
				// against an already-shown screen, where the child inherits an active
				// parent. Its own comment flags that difference. Passing true removes the
				// dependency on the parent's state, so both paths behave the same.
				CharacterContainer characterContainer = Util.KInstantiateUI<CharacterContainer>(
						instance.containerPrefab.gameObject, instance.containerParent, force_active: true);
					characterContainer.SetController(instance);
					characterContainer.SetReshufflingState(canRerollMinions);
					characterContainer.SetMinion(stats);

					instance.containers.Add(characterContainer);
				}
				else if(deliverable is CarePackageInfo pkg)
				{
					// Left exactly as it was. A care package has no personality to
					// miss and this container has never crashed, so there is nothing
					// here to fix - and the duplicant container just showed what
					// happens when this screen is changed on reasoning rather than on
					// evidence. Its coroutine also destroys and rebuilds an anim
					// controller, which is more than a stats assignment.
					CarePackageContainer carePackageContainer = Util.KInstantiateUI<CarePackageContainer>(
						instance.carePackageContainerPrefab.gameObject, instance.containerParent);
					carePackageContainer.SetController(instance);
					carePackageContainer.SetReshufflingState(canRerollCarePackages);
					Game.Instance.StartCoroutine(SetCarePackageInfoDelayed(carePackageContainer, pkg));
					instance.containers.Add(carePackageContainer);
				}
			}
			Debug.Log("Container Count after apply: " + instance.containers.Count);
		}
	}

	[HarmonyPatch(typeof(ImmigrantScreen), nameof(ImmigrantScreen.Initialize))]
	public static class ImmigrantScreenInitializePatch
	{
		public static void Postfix(ImmigrantScreen __instance)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession) return;

			DebugConsole.Log("[ImmigrantScreen] Initialize postfix triggered");

			// If options are already locked but containers weren't created by us, apply them
			if (ImmigrantScreenPatch.OptionsLocked && ImmigrantScreenPatch.AvailableOptions != null && ImmigrantScreenPatch.AvailableOptions.Count > 0)
			{
				DebugConsole.Log($"[ImmigrantScreen] Options already locked, applying {ImmigrantScreenPatch.AvailableOptions.Count} cached options");
				ImmigrantScreenPatch.ApplyOptionsToScreen(__instance);
				return;
			}

			// First-opener-wins: Whoever opens first captures and broadcasts
			// Use a delayed capture because container data isn't ready yet at Initialize time
			// Use Game.Instance because ImmigrantScreen is inactive at this point
			Game.Instance.StartCoroutine(DelayedCaptureAndBroadcast(__instance));
		}

		private static System.Collections.IEnumerator DelayedCaptureAndBroadcast(ImmigrantScreen screen)
		{
			using var _ = Profiler.Scope();

			// Wait for end of frame (let containers populate their data)
			yield return null;

			// Check again if locked (in case another player's packet arrived)
			if (ImmigrantScreenPatch.OptionsLocked)
			{
				DebugConsole.Log("[ImmigrantScreen] Options locked during delay, applying cached options");
				if (ImmigrantScreenPatch.AvailableOptions != null && ImmigrantScreenPatch.AvailableOptions.Count > 0)
				{
					ImmigrantScreenPatch.ApplyOptionsToScreen(screen);
				}
				yield break;
			}

			CaptureAndBroadcastOptions(screen);
		}

		private static void CaptureAndBroadcastOptions(ImmigrantScreen __instance)
		{
			using var _ = Profiler.Scope();

            if (__instance.Telepad == null) return;

            string role = MultiplayerSession.IsHost ? "Host" : "Client";
			DebugConsole.Log($"[ImmigrantScreen] {role}: Capturing options from containers...");

			// Get containers from ImmigrantScreen (inherited from CharacterSelectionController)

			DebugConsole.Log($"[ImmigrantScreen] Found {__instance.containers.Count} containers");

			var packet = new ImmigrantOptionsPacket();

			var containers = __instance.containers.OrderBy(c => c.GetGameObject().transform.GetSiblingIndex()).ToList();

			foreach (ITelepadDeliverableContainer container in containers)
			{
				if (container == null) continue;
				ImmigrantOptionEntry fromContainer = ImmigrantOptionEntry.INVALID;
				if (container is CharacterContainer cc)
					fromContainer = ImmigrantOptionEntry.FromGameDeliverable(cc.Stats);
				else if( container is CarePackageContainer cpc)
					fromContainer = ImmigrantOptionEntry.FromGameDeliverable(cpc.Info);

				if(fromContainer.IsValid)
					packet.Options.Add(fromContainer);
				else
					DebugConsole.Log($"[ImmigrantScreen]   Container {container.GetType().Name} has no stats or carePackageInfo");
			}

			if (packet.Options.Count > 0)
			{
				// Lock options for this cycle
				ImmigrantScreenPatch.AvailableOptions = packet.Options;
				ImmigrantScreenPatch.OptionsLocked = true;

				DebugConsole.Log($"[ImmigrantScreen] {role}: Broadcasting {packet.Options.Count} options (first-opener-wins)");

				if (MultiplayerSession.IsHost)
				{
					// Host sends to all clients
					PacketSender.SendToAllClients(packet);
				}
				else
				{
					// Client sends to host (host will rebroadcast)
					PacketSender.SendToHost(packet);
				}
			}
			else
			{
				DebugConsole.LogWarning($"[ImmigrantScreen] {role}: No options to broadcast!");
			}
		}
	}

	[HarmonyPatch(typeof(ImmigrantScreen), nameof(ImmigrantScreen.OnProceed))]
	public static class ImmigrantScreenProceedPatch
	{
		public static bool Prefix(ImmigrantScreen __instance)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession) return true;

			if (MultiplayerSession.IsHost)
			{
				// Host: Clear the lock after printing (Postfix will handle this)
				return true;
			}

            if (__instance.Telepad == null) return true;

			if (__instance.selectedDeliverables.Count == 0) return false; // Even though selectedDeliverables should always have something at index 0 for whatever reason its empty, so return false to stop crashing

            ITelepadDeliverable selectedDeliverable = __instance.selectedDeliverables[0];

			var packet = new ImmigrantSelectionPacket { selectedOption = ImmigrantOptionEntry.FromGameDeliverable(selectedDeliverable), PrintingPodWorldIndex = __instance.Telepad?.GetMyWorldId() ?? 0 };
			PacketSender.SendToHost(packet);

			DebugConsole.Log($"[ImmigrantScreen] Client: Selected packet {packet.selectedOption.GetId()}, sending to host");

			// Clear the options lock for the next cycle
			ImmigrantScreenPatch.ClearOptionsLock();

			// Suppress local printing - host handles it
			__instance.Deactivate();
			return false;
		}

		public static void Postfix()
		{
			using var _ = Profiler.Scope();

			// Host: Clear the lock after printing and notify clients
			if (MultiplayerSession.IsHost)
			{
				DebugConsole.Log("[ImmigrantScreen] Host: Selection made via screen, notifying clients to close");

				// Send -2 to close client screens
				// NOTE: For host's own selections via OnProceed, the game spawns the entity normally
				// Entity sync will be handled separately (e.g. via EntitySpawnPacket from a different hook)
				var packet = new ImmigrantSelectionPacket { PrintingPodWorldIndex = -2 };
				PacketSender.SendToAllClients(packet);

				ImmigrantScreenPatch.ClearOptionsLock();
			}
		}
	}

	// Patch for Reject All button
	[HarmonyPatch(typeof(ImmigrantScreen), "OnRejectAll")]
	public static class ImmigrantScreenRejectPatch
	{
		public static bool Prefix(ImmigrantScreen __instance)
		{
			using var _ = Profiler.Scope();

            if (__instance.Telepad == null) return true;

            if (!MultiplayerSession.InSession) return true;

			DebugConsole.Log("[ImmigrantScreen] Reject All clicked");

			if (MultiplayerSession.IsClient)
			{
				// Client: Send reject to host
				DebugConsole.Log("[ImmigrantScreen] Client: Sending Reject All to host");
				var packet = new ImmigrantSelectionPacket { PrintingPodWorldIndex = -1 };
				PacketSender.SendToHost(packet);

				// Clear local options and close screen
				ImmigrantScreenPatch.ClearOptionsLock();
				if (ImmigrantScreen.instance != null)
				{
					ImmigrantScreen.instance.Deactivate();
				}

				return false; // Don't execute original
			}

			// Host: Let original execute, Postfix will notify clients
			return true;
		}

		public static void Postfix()
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession) return;

			if (MultiplayerSession.IsHost)
			{
				DebugConsole.Log("[ImmigrantScreen] Host: Reject All, notifying clients");

				// Notify clients to close their screens
				var packet = new ImmigrantSelectionPacket { PrintingPodWorldIndex = -1 };
				PacketSender.SendToAllClients(packet);

				ImmigrantScreenPatch.ClearOptionsLock();
			}
		}
	}
}


