using System;
using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
	/// <summary>
	/// Every status item in the colony must be able to say its own name.
	///
	/// This is the client crash from the live session, turned into something that runs
	/// without a mouse. Hovering over an object makes ONI resolve the names of its
	/// status items, and one of them threw:
	///
	///   NullReferenceException at GameObject.GetComponent[T]
	///     Database.MiscStatusItems+&lt;&gt;c.&lt;CreateStatusItems&gt;b__74_30 (String, Object data)
	///     StatusItem.ResolveString -&gt; StatusItem.GetName
	///     StatusItemGroup+Entry.GetName
	///     SelectToolHoverTextCard.UpdateHoverElements &lt;- InterfaceTool.LateUpdate
	///
	/// Ten times in four seconds, once per frame that the cursor sat on it, and then the
	/// session ended - ONI turns repeated errors into a report and closes the game.
	///
	/// A hundred scenario runs never saw it, and could not have: the harness never moves
	/// the mouse. That is the same blind spot that hid the printing pod crash, and both
	/// were found by a person playing rather than by anything here.
	///
	/// But the mouse is not the mechanism - it only decides when GetName is called. So
	/// this calls GetName on every entry directly. Same code, no cursor, and it names the
	/// object instead of leaving a lambda index in a stack trace.
	/// </summary>
	public static class StatusItemHoverTests
	{
		[UnitTest(name: "Every status item can resolve its own name", category: "UI")]
		public static UnitTestResult StatusItemsResolve()
		{
			if (Game.Instance == null)
				return UnitTestResult.Skip("no colony loaded");

			var broken = new List<string>();
			int entriesChecked = 0;
			int objectsChecked = 0;

			foreach (var selectable in UnityEngine.Object.FindObjectsByType<KSelectable>(
						 FindObjectsInactive.Exclude, FindObjectsSortMode.None))
			{
				if (selectable.IsNullOrDestroyed() || selectable.gameObject.IsNullOrDestroyed())
					continue;

				var group = selectable.GetStatusItemGroup();
				if (group == null) continue;

				objectsChecked++;

				// The group is enumerated into a list first. GetName resolves strings and
				// a resolver is entitled to touch the group; mutating a collection while
				// enumerating it would turn a diagnostic into a second bug.
				var entries = new List<StatusItemGroup.Entry>();
				foreach (var entry in group) entries.Add(entry);

				foreach (var entry in entries)
				{
					entriesChecked++;
					try
					{
						// The exact call the hover card makes.
						var unused = entry.GetName();
					}
					catch (Exception ex)
					{
						if (broken.Count >= 8) continue;

						// With the top frames, because a lambda index is what the live
						// crash gave and it took a log comparison to place it. The object
						// name is what makes it findable in game.
						string where = "no stack";
						if (ex.StackTrace != null)
						{
							var frames = ex.StackTrace.Split('\n');
							var top = new List<string>();
							for (int i = 0; i < frames.Length && top.Count < 3; i++)
								top.Add(frames[i].Trim());
							where = string.Join(" | ", top);
						}

						broken.Add($"{selectable.gameObject.name}#{entry.id} threw " +
								   $"{ex.GetType().Name} :: {where}");
					}
				}
			}

			if (broken.Count > 0)
			{
				return UnitTestResult.Fail(
					$"{broken.Count} status item(s) cannot resolve their name, so hovering " +
					$"over the object throws once per frame until the game closes itself: " +
					string.Join(" ;; ", broken));
			}

			// Says what was covered. A colony with no status items would pass this while
			// proving nothing, and reading such a pass as a fix is a mistake this project
			// has made four times.
			if (entriesChecked == 0)
				return UnitTestResult.Skip(
					$"{objectsChecked} selectable(s) but no status items on any of them - " +
					"nothing to resolve, so this run cannot answer the question");

			return UnitTestResult.Pass(
				$"{entriesChecked} status item(s) on {objectsChecked} object(s) all resolved");
		}
	}
}
