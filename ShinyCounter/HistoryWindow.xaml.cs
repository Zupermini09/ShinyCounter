using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ShinyCounter;

public partial class HistoryWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _save;

    public HistoryWindow(AppSettings settings, Action save)
    {
        InitializeComponent();
        _settings = settings;
        _save = save;
        Refresh();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyTitleBar(this);
    }

    private void Refresh()
    {
        ListPanel.Children.Clear();
        EmptyText.Visibility = _settings.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in _settings.History)
        {
            ListPanel.Children.Add(BuildRow(entry));
        }
    }

    private Border BuildRow(HistoryEntry entry)
    {
        var title = new TextBlock
        {
            Text = "✨ " + entry.Name,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 3),
        };

        string time = entry.ElapsedSeconds >= 1
            ? " · " + MainWindow.FormatDuration(entry.ElapsedSeconds)
            : "";
        var detail = new TextBlock
        {
            Text = $"{entry.Count:N0} resets · 1/{entry.Odds:0.##}{time} · {entry.CompletedAt:dd MMM yyyy}",
            FontSize = 11,
            Foreground = (Brush)FindResource("MutedBrush"),
        };

        var textCol = new StackPanel();
        textCol.Children.Add(title);
        textCol.Children.Add(detail);

        var delete = new Button
        {
            Content = "✕",
            Style = (Style)FindResource("GhostBtn"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Remove this entry",
        };
        delete.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"Remove “{entry.Name}” from history?", "Hunt history",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            _settings.History.Remove(entry);
            _save();
            Refresh();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(delete, 1);
        grid.Children.Add(textCol);
        grid.Children.Add(delete);

        return new Border
        {
            Background = (Brush)FindResource("Surface2Brush"),
            BorderBrush = (Brush)FindResource("BorderBrush2"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

}
