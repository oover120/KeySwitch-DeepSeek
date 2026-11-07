using System.Collections.Generic;

namespace KeySwitch.App.Localization;

/// <summary>
/// Строки, которые видит пользователь вне окон настроек: меню и подсказка в трее,
/// баллуны, диалоги и предупреждения.
/// Ключи пишутся в нижнем регистре через точку: "tray.settings", "tray.pause".
/// Каждый ключ обязан существовать и в Ru, и в En.
/// </summary>
internal static class RuntimeStrings
{
    internal static readonly Dictionary<string, string> Ru = new(System.StringComparer.Ordinal)
    {
        // Меню трея.
        ["tray.autoswitch.on"] = "Автопереключение включено",
        ["tray.autoswitch.off"] = "Автопереключение выключено",
        ["tray.exceptions"] = "Исключения…",
        ["tray.settings"] = "Настройки…",
        ["tray.autostart"] = "Запускать при входе в Windows",
        ["tray.runasadmin"] = "Запускать от администратора",
        ["tray.exit"] = "Выход",

        // Подсказка значка: "KeySwitch DeepSeek · {язык} · {статус}".
        ["tray.tooltip"] = "KeySwitch DeepSeek · {0} · {1}",
        ["tray.layout.ru"] = "Русский",
        ["tray.layout.en"] = "Английский",
        ["tray.layout.none"] = "—",
        ["tray.status.safe"] = "безопасный режим",
        ["tray.status.disabled"] = "автопереключение выключено",
        ["tray.status.manual"] = "ручной режим в этой программе",
        ["tray.status.enabled"] = "автопереключение включено",

        // Баллуны.
        ["balloon.autostart.title"] = "Автозапуск",
        ["balloon.autostart.failed"] =
            "Не удалось включить автозапуск. Попробуйте ещё раз или переустановите приложение.",
        ["balloon.admin.title"] = "Запуск от администратора",
        ["balloon.admin.elevation_required"] =
            "Для запуска с правами администратора приложение нужно один раз запустить " +
            "от имени администратора и включить галочку заново.",
        ["balloon.uipi.text"] =
            "Активное окно запущено с правами администратора. Из-за защиты Windows (UIPI) " +
            "автопереключение в нём не работает.",
        ["balloon.hook.failed"] =
            "Не удалось запустить перехват клавиатуры. Автопереключение недоступно, " +
            "настройки и трей работают.",

        // Диалоги запуска.
        ["startup.data_folder.failed"] = "Не удалось подготовить папку данных приложения:\n{0}",
        ["startup.failed"] = "KeySwitch DeepSeek не смог запуститься.\nПодробности записаны в журнал:\n{0}",
    };

    internal static readonly Dictionary<string, string> En = new(System.StringComparer.Ordinal)
    {
        // Tray menu.
        ["tray.autoswitch.on"] = "Autoswitch is on",
        ["tray.autoswitch.off"] = "Autoswitch is off",
        ["tray.exceptions"] = "Exceptions…",
        ["tray.settings"] = "Settings…",
        ["tray.autostart"] = "Start with Windows",
        ["tray.runasadmin"] = "Run as administrator",
        ["tray.exit"] = "Exit",

        // Tray tooltip: "KeySwitch DeepSeek · language · status".
        ["tray.tooltip"] = "KeySwitch DeepSeek · {0} · {1}",
        ["tray.layout.ru"] = "Russian",
        ["tray.layout.en"] = "English",
        ["tray.layout.none"] = "—",
        ["tray.status.safe"] = "safe mode",
        ["tray.status.disabled"] = "autoswitch is off",
        ["tray.status.manual"] = "manual mode in this app",
        ["tray.status.enabled"] = "autoswitch is on",

        // Balloons.
        ["balloon.autostart.title"] = "Autostart",
        ["balloon.autostart.failed"] =
            "Couldn't turn on autostart. Try again or reinstall the app.",
        ["balloon.admin.title"] = "Run as administrator",
        ["balloon.admin.elevation_required"] =
            "To run as administrator, start the app elevated once and tick the box again.",
        ["balloon.uipi.text"] =
            "The active window is running as administrator. Windows UIPI blocks " +
            "autoswitch in it.",
        ["balloon.hook.failed"] =
            "Couldn't start the keyboard hook. Autoswitch is unavailable; " +
            "settings and the tray still work.",

        // Startup dialogs.
        ["startup.data_folder.failed"] = "Couldn't prepare the app data folder:\n{0}",
        ["startup.failed"] = "KeySwitch DeepSeek couldn't start.\nDetails were written to the log:\n{0}",
    };
}
