using System.IO;
using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using Steamworks;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Tools.Capture;

public class CaptureToolPacket : IPacket
{
    private ulong        SenderId = MultiplayerSession.LocalUserID;
    private Vector2         Min;
    private Vector2         Max;
    private PrioritySetting Priority;

    public CaptureToolPacket()
    {
    }

    public CaptureToolPacket(Vector2 min, Vector2 max)
    {
        using var _ = Profiler.Scope();

        Min = min;
        Max = max;
    }

    public void Serialize(BinaryWriter writer)
    {
        using var _ = Profiler.Scope();

        // See PriorityWire: a guarded assignment left the struct default, and zero is
        // not a priority the game accepts.
        Priority = PriorityWire.Sample();

        writer.Write(SenderId);
        writer.Write(Min);
        writer.Write(Max);
        PriorityWire.Write(writer, Priority);
    }

    public void Deserialize(BinaryReader reader)
    {
        using var _ = Profiler.Scope();

        SenderId = reader.ReadUInt64();
        Min      = reader.ReadVector2();
        Max      = reader.ReadVector2();
        Priority = PriorityWire.Read(reader);
    }

    public void OnDispatched()
    {
        using var _ = Profiler.Scope();

        var priorityScreen = ToolMenu.Instance?.PriorityScreen;
        if (priorityScreen == null)
        {
            DebugConsole.LogWarning("[CaptureToolPacket] PriorityScreen is null in OnDispatched; applying capture without overriding priority");
            CaptureTool.MarkForCapture(Min, Max, true);
            return;
        }

        Traverse        lastSelectedPriority = Traverse.Create(priorityScreen).Field("lastSelectedPriority");
        PrioritySetting prioritySetting      = lastSelectedPriority.GetValue<PrioritySetting>();

        lastSelectedPriority.SetValue(Priority);
        try
        {
            CaptureTool.MarkForCapture(Min, Max, true);
        }
        finally
        {
            lastSelectedPriority.SetValue(prioritySetting);
        }
    }
}
