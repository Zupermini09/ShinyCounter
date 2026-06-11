using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.Gaming.Input;

namespace ShinyCounter;

public partial class MainWindow : Window
{
    // ── State ────────────────────────────────────────────────────────────────
    private AppSettings _settings = new();
    private Hunt H => _settings.Hunts[_settings.ActiveHunt];

    internal AppSettings Model => _settings;
    internal Hunt CurrentHunt => H;

    /// Raised after any saved change so the settings window can stay in sync.
    internal event Action? AppStateChanged;

    internal enum BindTarget { None, Count, Undo }
    private BindTarget _listening = BindTarget.None;
    internal BindTarget Listening => _listening;

    private bool _keyHeld, _undoKeyHeld, _padHeld, _undoPadHeld;
    private bool _loading, _suppressHuntSelect, _mini;
    private DateTime? _lastIncrementAt;
    private const double IdleCapSeconds = 180; // AFK gaps longer than this don't count as hunt time

    private bool _padConnected;
    private string _padName = "";

    private SettingsWindow? _settingsWindow;

    private readonly DispatcherTimer _padTimer;
    private readonly SolidColorBrush _counterBrush;
    private SoundPlayer? _tick, _tickLow, _chime;

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShinyCounter");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public MainWindow()
    {
        InitializeComponent();

        _counterBrush = new SolidColorBrush(ThemeManager.Text);
        CounterText.Foreground = _counterBrush;
        MiniCount.Foreground = _counterBrush;

        InitSounds();

        _padTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(15)
        };
        _padTimer.Tick += PollGamepads;
        _padTimer.Start();
    }

    // ── Window lifecycle ─────────────────────────────────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyTitleBar(this);
        InstallKeyboardHook();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
    }

    protected override void OnClosed(EventArgs e)
    {
        _padTimer.Stop();
        RemoveKeyboardHook();
        SaveSettings();
        base.OnClosed(e);
    }

    // ── Settings window ──────────────────────────────────────────────────────

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this) { Owner = this };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    // ── Binding (trigger buttons) ────────────────────────────────────────────

    private Button ListenButton => _listening == BindTarget.Undo ? UndoBindBtn : BindBtn;

    private void BindBtn_Click(object sender, RoutedEventArgs e) => StartListening(BindTarget.Count);

    private void UndoBindBtn_Click(object sender, RoutedEventArgs e) => StartListening(BindTarget.Undo);

    internal void StartListening(BindTarget target)
    {
        if (_listening != BindTarget.None) CancelBinding();
        _listening = target;
        ListenButton.Content = "press key or button…";
        BoundDisplay.Text = "waiting… (Esc cancels)";
        StartBlink(ListenButton);
        AppStateChanged?.Invoke();
    }

    private void CancelBinding()
    {
        if (_listening == BindTarget.None) return;
        StopBlink(ListenButton);
        ListenButton.Content = "rebind";
        _listening = BindTarget.None;
        UpdateBoundDisplay();
        AppStateChanged?.Invoke();
    }

    private void FinishKeyBinding(int vk)
    {
        if (_listening == BindTarget.Count)
        {
            H.BindType = "key"; H.BindKey = vk;
            _keyHeld = true; // the key that bound is currently down — don't count it
        }
        else
        {
            H.UndoBindType = "key"; H.UndoBindKey = vk;
            _undoKeyHeld = true;
        }
        EndBinding();
    }

    private void FinishPadBinding(int buttonIndex)
    {
        if (_listening == BindTarget.Count)
        {
            H.BindType = "pad"; H.BindButton = buttonIndex;
            _padHeld = true;
        }
        else
        {
            H.UndoBindType = "pad"; H.UndoBindButton = buttonIndex;
            _undoPadHeld = true;
        }
        EndBinding();
    }

    private void EndBinding()
    {
        StopBlink(ListenButton);
        ListenButton.Content = "rebind";
        _listening = BindTarget.None;
        UpdateBoundDisplay();
        SaveSettings();
    }

    private void UpdateBoundDisplay()
    {
        BoundDisplay.Text =
            $"count: {BindDesc(H.BindType, H.BindKey, H.BindButton)}" +
            $"  ·  undo: {BindDesc(H.UndoBindType, H.UndoBindKey, H.UndoBindButton)}";
    }

    internal static string BindDesc(string type, int vk, int button) => type switch
    {
        "key" => KeyLabel(vk),
        "pad" => $"Button {button}",
        _ => "not bound",
    };

    private static string KeyLabel(int vk)
    {
        var key = KeyInterop.KeyFromVirtualKey(vk);
        return key switch
        {
            Key.Space => "Space",
            Key.Return => "Enter",
            Key.None => $"VK {vk}",
            _ => key.ToString(),
        };
    }

    // ── Global keyboard hook ─────────────────────────────────────────────────

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_ESCAPE = 0x1B;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hookId = IntPtr.Zero;

    private void InstallKeyboardHook()
    {
        _hookProc = HookCallback;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
    }

    private void RemoveKeyboardHook()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode

            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN) OnGlobalKeyDown(vk);
            else if (msg is WM_KEYUP or WM_SYSKEYUP) OnGlobalKeyUp(vk);
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void OnGlobalKeyDown(int vk)
    {
        if (_loading || _settings.Hunts.Count == 0) return;

        if (_listening != BindTarget.None)
        {
            if (vk == VK_ESCAPE) CancelBinding();
            else FinishKeyBinding(vk);
            return;
        }

        bool typing = Application.Current.Windows.OfType<Window>()
            .Any(w => w.IsActive) && Keyboard.FocusedElement is TextBox;

        if (H.BindType == "key" && vk == H.BindKey)
        {
            if (!_keyHeld) // ignore key auto-repeat
            {
                _keyHeld = true;
                if (!typing) Increment();
            }
        }

        if (H.UndoBindType == "key" && vk == H.UndoBindKey)
        {
            if (!_undoKeyHeld)
            {
                _undoKeyHeld = true;
                if (!typing) Undo();
            }
        }
    }

    private void OnGlobalKeyUp(int vk)
    {
        if (_loading || _settings.Hunts.Count == 0) return;
        if (H.BindType == "key" && vk == H.BindKey) _keyHeld = false;
        if (H.UndoBindType == "key" && vk == H.UndoBindKey) _undoKeyHeld = false;
    }

    // ── Gamepad polling (Windows.Gaming.Input — works in background) ─────────

    private void PollGamepads(object? sender, EventArgs e)
    {
        if (_loading || _settings.Hunts.Count == 0) return;

        IReadOnlyList<RawGameController> pads;
        try { pads = RawGameController.RawGameControllers; }
        catch { return; }

        bool countPressed = false, undoPressed = false, isSony = false;

        foreach (var gp in pads)
        {
            bool[] buttons = new bool[gp.ButtonCount];
            var switches = new GameControllerSwitchPosition[gp.SwitchCount];
            double[] axes = new double[gp.AxisCount];
            try { gp.GetCurrentReading(buttons, switches, axes); }
            catch { continue; }

            if (gp.HardwareVendorId == 0x054C) isSony = true;

            if (_listening != BindTarget.None)
            {
                for (int i = 0; i < buttons.Length; i++)
                {
                    if (buttons[i]) { FinishPadBinding(i); return; }
                }
            }
            else
            {
                if (H.BindType == "pad" && H.BindButton < buttons.Length && buttons[H.BindButton])
                    countPressed = true;
                if (H.UndoBindType == "pad" && H.UndoBindButton < buttons.Length && buttons[H.UndoBindButton])
                    undoPressed = true;
            }
        }

        if (_listening == BindTarget.None)
        {
            if (countPressed && !_padHeld) Increment();
            _padHeld = countPressed;

            if (undoPressed && !_undoPadHeld) Undo();
            _undoPadHeld = undoPressed;
        }

        UpdateStatusPill(pads.Count > 0, isSony ? "PS5 connected" : "controller connected");
    }

    private void UpdateStatusPill(bool connected, string name)
    {
        if (connected == _padConnected && (!connected || name == _padName)) return;
        _padConnected = connected;
        _padName = name;

        if (connected)
        {
            StatusText.Text = name;
            StatusText.Foreground = (Brush)FindResource("SuccessBrush");
            StatusDot.Fill = (Brush)FindResource("SuccessBrush");
            StatusPill.Background = (Brush)FindResource("SuccessDimBrush");
            StatusPill.BorderBrush = (Brush)FindResource("SuccessBrush");
            var pulse = new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(1))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            StatusDot.BeginAnimation(OpacityProperty, pulse);
        }
        else
        {
            StatusText.Text = "no controller";
            StatusText.Foreground = (Brush)FindResource("MutedBrush");
            StatusDot.Fill = (Brush)FindResource("MutedBrush");
            StatusPill.Background = Brushes.Transparent;
            StatusPill.BorderBrush = (Brush)FindResource("BorderBrush2");
            StatusDot.BeginAnimation(OpacityProperty, null);
            StatusDot.Opacity = 1;
        }
    }

    // ── Counter ──────────────────────────────────────────────────────────────

    private void Increment()
    {
        AccrueHuntTime();
        SetCount(H.Count + H.Step, flash: true);
        PlaySound(_tick);
    }

    private void AccrueHuntTime()
    {
        var now = DateTime.UtcNow;
        if (_lastIncrementAt is DateTime last)
        {
            H.ElapsedSeconds += Math.Min((now - last).TotalSeconds, IdleCapSeconds);
        }
        _lastIncrementAt = now;
    }

    private void Increment_Click(object sender, RoutedEventArgs e) => Increment();

    private void Undo()
    {
        if (H.Count == 0) return;
        SetCount(Math.Max(0, H.Count - H.Step));
        PlaySound(_tickLow);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (H.Count > 0 &&
            MessageBox.Show(this, "Reset counter to 0?", "Shiny Counter",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        H.ElapsedSeconds = 0;
        _lastIncrementAt = null;
        SetCount(0);
    }

    internal void SetCount(long value, bool flash = false)
    {
        H.Count = value;
        string text = value.ToString("N0");
        CounterText.Text = text;
        MiniCount.Text = text;
        if (flash) FlashCounter();
        UpdateStats();
        SaveSettings();
    }

    private void FlashCounter()
    {
        var anim = new ColorAnimation(ThemeManager.Accent, ThemeManager.Text, TimeSpan.FromMilliseconds(300));
        _counterBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    // ── Found / history ──────────────────────────────────────────────────────

    private void Found_Click(object sender, RoutedEventArgs e)
    {
        if (H.Count == 0)
        {
            MessageBox.Show(this, "The counter is at 0 — nothing to log yet.", "Shiny Counter",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this,
                $"Log “{H.Name}” as found after {H.Count:N0} resets and reset the counter?",
                "Shiny found!", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _settings.History.Insert(0, new HistoryEntry
        {
            Name = H.Name,
            Count = H.Count,
            Odds = H.Odds,
            ElapsedSeconds = H.ElapsedSeconds,
            CompletedAt = DateTime.Now,
        });

        H.ElapsedSeconds = 0;
        _lastIncrementAt = null;
        SetCount(0);
        PlaySound(_chime);
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        new HistoryWindow(_settings, SaveSettings) { Owner = this }.ShowDialog();
    }

    // ── Hunt profiles ────────────────────────────────────────────────────────

    private void RefreshHuntList()
    {
        _suppressHuntSelect = true;
        HuntSelect.ItemsSource = _settings.Hunts.Select(h => h.Name).ToList();
        HuntSelect.SelectedIndex = _settings.ActiveHunt;
        _suppressHuntSelect = false;
    }

    private void HuntSelect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressHuntSelect || _loading || HuntSelect.SelectedIndex < 0) return;
        _settings.ActiveHunt = HuntSelect.SelectedIndex;
        ApplyHuntToUi();
        SaveSettings();
    }

    private void NewHunt_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NameDialog("name the new hunt", $"Hunt {_settings.Hunts.Count + 1}") { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;

        _settings.Hunts.Add(new Hunt { Name = dlg.Result });
        _settings.ActiveHunt = _settings.Hunts.Count - 1;
        RefreshHuntList();
        ApplyHuntToUi();
        SaveSettings();
    }

    private void RenameHunt_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NameDialog("rename hunt", H.Name) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;

        H.Name = dlg.Result;
        MiniName.Text = H.Name;
        RefreshHuntList();
        SaveSettings();
    }

    private void DeleteHunt_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Hunts.Count == 1)
        {
            MessageBox.Show(this, "You can't delete the only hunt — rename or reset it instead.",
                "Shiny Counter", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this,
                $"Delete hunt “{H.Name}” ({H.Count:N0} resets)? This can't be undone.",
                "Delete hunt", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _settings.Hunts.RemoveAt(_settings.ActiveHunt);
        _settings.ActiveHunt = Math.Min(_settings.ActiveHunt, _settings.Hunts.Count - 1);
        RefreshHuntList();
        ApplyHuntToUi();
        SaveSettings();
    }

    /// Push the active hunt's state into every control.
    private void ApplyHuntToUi()
    {
        CounterText.Text = H.Count.ToString("N0");
        MiniCount.Text = H.Count.ToString("N0");
        MiniName.Text = H.Name;
        IncrementBtn.Content = $"+ {H.Step} manual";
        UpdateBoundDisplay();

        _keyHeld = _undoKeyHeld = _padHeld = _undoPadHeld = false;
        _lastIncrementAt = null;

        UpdateStats();
    }

    // ── Hunt config (called from the settings window) ────────────────────────

    internal void ApplyStep(int v)
    {
        H.Step = v;
        IncrementBtn.Content = $"+ {v} manual";
        SaveSettings();
    }

    internal void ApplyOdds(double v)
    {
        H.Odds = v;
        UpdateStats();
        SaveSettings();
    }

    internal void ApplySound(bool on) => SoundToggleBtn.IsChecked = on;

    internal void ApplyScaleSetting(double v)
    {
        _settings.UiScale = v;
        ApplyScale(v);
        SaveSettings();
    }

    internal void ApplyTheme(string name)
    {
        _settings.Theme = name;
        ThemeManager.Apply(name);
        OnThemeApplied();
        SaveSettings();
    }

    private void OnThemeApplied()
    {
        _counterBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _counterBrush.Color = ThemeManager.Text;
        // Re-resolve the status pill brushes for the new palette
        bool wasConnected = _padConnected;
        string name = _padName;
        _padConnected = !wasConnected;
        UpdateStatusPill(wasConnected, name);
    }

    // ── Stats ────────────────────────────────────────────────────────────────

    private void UpdateStats()
    {
        double fifty = Math.Log(0.5) / Math.Log(1 - 1 / H.Odds);
        ExpectedVal.Text = Math.Round(fifty).ToString("N0");

        TimeVal.Text = H.ElapsedSeconds >= 1 ? FormatDuration(H.ElapsedSeconds) : "—";

        if (H.Count > 0 && H.ElapsedSeconds >= 60)
        {
            double perHour = H.Count / (H.ElapsedSeconds / 3600);
            RateVal.Text = $"{perHour:N0} / hr";

            double remaining = fifty - H.Count;
            EtaVal.Text = remaining <= 0 ? "passed" : FormatDuration(remaining / perHour * 3600);
        }
        else
        {
            RateVal.Text = "—";
            EtaVal.Text = "—";
        }

        if (H.Count == 0)
        {
            OddsVal.Text = "—";
            ProbVal.Text = "0%";
            MiniProb.Text = "0% chance hit";
            BarFill.Width = 0;
            BarLabel.Text = "0% probability";
            return;
        }

        OddsVal.Text = "1 / " + H.Count.ToString("N0");

        double prob = (1 - Math.Pow(1 - 1 / H.Odds, H.Count)) * 100;
        string probStr = prob.ToString("F2") + "%";
        ProbVal.Text = probStr;
        MiniProb.Text = probStr + " chance hit";
        BarLabel.Text = probStr + " probability";

        double trackWidth = BarTrack.ActualWidth;
        if (trackWidth > 0)
        {
            BarFill.Width = trackWidth * Math.Min(prob, 100) / 100;
        }
    }

    public static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m";
        return $"{(int)seconds}s";
    }

    // ── Header toggles ───────────────────────────────────────────────────────

    private void Pin_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = _mini || PinToggleBtn.IsChecked == true;
        if (!_loading)
        {
            _settings.AlwaysOnTop = PinToggleBtn.IsChecked == true;
            SaveSettings();
        }
    }

    private void Sound_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.SoundOn = SoundToggleBtn.IsChecked == true;
        SaveSettings();
        if (_settings.SoundOn) PlaySound(_tick);
    }

    // ── Window scale ─────────────────────────────────────────────────────────

    private void ApplyScale(double v)
    {
        CardScale.ScaleX = v;
        CardScale.ScaleY = v;
        // Re-measure the bar fill after layout settles at the new size
        Dispatcher.BeginInvoke(UpdateStats, DispatcherPriority.Loaded);
    }

    // ── Mini overlay mode ────────────────────────────────────────────────────

    private void Mini_Click(object sender, RoutedEventArgs e)
    {
        _mini = true;
        Card.Visibility = Visibility.Collapsed;
        MiniPanel.Visibility = Visibility.Visible;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;

        // Round the borderless window's corners (Windows 11)
        var handle = new WindowInteropHelper(this).Handle;
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    private void MiniRestore_Click(object sender, RoutedEventArgs e)
    {
        _mini = false;
        MiniPanel.Visibility = Visibility.Collapsed;
        Card.Visibility = Visibility.Visible;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanMinimize;
        Topmost = PinToggleBtn.IsChecked == true;
        Dispatcher.BeginInvoke(UpdateStats, DispatcherPriority.Loaded);
    }

    private void Mini_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    // ── Sounds ───────────────────────────────────────────────────────────────

    private void InitSounds()
    {
        try
        {
            _tick = LoadTone((1175, 0.07));
            _tickLow = LoadTone((587, 0.07));
            _chime = LoadTone((880, 0.12), (1109, 0.12), (1319, 0.22));
        }
        catch { /* no sound is better than no app */ }
    }

    private static SoundPlayer LoadTone(params (double freq, double seconds)[] notes)
    {
        const int rate = 44100;
        const double amp = 0.22;

        int totalSamples = notes.Sum(n => (int)(rate * n.seconds));
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        int dataLen = totalSamples * 2;

        bw.Write("RIFF"u8); bw.Write(36 + dataLen); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(dataLen);

        foreach (var (freq, seconds) in notes)
        {
            int n = (int)(rate * seconds);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate;
                double envelope = Math.Exp(-t * 30);
                short sample = (short)(Math.Sin(2 * Math.PI * freq * t) * envelope * amp * short.MaxValue);
                bw.Write(sample);
            }
        }

        ms.Position = 0;
        var player = new SoundPlayer(ms);
        player.Load();
        return player;
    }

    private void PlaySound(SoundPlayer? player)
    {
        if (!_settings.SoundOn || player is null) return;
        try { player.Play(); } catch { }
    }

    // ── Blink animation for the rebind buttons ───────────────────────────────

    private static void StartBlink(UIElement el)
    {
        var blink = new DoubleAnimation(1, 0.5, TimeSpan.FromMilliseconds(500))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        el.BeginAnimation(OpacityProperty, blink);
    }

    private static void StopBlink(UIElement el)
    {
        el.BeginAnimation(OpacityProperty, null);
        el.Opacity = 1;
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    internal void SaveSettings()
    {
        if (_loading) return;
        try
        {
            Directory.CreateDirectory(SettingsDir);
            // Write-then-swap so another instance can never read a half-written file
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp,
                JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch { /* never block counting on a failed save */ }
        AppStateChanged?.Invoke();
    }

    private void LoadSettings()
    {
        _loading = true;
        // Retry a few times in case a closing instance is mid-save
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(SettingsPath)) break;

                string json = File.ReadAllText(SettingsPath);
                using var doc = JsonDocument.Parse(json);

                _settings = doc.RootElement.TryGetProperty("Hunts", out _)
                    ? JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings()
                    : MigrateV1(doc.RootElement);
                break;
            }
            catch (Exception ex) when (ex is IOException or JsonException && attempt < 4)
            {
                Thread.Sleep(150);
            }
            catch
            {
                _settings = new AppSettings();
                break;
            }
        }

        if (_settings.Hunts.Count == 0) _settings.Hunts.Add(new Hunt());
        _settings.ActiveHunt = Math.Clamp(_settings.ActiveHunt, 0, _settings.Hunts.Count - 1);
        if (_settings.UiScale < 0.5 || _settings.UiScale > 2) _settings.UiScale = 1.0;

        ThemeManager.EnsureStockThemes();
        ThemeManager.Apply(_settings.Theme);
        OnThemeApplied();

        SoundToggleBtn.IsChecked = _settings.SoundOn;
        PinToggleBtn.IsChecked = _settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        ApplyScale(_settings.UiScale);

        _loading = false;

        RefreshHuntList();
        ApplyHuntToUi();
    }

    /// Convert the original single-hunt settings format.
    private static AppSettings MigrateV1(JsonElement root)
    {
        var hunt = new Hunt { Name = "Hunt 1" };
        var s = new AppSettings();

        if (root.TryGetProperty("Count", out var c) && c.TryGetInt64(out long count))
            hunt.Count = Math.Max(0, count);
        if (root.TryGetProperty("Step", out var st) && st.TryGetInt32(out int step))
            hunt.Step = Math.Max(1, step);
        if (root.TryGetProperty("Odds", out var o) && o.TryGetDouble(out double odds) && odds >= 2)
            hunt.Odds = odds;
        if (root.TryGetProperty("BindType", out var bt) && bt.GetString() is "key" or "pad")
            hunt.BindType = bt.GetString()!;
        if (root.TryGetProperty("BindKey", out var bk) && bk.TryGetInt32(out int key))
            hunt.BindKey = key;
        if (root.TryGetProperty("BindButton", out var bb) && bb.TryGetInt32(out int btn))
            hunt.BindButton = btn;
        if (root.TryGetProperty("AlwaysOnTop", out var aot) && aot.ValueKind == JsonValueKind.True)
            s.AlwaysOnTop = true;

        s.Hunts.Add(hunt);
        return s;
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
