using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace NetTrace.Controls;

// Only mouse-wheel movement is eased. The native ScrollViewer remains responsible
// for keyboard, touch, scrollbar and programmatic movement.
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(ScrollState), typeof(SmoothScroll), new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ScrollViewer viewer) return;
        if (viewer.GetValue(StateProperty) is ScrollState previous) previous.Detach();
        viewer.ClearValue(StateProperty);
        if ((bool)args.NewValue)
        {
            var state = new ScrollState(viewer);
            viewer.SetValue(StateProperty, state);
            state.Attach();
        }
    }

    private sealed class ScrollState(ScrollViewer viewer)
    {
        private const double DurationSeconds = 0.2;
        private const double LineHeight = 16;
        private const double OffsetTolerance = 0.1;
        private bool _animating;
        private double _start;
        private double _target;
        private double _lastRequestedOffset;
        private long _startedAt;
        private Window? _window;

        public void Attach()
        {
            viewer.PreviewMouseWheel += OnMouseWheel;
            viewer.PreviewMouseDown += OnMouseDown;
            viewer.PreviewKeyDown += OnKeyDown;
            viewer.PreviewTouchDown += OnTouchDown;
            viewer.PreviewStylusDown += OnStylusDown;
            viewer.ScrollChanged += OnScrollChanged;
            viewer.IsVisibleChanged += OnIsVisibleChanged;
            viewer.Loaded += OnLoaded;
            viewer.Unloaded += OnUnloaded;
            if (viewer.IsLoaded) AttachWindow();
        }

        public void Detach()
        {
            Stop();
            viewer.PreviewMouseWheel -= OnMouseWheel;
            viewer.PreviewMouseDown -= OnMouseDown;
            viewer.PreviewKeyDown -= OnKeyDown;
            viewer.PreviewTouchDown -= OnTouchDown;
            viewer.PreviewStylusDown -= OnStylusDown;
            viewer.ScrollChanged -= OnScrollChanged;
            viewer.IsVisibleChanged -= OnIsVisibleChanged;
            viewer.Loaded -= OnLoaded;
            viewer.Unloaded -= OnUnloaded;
            DetachWindow();
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs args)
        {
            if (args.Handled || args.Delta == 0 || !viewer.IsLoaded || !viewer.IsVisible) return;
            if (Keyboard.Modifiers != ModifierKeys.None || viewer.CanContentScroll ||
                OwnsWheelInteraction(args.OriginalSource as DependencyObject))
            {
                Stop();
                return;
            }

            var lines = SystemParameters.WheelScrollLines;
            if (lines == 0)
            {
                Stop();
                args.Handled = true;
                return;
            }
            var distance = lines < 0 ? viewer.ViewportHeight : lines * LineHeight;
            var delta = -(double)args.Delta / Mouse.MouseWheelDeltaForOneLine * distance;
            var current = viewer.VerticalOffset;
            // Reverse direction immediately, rather than first spending the new
            // tick cancelling a previous tick's uncompleted movement.
            var basis = _animating && Math.Sign(delta) == Math.Sign(_target - current) ? _target : current;
            var target = Clamp(basis + delta);
            if (Math.Abs(target - current) < OffsetTolerance)
            {
                Stop();
                return;
            }

            args.Handled = true;
            if (!SystemParameters.ClientAreaAnimation)
            {
                Stop();
                viewer.ScrollToVerticalOffset(target);
                return;
            }

            _start = current;
            _target = target;
            _startedAt = Stopwatch.GetTimestamp();
            if (!_animating)
            {
                _lastRequestedOffset = current;
                _animating = true;
                CompositionTarget.Rendering += OnRendering;
            }
        }

        private void OnRendering(object? sender, EventArgs args)
        {
            if (!viewer.IsLoaded || !viewer.IsVisible)
            {
                Stop();
                return;
            }
            var progress = SystemParameters.ClientAreaAnimation
                ? Math.Min(1, Stopwatch.GetElapsedTime(_startedAt).TotalSeconds / DurationSeconds) : 1;
            var eased = 1 - Math.Pow(1 - progress, 3);
            _lastRequestedOffset = Clamp(_start + (_target - _start) * eased);
            viewer.ScrollToVerticalOffset(_lastRequestedOffset);
            if (progress >= 1) Stop();
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs args)
        {
            if (!_animating || !ReferenceEquals(args.OriginalSource, viewer)) return;
            // ScrollToVerticalOffset is processed by WPF during layout. A change
            // unlike our last request comes from native or programmatic input.
            if (args.ExtentHeightChange != 0 || args.ViewportHeightChange != 0 ||
                (args.VerticalChange != 0 && Math.Abs(args.VerticalOffset - _lastRequestedOffset) > OffsetTolerance))
                Stop();
        }

        private bool OwnsWheelInteraction(DependencyObject? source)
        {
            for (var current = source; current is not null && !ReferenceEquals(current, viewer); current = Parent(current))
                if (current is ScrollViewer or Selector or TextBoxBase or PasswordBox or RangeBase)
                    return true;
            return false;
        }

        private static DependencyObject? Parent(DependencyObject element) => element switch
        {
            Visual or Visual3D => VisualTreeHelper.GetParent(element),
            FrameworkContentElement content => content.Parent,
            ContentElement content => ContentOperations.GetParent(content),
            _ => LogicalTreeHelper.GetParent(element)
        };

        private double Clamp(double offset) => Math.Clamp(offset, 0, Math.Max(0, viewer.ScrollableHeight));
        private void OnMouseDown(object sender, MouseButtonEventArgs args) => Stop();
        private void OnKeyDown(object sender, KeyEventArgs args) => Stop();
        private void OnTouchDown(object? sender, TouchEventArgs args) => Stop();
        private void OnStylusDown(object sender, StylusDownEventArgs args) => Stop();
        private void OnLoaded(object sender, RoutedEventArgs args) => AttachWindow();
        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            Stop();
            DetachWindow();
        }
        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!(bool)args.NewValue) Stop();
        }

        private void Stop()
        {
            if (!_animating) return;
            _animating = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private void AttachWindow()
        {
            DetachWindow();
            _window = Window.GetWindow(viewer);
            if (_window is not null) _window.StateChanged += OnWindowStateChanged;
        }

        private void DetachWindow()
        {
            if (_window is not null) _window.StateChanged -= OnWindowStateChanged;
            _window = null;
        }

        private void OnWindowStateChanged(object? sender, EventArgs args)
        {
            if (_window?.WindowState == WindowState.Minimized) Stop();
        }
    }
}
