using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Thump;

/// <summary>Label + slider + live value readout on one row.</summary>
public sealed class LabeledSlider : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LabeledSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((LabeledSlider)d).UpdateText()));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(LabeledSlider),
        new PropertyMetadata("", (d, e) => ((LabeledSlider)d)._label.Text = (string)e.NewValue));

    readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Slider _slider = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    readonly TextBlock _value = new() { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };

    public LabeledSlider()
    {
        Margin = new Thickness(0, 2, 0, 2);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        SetColumn(_slider, 1);
        SetColumn(_value, 2);
        Children.Add(_label);
        Children.Add(_slider);
        Children.Add(_value);
        _slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Value)) { Source = this, Mode = BindingMode.TwoWay });
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Minimum { get => _slider.Minimum; set => _slider.Minimum = value; }
    public double Maximum { get => _slider.Maximum; set => _slider.Maximum = value; }
    /// <summary>.NET format string for the readout, e.g. "0.00", "0 ms", "0%".</summary>
    public string Format { get => _format; set { _format = value; UpdateText(); } }
    string _format = "0.00";
    public string? Tip { get => ToolTip as string; set => ToolTip = value; }

    void UpdateText() => _value.Text = Value.ToString(_format);
}
