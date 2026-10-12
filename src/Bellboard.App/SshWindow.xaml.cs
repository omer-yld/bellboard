using System.Windows;
using System.Windows.Input;

namespace Bellboard;

/// <summary>"Connect to a host…" in the shell menu: asks for an ssh target and returns it as Target.</summary>
public partial class SshWindow : Window
{
    public string? Target { get; private set; }

    public SshWindow(Window owner, string verb)
    {
        InitializeComponent();
        Owner = owner;
        IntroText.Text = $"Opens Windows' OpenSSH client in a new {verb}. Your keys, ssh-agent and ~/.ssh/config work as usual.";
        Loaded += (_, _) => { HostBox.Focus(); Keyboard.Focus(HostBox); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Connect_Click(this, new RoutedEventArgs()); e.Handled = true; }
        };
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var target = HostBox.Text.Trim();
        if (target.Length == 0)
        {
            ErrorText.Text = "Enter a host, for example me@devbox.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Target = target;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
