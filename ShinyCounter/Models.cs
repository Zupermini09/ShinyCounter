namespace ShinyCounter;

public sealed class Hunt
{
    public string Name { get; set; } = "Hunt 1";
    public long Count { get; set; }
    public int Step { get; set; } = 1;
    public double Odds { get; set; } = 8192;
    public double ElapsedSeconds { get; set; }

    public string BindType { get; set; } = "none"; // none | key | pad
    public int BindKey { get; set; }
    public int BindButton { get; set; }

    public string UndoBindType { get; set; } = "none";
    public int UndoBindKey { get; set; }
    public int UndoBindButton { get; set; }
}

public sealed class HistoryEntry
{
    public string Name { get; set; } = "";
    public long Count { get; set; }
    public double Odds { get; set; }
    public double ElapsedSeconds { get; set; }
    public DateTime CompletedAt { get; set; }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 2;
    public int ActiveHunt { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool SoundOn { get; set; } = true;
    public double UiScale { get; set; } = 1.0;
    public string Theme { get; set; } = "Dark";
    public List<Hunt> Hunts { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();
}
