using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.Packets.World;
using UnityEngine;

namespace ONI_Together.Networking.Components.StructureStateSyncers
{
	/// <summary>
	/// Replicates the building state a player sets by clicking: enabled or disabled, a
	/// door's control state, a manual delivery amount.
	///
	/// These were synced only by event patches - Toggleable.Toggle, Door.QueueStateChange,
	/// the side screens - and an event has no repair path. If the packet is lost, culled
	/// to a viewport, or sent while the other peer is loading, the two sides disagree for
	/// the rest of the session and nothing ever revisits it.
	///
	/// That is not a theoretical risk. The same shape was measured this session on
	/// storage: a MetalRefinery held 310 kg of water on the host and 800 on the client
	/// because the delta had been missed once and the host's value never changed again.
	/// Adding a fifteen-second keyframe closed half those gaps immediately and ignoring
	/// the viewport for keyframes closed the rest, taking mass disagreements from 99 to 6
	/// with no measurable frame cost.
	///
	/// So this routes player-set flags through the same machinery: delta when they
	/// change, keyframe every fifteen seconds regardless, and the record of "sent" only
	/// advanced when a client actually received it. The event patches stay - they make
	/// the change appear immediately - but they are no longer the only mechanism.
	///
	/// Attached by the state that needs it rather than by a list of building types. A
	/// list would need extending for every building the game adds, which is exactly how
	/// storage syncing came to cover four types and miss forty.
	///
	/// Every accessor here was read out of the running game with the api verb, not
	/// guessed: Door.CurrentState and QueueStateChange, BuildingEnabledButton.IsEnabled
	/// (get and set), ManualDeliveryKG.capacity and paused.
	///
	/// This was written, measured and then held back for one release. Attaching it put a
	/// second StructureSyncerBase on buildings that already had a storage one, and the
	/// receiver handed every packet to every syncer on the object - so the storage syncer
	/// spent the run reading flag packets and logging "Key: stor not found", 149 to 229
	/// times on a client that had been at zero errors. The flags themselves behaved
	/// throughout: 700 buildings watched, 10 to 26 repairs a run, frame time unchanged.
	///
	/// StructureStatePacket now carries the name of the syncer that produced it and the
	/// receiver routes on that, so a building may hold several. That limit was never
	/// written down; it was found by hitting it.
	///
	/// And the need is measured, not assumed. A cross-peer comparison found
	/// PressureDoor@43123 Locked on the host and Opened on the client, two more doors the
	/// same, and a Generator and an IceCooledFan paused on one side and running on the
	/// other - in a single run.
	/// </summary>
	public class BuildingFlagsSyncer : StructureSyncerBase
	{
		private BuildingEnabledButton enabledButton;
		private Door door;
		private ManualDeliveryKG delivery;

		/// <summary>Flags corrected on a client because they had drifted apart.</summary>
		public static int FlagsRepaired { get; private set; }

		/// <summary>Buildings carrying player-set flags, so a zero above can be read.</summary>
		public static int Watched { get; private set; }

		private const string KeyEnabled = "bfEnabled";
		private const string KeyDoorState = "bfDoor";
		private const string KeyCapacity = "bfCapacity";
		private const string KeyPaused = "bfPaused";

		protected override void Initialize()
		{
			enabledButton = GetComponent<BuildingEnabledButton>();
			door = GetComponent<Door>();
			delivery = GetComponent<ManualDeliveryKG>();
			Watched++;

			// The optional values are the whole payload here, so they must be compared.
			// StorageStateSyncer switches this off because its own mass is the trigger;
			// this syncer has no scalar of its own that moves.
			checkOptionalsValuesForChanges = true;
		}

		protected override void SampleState(out Variant value, out bool active,
			out Dictionary<string, Variant> optionalValues)
		{
			active = false;
			optionalValues = new Dictionary<string, Variant>();

			// A packed summary as the value, so a change is detected even if the
			// optional comparison is ever switched off again.
			int packed = 0;

			if (!enabledButton.IsNullOrDestroyed())
			{
				bool on = enabledButton.IsEnabled;
				optionalValues[KeyEnabled] = on ? 1f : 0f;
				if (on) packed |= 1;
			}

			if (!door.IsNullOrDestroyed())
			{
				int state = (int)door.CurrentState;
				optionalValues[KeyDoorState] = (float)state;
				packed |= (state + 1) << 1;
			}

			if (!delivery.IsNullOrDestroyed())
			{
				optionalValues[KeyCapacity] = delivery.capacity;
				optionalValues[KeyPaused] = delivery.paused ? 1f : 0f;
				packed |= (delivery.paused ? 1 : 0) << 8;
			}

			value = (float)packed;
		}

		/// <summary>
		/// Nothing extra forces a send here - the packed value and the optional
		/// comparison already catch every flag. StorageStateSyncer needs this because
		/// temperature moves without its mass changing; a toggle does not.
		/// </summary>
		protected override bool ShouldForceSync() => false;

		protected override void ApplyState(StructureStatePacket packet)
		{
			// The event patches all bail out while this is set, so applying a correction
			// cannot bounce straight back to the host as a new change.
			bool previous = BuildingConfigPacket.IsApplyingPacket;
			BuildingConfigPacket.IsApplyingPacket = true;
			try
			{
				ApplyFlags(packet);
			}
			finally
			{
				BuildingConfigPacket.IsApplyingPacket = previous;
			}
		}

		private void ApplyFlags(StructureStatePacket packet)
		{
			if (!enabledButton.IsNullOrDestroyed()
				&& packet.OptionalValues.TryGetValue(KeyEnabled, out var enabled))
			{
				bool wanted = enabled.Float > 0.5f;
				// Only on a difference. Writing the same value every keyframe would fight
				// whatever the local simulation is doing with it.
				if (enabledButton.IsEnabled != wanted)
				{
					enabledButton.IsEnabled = wanted;
					FlagsRepaired++;
					Note($"'{gameObject.PrefabID()}' was {!wanted} and the host says {wanted}");
				}
			}

			if (!door.IsNullOrDestroyed()
				&& packet.OptionalValues.TryGetValue(KeyDoorState, out var doorState))
			{
				var wanted = (Door.ControlState)Mathf.RoundToInt(doorState.Float);
				// Through QueueStateChange, which is how the game changes a door - the
				// fields are read-only and setting one would skip the animation and the
				// pathing update. RequestedState, not CurrentState: a door in transit is
				// already heading somewhere and re-queueing the same destination every
				// keyframe would restart it.
				if (door.RequestedState != wanted)
				{
					door.QueueStateChange(wanted);
					FlagsRepaired++;
					Note($"door at {Grid.PosToCell(gameObject)} was heading to " +
						 $"{door.RequestedState} and the host says {wanted}");
				}
			}

			if (!delivery.IsNullOrDestroyed())
			{
				if (packet.OptionalValues.TryGetValue(KeyCapacity, out var capacity)
					&& !Mathf.Approximately(delivery.capacity, capacity.Float))
				{
					delivery.capacity = capacity.Float;
					FlagsRepaired++;
				}

				if (packet.OptionalValues.TryGetValue(KeyPaused, out var paused))
				{
					bool wanted = paused.Float > 0.5f;
					if (delivery.paused != wanted)
					{
						delivery.paused = wanted;
						FlagsRepaired++;
					}
				}
			}
		}

		/// <summary>
		/// Named for the first few and then counted.
		///
		/// A repair says the two peers had drifted, which is worth seeing - but a keyframe
		/// arrives for every watched building every fifteen seconds, and a line each is
		/// how per-cell logging once froze a host.
		/// </summary>
		private void Note(string what)
		{
			if (FlagsRepaired <= 5)
				DebugConsole.LogWarning($"[BuildingFlags] repaired: {what}");
		}
	}
}
