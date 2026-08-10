using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Patches.Navigation;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Shared.Profiling;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace ONI_Together.Networking.Packets.Core
{
	public class NavigatorPathPacket : IPacket
	{
		public int NetId;

		public struct PathStep
		{
			public int Cell;
			public NavType NavType;
			public byte TransitionId;

			public void Serialize(BinaryWriter writer)
			{
				using var _ = Profiler.Scope();

				writer.Write(Cell);
				writer.Write((byte)NavType);
				writer.Write(TransitionId);
			}

			public static PathStep Deserialize(BinaryReader reader)
			{
				using var _ = Profiler.Scope();

				return new PathStep
				{
					Cell = reader.ReadInt32(),
					NavType = (NavType)reader.ReadByte(),
					TransitionId = reader.ReadByte()
				};
			}
		}

		public List<PathStep> Steps = new List<PathStep>();

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			using (var memStream = new MemoryStream())
			{
				using (var tempWriter = new BinaryWriter(memStream, System.Text.Encoding.Default, leaveOpen: true))
				{
					tempWriter.Write(NetId);
					tempWriter.Write(Steps.Count);
					foreach (var step in Steps)
						step.Serialize(tempWriter);
				}

				byte[] rawData = memStream.ToArray();

				using (var compressed = new MemoryStream())
				{
					using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
					{
						gzip.Write(rawData, 0, rawData.Length);
					}

					byte[] compressedBytes = compressed.ToArray();
					writer.Write(compressedBytes.Length);
					writer.Write(compressedBytes);
				}
			}
		}

		/// <summary>A path is a route across one asteroid; anything larger is not a path.</summary>
		private const int MaxSteps = 4096;

		/// <summary>Chunking splits anything bigger, so one frame cannot legitimately exceed this.</summary>
		private const int MaxCompressedBytes = 64 * 1024;

		/// <summary>Five bytes a step, so an order of magnitude above the largest real path.</summary>
		private const int MaxDecompressedBytes = 256 * 1024;

		/// <summary>
		/// Copies at most <paramref name="limit"/> bytes and then stops.
		/// Stream.CopyTo has no ceiling, which is what makes a decompression bomb
		/// possible: the cost is set by the sender, not by the receiver.
		/// </summary>
		private static void CopyBounded(Stream from, Stream to, int limit)
		{
			var buffer = new byte[8192];
			int total = 0;
			int read;
			while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
			{
				total += read;
				if (total > limit)
				{
					ThrottledLog.Warn($"[NavigatorPath] path expanded past {limit} bytes; truncating");
					break;
				}
				to.Write(buffer, 0, read);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			// Three bounds, because this is the only packet on the wire that
			// decompresses, and a compressed payload is the one place where a
			// small number of received bytes can cost an unbounded amount of
			// memory. The length was believed as given, the bytes were read
			// against it, and the gzip stream was copied out with no ceiling at
			// all - a few hundred bytes of zeroes expand to gigabytes.
			int compressedLength = reader.ReadInt32();
			if (compressedLength < 0 || compressedLength > MaxCompressedBytes)
			{
				ThrottledLog.Warn(
					$"[NavigatorPath] refusing a path claiming {compressedLength} compressed bytes " +
					$"(limit {MaxCompressedBytes})");
				Steps.Clear();
				return;
			}

			byte[] compressedBytes = reader.ReadBytes(compressedLength);

			using (var compressed = new MemoryStream(compressedBytes))
			using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
			using (var decompressed = new MemoryStream())
			{
				CopyBounded(gzip, decompressed, MaxDecompressedBytes);
				decompressed.Position = 0;

				using (var tempReader = new BinaryReader(decompressed))
				{
					NetId = tempReader.ReadInt32();
					int count = PacketList.ReadCount(tempReader, "NavigatorPathPacket.Steps", MaxSteps);

					Steps.Clear();
					for (int i = 0; i < count; i++)
						Steps.Add(PathStep.Deserialize(tempReader));
				}
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost)
				return;

			if (!PassesPreliminaryChecks(out var entity, out var navigator))
				return;

			var newPath = new PathFinder.Path
			{
				nodes = new List<PathFinder.Path.Node>(Steps.Count)
			};

			foreach (var step in Steps)
			{
				newPath.nodes.Add(new PathFinder.Path.Node
				{
					cell = step.Cell,
					navType = step.NavType,
					transitionId = step.TransitionId
				});
			}

			navigator.path = newPath;

			// Final destination position
			int finalCell = Steps[Steps.Count - 1].Cell;
			Vector3 finalPos = Grid.CellToPosCBC(finalCell, Grid.SceneLayer.Move);

			// Create a dummy GameObject
			GameObject dummyTarget = new GameObject($"NetNav_Target_{NetId}");
			dummyTarget.transform.position = finalPos;
			dummyTarget.transform.SetParent(Game.Instance.transform, worldPositionStays: true);

			var targetBehaviour = dummyTarget.AddComponent<KMonoBehaviour>();

			System.Action cleanup = () =>
			{
				if (dummyTarget != null)
				{
					Object.Destroy(dummyTarget);
					DebugConsole.Log($"[NavigatorPathPacket] Cleaned up dummy target for NetId {NetId}");
					navigator.SetCanAdvance(false);
				}
			};

			// Inject callback into navigator events
			navigator.Subscribe((int)GameHashes.DestinationReached, (data) => cleanup.Invoke());
			navigator.Subscribe((int)GameHashes.NavigationFailed, (data) => cleanup.Invoke());

			// Trigger movement
			bool result = navigator.ClientGoTo(targetBehaviour, new CellOffset[] { CellOffset.none });

			if (!result)
			{
				DebugConsole.LogWarning($"[NavigatorPathPacket] ClientGoTo failed for {NetId}");
				Object.Destroy(dummyTarget); // immediate fallback cleanup
			}

			DebugConsole.Log($"[NavigatorPathPacket] Path with {Steps.Count} nodes applied to NetId {NetId}");
		}

		private bool PassesPreliminaryChecks(out Component entity, out Navigator navigator)
		{
			using var _ = Profiler.Scope();

			entity = null;
			navigator = null;

			if (!NetworkIdentityRegistry.TryGet(NetId, out var foundEntity))
			{
				ThrottledLog.Warn($"[NavigatorPathPacket] Could not find entity with NetId {NetId}");
				return false;
			}

			if (!foundEntity)
				return false;

			entity = foundEntity;

			if (!entity.TryGetComponent(out navigator))
			{
				DebugConsole.LogWarning($"[NavigatorPathPacket] Entity {NetId} has no Navigator");
				return false;
			}

			if (!navigator)
				return false;

			// The count half of this was commented out, which left Steps[-1]
			// reachable below for an empty path.
			if (Steps == null || Steps.Count == 0)
			{
				DebugConsole.LogWarning($"[NavigatorPathPacket] Received invalid path for {NetId}");
				return false;
			}

			// CellToPosCBC divides by Grid.WidthInCells, zero until a world
			// exists, and a client processes packets while still in the menu.
			if (Grid.WidthInCells == 0)
				return false;

			return true;
		}

	}
}
