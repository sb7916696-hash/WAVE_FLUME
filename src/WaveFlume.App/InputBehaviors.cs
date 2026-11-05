using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WaveFlume.App;

/// <summary>"Enter applies" (spec §11.0): pressing Enter in a TextBox pushes its value to the binding.</summary>
public static class InputBehaviors
{
    public static readonly DependencyProperty CommitOnEnterProperty = DependencyProperty.RegisterAttached(
        "CommitOnEnter", typeof(bool), typeof(InputBehaviors), new PropertyMetadata(false, OnChanged));

    public static bool GetCommitOnEnter(DependencyObject d) => (bool)d.GetValue(CommitOnEnterProperty);
    public static void SetCommitOnEnter(DependencyObject d, bool value) => d.SetValue(CommitOnEnterProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.KeyDown -= OnKeyDown;
        if ((bool)e.NewValue) box.KeyDown += OnKeyDown;
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box && !box.AcceptsReturn)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    /// <summary>
    /// A box holding text that is not a valid value (for example "abc" in a number box) is not applied: WPF keeps the
    /// previous value, so the screen would show one number and the program would use another. When the operator leaves
    /// such a box it is put back to the value that is actually in use.
    /// </summary>
    public static readonly DependencyProperty RevertInvalidOnLostFocusProperty = DependencyProperty.RegisterAttached(
        "RevertInvalidOnLostFocus", typeof(bool), typeof(InputBehaviors), new PropertyMetadata(false, OnRevertChanged));

    public static bool GetRevertInvalidOnLostFocus(DependencyObject d) => (bool)d.GetValue(RevertInvalidOnLostFocusProperty);
    public static void SetRevertInvalidOnLostFocus(DependencyObject d, bool value) => d.SetValue(RevertInvalidOnLostFocusProperty, value);

    private static void OnRevertChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.LostFocus -= OnLostFocus;
        if ((bool)e.NewValue) box.LostFocus += OnLostFocus;
    }

    private static void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        // Deferred, so the binding has finished its own lost-focus update (and found the error) first.
        box.Dispatcher.BeginInvoke(() =>
        {
            if (box.GetBindingExpression(TextBox.TextProperty) is { HasError: true } binding)
            {
                binding.UpdateTarget();
                if (binding.HasError) Validation.ClearInvalid(binding);
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }
}
