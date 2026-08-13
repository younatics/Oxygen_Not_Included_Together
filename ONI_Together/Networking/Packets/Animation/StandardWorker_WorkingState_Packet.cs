using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shared.Interfaces.Networking;
using Shared.Profiling;
using UnityEngine;
using static RancherChore;

namespace ONI_Together.Networking.Packets.Animation
{
	internal class StandardWorker_WorkingState_Packet : IPacket, IRequiresLoadedWorld, IAddressedPacket
	{
		/// <summary>
		/// Both ends of this packet are addresses, and either can be missing.
		///
		/// Measured on a client: "Could not resolve workable 0 for worker Humphrey" -
		/// the worker was fine and the workable had no id, because it was something ONI
		/// refuses one to. The receiver's only option is to drop it, so it is dropped
		/// here instead, where it is counted and costs no bandwidth.
		///
		/// The workable is only checked when starting, because that is the only case
		/// that serialises it.
		/// </summary>
		public bool IsAddressable =>
			WorkerNetId != 0 && (!StartingToWork || WorkableNetId != 0);

		public StandardWorker_WorkingState_Packet() { }

		public StandardWorker_WorkingState_Packet(StandardWorker worker, Workable workable, bool startedWorking)
		{
			using var _ = Profiler.Scope();

			WorkerNetId = worker.GetNetId();
			StartingToWork = startedWorking;
			if (startedWorking)
			{
				WorkableNetId = workable.GetNetId();
				WorkableType = workable.GetType().AssemblyQualifiedName;
			}
		}

		int WorkerNetId, WorkableNetId;
		string WorkableType;
		bool StartingToWork;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(WorkerNetId);
			writer.Write(StartingToWork);
			if (StartingToWork)
			{
				writer.Write(WorkableNetId);
				writer.Write(WorkableType);
			}
		}
		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			WorkerNetId = reader.ReadInt32();
			StartingToWork = reader.ReadBoolean();
			if (StartingToWork)
			{
				WorkableNetId = reader.ReadInt32();
				WorkableType = reader.ReadString();
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost)
				return;

			if (TryApply())
				return;

			if (StartingToWork && Game.Instance != null)
			{
				Game.Instance.StartCoroutine(RetryStartWork(Clone()));
			}
		}

		private bool TryApply(bool logFailure = false)
		{
			using var _ = Profiler.Scope();

			if (!NetworkIdentityRegistry.TryGetComponent<StandardWorker>(WorkerNetId, out var worker))
			{
				if (logFailure)
				{
					DebugConsole.LogWarning($"[StandardWorker_WorkingState_Packet] Could not find worker {WorkerNetId}");
				}
				return false;
			}

			GameObject workableGO = null;
			if (!StartingToWork)
			{
				worker.StopWork();
				DebugConsole.Log("[StandardWorker_WorkingState_Packet] workable change triggered for " + worker.name + ": stopped working");
				return true;
			}

			if (!NetworkIdentityRegistry.TryGetComponent<Workable>(WorkableNetId, out var protoWorkable))
			{
				if (logFailure)
				{
					DebugConsole.LogWarning($"[StandardWorker_WorkingState_Packet] Could not resolve workable {WorkableNetId} for worker {worker.name}");
				}
				return false;
			}

			workableGO = protoWorkable.gameObject;

			var workableType = AccessTools.TypeByName(WorkableType);
			if (workableType == null)
			{
				if (logFailure)
				{
					DebugConsole.LogWarning("Could not find workable type " + WorkableType);
				}
				return false;
			}

			var targetWorkableCmp = workableGO.GetComponent(workableType);
			if (targetWorkableCmp == null || targetWorkableCmp is not Workable workable)
			{
				if (logFailure)
				{
					DebugConsole.LogWarning("Could not find workable of type " + WorkableType + " on " + workableGO.GetProperName());
				}
				return false;
			}

			try
			{
				if (!worker.state.Equals(StandardWorker.State.Idle))
				{
					worker.StopWork();
				}
				worker.StartWork(new(workable));
			}
			catch (System.Exception ex)
			{
				if (logFailure)
				{
					DebugConsole.LogWarning($"[StandardWorker_WorkingState_Packet] StartWork failed for {worker.name} on {workableGO.name}: {ex.GetType().Name}");
				}
				return false;
			}

			DebugConsole.Log("[StandardWorker_WorkingState_Packet] workable change triggered for " + worker.name + ": Started working on " + workableGO.name);
			return true;
		}

		private StandardWorker_WorkingState_Packet Clone()
		{
			return new StandardWorker_WorkingState_Packet
			{
				WorkerNetId = WorkerNetId,
				WorkableNetId = WorkableNetId,
				WorkableType = WorkableType,
				StartingToWork = StartingToWork
			};
		}

		private static IEnumerator RetryStartWork(StandardWorker_WorkingState_Packet packet)
		{
			for (int attempt = 0; attempt < 10; attempt++)
			{
				yield return null;

				if (!MultiplayerSession.InSession || MultiplayerSession.IsHost)
					yield break;

				if (packet.TryApply(logFailure: attempt == 9))
					yield break;
			}
		}
	}
}
