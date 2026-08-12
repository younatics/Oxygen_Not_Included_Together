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

		/// <summary>
		/// The navigation currently driven by a received path, per entity, so a
		/// replacement path can take the previous one down. One entry per moving
		/// duplicant rather than one per packet.
		/// </summary>
		private sealed class PendingNavigation
		{
			public GameObject Target;
			public Navigator Navigator;
			public int ReachedHandle = -1;
			public int FailedHandle = -1;
			public bool Done;
		}

		private static readonly Dictionary<int, PendingNavigation> _pending = new Dictionary<int, PendingNavigation>();

		/// <summary>How many navigations are being tracked. Zero-growth is the property under test.</summary>
		public static int PendingCount => _pending.Count;

		private static void Release(PendingNavigation nav, bool stopAdvancing)
		{
			if (nav == null || nav.Done)
				return;
			nav.Done = true;

			// Unsubscribing has to survive a navigator that has since been
			// destroyed: this runs from a packet, and the duplicant it names may
			// have been removed in between.
			if (!nav.Navigator.IsNullOrDestroyed() && !nav.Navigator.gameObject.IsNullOrDestroyed())
			{
				if (nav.ReachedHandle != -1) nav.Navigator.gameObject.Unsubscribe(nav.ReachedHandle);
				if (nav.FailedHandle != -1) nav.Navigator.gameObject.Unsubscribe(nav.FailedHandle);
				if (stopAdvancing) nav.Navigator.SetCanAdvance(false);
			}

			if (!nav.Target.IsNullOrDestroyed())
				Object.Destroy(nav.Target);
		}

		/// <summary>The path finished or failed on its own.</summary>
		private static void Finish(int netId, PendingNavigation nav)
		{
			Release(nav, stopAdvancing: true);
			if (_pending.TryGetValue(netId, out var current) && ReferenceEquals(current, nav))
				_pending.Remove(netId);
		}

		/// <summary>A newer path arrived, or this one never started.</summary>
		private static void Cancel(int netId)
		{
			if (!_pending.TryGetValue(netId, out var nav))
				return;
			_pending.Remove(netId);
			// Not SetCanAdvance(false): a replacement path is about to move this
			// duplicant, and cancelling the old one must not stop the new one.
			Release(nav, stopAdvancing: false);
		}

		public static void ResetForNewSession()
		{
			foreach (var nav in _pending.Values)
				Release(nav, stopAdvancing: false);
			_pending.Clear();
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

			// A path replaces the one before it, so the one before it has to be
			// taken down. Nothing did that.
			//
			// Every packet that arrived created a dummy target parented to
			// Game.Instance and subscribed two handlers to the navigator, and both
			// were released only if DestinationReached or NavigationFailed fired
			// for that exact path. A duplicant re-routes constantly, so most paths
			// are superseded instead of finished: the GameObject stayed in the
			// scene for the rest of the session and the two handlers stayed on the
			// navigator's list. The list is walked on every event, so arriving
			// anywhere cost more the longer the session had run - and it never got
			// cheaper. This is the shape of a game that starts fine and degrades,
			// and the client that had to be killed after fourteen minutes had 4291
			// registry misses and a stalled main thread to go with it.
			Cancel(NetId);

			var pending = new PendingNavigation { Target = dummyTarget, Navigator = navigator };
			_pending[NetId] = pending;

			int netId = NetId;
			System.Action cleanup = () => Finish(netId, pending);

			pending.ReachedHandle = navigator.gameObject.Subscribe(
				(int)GameHashes.DestinationReached, _ => cleanup.Invoke());
			pending.FailedHandle = navigator.gameObject.Subscribe(
				(int)GameHashes.NavigationFailed, _ => cleanup.Invoke());

			// Trigger movement
			bool result = navigator.ClientGoTo(targetBehaviour, new CellOffset[] { CellOffset.none });

			if (!result)
			{
				DebugConsole.LogWarning($"[NavigatorPathPacket] ClientGoTo failed for {NetId}");
				Cancel(NetId); // immediate fallback cleanup, handlers included
			}

			// Was one line per packet, on the busiest packet there is. Per-object
			// logging at packet rate is what froze a host earlier in this work.
			ThrottledLog.Info("[NavigatorPathPacket] paths applied");
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
