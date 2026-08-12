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

				if (!MultiplayerSession.InSession || !MultiplayerSession.IsClient)
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
