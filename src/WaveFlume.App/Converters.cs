using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WaveFlume.App;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is false;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is false;
}

/// <summary>null/empty → Collapsed, anything else → Visible.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Operator-facing names for the enum values shown in drop-downs; anything else shows its code name.</summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public static string Label(object? value) => value switch
    {
        WaveFlume.Core.WaveType.Monochromatic => "Monochromatic (regular waves)",
        WaveFlume.Core.WaveType.Bichromatic => "Bichromatic (two regular waves)",
        WaveFlume.Core.WaveType.Random => "Random (irregular waves)",
        WaveFlume.Core.WaveType.Cnoidal => "Cnoidal (long shallow-water waves)",
        WaveFlume.Core.WaveType.Solitary => "Solitary (single wave)",
        WaveFlume.Core.WaveType.Focusing => "Focusing (wave group)",
        WaveFlume.Core.WaveType.UserDefinedWave => "User-defined wave (from file)",
        WaveFlume.Core.WaveType.UserDefinedStroke => "User-defined stroke (from file)",
        WaveFlume.Core.SpectralShape.PiersonMoskowitz => "Pierson-Moskowitz (PM)",
        WaveFlume.Core.SpectralShape.GeneralizedPm => "Generalised PM",
        WaveFlume.Core.SpectralShape.Jonswap => "JONSWAP",
        WaveFlume.Core.SpectralShape.Tma => "TMA (shallow water)",
        WaveFlume.Core.SpectralShape.Darbyshire => "Darbyshire",
        WaveFlume.Core.RandomPath.PaddleSpectrum => "A: spectrum to paddle",
        WaveFlume.Core.RandomPath.SurfaceThenTransfer => "B: surface, then transfer function",
        null => "",
        _ => value.ToString() ?? "",
    };

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => Label(value);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Enum value ⇄ bool for radio buttons; ConverterParameter is the enum member name.</summary>
public sealed class EnumIsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}
