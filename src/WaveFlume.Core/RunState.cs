namespace WaveFlume.Core;

/// <summary>Application state machine (spec §5). Drives which buttons are enabled.</summary>
public enum RunState { Disconnected, Connected, ServosEnabled, Homed, TrajectoryLoaded, Running, Stopping, Faulted }

public enum OperatorAction { Connect, Disconnect, EnableServos, Home, Send, Start, SmoothStop, EStop, Reset }

public static class RunStateRules
{
    /// <summary>
    /// E-stop is always allowed while connected (the hardwired chain works regardless).
    /// Send also needs a calculated, safety-approved trajectory — checked by the caller.
    /// </summary>
    public static bool IsAllowed(RunState s, OperatorAction a) => a switch
    {
        OperatorAction.Connect => s is RunState.Disconnected,
        OperatorAction.Disconnect => s is not (RunState.Disconnected or RunState.Running or RunState.Stopping),
        OperatorAction.EnableServos => s is RunState.Connected,
        OperatorAction.Home => s is RunState.ServosEnabled or RunState.Homed or RunState.TrajectoryLoaded,
        OperatorAction.Send => s is RunState.Homed or RunState.TrajectoryLoaded,
        OperatorAction.Start => s is RunState.TrajectoryLoaded,
        OperatorAction.SmoothStop => s is RunState.Running,
        OperatorAction.EStop => s is not RunState.Disconnected,
        OperatorAction.Reset => s is RunState.Faulted,
        _ => false,
    };
}
