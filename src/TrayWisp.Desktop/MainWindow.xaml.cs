using System.Reflection;
using System.Windows;

namespace TrayWisp.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        string? version = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        VersionText.Text = $"Version {version ?? "unknown"}";
    }
}
