using WaveFlume.Core;

namespace WaveFlume.Plc;

public static class StatusMapping
{
    /// <summary>Derives the application state from the PLC StatusWord — the PLC is the source of truth.</summary>
    public static RunState ToRunState(bool connected, StatusBits s)
    {
        if (!connected) return RunState.Disconnected;
        if ((s & (StatusBits.Fault | StatusBits.EStopActive)) != 0) return RunState.Faulted;
        if (s.HasFlag(StatusBits.SmoothStopping)) return RunState.Stopping;
        if (s.HasFlag(StatusBits.Running)) return RunState.Running;
        if (!s.HasFlag(StatusBits.Enabled)) return RunState.Connected;
        if (!s.HasFlag(StatusBits.Homed)) return RunState.ServosEnabled;
        return s.HasFlag(StatusBits.TrajectoryLoaded) ? RunState.TrajectoryLoaded : RunState.Homed;
    }
}
