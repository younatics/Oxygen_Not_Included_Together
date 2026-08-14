using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using System.IO;
using Shared.Profiling;
using UnityEngine;
using TemplateClasses;

namespace ONI_Together.Networking.Packets.World
{
	public class WorldDamageSpawnResourcePacket : IPacket
	{
		public int NetId;
		public Vector3 Position;
		public float Mass;
		public float Temperature;
		public ushort ElementIndex;
		public byte DiseaseIndex;
		public int DiseaseCount;

		public WorldDamageSpawnResourcePacket() { }

		public WorldDamageSpawnResourcePacket(int netId, Vector3 pos, float mass, float temp, ushort elementIdx, byte diseaseIdx, int diseaseCount)
		{
			using var _ = Profiler.Scope();

			NetId = netId;
			Position = pos;
			Mass = mass;
			Temperature = temp;
			ElementIndex = elementIdx;
			DiseaseIndex = diseaseIdx;
			DiseaseCount = diseaseCount;
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(NetId);
			writer.Write(Position.x);
			writer.Write(Position.y);
			writer.Write(Position.z);
			writer.Write(Mass);
			writer.Write(Temperature);
			writer.Write(ElementIndex);
			writer.Write(DiseaseIndex);
			writer.Write(DiseaseCount);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			NetId = reader.ReadInt32();
			Position = new Vector3(
					reader.ReadSingle(),
					reader.ReadSingle(),
					reader.ReadSingle()
			);
			Mass = reader.ReadSingle();
			Temperature = reader.ReadSingle();
			ElementIndex = reader.ReadUInt16();
			DiseaseIndex = reader.ReadByte();
			DiseaseCount = reader.ReadInt32();
		}

		/// <summary>
		/// Ore ids that were put on an object whose prefab is not the element they were
		/// issued for. Each one is a host packet that will address the wrong thing.
		/// </summary>
		public static int RenamedSomethingElse { get; private set; }

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			Element element = ElementLoader.elements[ElementIndex];

			// This is the busiest spawn path there is - every dig, every broken
			// tile, every meteor - so a joining client that is still in the menu
			// receives these before anything else. Spawning then runs the ore's
			// OnPrefabInit against an empty Grid, and Pickupable divides by
			// Grid.WidthInCells, which is zero until a world exists.
			if (Grid.WidthInCells == 0 || !Grid.IsValidCell(Grid.PosToCell(Position)))
			{
				DebugConsole.LogWarning(
					$"[WorldDamageSpawnResource] ignoring drop at {Position}: no world loaded yet");
				return;
			}

			InvokePlaySoundForSubstance(element, Position);

			float dropMass = Mass;
			if (dropMass <= 0f)
				return;

			// Claim the id before the object exists. SpawnResource returns an
			// active object, so OnSpawn has already run by the time we get it -
			// and without this it will have minted an id of its own and taken a
			// registry slot that belongs to somebody else.
			// Named, so only the pile this id is for can take it.
			//
			// The frame guard was not enough. SpawnResource sometimes merges into an
			// existing pile and returns it without an OnSpawn, so the reservation is
			// still set when the next object of that frame spawns - and that object
			// collects an id the host issued for this element. Three ids survived every
			// other fix that way, the host holding food and the client holding a
			// Creature pile under the same number.
			//
			// The element's tag is what the spawned pile's PrefabID reads as, which is
			// what makes the comparison on the other side possible at all.
			NetworkIdentity.ReserveNextNetId(NetId, element.tag.Name);

			// Marked as host-authored so the counter that watches for a client making
			// matter of its own does not count the matter the host just asked for.
			GameObject dropped;
			using (Patches.World.Substance_SpawnResource_Patch.HostAuthored())
				dropped = element.substance.SpawnResource(Position, dropMass, Temperature, DiseaseIndex, DiseaseCount);

			// Released here, on every path, whatever SpawnResource did.
			//
			// The reservation exists for the object this one call creates, and by the
			// time it returns that object's OnSpawn has already run - so from this
			// line on the reservation can only be consumed by something else. It was
			// cleared on the two failure paths below and not on the success path, and
			// success is exactly where it survives: when SpawnResource merges into an
			// existing pile it creates nothing, no OnSpawn runs, and the id stays
			// reserved for whatever spawns next. Ten-run batches showed the result
			// three times in ten - a resource and an unrelated critter or gas sharing
			// one id, with the field and the registry disagreeing.
			NetworkIdentity.ReserveNextNetId(0);

			// SpawnResource returns null when the element cannot be placed where
			// it was asked for, and the identity is only there if the prefab
			// carries one. Both were dereferenced unchecked, so a refused drop
			// threw out of the packet handler instead of being dropped - and the
			// reserved id above would have been left dangling either way.
			if (dropped == null)
			{
				DebugConsole.LogWarning(
					$"[WorldDamageSpawnResource] {element?.id} could not be spawned at {Position}");
				NetworkIdentity.ReserveNextNetId(0);
				return;
			}

			if (!dropped.TryGetComponent<NetworkIdentity>(out var identity) || identity == null)
			{
				DebugConsole.LogWarning(
					$"[WorldDamageSpawnResource] {dropped.name} has no NetworkIdentity; NetId {NetId} is unclaimed");
				return;
			}

			// Only rename it if it is the thing that was asked for.
			//
			// SpawnResource does not always hand back a new object: it can merge
			// the mass into a pile that is already there, and it returns that pile.
			// Renaming whatever comes back means the host's id can land on an
			// unrelated object that happens to be at the same place - and it did.
			// A cross-peer comparison found NetId -1258014647 held by Hydrogen at
			// cell 52119 on the host and DirtyWater at 43903 on the client. The
			// host's number is its own hash exactly; the client's DirtyWater hashes
			// to something 174 million away, so it did not compute that id, it was
			// given it here. Every packet the host then sent about its hydrogen was
			// applied to the client's dirty water.
			//
			// Cheap to check, because the packet already says which element it is.
			if (!dropped.TryGetComponent<PrimaryElement>(out var spawnedElement)
				|| spawnedElement.ElementID != element.id)
			{
				DebugConsole.LogWarning(
					$"[WorldDamageSpawnResource] refusing to put NetId {NetId} on " +
					$"{dropped.PrefabID()} - asked for {element.id} and got " +
					$"{(spawnedElement == null ? "no element" : spawnedElement.ElementID.ToString())}. " +
					"SpawnResource returned something else, probably an existing pile.");
				NetworkIdentity.ReserveNextNetId(0);
				return;
			}

			// What is actually being named, when it is not what the packet is about.
			//
			// The element check above is meant to stop this id landing on something
			// else, and it passes - yet a cross-peer comparison finds eight objects on
			// the client whose PrefabID reads 'Creature', each holding the id the host
			// gave a BasicPlantFood, a HatchBaby or a CrabBaby, and the client's own log
			// attributes the naming here: "[IdMove] 'Creature' 0 -> N (named by the host
			// by WorldDamageSpawnResourcePacket.OnDispatched)".
			//
			// So either the element matches on something that is not an ore pile, or the
			// object being renamed is not the one that was spawned. The name and the cell
			// say which, and neither has been printed at this line before.
			string droppedName = dropped.PrefabID().ToString();
			if (droppedName != element.tag.Name)
			{
				RenamedSomethingElse++;
				DebugConsole.LogWarning(
					$"[WorldDamageSpawnResource] naming '{droppedName}' at cell " +
					$"{Grid.PosToCell(dropped)} with NetId {NetId}, which was issued for " +
					$"'{element.tag.Name}' - the element matched but the object does not");
			}

			if (identity.NetId != NetId)
				identity.OverrideNetId(NetId);
			DebugConsole.Log("[WorldDamageSpawnResourcePacket] Synchronized Network ID");

			// First check GroundItemPickedUp, then PickupItem then StoreItem, TODO: Rope into 1 list
			if (GroundItemPickedUpPacket.TryConsumePending(NetId) || StorageItemPacket.TryConsumePending(NetId))
			{
				DebugConsole.Log($"[WorldDamageSpawnResourcePacket] Consumed pending ground-item pickup for NetId {NetId}");
				Util.KDestroyGameObject(dropped);
				return;
			}

			Pickupable pickup = dropped.GetComponent<Pickupable>();
			if (pickup != null && pickup.GetMyWorld()?.worldInventory.IsReachable(pickup) == true)
			{
				PopFXManager.Instance.SpawnFX(
						PopFXManager.Instance.sprite_Resource,
						Mathf.RoundToInt(dropMass) + " " + element.name,
						dropped.transform
				);
			}
		}

		private static void InvokePlaySoundForSubstance(Element element, Vector3 position)
		{
			using var _ = Profiler.Scope();

			var method = typeof(WorldDamage).GetMethod("PlaySoundForSubstance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

			if (method == null)
			{
				DebugConsole.LogWarning("[Multiplayer] Could not find PlaySoundForSubstance via reflection.");
				return;
			}

			var worldDamage = WorldDamage.Instance;

			if (worldDamage == null)
			{
				DebugConsole.LogWarning("[Multiplayer] WorldDamage.Instance is null.");
				return;
			}

			method.Invoke(worldDamage, new object[] { element, position });
		}

	}
}
