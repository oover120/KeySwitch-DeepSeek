using KeySwitch.Core.Detect;
using KeySwitch.Core.Learning;
using KeySwitch.Core.PostProcess;
using KeySwitch.Data;

namespace KeySwitch.App.Services;

/// <summary>
/// Обучение с учётом настройки LearningEnabled: когда пользователь его выключил,
/// движок не должен ни читать, ни накапливать статистику, но данные остаются на диске.
/// </summary>
internal sealed class SettingsAwareLearningStore : ILearningStore
{
    private readonly ILearningStore _inner;
    private readonly AppSettings _settings;

    public SettingsAwareLearningStore(ILearningStore inner, AppSettings settings)
    {
        _inner = inner;
        _settings = settings;
    }

    public int GetUndoCount(string word, string sourceLayoutId, string targetLayoutId)
        => _settings.LearningEnabled ? _inner.GetUndoCount(word, sourceLayoutId, targetLayoutId) : 0;

    public bool IsSuppressed(string word, string sourceLayoutId, string targetLayoutId)
        => _settings.LearningEnabled && _inner.IsSuppressed(word, sourceLayoutId, targetLayoutId);

    public void RecordUndo(string word, string sourceLayoutId, string targetLayoutId)
    {
        if (_settings.LearningEnabled)
            _inner.RecordUndo(word, sourceLayoutId, targetLayoutId);
    }

    public void RecordManualCorrection(string original, string corrected, string sourceLayoutId, string targetLayoutId)
    {
        if (_settings.LearningEnabled)
            _inner.RecordManualCorrection(original, corrected, sourceLayoutId, targetLayoutId);
    }

    public void RecordAccepted(string word, string sourceLayoutId, string targetLayoutId)
    {
        if (_settings.LearningEnabled)
            _inner.RecordAccepted(word, sourceLayoutId, targetLayoutId);
    }
}

/// <summary>
/// Постобработчик, который включается и выключается настройкой «на лету»:
/// движок создаётся один раз и не пересобирается при сохранении настроек.
/// </summary>
internal sealed class ToggleablePostProcessor : IPostProcessor
{
    private readonly IPostProcessor _inner;
    private readonly Func<bool> _enabled;

    public ToggleablePostProcessor(IPostProcessor inner, Func<bool> enabled)
    {
        _inner = inner;
        _enabled = enabled;
    }

    public string Id => _inner.Id;

    public string Name => _inner.Name;

    public bool TryProcess(string word, out string result)
    {
        if (!_enabled())
        {
            result = word;
            return false;
        }

        return _inner.TryProcess(word, out result);
    }
}

/// <summary>
/// Отложенные языковые ресурсы: движок создаётся сразу, а словари грузятся в фоне.
/// До загрузки провайдер отвечает null — хук в это время ещё не запущен, так что
/// на горячем пути такие вызовы не случаются.
/// </summary>
internal sealed class DeferredLanguageProvider : ILanguageResourcesProvider
{
    private volatile LanguageResources? _inner;

    public LanguageResources? Inner
    {
        get => _inner;
        set => _inner = value;
    }

    public bool IsLoaded => _inner?.IsLoaded ?? false;

    public IWordDictionary? GetDictionary(string language) => _inner?.GetDictionary(language);

    public ICharNgramModel? GetTrigrams(string language) => _inner?.GetTrigrams(language);

    public ICharNgramModel? GetQuadgrams(string language) => _inner?.GetQuadgrams(language);
}
