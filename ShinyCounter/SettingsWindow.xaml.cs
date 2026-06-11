using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ShinyCounter;

public partial class SettingsWindow : Window
{
    private const string RepoUrl = "https://github.com/Zupermini09/ShinyCounter";
    private const string CoffeeUrl = "https://buymeacoffee.com/emilianovec";

    private readonly MainWindow _main;
    private bool _refreshing;

    public SettingsWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _main.AppStateChanged += RefreshFromState;
        Closed += (_, _) => _main.AppStateChanged -= RefreshFromState;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? "Shiny Counter" : $"Shiny Counter v{v.Major}.{v.Minor}.{v.Build}";

        ReloadThemeList();
        RefreshFromState();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyTitleBar(this);
    }

    /// Pull everything from the main window's state into the controls.
    private void RefreshFromState()
    {
        _refreshing = true;
        var s = _main.Model;
        var h = _main.CurrentHunt;

        SoundToggle.IsChecked = s.SoundOn;
        SoundToggle.Content = s.SoundOn ? "🔊 sound on" : "🔇 muted";

        RadioButton? scalePill = s.UiScale switch
        {
            0.7 => Scale70, 0.85 => Scale85, 1.0 => Scale100, 1.15 => Scale115, _ => null
        };
        foreach (var rb in new[] { Scale70, Scale85, Scale100, Scale115 }) rb.IsChecked = rb == scalePill;

        if (ThemeSelect.SelectedItem as string != s.Theme)
        {
            ThemeSelect.SelectedItem = ThemeSelect.Items.Cast<string>()
                .FirstOrDefault(n => n.Equals(s.Theme, StringComparison.OrdinalIgnoreCase));
        }

        HuntHeader.Text = "HUNT — " + h.Name.ToUpperInvariant();

        if (_main.Listening == MainWindow.BindTarget.Count)
            CountBindLabel.Text = "press key or button…";
        else
            CountBindLabel.Text = MainWindow.BindDesc(h.BindType, h.BindKey, h.BindButton);

        if (_main.Listening == MainWindow.BindTarget.Undo)
            UndoBindLabel.Text = "press key or button…";
        else
            UndoBindLabel.Text = MainWindow.BindDesc(h.UndoBindType, h.UndoBindKey, h.UndoBindButton);

        RadioButton? oddsPill = h.Odds switch
        {
            8192 => Odds8192, 4096 => Odds4096, 1365.33 => Odds1365, 512 => Odds512, _ => null
        };
        foreach (var rb in new[] { Odds8192, Odds4096, Odds1365, Odds512 }) rb.IsChecked = rb == oddsPill;
        OddsInput.Tag = oddsPill is null ? h.Odds.ToString("0.##") : "custom";

        RadioButton? stepPill = h.Step switch
        {
            1 => Step1, 2 => Step2, 3 => Step3, 4 => Step4, _ => null
        };
        foreach (var rb in new[] { Step1, Step2, Step3, Step4 }) rb.IsChecked = rb == stepPill;
        StepInput.Tag = stepPill is null ? h.Step.ToString() : "custom";

        _refreshing = false;
    }

    // ── General ──────────────────────────────────────────────────────────────

    private void Sound_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        _main.ApplySound(SoundToggle.IsChecked == true);
    }

    private void ScalePill_Checked(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        if (sender is RadioButton rb &&
            double.TryParse((string)rb.Tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            _main.ApplyScaleSetting(v);
        }
    }

    private void ReloadThemeList()
    {
        _refreshing = true;
        ThemeSelect.ItemsSource = ThemeManager.LoadAll().Select(t => t.Name).ToList();
        ThemeSelect.SelectedItem = ThemeSelect.Items.Cast<string>()
            .FirstOrDefault(n => n.Equals(_main.Model.Theme, StringComparison.OrdinalIgnoreCase))
            ?? ThemeSelect.Items.Cast<string>().FirstOrDefault();
        _refreshing = false;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || ThemeSelect.SelectedItem is not string name) return;
        _main.ApplyTheme(name);
        ThemeManager.ApplyTitleBar(this);
    }

    private void OpenThemes_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ThemeManager.EnsureStockThemes();
            Process.Start(new ProcessStartInfo(ThemeManager.ThemesDir) { UseShellExecute = true });
        }
        catch { }
    }

    private void RefreshThemes_Click(object sender, RoutedEventArgs e) => ReloadThemeList();

    // ── Hunt ─────────────────────────────────────────────────────────────────

    private void BindCount_Click(object sender, RoutedEventArgs e) =>
        _main.StartListening(MainWindow.BindTarget.Count);

    private void BindUndo_Click(object sender, RoutedEventArgs e) =>
        _main.StartListening(MainWindow.BindTarget.Undo);

    private void OddsPill_Checked(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        if (sender is RadioButton rb &&
            double.TryParse((string)rb.Tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            _main.ApplyOdds(v);
            OddsInput.Text = "";
        }
    }

    private void SetOddsCustom_Click(object sender, RoutedEventArgs e) => ApplyCustomOdds();

    private void OddsInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyCustomOdds();
    }

    private void ApplyCustomOdds()
    {
        string raw = OddsInput.Text.Trim();
        if ((double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) ||
             double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) && v >= 2)
        {
            _main.ApplyOdds(v);
            OddsInput.Text = "";
        }
    }

    private void StepPill_Checked(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        if (sender is RadioButton rb && int.TryParse((string)rb.Tag, out int v))
        {
            _main.ApplyStep(v);
            StepInput.Text = "";
        }
    }

    private void SetStepCustom_Click(object sender, RoutedEventArgs e) => ApplyCustomStep();

    private void StepInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyCustomStep();
    }

    private void ApplyCustomStep()
    {
        if (int.TryParse(StepInput.Text.Trim(), out int v) && v >= 1 && v <= 9999)
        {
            _main.ApplyStep(v);
            StepInput.Text = "";
        }
    }

    private void SetManual_Click(object sender, RoutedEventArgs e) => ApplyManualCount();

    private void ManualInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyManualCount();
    }

    private void ApplyManualCount()
    {
        if (long.TryParse(ManualInput.Text.Trim().Replace(",", "").Replace(" ", ""), out long v) && v >= 0)
        {
            _main.SetCount(v, flash: true);
            ManualInput.Text = "";
        }
    }

    // ── About ────────────────────────────────────────────────────────────────

    private void OpenGitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl);

    private void OpenIssues_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl + "/issues");

    private void OpenCoffee_Click(object sender, RoutedEventArgs e) => OpenUrl(CoffeeUrl);

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
