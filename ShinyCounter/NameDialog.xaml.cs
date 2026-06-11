using System.Windows;
using System.Windows.Input;

namespace ShinyCounter;

public partial class NameDialog : Window
{
    public string? Result { get; private set; }

    public NameDialog(string title, string initial)
    {
        InitializeComponent();
        TitleText.Text = title;
        NameBox.Text = initial;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Accept();
        else if (e.Key == Key.Escape) DialogResult = false;
    }

    private void Window_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Accept()
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        Result = name;
        DialogResult = true;
    }
}
