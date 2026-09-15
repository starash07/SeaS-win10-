using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SeaS.App;

public static class DialogMotion
{
    private static readonly DependencyProperty CloseAnimationStateProperty =
        DependencyProperty.RegisterAttached(
            "CloseAnimationState",
            typeof(CloseAnimationState),
            typeof(DialogMotion),
            new PropertyMetadata(null));

    public static void Enable(Window window)
    {
        // Keep the old Window overload as a safe no-op.
        // WPF Window itself cannot be scaled with RenderTransform reliably.
        _ = window;
    }

    public static void EnablePop(FrameworkElement root)
    {
        root.RenderTransformOrigin = new Point(0.5, 0.5);

        if (root.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(0.94, 0.94);
            root.RenderTransform = scale;
        }
        else
        {
            scale.ScaleX = 0.94;
            scale.ScaleY = 0.94;
        }

        AttachCloseAnimation(root);

        if (root.IsLoaded)
        {
            BeginPop(scale);
            return;
        }

        root.Loaded += (_, _) => BeginPop(scale);
    }

    private static void BeginPop(ScaleTransform scale)
    {
        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            CreatePopAnimation(),
            HandoffBehavior.SnapshotAndReplace);

        scale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            CreatePopAnimation(),
            HandoffBehavior.SnapshotAndReplace);
    }

    private static void AttachCloseAnimation(FrameworkElement root)
    {
        if (root.GetValue(CloseAnimationStateProperty) is CloseAnimationState)
        {
            return;
        }

        var state = new CloseAnimationState(root);
        root.SetValue(CloseAnimationStateProperty, state);

        if (root.IsLoaded)
        {
            state.Attach();
            return;
        }

        root.Loaded += (_, _) => state.Attach();
    }

    private static DoubleAnimationUsingKeyFrames CreatePopAnimation()
    {
        return new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new EasingDoubleKeyFrame(
                    0.94,
                    KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new EasingDoubleKeyFrame(
                    1.015,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(105)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                },
                new EasingDoubleKeyFrame(
                    1,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                }
            }
        };
    }

    private static DoubleAnimationUsingKeyFrames CreateCloseAnimation()
    {
        return new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new EasingDoubleKeyFrame(
                    1,
                    KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new EasingDoubleKeyFrame(
                    1.012,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(58)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                },
                new EasingDoubleKeyFrame(
                    0.92,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(185)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                }
            }
        };
    }

    private sealed class CloseAnimationState(FrameworkElement root)
    {
        private readonly FrameworkElement _root = root;
        private Window? _window;
        private bool _allowClose;
        private bool _isAnimating;

        public void Attach()
        {
            var window = Window.GetWindow(_root);
            if (window is null || ReferenceEquals(window, _window))
            {
                return;
            }

            if (_window is not null)
            {
                _window.Closing -= Window_Closing;
            }

            _window = window;
            _window.Closing += Window_Closing;
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowClose || !_root.IsLoaded)
            {
                return;
            }

            e.Cancel = true;

            if (_isAnimating)
            {
                return;
            }

            _isAnimating = true;
            _root.IsHitTestVisible = false;

            var scale = EnsureScaleTransform(_root);
            var animation = CreateCloseAnimation();
            var completed = false;
            animation.Completed += (_, _) =>
            {
                if (completed)
                {
                    return;
                }

                completed = true;
                _allowClose = true;
                _window?.Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        try
                        {
                            _window?.Close();
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    }),
                    DispatcherPriority.Send);
            };

            scale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);

            scale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                animation.Clone(),
                HandoffBehavior.SnapshotAndReplace);
        }
    }

    private static ScaleTransform EnsureScaleTransform(FrameworkElement root)
    {
        root.RenderTransformOrigin = new Point(0.5, 0.5);

        if (root.RenderTransform is ScaleTransform scale)
        {
            return scale;
        }

        scale = new ScaleTransform(1, 1);
        root.RenderTransform = scale;
        return scale;
    }
}
