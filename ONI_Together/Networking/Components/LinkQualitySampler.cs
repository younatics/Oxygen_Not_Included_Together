using System.Collections.Generic;
using System.Linq;
using ONI_Together.DebugTools;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	/// <summary>
	/// The round-trip time of the link, sampled while nothing is stressing it.
	///
	/// A single instantaneous read is not a measurement of a link, it is a
	/// measurement of whatever happened to be in flight. The Latency check read one
	/// and failed on 169, 193, 205, 221, 228, 236, 244, 261 and 262 ms across nine
	/// runs, and each of those readings was taken in the middle of the test suite -
	/// which dumps eight thousand identities, feeds the handlers deliberate rubbish,
	/// and is by far the heaviest traffic the session produces. The health row,
	/// sampling the same link a minute later, read 41 to 76.
	///
	/// So the reading was true and the conclusion from it was wrong: the suite was
	/// measuring itself. This keeps a rolling window of samples taken outside the
	/// suite and reports the median, which is what "how is the link" was supposed to
	/// mean. The median rather than the mean, because one dropped heartbeat should
	/// not decide the answer.
	/// </summary>
	public class LinkQualitySampler : MonoBehaviour
	{
		private const float SampleIntervalSeconds = 1f;

		/// <summary>Two minutes of samples - long enough to survive a hard sync.</summary>
		private const int WindowSize = 120;

		private static readonly List<int> _samples = new List<int>();

		private float _next;

		/// <summary>Time series interval. Ten seconds gives thirty points in a
		/// five-minute run, which is enough to see a slope.</summary>
		private const float TraceIntervalSeconds = 10f;

		private float _nextTrace;
		private float _sessionStart = -1f;

		/// <summary>How many quiet samples are held. Below a handful, say so rather
		/// than answering from two readings.</summary>
		public static int SampleCount => _samples.Count;

		/// <summary>
		/// The median quiet round-trip time, or -1 when there is nothing to report -
		/// not in a session, hosting, or too few samples yet.
		/// </summary>
		public static int MedianRttMs
		{
			get
			{
				if (_samples.Count == 0) return -1;
				var sorted = _samples.OrderBy(x => x).ToList();
				return sorted[sorted.Count / 2];
			}
		}

		/// <summary>The worst quiet sample in the window, so a spike is still visible
		/// after the median has smoothed it away.</summary>
		public static int WorstRttMs => _samples.Count == 0 ? -1 : _samples.Max();

		public static void Reset() => _samples.Clear();

		private void Update()
		{
			using var _ = Profiler.Scope();

			// Driven from here because this component ticks whenever the game is
			// loaded, session or not - and "not in a session any more" is precisely
			// the state a post-load reconnect has to act in. The transport's own
			// Update stops being called then, which is why the first attempt at this
			// set the flag correctly and never acted on it.
			// Transport.Lan, not Transport.Riptide - the folder is named Riptide and
			// the namespace is not.
			Transport.Lan.RiptideClient.TryReconnectAfterLoad();

			if (Time.unscaledTime < _next) return;
			_next = Time.unscaledTime + SampleIntervalSeconds;

			// Only a client has a round trip to measure - the host is the other end.
			if (!MultiplayerSession.InSession || MultiplayerSession.IsHost) return;
			if (NetworkConfig.TransportClient == null) return;

			if (_sessionStart < 0f) _sessionStart = Time.unscaledTime;

			// A time series, because the first explanation was wrong and a single
			// number could not have shown that.
			//
			// The reading was blamed on the test suite's own traffic, and the quiet
			// median was supposed to settle it. It did - against the hypothesis: the
			// health rows read 65 and 68 ms in the first two minutes while the quiet
			// samples taken later clustered at 262 to 264. Load is not the variable
			// that moved. Elapsed time is.
			//
			// So this logs the round trip against seconds-since-joined with the suite
			// state beside it. A slope says the link degrades as the session runs,
			// which is the shape of the complaint that started this work; a step at
			// the moment the suite begins says the opposite. One run of thirty points
			// distinguishes them, and neither could be told from an average.
			int trace = NetworkConfig.TransportClient.GetPing();
			if (Time.unscaledTime >= _nextTrace)
			{
				_nextTrace = Time.unscaledTime + TraceIntervalSeconds;
				DebugConsole.Log(
					$"[RTT] t={Time.unscaledTime - _sessionStart:0}s rtt={trace}ms " +
					$"suite={(UnitTestRegistry.IsRunning ? 1 : 0)} " +
					$"quietSamples={_samples.Count}");
			}

			// The whole point: skip while the suite is running, because the suite is
			// the load. Without this the window fills with the thing being excluded.
			if (UnitTestRegistry.IsRunning) return;

			int rtt = NetworkConfig.TransportClient.GetPing();

			// Zero or negative means the transport has no estimate yet, which is not
			// the same as a fast link and must not be averaged in as one.
			if (rtt <= 0) return;

			_samples.Add(rtt);
			if (_samples.Count > WindowSize)
				_samples.RemoveAt(0);
		}
	}
}
