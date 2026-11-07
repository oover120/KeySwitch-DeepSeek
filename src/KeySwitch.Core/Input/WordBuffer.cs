using System.Text;
using KeySwitch.Core.Layout;

namespace KeySwitch.Core.Input;

/// <summary>Завершённое слово, снятое с буфера.</summary>
public sealed record WordToken(string Text, string LayoutId, IReadOnlyList<KeyStroke> Strokes)
{
    public override string ToString() => $"'{Text}' [{LayoutId}]";
}

/// <summary>
/// Буфер текущего слова. Хранит и символы (в раскладке, активной в момент набора),
/// и сами нажатия — нажатия нужны для точной повторной эмуляции и диагностики.
/// </summary>
public sealed class WordBuffer
{
    private readonly List<char> _chars = new(32);
    private readonly List<KeyStroke> _strokes = new(32);
    private readonly StringBuilder _text = new(32);

    /// <summary>Раскладка, в которой набрано слово (по первому символу).</summary>
    public string LayoutId { get; private set; } = LayoutCatalog.EnUsId;

    public string Text => _text.ToString();

    public int Length => _chars.Count;

    public bool IsEmpty => _chars.Count == 0;

    public IReadOnlyList<KeyStroke> Strokes => _strokes;

    public char this[int index] => _chars[index];

    public void Append(KeyStroke stroke, char character, string layoutId)
    {
        if (_chars.Count == 0)
            LayoutId = layoutId;

        _chars.Add(character);
        _strokes.Add(stroke);
        _text.Append(character);
    }

    public bool Backspace()
    {
        if (_chars.Count == 0)
            return false;

        _chars.RemoveAt(_chars.Count - 1);
        _strokes.RemoveAt(_strokes.Count - 1);
        _text.Length = _chars.Count;
        return true;
    }

    public WordToken ToToken() => new(Text, LayoutId, _strokes.ToArray());

    public void Clear()
    {
        _chars.Clear();
        _strokes.Clear();
        _text.Clear();
    }
}
