using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MaintenancePC.Models;

namespace MaintenancePC.Controls;

/// <summary>Zone de contenu qui fait apparaître chaque nouvel onglet en fondu, avec un léger glissement.</summary>
public class AnimatedContentControl : ContentControl
{
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (newContent is null) return;

        var slide = new TranslateTransform();
        RenderTransform = slide;
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, duration) { EasingFunction = ease });
    }
}

/// <summary>Propriétés « attachées » : de petits comportements qu'on ajoute à des contrôles existants depuis le XAML.</summary>
public static class Behaviors
{
    // SmoothValue : la barre de progression glisse vers la nouvelle valeur au lieu de sauter.
    public static readonly DependencyProperty SmoothValueProperty = DependencyProperty.RegisterAttached(
        "SmoothValue", typeof(double), typeof(Behaviors), new PropertyMetadata(0.0, OnSmoothValueChanged));

    public static double GetSmoothValue(DependencyObject d) => (double)d.GetValue(SmoothValueProperty);
    public static void SetSmoothValue(DependencyObject d, double value) => d.SetValue(SmoothValueProperty, value);

    private static void OnSmoothValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RangeBase bar) return;
        bar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(Math.Max(0, (double)e.NewValue), TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // AutoScroll : la zone de journal reste calée sur la dernière ligne.
    public static readonly DependencyProperty AutoScrollProperty = DependencyProperty.RegisterAttached(
        "AutoScroll", typeof(bool), typeof(Behaviors), new PropertyMetadata(false, OnAutoScrollChanged));

    public static bool GetAutoScroll(DependencyObject d) => (bool)d.GetValue(AutoScrollProperty);
    public static void SetAutoScroll(DependencyObject d, bool value) => d.SetValue(AutoScrollProperty, value);

    private static void OnAutoScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.TextChanged -= ScrollToEnd;
        if ((bool)e.NewValue) box.TextChanged += ScrollToEnd;
    }

    private static void ScrollToEnd(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();
}

// ───────────────────────── Convertisseurs (transforment une valeur du ViewModel pour l'affichage) ─────────────────────────

/// <summary>État → couleur (vert, orange, rouge, bleu).</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value switch
        {
            Status.Ok => "OkBrush",
            Status.Warning => "WarningBrush",
            Status.Error => "ErrorBrush",
            Status.Working => "AccentBrush",
            _ => "TextMutedBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>État → caractère de la police d'icônes.</summary>
public sealed class StatusToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Status.Ok => "",        // coche
        Status.Warning => "",   // triangle d'avertissement
        Status.Error => "",     // erreur
        Status.Working => "",   // flèches de synchronisation
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible si la valeur (convertie en texte) est égale au paramètre. Ajoute « ! » devant le paramètre pour inverser.</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string expected = parameter?.ToString() ?? "";
        bool invert = expected.StartsWith('!');
        if (invert) expected = expected[1..];

        bool equal = string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture), expected, StringComparison.OrdinalIgnoreCase);
        return equal != invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
