namespace WaveFlume.Core;

/// <summary>Conversions used only at the UI and PLC boundaries. Internal units are SI.</summary>
public static class Units
{
    public static double MmToM(double mm) => mm / 1000.0;
    public static double MToMm(double m) => m * 1000.0;
    public static double DegToRad(double deg) => deg * Math.PI / 180.0;
    public static double RadToDeg(double rad) => rad * 180.0 / Math.PI;

    public static long MmToMotorCounts(double mm, int encoderCountsPerRev, DriveConfig drive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encoderCountsPerRev);
        return (long)Math.Round(mm / drive.MmPerMotorRev * encoderCountsPerRev);
    }

    public static double MotorCountsToMm(long counts, int encoderCountsPerRev, DriveConfig drive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encoderCountsPerRev);
        return (double)counts / encoderCountsPerRev * drive.MmPerMotorRev;
    }
}
