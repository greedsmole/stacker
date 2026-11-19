using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Stacker.Desktop;

internal static class WorkspaceMotion
{
    public static Task RevealAsync(Control control, TimeSpan duration, double x = 0, double y = 0,
        CancellationToken cancellationToken = default)
    {
        var animation = new Animation
        {
            Duration = duration,
            Easing = new SplineEasing { X1 = 0.23, Y1 = 1, X2 = 0.32, Y2 = 1 }
        };
        animation.Children.Add(Frame(0, 0, x, y));
        animation.Children.Add(Frame(1, 1, 0, 0));
        return animation.RunAsync(control, cancellationToken: cancellationToken);
    }

    private static KeyFrame Frame(double cue, double opacity, double x, double y) => new()
    {
        Cue = new Cue(cue),
        Setters =
        {
            new Setter(Visual.OpacityProperty, opacity),
            new Setter(TranslateTransform.XProperty, x),
            new Setter(TranslateTransform.YProperty, y)
        }
    };
}
