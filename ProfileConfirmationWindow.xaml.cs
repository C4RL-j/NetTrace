using System.Windows;

namespace NetTrace;

public partial class ProfileConfirmationWindow : Window
{
    public ProfileConfirmationWindow(string title, string message, string actionLabel)
    {
        InitializeComponent();
        Title = title;
        ConfirmationMessageText.Text = message;
        ConfirmActionButton.Content = actionLabel;
    }

    private void ConfirmAction_OnClick(object sender, RoutedEventArgs args) => DialogResult = true;

    private void CancelAction_OnClick(object sender, RoutedEventArgs args) => DialogResult = false;
}
