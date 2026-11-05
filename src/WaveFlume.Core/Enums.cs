namespace WaveFlume.Core;

public enum PaddleType { Piston, Flap }

public enum WaveOrder { First = 1, Second = 2 }

public enum WaveType
{
    Monochromatic,
    Bichromatic,
    Random,
    Cnoidal,
    Solitary,
    Focusing,
    UserDefinedWave,
    UserDefinedStroke,
}

public enum DepthRegime { Shallow, Intermediate, Deep }
