using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MaintenancePC.Controls;

/// <summary>
/// Le gros bouton rond des onglets. Au repos c'est un simple anneau ;
/// pendant une opération, un trait tourne autour et un arc se remplit selon <see cref="Progress"/>.
/// Son apparence est décrite dans Themes/Controls.xaml.
/// </summary>
public class RingButton : Button
{
    // Géométrie dessinée dans un carré de 100 x 100 (le style l'agrandit ensuite à la taille voulue).
    private const double Center = 50, Radius = 43.5;

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(RingButton), new PropertyMetadata(false));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(RingButton), new PropertyMetadata(-1.0, OnProgressChanged));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(RingButton), new PropertyMetadata(""));

    // Valeur intermédiaire animée : c'est elle qui fait avancer l'arc en douceur.
    private static readonly DependencyProperty AnimatedProgressProperty = DependencyProperty.Register(
        "AnimatedProgress", typeof(double), typeof(RingButton), new PropertyMetadata(0.0, OnAnimatedProgressChanged));

    private static readonly DependencyPropertyKey ArcGeometryKey = DependencyProperty.RegisterReadOnly(
        nameof(ArcGeometry), typeof(Geometry), typeof(RingButton), new PropertyMetadata(Geometry.Empty));

    public static readonly DependencyProperty ArcGeometryProperty = ArcGeometryKey.DependencyProperty;

    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    /// <summary>De 0 à 100. Une valeur négative masque l'arc.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>Petit texte sous le texte principal (« Annuler »).</summary>
    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public Geometry ArcGeometry => (Geometry)GetValue(ArcGeometryProperty);

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (RingButton)d;
        double target = Math.Clamp((double)e.NewValue, 0, 100);

        if ((double)e.NewValue < 0)
        {
            // Retour au repos : on efface l'arc immédiatement.
            ring.BeginAnimation(AnimatedProgressProperty, null);
            ring.SetValue(AnimatedProgressProperty, 0.0);
            return;
        }

        ring.BeginAnimation(AnimatedProgressProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static void OnAnimatedProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.SetValue(ArcGeometryKey, BuildArc((double)e.NewValue));

    /// <summary>Arc de cercle partant du haut, dans le sens des aiguilles d'une montre.</summary>
    private static Geometry BuildArc(double percent)
    {
        if (percent <= 0) return Geometry.Empty;

        double angle = Math.Min(percent, 99.99) / 100 * 2 * Math.PI;
        var start = new Point(Center, Center - Radius);
        var end = new Point(Center + Radius * Math.Sin(angle), Center - Radius * Math.Cos(angle));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(Radius, Radius), 0, isLargeArc: angle > Math.PI,
                SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }
}
