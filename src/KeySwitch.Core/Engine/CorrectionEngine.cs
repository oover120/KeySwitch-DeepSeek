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
    private (string? Previous, string? Latest) _wordLanguages;
    private (string? Previous, string? Latest) _languagesBeforeLastWord;
    private (string Text, string LayoutId)? _erasedWord;
    private int _charactersLeftToErase;
    private int _spacesAfterUnchangedWord;

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

    public void AppendChar(KeyStroke stroke, char character, string layoutId)
    {
        _spacesAfterUnchangedWord = 0;
        if (Buffer.IsEmpty)
            ClearUndo();
        if (_charactersLeftToErase != 0)
            ClearRetype();
        Buffer.Append(stroke, character, layoutId);
    }

    public void Backspace(bool wholeWord = false, bool trackRetype = true)
    {
        if (!trackRetype)
            ClearRetype();
        else if (!Buffer.IsEmpty && _charactersLeftToErase == 0)
        {
            _erasedWord = (Buffer.Text, Buffer.LayoutId);
            _charactersLeftToErase = Buffer.Length;
        }
        else if (Buffer.IsEmpty && _spacesAfterUnchangedWord > 0 && _lastWord is { } last)
        {
            _erasedWord = (last.Text, last.LayoutId);
            _charactersLeftToErase = last.Text.Length + _spacesAfterUnchangedWord;
            _lastWord = null;
        }
        else if (Buffer.IsEmpty && _charactersLeftToErase == 0)
            ClearRetype();

        _spacesAfterUnchangedWord = 0;
        if (wholeWord)
        {
            Buffer.Clear();
            _charactersLeftToErase = 0;
            ClearLanguageContext();
        }
        else
        {
            if (!Buffer.Backspace())
                ClearLanguageContext();
            if (_charactersLeftToErase > 0)
                _charactersLeftToErase--;
        }
    }

    /// <summary>Смена раскладки меняет клавиши, а текст и позиция ввода остаются прежними.</summary>
    public void OnLayoutShortcut()
    {
        ClearLanguageContext();
        ClearUndo();
    }

    public void Reset(bool forgetLastWord = false)
    {
        Buffer.Clear();
        ClearLanguageContext();
        ClearRetype();
        if (!forgetLastWord)
            return;

        _lastWord = null;
        _lastTerminator = null;
        _lastTerminatorTyped = false;
        LastApplied = null;
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
        ClearLanguageContext();
        ClearRetype();
        LastApplied = plan;
        return plan;
    }

    /// <summary>Завершение слова (пробел, Enter, Tab или ручная конвертация).</summary>
    public EngineResult CompleteWord(char? terminator, bool terminatorAlreadyTyped, bool automaticInput = true)
    {
        if (!automaticInput || (Buffer.IsEmpty && EndsLanguageContext(terminator)))
            ClearLanguageContext();

        if (Buffer.IsEmpty)
        {
            if (automaticInput && terminator == ' ' && _spacesAfterUnchangedWord > 0)
                _spacesAfterUnchangedWord++;
            else
                ClearRetype();
            return EngineResult.Empty;
        }

        var token = Buffer.ToToken();
        Buffer.Clear();
        _lastWord = token;
        _lastTerminator = terminator;
        _lastTerminatorTyped = terminatorAlreadyTyped;

        var reason = "автопереключение выключено";
        CorrectionPlan? plan = null;
        if (automaticInput)
        {
            if (TryLearnRetype(token))
                reason = "запомнено ручное исправление раскладки";
            else
                plan = BuildPlan(token, terminator, terminatorAlreadyTyped, manual: false, out reason);

            _languagesBeforeLastWord = _wordLanguages;
            RememberWordLanguage(plan?.ReplacementText ?? token.Text, terminator);
        }

        ClearRetype();
        if (automaticInput && plan == null && terminator == ' ')
            _spacesAfterUnchangedWord = 1;

        if (plan == null)
        {
            // Последнее действие — «оставить как есть». Отменять нечего: иначе двойной Shift
            // откатил бы исправление, сделанное несколькими словами раньше.
            LastApplied = null;
            return new EngineResult(token, null, reason);
        }

        LastApplied = plan;
        return new EngineResult(token, plan, plan.Reason);
    }

    /// <summary>Ручная конвертация: текущий буфер, иначе последнее завершённое слово.</summary>
    public EngineResult ConvertManually()
    {
        ClearLanguageContext();
        ClearRetype();
        if (!Buffer.IsEmpty)
            return CompleteWord(null, false);

        if (_lastWord == null)
            return EngineResult.Empty;

        var plan = BuildPlan(_lastWord, _lastTerminator, _lastTerminatorTyped, manual: true, out _);
        if (plan == null)
            return new EngineResult(_lastWord, null, "конвертация не требуется");

        LastApplied = plan;
        return new EngineResult(_lastWord, plan, plan.Reason);
    }

    /// <summary>
    /// Принудительная конвертация последнего слова (или текущего буфера) без порога уверенности:
    /// двойной тап Shift. Человек знает лучше детектора — незнакомое слово, код, аббревиатура.
    /// Технические ограничения остаются: минимальная длина слова и наличие пары раскладок.
    /// </summary>
    public EngineResult ForceConvert()
    {
        ClearLanguageContext();
        ClearRetype();
        if (!Buffer.IsEmpty)
        {
            var current = Buffer.ToToken();
            Buffer.Clear();
            _lastWord = current;
            _lastTerminator = null;
            _lastTerminatorTyped = false;

            var currentPlan = BuildForcedPlan(current, null, terminatorAlreadyTyped: false);
            if (currentPlan == null)
                return new EngineResult(current, null, "конвертация не требуется");

            LastApplied = currentPlan;
            return new EngineResult(current, currentPlan, currentPlan.Reason);
        }

        if (_lastWord is not { } token)
            return EngineResult.Empty;

        var plan = BuildForcedPlan(token, _lastTerminator, _lastTerminatorTyped);
        if (plan == null)
            return new EngineResult(token, null, "конвертация не требуется");

        LastApplied = plan;
        return new EngineResult(token, plan, plan.Reason);
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

        ClearLanguageContext();
        ClearRetype();
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
    public bool TryUndo(out UndoPlan? plan, bool confirmOriginal = false)
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

        _learning.RecordUndo(applied.OriginalText, applied.SourceLayoutId, applied.TargetLayoutId,
            keepOriginal: confirmOriginal && applied.Kind == CorrectionKind.LayoutSwitch && applied.Terminator.HasValue);
        ClearRetype();
        _wordLanguages = _languagesBeforeLastWord;
        RememberWordLanguage(applied.OriginalText, applied.Terminator);
        LastApplied = null;
        return true;
    }

    /// <summary>Сбрасывает доступность отмены (например, пользователь начал новое слово).</summary>
    public void ClearUndo() => LastApplied = null;

    private CorrectionPlan? BuildForcedPlan(WordToken token, char? terminator, bool terminatorAlreadyTyped)
    {
        var source = LayoutCatalog.TryGet(token.LayoutId);
        if (source == null)
            return null;

        var detection = _detector.ForceConvert(token.Text, source);
        if (!detection.Convert)
            return null;

        var deleteCount = token.Text.Length + (terminatorAlreadyTyped && terminator.HasValue ? 1 : 0);
        var plan = new CorrectionPlan(
            CorrectionKind.LayoutSwitch,
            token.Text,
            detection.ConvertedText,
            deleteCount,
            terminator,
            detection.SourceLayoutId,
            detection.TargetLayoutId,
            1.0,
            detection.Reason);

        // Принудительная конвертация — сигнал «здесь должна быть другая раскладка»:
        // записываем как положительный пример, чтобы в следующий раз детектор справился сам.
        _learning.RecordManualCorrection(token.Text, plan.ReplacementText, detection.SourceLayoutId, detection.TargetLayoutId);
        return plan;
    }

    private CorrectionPlan? BuildPlan(WordToken token, char? terminator, bool terminatorAlreadyTyped, bool manual,
        out string reason)
    {
        reason = "нет пары раскладок";
        var source = LayoutCatalog.TryGet(token.LayoutId);
        if (source == null)
            return null;

        var text = token.Text;
        var letterCount = CountLetters(text);

        // Одиночную букву проверяет отдельное правило детектора («b» → «и»), поэтому общий
        // порог минимальной длины к ней не применяется.
        if (letterCount != 1 && letterCount < _settings.MinWordLength)
        {
            reason = "слово слишком короткое";
            return null;
        }

        var target = _detector.Pairs.GetAlternate(source);
        if (target == null)
            return null;

        var learned = string.Empty;
        if (!manual)
        {
            var verdict = _exceptions.CheckWord(text, source.Id, target.Id);
            var hasLearned = _learning.TryGetManualCorrection(text, source.Id, target.Id, out learned);
            if (verdict != WordVerdict.Allow &&
                !(hasLearned && verdict is WordVerdict.BlockedByLearning or WordVerdict.TooFrequent or WordVerdict.TooShort))
            {
                reason = $"пропущено политикой исключений ({verdict})";
                return null;
            }

            if (_learning.IsSuppressed(text.ToLowerInvariant(), source.Id, target.Id))
            {
                reason = "пропущено по истории отмен";
                return null;
            }
        }

        var deleteCount = text.Length + (terminatorAlreadyTyped && terminator.HasValue ? 1 : 0);
        if (learned.Length != 0)
        {
            reason = "подтверждённое ручное исправление";
            return new CorrectionPlan(CorrectionKind.LayoutSwitch, text, learned, deleteCount, terminator,
                source.Id, target.Id, 1.0, reason);
        }

        var contextLanguage = _wordLanguages.Latest == _wordLanguages.Previous ? _wordLanguages.Latest : null;
        var detection = _detector.Decide(text, source, target, early: false, contextLanguage: contextLanguage);
        reason = detection.Reason;

        // Терминатор печатается всегда, когда он известен: если клавиша была проглочена хуком,
        // её нужно напечатать самим, а если она уже дошла до окна — мы её удалили (DeleteCount + 1)
        // и обязаны вернуть, иначе пробел/перевод строки потеряется.
        var terminatorToType = terminator;

        if (detection.Convert)
        {
            var replacement = detection.ConvertedText;
            if (_postProcessors.TryCorrectConvertedWord(replacement, out var corrected))
            {
                replacement = corrected;
                reason += ", исправлена опечатка";
            }

            var plan = new CorrectionPlan(
                CorrectionKind.LayoutSwitch,
                text,
                replacement,
                deleteCount,
                terminatorToType,
                source.Id,
                target.Id,
                detection.Confidence,
                reason);

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

    private bool TryLearnRetype(WordToken replacement)
    {
        if (_charactersLeftToErase != 0 || _erasedWord is not { } original ||
            LayoutCatalog.TryGet(original.LayoutId) is not { } source ||
            _detector.Pairs.GetAlternate(source) is not { } target ||
            !string.Equals(target.Id, replacement.LayoutId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var mapped = LayoutConverter.Convert(original.Text, source, target);
        if (!string.Equals(mapped, replacement.Text, StringComparison.OrdinalIgnoreCase) &&
            (!_postProcessors.TryCorrectConvertedWord(mapped, out var corrected) ||
             !string.Equals(corrected, replacement.Text, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return _learning.RecordManualCorrection(original.Text, replacement.Text, source.Id, target.Id);
    }

    private void ClearRetype()
    {
        _erasedWord = null;
        _charactersLeftToErase = 0;
        _spacesAfterUnchangedWord = 0;
    }

    private void RememberWordLanguage(string text, char? terminator)
    {
        _wordLanguages = EndsLanguageContext(terminator) || text.EndsWith('.')
            ? default
            : (_wordLanguages.Latest, _detector.GetKnownLanguage(text));
    }

    private static bool EndsLanguageContext(char? terminator) => terminator is '\r' or '\n' or '\t' or '.' or '!' or '?';

    private void ClearLanguageContext()
    {
        _wordLanguages = default;
        _languagesBeforeLastWord = default;
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
