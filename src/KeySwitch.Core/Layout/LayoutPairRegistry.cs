namespace KeySwitch.Core.Layout;

/// <summary>
/// Регистр пар раскладок. Именно пары (а не глобальный список) определяют,
/// между какими языками возможна автоконвертация.
/// </summary>
public sealed class LayoutPairRegistry
{
    private readonly Dictionary<string, List<string>> _neighbours = new(StringComparer.OrdinalIgnoreCase);

    public static LayoutPairRegistry CreateDefault()
    {
        var registry = new LayoutPairRegistry();
        registry.AddPair(LayoutCatalog.EnUsId, LayoutCatalog.RuRuId);
        return registry;
    }

    public void AddPair(string layoutA, string layoutB)
    {
        AddDirected(layoutA, layoutB);
        AddDirected(layoutB, layoutA);
    }

    private void AddDirected(string from, string to)
    {
        if (!_neighbours.TryGetValue(from, out var list))
            _neighbours[from] = list = new List<string>();
        if (!list.Contains(to, StringComparer.OrdinalIgnoreCase))
            list.Add(to);
    }

    /// <summary>Возвращает альтернативную раскладку для указанной, либо null, если пара не настроена.</summary>
    public LayoutTable? GetAlternate(LayoutTable source)
    {
        if (!_neighbours.TryGetValue(source.Id, out var list) || list.Count == 0)
            return null;
        return LayoutCatalog.TryGet(list[0]);
    }

    public IReadOnlyList<LayoutTable> GetAlternates(LayoutTable source)
    {
        if (!_neighbours.TryGetValue(source.Id, out var list))
            return Array.Empty<LayoutTable>();
        var result = new List<LayoutTable>(list.Count);
        foreach (var id in list)
        {
            var table = LayoutCatalog.TryGet(id);
            if (table != null)
                result.Add(table);
        }
        return result;
    }
}
