using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetTrace.Presentation;

namespace NetTrace.Controls;

// Hover supplies the usual tooltip; keyboard focus and click keep the same text
// readable until focus leaves or Escape is pressed. No separate window is created.
public sealed class NetworkHelpButton : Button
{
    public static readonly DependencyProperty TermProperty = DependencyProperty.Register(
        nameof(Term), typeof(string), typeof(NetworkHelpButton),
        new PropertyMetadata("", (owner, _) => ((NetworkHelpButton)owner).UpdateDefinition()));
    private readonly TextBlock _explanation;
    public ToolTip HelpToolTip { get; }
    public string Term { get => (string)GetValue(TermProperty); set => SetValue(TermProperty, value); }

    public NetworkHelpButton()
    {
        Content = "ⓘ";
        Width = 18;
        Height = 18;
        Padding = new Thickness(0);
        FontSize = 14;
        Background = Brushes.Transparent;
        BorderBrush = Brushes.Transparent;
        Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        _explanation = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 300,
            Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252))
        };
        HelpToolTip = new ToolTip
        {
            Content = _explanation,
            ContentTemplate = null,
            MaxWidth = 322,
            Padding = new Thickness(10),
            Background = new SolidColorBrush(Color.FromRgb(23, 32, 54)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(43, 57, 87)),
            BorderThickness = new Thickness(1),
            PlacementTarget = this
        };
        ToolTip = HelpToolTip;
        ToolTipService.SetInitialShowDelay(this, 200);
        ToolTipService.SetShowDuration(this, 20000);
        Unloaded += (_, _) => HelpToolTip.IsOpen = false;
    }

    private void UpdateDefinition()
    {
        _explanation.Text = NetworkHelpText.Get(Term);
        AutomationProperties.SetName(this, "Help for " + Term);
        AutomationProperties.SetHelpText(this, _explanation.Text);
    }

    protected override void OnClick()
    {
        base.OnClick();
        HelpToolTip.IsOpen = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs args)
    {
        base.OnGotKeyboardFocus(args);
        HelpToolTip.IsOpen = true;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs args)
    {
        base.OnLostKeyboardFocus(args);
        HelpToolTip.IsOpen = false;
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            HelpToolTip.IsOpen = false;
            args.Handled = true;
        }
        base.OnKeyDown(args);
    }
}
