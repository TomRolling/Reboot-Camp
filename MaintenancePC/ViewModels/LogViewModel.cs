using CommunityToolkit.Mvvm.ComponentModel;

namespace MaintenancePC.ViewModels;

/// <summary>Petit panneau qui affiche la sortie de winget, sfc ou dism en direct.</summary>
public sealed partial class LogViewModel : ObservableObject
{
    private const int MaxLines = 300;
    private readonly List<string> _lines = [];

    [ObservableProperty] public partial string Text { get; set; }

    public LogViewModel() => Text = "";

    public void Clear()
    {
        _lines.Clear();
        Text = "";
    }

    public void Add(string line)
    {
        // Les barres de progression (« [====  45.0%  ] ») remplacent la ligne précédente au lieu de s'empiler.
        if (_lines.Count > 0 && IsProgressLine(line) && IsProgressLine(_lines[^1]))
            _lines[^1] = line;
        else
            _lines.Add(line);

        if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        Text = string.Join(Environment.NewLine, _lines);
    }

    private static bool IsProgressLine(string line) =>
        line.Contains('%') && (line.Contains('[') || line.Contains('█') || line.Contains('▒') || line.Length < 60);
}
