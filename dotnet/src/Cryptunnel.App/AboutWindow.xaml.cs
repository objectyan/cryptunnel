using System.Windows;
using System.Windows.Input;

namespace Cryptunnel.App;

public partial class AboutWindow : Window
{
    public AboutWindow(string version, string configDir, string logDir)
    {
        InitializeComponent();
        VersionText.Text = version;
        ConfigDirText.Text = configDir;
        LogDirText.Text = logDir;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}