using System.IO;
using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using Steamworks;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Tools.Attack;

public class AttackToolPacket : IPacket
{
    private ulong        SenderId = MultiplayerSession.LocalUserID;
    private Vector2         Min;
    private Vector2         Max;
    private PrioritySetting Priority;

    public AttackToolPacket()
    {
    }

    public AttackToolPacket(Vector2 min, Vector2 max)
    {
        using var _ = Profiler.Scope();

        Min = min;
        Max = max;
    }

    public void Serialize(BinaryWriter writer)
    {
        using var _ = Profiler.Scope();

        // Through PriorityWire, which cannot produce a value the game refuses. The
        // guarded assignment here used to leave the struct default - class 0, value 0 -
        // and the receiver pushed that into its priority screen and ran the tool.
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
            DebugConsole.LogWarning("[AttackToolPacket] PriorityScreen is null in OnDispatched; applying attack without overriding priority");
            AttackTool.MarkForAttack(Min, Max, true);
            return;
        }

        Traverse        lastSelectedPriority = Traverse.Create(priorityScreen).Field("lastSelectedPriority");
        PrioritySetting prioritySetting      = lastSelectedPriority.GetValue<PrioritySetting>();

        lastSelectedPriority.SetValue(Priority);
        try
        {
            AttackTool.MarkForAttack(Min, Max, true);
        }
        finally
        {
            lastSelectedPriority.SetValue(prioritySetting);
        }
    }
}
