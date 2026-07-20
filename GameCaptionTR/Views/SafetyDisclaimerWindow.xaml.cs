using System.Windows;

namespace GameCaptionTR.Views;

public partial class SafetyDisclaimerWindow : Window
{
    public bool Accepted { get; private set; }
    public bool DontShowAgain => DontShowAgainCheck.IsChecked == true;
    public bool InformationalOnly { get; private set; }

    public SafetyDisclaimerWindow()
    {
        InitializeComponent();
    }

    public void SetInformationalOnly(bool informational)
    {
        InformationalOnly = informational;
        if (!informational)
        {
            return;
        }

        DontShowAgainCheck.Visibility = Visibility.Collapsed;
        DeclineButton.Content = "Kapat";
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        DialogResult = true;
        Close();
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        DialogResult = InformationalOnly ? true : false;
        Close();
    }
}
