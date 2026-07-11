using System.Text.Json;

namespace Studio.Store;

/// <summary>
/// Compteur de numéros du jour, persisté dans counters/daily.json,
/// remis à 1 à chaque changement de date. Un seul poste (l'opérateur) attribue les numéros.
/// </summary>
public sealed class DailyCounter
{
    private sealed record State(string Date, int Value);

    private readonly string _path;
    private readonly object _lock = new();

    public DailyCounter(string path) => _path = path;

    public int Next() => Next(DateOnly.FromDateTime(DateTime.Now));

    public int Next(DateOnly today)
    {
        lock (_lock)
        {
            var state = Read();
            var value = state is not null && state.Date == today.ToString("yyyy-MM-dd")
                ? state.Value + 1
                : 1;
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(new State(today.ToString("yyyy-MM-dd"), value)));
            return value;
        }
    }

    private State? Read()
    {
        var json = AtomicFile.ReadAllTextOrNull(_path);
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<State>(json);
        }
        catch (JsonException)
        {
            return null; // fichier illisible : on repart à 1, le journal des commandes fait foi
        }
    }
}
