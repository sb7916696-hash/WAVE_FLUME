using WaveFlume.Core;

namespace WaveFlume.App.Services;

/// <summary>What the Details dialog shows for an alarm: what went wrong, and what the operator should do.</summary>
public sealed record AlarmAdviceText(string Problem, string Solution);

/// <summary>
/// Plain-language problem and solution for every alarm the program can raise. Wave-calculation alarms carry
/// the gatekeeper code in front of the message ("STROKE: ..."); the others are matched on their wording.
/// </summary>
public static class AlarmAdvice
{
    private const string Generic =
        "Read the message above. If it happens again, write down the time and the message and contact your service engineer.";

    private static readonly Dictionary<string, AlarmAdviceText> ByCode = new()
    {
        ["ACCEL_NOT_CONFIGURED"] = new("The maximum paddle acceleration has not been entered, so no wave can be sent.",
            "Go to Settings, type the Max Acceleration of your drive, then press Save Settings. Calculate again."),
        ["DEPTH_RANGE"] = new("The water depth is outside the allowed range.",
            "Type a water depth inside the range shown in the message. The range can be changed in Settings, Range Bounds."),
        ["HEIGHT_RANGE"] = new("The wave height is outside the allowed range.",
            "Type a wave height inside the range shown in the message. The range can be changed in Settings, Range Bounds."),
        ["NUM_WAVES_RANGE"] = new("The number of waves is outside the allowed range.",
            "Type a number of waves inside the range shown in the message."),
        ["FS_RANGE"] = new("The sampling frequency is outside the allowed range.",
            "Type a sampling frequency inside the range shown in the message (125 Hz is the normal value)."),
        ["PERIOD_RANGE"] = new("The wave period is outside the range the wavemaker can make.",
            "Use a period between T min and T max shown in the message. For random waves this also applies to the frequency band, so try a Tp nearer the middle of the range."),
        ["PERIOD_FLOOR"] = new("The wave period is shorter than the absolute safe minimum.",
            "Use a longer period. This limit cannot be changed in Settings."),
        ["BREAKING"] = new("The wave is higher than the water can carry: it would break.",
            "Reduce the wave height, or use a longer period or a greater water depth. The Limit plot on the Wave Generation tab shows the highest wave allowed for each period."),
        ["STROKE"] = new("The paddle would have to travel further than its maximum stroke.",
            "Reduce the wave height, use a shorter period, or lower the % Signal Modification. Check that Max Stroke in Settings matches the paddle."),
        ["VELOCITY"] = new("The paddle would have to move faster than its maximum speed.",
            "Reduce the wave height or use a longer period."),
        ["ACCELERATION"] = new("The paddle would have to accelerate harder than its maximum acceleration.",
            "Reduce the wave height or use a longer period. Check that Max Acceleration in Settings is correct for the drive."),
        ["FS_TOO_LOW"] = new("The sampling frequency is too low for the shortest wave in this calculation.",
            "Raise the sampling frequency, or use a longer period."),
        ["PHASE_STEP"] = new("The wave angle is too large for the paddle width at this period, so the paddles cannot make a clean angled wave.",
            "Reduce the wave angle or use a longer period."),
        ["START_NOT_ZERO"] = new("The calculated motion does not start from the paddle zero position.",
            "Calculate again. If it keeps happening, note the wave settings and contact your service engineer."),
        ["URSELL"] = new("The wave is long and high for this water depth, so the calculation is less accurate.",
            "The wave can still be sent. For a more accurate wave, reduce the height or use a greater depth."),
        ["SHALLOW_KH"] = new("The water is very shallow for this period, so the calculation is less accurate.",
            "The wave can still be sent. For a more accurate wave, use a greater depth or a shorter period."),
        ["BAND_LIMITED"] = new("Part of the random sea lies at wave periods the wavemaker cannot make, so that part is left out.",
            "The wave can still be sent, but it will not match the chosen spectrum exactly. For a closer match use a peak period nearer the middle of the range (about 1.5 to 3 seconds)."),
        ["CNOIDAL_RANGE"] = new("Cnoidal theory is meant for long waves in shallow water, and this wave is not long enough for it to be accurate.",
            "Use a longer wave period or a smaller wave height, or choose Monochromatic for shorter waves. The wave can still be sent."),
        ["OBLIQUE_WALLS"] = new("Angled waves bounce off the flume side walls unless absorbers are fitted.",
            "Use a wave angle of 0 for a straight wave, or make sure wall absorbers are in place."),
        ["LARGE_TRAJECTORY"] = new("This test is very long, so sending it will take a while and use a lot of memory.",
            "You can continue, or shorten the test by reducing the number of waves."),
    };

    public static AlarmAdviceText For(Alert alert)
    {
        string text = alert.Description;

        int colon = text.IndexOf(':');
        if (colon > 0 && ByCode.TryGetValue(text[..colon], out var byCode)) return byCode;

        if (Has(text, "E-STOP pressed while not connected"))
            return new("The emergency stop was pressed on the screen while the program was not connected to the controller, so nothing could be stopped from here.",
                "Use the hardwired emergency stop on the flume. Connect to the controller before starting any wave.");
        if (Has(text, "E-STOP pressed by operator"))
            return new("The EMERGENCY STOP button was pressed and the paddles were told to stop.",
                "Make sure the flume is safe and remove the cause. Press Reset Fault, then Enable Servos and Starting Position again before the next run.");
        if (Has(text, "Connect failed"))
            return new("The program could not connect to the controller.",
                "Check that the controller is switched on and the network cable is plugged in. In Settings check Protocol, IP Address and Port, then press Test Connection. If you only want to try the program, choose Simulator.");
        if (Has(text, "refused"))
            return new("The controller refused the command because it was not in the right state for it.",
                "Follow the order Connect, Enable Servos, Starting Position, Send to Wave Paddle, Start Wave. If a fault is showing, press Reset Fault first.");
        if (Has(text, "upload failed"))
            return new("The calculated wave could not be sent to the controller.",
                "Check the connection, then press Send to Wave Paddle again. If it keeps failing, check the network cable and that the controller is in run mode.");
        if (Has(text, "heartbeat lost"))
            return new("The controller stopped answering, so the paddles were told to stop smoothly.",
                "Check the controller power and the network cable. Press Disconnect and Connect, then Reset Fault, Enable Servos and Starting Position before running again.");
        if (Has(text, "Telemetry failed"))
            return new("Live data from the controller was interrupted. The program is trying to reconnect by itself.",
                "Check the network cable and controller power. If it does not recover in a few seconds, press Disconnect and Connect.");
        if (Has(text, "Communication restored"))
            return new("The connection to the controller dropped for a moment and is working again.",
                "No action is needed. If it happens often, check the network cable.");
        if (Has(text, "of clamp"))
            return new("A paddle moved very close to the end of its allowed travel, so the paddles were told to stop smoothly.",
                "Reduce the wave height, use a shorter period or a lower % Signal Modification. Check that the paddles were moved to the starting position and that Max Stroke in Settings is correct.");
        if (Has(text, "hard fault"))
            return new("The controller reported a serious fault and the emergency stop was applied.",
                "Check the servo drives and the safety circuit and clear the cause. Then press Reset Fault, Enable Servos and Starting Position.");
        if (Has(text, "loop fault"))
            return new("A wave probe is giving a signal outside its normal 4 to 20 mA range, which usually means a broken wire, no power or a damaged probe. The paddles were told to stop smoothly.",
                "Check the probe cable and power supply. In Settings, Channels and Calibrations, check the probe calibration. Press Reset Fault and run again.");
        if (Has(text, "PLC fault"))
            return new("The controller reported a fault.",
                "Look at the drive and controller displays for the cause, fix it, then press Reset Fault.");

        return new(text, Generic);
    }

    private static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
