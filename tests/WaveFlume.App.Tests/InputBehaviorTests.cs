using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using FluentAssertions;

namespace WaveFlume.App.Tests;

/// <summary>Black-box: typing something that is not a number into a number box.</summary>
public class InputBehaviorTests
{
    private sealed class Model : INotifyPropertyChanged
    {
        private double _height = 0.05;
        public double Height
        {
            get => _height;
            set { _height = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Height))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Runs a WPF test on a single-threaded-apartment thread, as WPF controls require.</summary>
    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static (TextBox Box, Model Model, BindingExpression Binding) Make(bool revert)
    {
        var model = new Model();
        var box = new TextBox { DataContext = model };
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(Model.Height)));
        InputBehaviors.SetRevertInvalidOnLostFocus(box, revert);
        return (box, model, box.GetBindingExpression(TextBox.TextProperty));
    }

    private static void Type(TextBox box, BindingExpression binding, string text)
    {
        box.Text = text;
        binding.UpdateSource(); // what happens when the operator leaves the box or presses Enter
    }

    private static void LeaveBox(TextBox box)
    {
        box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        box.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); // let the deferred revert run
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0.5x")]
    [InlineData("-")]
    [InlineData("")]
    [InlineData("0..5")]
    [InlineData("1e")]
    [InlineData("--3")]
    public void Text_that_is_not_a_number_is_not_applied_and_the_box_goes_back_to_the_value_in_use(string typed) => OnSta(() =>
    {
        var (box, model, binding) = Make(revert: true);
        box.Text.Should().Be("0.05");

        Type(box, binding, typed);
        model.Height.Should().Be(0.05, "the program keeps using the last valid number");
        binding.HasError.Should().BeTrue();

        LeaveBox(box);
        box.Text.Should().Be("0.05", "what is on screen is what is in use");
        binding.HasError.Should().BeFalse();
    });

    [Fact]
    public void A_valid_number_is_applied_and_stays() => OnSta(() =>
    {
        var (box, model, binding) = Make(revert: true);
        Type(box, binding, "0.12");
        model.Height.Should().Be(0.12);
        binding.HasError.Should().BeFalse();
        LeaveBox(box);
        box.Text.Should().Be("0.12");
    });

    [Fact]
    public void A_comma_typed_as_a_decimal_separator_becomes_a_much_larger_number_that_the_range_checks_then_block()
    {
        // In this regional setting "," groups thousands, so "0,05" is read as 5. That is how WPF reads numbers; the
        // safety checks refuse a wave height of 5 m, a period of 15 s or a depth of 6 m, so nothing unsafe can result.
        var ci = new System.Globalization.CultureInfo("en-US");
        double.Parse("0,05", System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, ci).Should().Be(5);
        var cfg = new WaveFlume.Core.FlumeConfig { Limits = new WaveFlume.Core.OperationalLimits { MaxAccelMs2 = 20 } };
        var r = new WaveFlume.Physics.WaveMathEngine(cfg).Calculate(new WaveFlume.Core.WaveRequest { WaveHeightM = 5 });
        r.Safety.Ok.Should().BeFalse();
        r.Safety.Violations.Select(v => v.Code).Should().Contain("HEIGHT_RANGE");
    }

    [Fact]
    public void Without_the_behaviour_the_bad_text_would_stay_on_screen_while_the_old_value_is_used() => OnSta(() =>
    {
        // Documents the problem the behaviour solves.
        var (box, model, binding) = Make(revert: false);
        Type(box, binding, "abc");
        LeaveBox(box);
        box.Text.Should().Be("abc");
        model.Height.Should().Be(0.05);
    });

    [Fact]
    public void Every_text_box_in_the_application_gets_the_behaviour_from_the_shared_style()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "WaveFlume.sln"))) dir = Path.GetDirectoryName(dir) ?? throw new FileNotFoundException("WaveFlume.sln");
        string xaml = File.ReadAllText(Path.Combine(dir, "src", "WaveFlume.App", "Themes", "Controls.xaml"));
        xaml.Should().Contain("InputBehaviors.RevertInvalidOnLostFocus").And.Contain("Value=\"True\"");
    }
}
