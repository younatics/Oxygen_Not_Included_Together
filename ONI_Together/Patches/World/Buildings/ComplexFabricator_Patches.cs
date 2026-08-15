using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Packets.World.Buildings;
using Shared.Profiling;
using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.Patches.World.Buildings
{
	internal class ComplexFabricator_Patches
	{
		private const float SEND_INTERVAL = 0.5f;
		private static readonly Dictionary<int, float> _nextSendTime = new();

		[HarmonyPatch(typeof(ComplexFabricatorWorkable), nameof(ComplexFabricatorWorkable.UpdateOrderProgress))]
		public class ComplexFabricatorWorkable_UpdateOrderProgress_Patch
		{
			public static void Postfix(ComplexFabricatorWorkable __instance)
			{
				using var _ = Profiler.Scope();

				if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession || __instance.IsNullOrDestroyed())
					return;

				if (!__instance.TryGetComponent<ComplexFabricator>(out var fabricator) || fabricator == null || fabricator.IsNullOrDestroyed())
					return;

				int netId = fabricator.GetNetId();
				if (netId == 0)
					return;

				float now = Time.time;
				if (_nextSendTime.TryGetValue(netId, out float next) && now < next)
					return;

				_nextSendTime[netId] = now + SEND_INTERVAL;
				PacketSender.SendToAllClients(WorkableProgressPacket.CreateComplexFabricator(fabricator, showProgressBar: true), PacketSendMode.Unreliable);
			}
		}

		/// <summary>
		/// Times a client's fabricator was stopped from throwing its ingredients away.
		/// Zero means this is not what empties them, whatever else is.
		/// </summary>
		public static int ClientIngredientDropsBlocked { get; private set; }

		/// <summary>
		/// Working orders a client's fabricator was stopped from starting.
		/// </summary>
		public static int ClientOrdersBlocked { get; private set; }

		[HarmonyPatch(typeof(ComplexFabricator), "StartWorkingOrder")]
		public class ComplexFabricator_StartWorkingOrder_Patch
		{
			/// <summary>
			/// A client's fabricator does not run orders. The host runs them and says
			/// what came out.
			///
			/// The storage packet lands correctly - verified at the moment of applying,
			/// storeNotLanded is 0 while the same container still disagrees at the end
			/// of the run - so the ingredients are put in and then taken out again.
			/// Three specific removers have been eliminated: the prefab is found, the
			/// container accepts the item, and DropExcessIngredients is already blocked
			/// and fired 34 to 61 times a run without changing the outcome.
			///
			/// What remains is the order machinery, and naming one more method inside it
			/// would be a fourth guess. Starting an order is the door all of it goes
			/// through: StartWorkingOrder calls TransferCurrentRecipeIngredientsForBuild,
			/// which moves the host's ingredients out of the storage that was just
			/// synced, and everything downstream follows from there.
			///
			/// This is the same rule SpawnOrderProduct already follows, applied one step
			/// earlier. The client keeps showing progress and queue counts, which arrive
			/// as packets rather than being simulated here.
			/// </summary>
			public static bool Prefix()
			{
				using var _ = Profiler.Scope();

				// Reconnect included: a client fabricator that keeps working through the
				// gap makes products the host also makes, and both survive.
				if (!MultiplayerSession.IsClientOrReconnecting)
					return true;

				ClientOrdersBlocked++;
				return false;
			}
		}

		[HarmonyPatch(typeof(ComplexFabricator), "DropExcessIngredients")]
		public class ComplexFabricator_DropExcessIngredients_Patch
		{
			/// <summary>
			/// The host decides what a fabricator holds; a client must not throw it out.
			///
			/// One container disagrees on every run and always the same one: a
			/// MicrobeMusher with BasicPlantFood on the host and none on the client,
			/// while the Dirt and Water beside it agree to the decimal. Two explanations
			/// were eliminated by counters rather than argument - the item's prefab is
			/// found (storeNoPrefab 0 with storeMade 44 to 68) and the container accepts
			/// it (storeRefused 0) - so it is put in and then taken out again.
			///
			/// This is the candidate that fits both halves of what is measured: the
			/// musher is empty and the client holds twenty-two of that food loose in the
			/// colony, which is where dropped ingredients go. A client's fabricator has
			/// its own idea of what its orders need, because only SpawnOrderProduct is
			/// blocked and the rest of the order machinery runs.
			///
			/// Counted as well as blocked, so this round answers whether it was right.
			/// A zero here with the container still disagreeing refutes it outright and
			/// costs nothing, since a client that never drops ingredients is corrected
			/// by the next storage keyframe anyway.
			/// </summary>
			public static bool Prefix()
			{
				using var _ = Profiler.Scope();

				// Reconnect included: a client fabricator that keeps working through the
				// gap makes products the host also makes, and both survive.
				if (!MultiplayerSession.IsClientOrReconnecting)
					return true;

				ClientIngredientDropsBlocked++;
				return false;
			}
		}

		[HarmonyPatch(typeof(ComplexFabricator), nameof(ComplexFabricator.CancelWorkingOrder))]
		public class ComplexFabricator_CancelWorkingOrder_Patch
		{
			public static void Postfix(ComplexFabricator __instance)
			{
				using var _ = Profiler.Scope();

				if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession || __instance.IsNullOrDestroyed())
					return;

				PacketSender.SendToAllClients(WorkableProgressPacket.CreateComplexFabricator(__instance, showProgressBar: false), PacketSendMode.ReliableImmediate);
			}
		}

		/// <summary>Products blocked on this client, reported in the health row.</summary>
		public static int ClientProductsBlocked { get; private set; }

		[HarmonyPatch(typeof(ComplexFabricator), nameof(ComplexFabricator.SpawnOrderProduct))]
		public class ComplexFabricator_SpawnOrderProduct_Patch
		{
			/// <summary>
			/// The host makes the product; the client is told about it.
			///
			/// The client's fabricators kept running - "MushBar created on the client
			/// by MicrobeMusher.SpawnOrderProduct" came out of the creation-time
			/// attribution - and the host announces its own product for the same
			/// order, so the client held two where there should be one. The extra one
			/// carries an id the host never issued, which is what turns into a failed
			/// lookup every time a packet mentions the real one.
			///
			/// An empty list, never null: callers iterate the result, and swapping a
			/// duplicate for a NullReferenceException is not a fix. This is also why
			/// EntitySplitter.Split is left alone for now - its callers use the object
			/// they get back, so it needs its own answer rather than this one.
			/// </summary>
			public static bool Prefix(ref List<GameObject> __result)
			{
				using var _ = Profiler.Scope();

				// Reconnect included: a client fabricator that keeps working through the
				// gap makes products the host also makes, and both survive.
				if (!MultiplayerSession.IsClientOrReconnecting)
					return true;

				// The one call that must still run: the handler applying the host's
				// own product. Without this a client would receive nothing at all.
				if (ComplexFabricatorSpawnProductPacket.IsApplying)
					return true;

				ClientProductsBlocked++;
				__result = new List<GameObject>();
				return false;
			}

			public static void Postfix(ComplexFabricator __instance)
			{
				using var _ = Profiler.Scope();

				if (!MultiplayerSession.InSession || !MultiplayerSession.IsHost)
					return;

				PacketSender.SendToAllClients(WorkableProgressPacket.CreateComplexFabricator(__instance, showProgressBar: false), PacketSendMode.ReliableImmediate);
				PacketSender.SendToAllClients(new ComplexFabricatorSpawnProductPacket(__instance));
			}
		}
	}
}
