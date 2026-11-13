namespace KeySwitch.Core.Updates;

/// <summary>
/// Результат разбора ответа GitHub о последнем релизе.
/// <see cref="HasUpdate"/> истинно только когда опубликованная версия новее текущей.
/// </summary>
public sealed record UpdateInfo(
    bool HasUpdate,
    string? LatestVersion,
    string? DownloadUrl,
    string? ReleaseUrl,
    string? Notes,
    string? Error)
{
    /// <summary>Проверка не удалась: сети нет, ответ не разобрался, таймаут.</summary>
    public static UpdateInfo Failure(string error) => new(false, null, null, null, null, error);
}
