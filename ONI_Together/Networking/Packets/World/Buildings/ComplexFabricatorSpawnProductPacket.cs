using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World.Buildings
{
	internal class ComplexFabricatorSpawnProductPacket : IPacket
	{
		public int NetId, CompletedRecipeIdx;

		/// <summary>
		/// True while this handler is producing the host's product on a client.
		///
		/// Read by the patch that otherwise stops a client fabricating for itself.
		/// Internal to this class and set only around the one call, so "the host told
		/// me to" is distinguishable from "my own simulation decided to" - which is
		/// the whole distinction the client needs and cannot otherwise make.
		/// </summary>
		internal static bool IsApplying { get; private set; }

		public ComplexFabricatorSpawnProductPacket() { }
		public ComplexFabricatorSpawnProductPacket(ComplexFabricator cf)
		{
			using var _ = Profiler.Scope();

			NetId = cf.GetNetId();
			CompletedRecipeIdx = cf.CurrentOrderIdx;
		}
		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(NetId);
			writer.Write(CompletedRecipeIdx);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			NetId = reader.ReadInt32();
			CompletedRecipeIdx = reader.ReadInt32();
		}


		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if(!NetworkIdentityRegistry.TryGetComponent<ComplexFabricator>(NetId, out var fab))
			{
				DebugConsole.LogWarning("[ComplexFabricatorSpawnProductPacket] Could not find ComplexFabricator for netId " + NetId);
				return;
			}

			ComplexRecipe complexRecipe = fab.recipe_list[CompletedRecipeIdx];
			DebugConsole.Log($"[ComplexFabricatorSpawnProductPacket] spawning product {complexRecipe.id} for {fab.name} with netId {NetId}");

			// Marked, because a client's own fabricator output is now blocked and this
			// is the one call that must still go through.
			//
			// The client runs its fabricators for itself: creation-time attribution
			// caught "MushBar created on the client by MicrobeMusher.SpawnOrderProduct",
			// and the host announces its own MushBar for the same order, so the client
			// ends up with two - one of them holding an id the host never issued.
			//
			// Blocking the method outright would also block this line, and then a
			// client would never receive any product at all. The same reentrancy flag
			// pattern the priority packet uses, for the same reason.
			IsApplying = true;
			try
			{
				fab.SpawnOrderProduct(complexRecipe);
			}
			finally
			{
				IsApplying = false;
			}

			RemoteProgressRegistry.Clear(NetId, RemoteProgressKind.ComplexFabricatorOrder);
		}
	}
}
