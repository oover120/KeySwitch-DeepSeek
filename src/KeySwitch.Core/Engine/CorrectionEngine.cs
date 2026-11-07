using KeySwitch.Core.Correction;
using KeySwitch.Core.Detect;
using KeySwitch.Core.Input;
using KeySwitch.Core.Learning;
using KeySwitch.Core.Layout;
using KeySwitch.Core.PostProcess;

namespace KeySwitch.Core.Engine;

/// <summary>Результат обработки слова: слово, план исправления (если нужен) и пояснение.</summary>
public sealed record EngineResult(WordToken? Word, CorrectionPlan? Plan, string? Note)
{
    public static readonly EngineResult Empty = new(null, null, null);

    public bool HasPlan => Plan != null;
}

/// <summary>
/// Ядро автопереключения: собирает слово, проверяет исключения, спрашивает детектор,
/// применяет постобработку и выдаёт план исправления. Никаких вызовов WinAPI —
/// всё исполнение делает слой Hooks, поэтому ядро полностью тестируемо.
/// </summary>
public sealed class CorrectionEngine
{
    private readonly LayoutDetector _detector;
    private readonly DetectionSettings _settings;
    private readonly ILearningStore _learning;
    private readonly IExceptionPolicy _exceptions;
    private readonly PostProcessorPipeline _postProcessors;

    private WordToken? _lastWord;
    private char? _lastTerminator;
    private bool _lastTerminatorTyped;

    public CorrectionEngine(
        LayoutDetector detector,
        DetectionSettings settings,
        ILearningStore learning,
        IExceptionPolicy exceptions,
        PostProcessorPipeline postProcessors)
    {
        _detector = detector;
        _settings = settings;
        _learning = learning;
        _exceptions = exceptions;
        _postProcessors = postProcessors;
    }

    public WordBuffer Buffer { get; } = new();

    /// <summary>Последнее выданное исправление (для отмены).</summary>
    public CorrectionPlan? LastApplied { get; private set; }

    public bool CanUndo => LastApplied != null;

    public WordToken? LastWord => _lastWord;

    public DetectionSettings Settings => _settings;

    public void AppendChar(KeyStroke stroke, char character, string layoutId) => Buffer.Append(stroke, character, layoutId);

    public void Backspace() => Buffer.Backspace();

    public void Reset()
    {
        Buffer.Clear();
    }

    /// <summary>Раннее срабатывание: проверка текущего недописанного слова.</summary>
    public CorrectionPlan? TryEarlySwitch()
    {
        if (!_settings.EarlySwitchEnabled || Buffer.Length < _settings.EarlySwitchMinLength)
            return null;

        var source = LayoutCatalog.TryGet(Buffer.LayoutId);
        if (source == null)
            return null;

        var result = _detector.Decide(Buffer.Text, source, _detector.Pairs.GetAlternate(source)!, early: true);
        if (!result.Convert)
            return null;

        var plan = new CorrectionPlan(
            CorrectionKind.LayoutSwitch,
            Buffer.Text,
            result.ConvertedText,
            Buffer.Length,
            null,
            result.SourceLayoutId,
            result.TargetLayoutId,
            result.Confidence,
            "раннее переключение: " + result.Reason);

        _lastWord = Buffer.ToToken();
        _lastTerminator = null;
        _lastTerminatorTyped = false;
        Buffer.Clear();
        LastApplied = plan;
        return plan;
    }

    /// <summary>Завершение слова (пробел, Enter, Tab или ручная конвертация).</summary>
    public EngineResult CompleteWord(char? terminator, bool terminatorAlreadyTyped)
    {
        if (Buffer.IsEmpty)
            return EngineResult.Empty;

        var token = Buffer.ToToken();
        Buffer.Clear();
        _lastWord = token;
        _lastTerminator = terminator;
        _lastTerminatorTyped = terminatorAlreadyTyped;

        var plan = BuildPlan(token, terminator, terminatorAlreadyTyped, manual: false);
        if (plan == null)
            return new EngineResult(token, null, null);

        LastApplied = plan;
        return new EngineResult(token, plan, plan.Reason);
    }

    /// <summary>Ручная конвертация: текущий буфер, иначе последнее завершённое слово.</summary>
    public EngineResult ConvertManually()
    {
        if (!Buffer.IsEmpty)
            return CompleteWord(null, false);

        if (_lastWord == null)
            return EngineResult.Empty;

        var plan = BuildPlan(_lastWord, _lastTerminator, _lastTerminatorTyped, manual: true);
        if (plan == null)
            return new EngineResult(_lastWord, null, "конвертация не требуется");

        LastApplied = plan;
        return new EngineResult(_lastWord, plan, plan.Reason);
    }

    /// <summary>Конвертация произвольного текста (выделение). Возвращает null, если не нужно.</summary>
    public CorrectionPlan? ConvertSelection(string text, string layoutId)
    {
        var source = LayoutCatalog.TryGet(layoutId);
        if (source == null || string.IsNullOrWhiteSpace(text))
            return null;

        var result = _detector.Decide(text, source);
        if (!result.Convert)
            return null;

        var plan = new CorrectionPlan(
            CorrectionKind.Selection,
            text,
            result.ConvertedText,
            0,
            null,
            result.SourceLayoutId,
            result.TargetLayoutId,
            result.Confidence,
            result.Reason);

        _learning.RecordManualCorrection(text, result.ConvertedText, result.SourceLayoutId, result.TargetLayoutId);
        LastApplied = plan;
        return plan;
    }

    /// <summary>Отмена последнего исправления. Возвращает план обратного исправления.</summary>
    public bool TryUndo(out UndoPlan? plan)
    {
        plan = null;
        if (LastApplied is not { } applied)
            return false;

        var terminatorLength = applied.Terminator.HasValue ? 1 : 0;
        var restoreLayout = applied.SourceLayoutId;
        var currentLayout = applied.TargetLayoutId;

        plan = new UndoPlan(
            applied.Kind,
            applied.ReplacementText,
            applied.OriginalText,
            applied.ReplacementText.Length + terminatorLength,
            restoreLayout,
            currentLayout,
            applied.Terminator);

        _learning.RecordUndo(applied.OriginalText, applied.SourceLayoutId, applied.TargetLayoutId);
        LastApplied = null;
        return true;
    }

    /// <summary>Сбрасывает доступность отмены (например, пользователь начал новое слово).</summary>
    public void ClearUndo() => LastApplied = null;

    private CorrectionPlan? BuildPlan(WordToken token, char? terminator, bool terminatorAlreadyTyped, bool manual)
    {
        var source = LayoutCatalog.TryGet(token.LayoutId);
        if (source == null)
            return null;

        var text = token.Text;
        if (CountLetters(text) < _settings.MinWordLength)
            return null;

        var target = _detector.Pairs.GetAlternate(source);
        if (target == null)
            return null;

        if (!manual)
        {
            var verdict = _exceptions.CheckWord(text, source.Id, target.Id);
            if (verdict != WordVerdict.Allow)
                return null;

            if (_learning.IsSuppressed(text.ToLowerInvariant(), source.Id, target.Id))
                return null;
        }

        var detection = _detector.Decide(text, source, target, early: false);
        var deleteCount = text.Length + (terminatorAlreadyTyped && terminator.HasValue ? 1 : 0);

        // Терминатор печатается всегда, когда он известен: если клавиша была проглочена хуком,
        // её нужно напечатать самим, а если она уже дошла до окна — мы её удалили (DeleteCount + 1)
        // и обязаны вернуть, иначе пробел/перевод строки потеряется.
        var terminatorToType = terminator;

        if (detection.Convert)
        {
            var plan = new CorrectionPlan(
                CorrectionKind.LayoutSwitch,
                text,
                detection.ConvertedText,
                deleteCount,
                terminatorToType,
                source.Id,
                target.Id,
                detection.Confidence,
                detection.Reason);

            _learning.RecordAccepted(text.ToLowerInvariant(), source.Id, target.Id);
            return plan;
        }

        // Раскладка верна — пробуем Pro-постобработку (опечатки, ПРивет, ё).
        if (_postProcessors.IsEmpty)
            return null;

        if (!_postProcessors.TryProcess(text, out var fixedText, out var processorId))
            return null;

        return new CorrectionPlan(
            processorId switch
            {
                "typo" => CorrectionKind.Typo,
                "caps" => CorrectionKind.CapsFix,
                "yo" => CorrectionKind.YoFix,
                _ => CorrectionKind.Typo
            },
            text,
            fixedText,
            deleteCount,
            terminatorToType,
            source.Id,
            source.Id,
            1.0,
            $"постобработка {processorId}");
    }

    private static int CountLetters(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (char.IsLetter(ch))
                count++;
        }
        return count;
    }
}
