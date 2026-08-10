using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System.IO;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Tools.Deconstruct
{
	public class DeconstructCompletePacket : IPacket
	{
		public int Cell, ObjectLayer;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(Cell);
			writer.Write(ObjectLayer);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			Cell = reader.ReadInt32();
			ObjectLayer = reader.ReadInt32();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (!Grid.IsValidCell(Cell))
				return;

			// A missed deconstruct is a hard, permanent divergence: this peer
			// keeps a building the host destroyed, and BuildingSyncer then
			// reconciles around a world that is wrong. The layer comes off the
			// wire, so a building registered on a different layer than the host
			// computed used to be missed here in silence - worth looking at the
			// other layers of the same cell before giving up.
			GameObject go = Grid.Objects[Cell, ObjectLayer];

			if (go == null)
			{
				for (int layer = 0; layer < (int)global::ObjectLayer.NumLayers; layer++)
				{
					var candidate = Grid.Objects[Cell, layer];
					if (candidate == null) continue;
					if (!candidate.TryGetComponent<Deconstructable>(out var candidateDeconstructable) || candidateDeconstructable == null) continue;

					DebugConsole.LogWarning(
						$"[DeconstructComplete] nothing on layer {ObjectLayer} at cell {Cell}; " +
						$"deconstructing '{candidate.name}' found on layer {layer} instead");
					go = candidate;
					break;
				}
			}

			if (go == null)
			{
				ThrottledLog.Warn(
					$"[DeconstructComplete] nothing to deconstruct at cell {Cell} - this peer keeps a building the host destroyed");
				return;
			}

			if (go.TryGetComponent<Deconstructable>(out var deconstructable) && !deconstructable.HasBeenDestroyed)
			{
				DebugConsole.Log($"[DeconstructCompletePacket] Forcing deconstruct at cell {Cell} on objectlayer {ObjectLayer} on client.");
				deconstructable.ForceDestroyAndGetMaterials();
			}
			else
			{
				ThrottledLog.Warn($"[DeconstructComplete] '{go.name}' at cell {Cell} cannot be deconstructed here");
			}
		}
	}
}
