using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	public class PrioritizeStatePacket : IPacket
	{
		public struct PriorityData
		{
			public int NetId;
			public int PriorityClass;
			public int PriorityValue;

			/// <summary>
			/// Where the object is, for the ones that have no address.
			///
			/// A dig marker is defined entirely by its cell - there is nothing else to
			/// say about it - and it never gets a NetId, so every priority a player set
			/// on one was dropped at the sender: 7 to 9 a run, named in the host log as
			/// "not sending a priority change for 'DigPlacer': it has no NetId".
			///
			/// Buildings and duplicants keep travelling by address, because a cell does
			/// not identify a duplicant and a cell can hold several buildings. This is
			/// the fallback for the case where the cell is the whole identity.
			/// </summary>
			public int Cell;
		}

		public List<PriorityData> Priorities = new List<PriorityData>();

		/// <summary>Priority changes applied to a marker found by its cell.</summary>
		public static int AppliedByCell { get; private set; }

		/// <summary>Markers found by cell that already held the priority sent.</summary>
		public static int MatchedByCell { get; private set; }

		/// <summary>Cell-addressed priorities that found no marker at that cell.</summary>
		public static int NoMarkerAtCell { get; private set; }
		public static bool IsApplying = false;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(Priorities.Count);
			foreach (var p in Priorities)
			{
				writer.Write(p.NetId);
				writer.Write(p.PriorityClass);
				writer.Write(p.PriorityValue);
				writer.Write(p.Cell);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			int count = PacketList.ReadCount(reader, "PrioritizeStatePacket.Priorities");
			Priorities = new List<PriorityData>(count);
			for (int i = 0; i < count; i++)
			{
				Priorities.Add(new PriorityData
				{
					NetId = reader.ReadInt32(),
					PriorityClass = reader.ReadInt32(),
					PriorityValue = reader.ReadInt32(),
					Cell = reader.ReadInt32()
				});
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			// Both host and client need to apply priority changes
			try
			{
				IsApplying = true;
				foreach (var p in Priorities)
				{
					// By address when there is one, by cell when there is not.
					//
					// The cell path is for markers whose cell is their whole identity -
					// a dig order. Resolved through Grid.Objects on the marker's own
					// layer rather than by searching, so it cannot pick up a building
					// that happens to share the cell.
					Prioritizable byCell = null;
					if (p.NetId == 0 && Grid.IsValidCell(p.Cell))
					{
						var marker = Grid.Objects[p.Cell, (int)ObjectLayer.DigPlacer];
						if (marker != null && !marker.IsNullOrDestroyed())
							marker.TryGetComponent<Prioritizable>(out byCell);

						if (byCell != null && !byCell.IsNullOrDestroyed())
						{
							var wanted = new PrioritySetting((PriorityScreen.PriorityClass)p.PriorityClass, p.PriorityValue);
							if (!byCell.GetMasterPriority().Equals(wanted))
							{
								byCell.SetMasterPriority(wanted);
								AppliedByCell++;
							}
							else
							{
								// Found and already right.
								//
								// Counted apart from a change, because otherwise a zero
								// applied count says both "the marker was not there" and
								// "it was there and needed nothing", and this project has
								// read that kind of zero as a failed fix four times.
								MatchedByCell++;
							}
						}
						else
						{
							NoMarkerAtCell++;
						}
						continue;
					}

					if (NetworkIdentityRegistry.TryGet(p.NetId, out var identity) && identity != null)
					{
						var prioritizable = identity.GetComponent<Prioritizable>();
						if (prioritizable != null)
						{
							var newSetting = new PrioritySetting((PriorityScreen.PriorityClass)p.PriorityClass, p.PriorityValue);
							// Only update if different to avoid event spam
							if (!prioritizable.GetMasterPriority().Equals(newSetting))
							{
								prioritizable.SetMasterPriority(newSetting);
							}
						}
					}
				}
			}
			finally
			{
				IsApplying = false;
			}

			// If host received from client, rebroadcast to all other clients
			if (MultiplayerSession.IsHost && Priorities.Count > 0)
			{
				PacketSender.SendToAllClients(this);
			}
		}
	}
}
