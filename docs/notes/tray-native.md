# Трей на чистом Win32 (без WinForms и System.Drawing)

Задача: убрать из `src/KeySwitch.App` зависимости `System.Windows.Forms` и `System.Drawing`
и переписать трей на `Shell_NotifyIcon`, а иконки — на GDI-рендер в 32bpp DIB. Мотив —
память: при простое с WinForms процесс занимал 122 МБ рабочего набора при цели ТЗ ≤80 МБ.

Правки только в разрешённой зоне:

- `src/KeySwitch.App/KeySwitch.App.csproj` — удалены `<UseWindowsForms>true</UseWindowsForms>`
  и `NoWarn` с `WFAC010` (он был нужен только из-за WinForms).
- `src/KeySwitch.App/Tray/TrayIconFactory.cs` — переписан с GDI+/`System.Drawing` на GDI + DIB.
- `src/KeySwitch.App/Services/TrayService.cs` — переписан с WinForms `NotifyIcon` на P/Invoke
  `Shell_NotifyIconW`.
- `src/KeySwitch.App/Services/AppHost.cs` — `using System.Windows.Forms` заменён на
  `using KeySwitch.App.Tray`, `ToolTipIcon.Error` → `TrayBalloonIcon.Error` (строка ~219).

Итог: в проекте App не осталось ни одного `using System.Windows.Forms`/`System.Drawing`,
ни `UseWindowsForms`; упоминания этих имён есть только в комментариях.

## Как устроен трей

**Скрытое окно-владелец.** `RegisterClassExW` + `CreateWindowExW` с `WS_EX_TOOLWINDOW`,
стиль `0`, размер 1×1, позиция `(-32000, -32000)`, без `ShowWindow`. Делегат WndProc
хранится в поле (`_wndProc`/`_wndProcPtr`), иначе GC соберёт его и окно начнёт падать.

Окно **не** message-only (`HWND_MESSAGE`), хотя это первое, что приходит в голову: у
message-only окна нет Z-порядка и его нельзя сделать foreground-окном. Без foreground
`TrackPopupMenuEx` показывает меню, но оно не закрывается по клику вне меню, а пунктирная
рамка остаётся «прилипшей». Поэтому используется обычное top-level окно, спрятанное за
пределами экрана и скрытое из панели задач через `WS_EX_TOOLWINDOW` — это стандартный
приём для tray-приложений.

**Сообщения.** `RegisterWindowMessageW("KeySwitch.TrayIcon")` — а не `WM_APP + 1` — потому
что tray-сообщение должно быть уникальным среди процессов: `WM_APP + N` может совпасть с
сообщением другого окна, если окна-владельцы окажутся в одном thread/очереди. Имя
регистрируется в системе один раз и одинаково для всех процессов; на случай сбоя
`RegisterWindowMessage` оставлен fallback на `WM_APP + 1` с WARN в лог.
Дополнительно регистрируется `TaskbarCreated`: после перезапуска Explorer иконку нужно
добавить заново (`NIM_ADD`), оболочка при рестарте забывает значки.

Код уведомления берётся из младшего слова `lParam` (`NOTIFYICON_VERSION_4`):
`NIN_SELECT`/`NIN_KEYSELECT`/`WM_LBUTTONUP`/`WM_LBUTTONDBLCLK` → открыть настройки,
`WM_CONTEXTMENU`/`WM_RBUTTONUP` → контекстное меню. `WM_DPICHANGED`/`WM_SETTINGCHANGE`
помечают иконку к перерисовке.

**Меню.** Каждый показ: `CreatePopupMenu` + `AppendMenuW` (`MF_STRING`, `MF_CHECKED`,
`MF_SEPARATOR`, `MF_GRAYED`), галочки — по текущим настройкам. Затем `SetForegroundWindow`
(иначе меню не закроется по клику вне), `TrackPopupMenuEx` с
`TPM_RETURNCMD | TPM_RIGHTBUTTON`, после — `PostMessage(WM_NULL)` (документированный
трюк, чтобы меню корректно ушло), `DestroyMenu`. Команды разбираются в `switch` по id и
выполняются отложенно через `Dispatcher.BeginInvoke`: модальные окна («Настройки»,
«Исключения») нельзя открывать внутри обработки меню, пока `TrackPopupMenuEx` ещё не
вернулся.

Состав и порядок пунктов сохранены: Автопереключение (галочка), Исключения…, Настройки…,
Запускать при входе в Windows (галочка), Запускать от администратора (галочка, `MF_GRAYED`
когда автозапуск выключен), Выход.

**Иконки.** `TrayIconFactory` рисует в `byte[]` (straight BGRA, top-down):

- скруглённый квадрат — субпиксельным покрытием 4×4, радиус `0.24 * size`;
- буквы `EN`/`RU` — GDI в отдельной 32bpp DIB-секции: `CreateFontW` (Segoe UI Bold,
  `ANTIALIASED_QUALITY`), `GetTextExtentPoint32W` для подгонки кегля, `DrawTextW`
  (`DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_NOCLIP`); покрытие глифа =
  максимум каналов маски, поэтому цвет букв не зависит от цвета фона;
- маска рисуется с 4× суперсэмплингом и усредняется 4×4 с лёгкой гамма-коррекцией (0.85):
  без этого на 16 px штрихи «худели» и буквы выглядели бледнее прежнего WPF-рендера;
- перечёркивание (выключенный режим) — полоса по субпикселям с круглыми концами,
  толщина `max(1.5, 0.09 * size)`;
- жёлтая точка «только ручная конвертация» — диск + кольцо в правом верхнем углу,
  радиус `0.135 * size`; текст в этом режиме сдвинут влево-вниз.

Из пикселей делается HICON: `CreateDIBSection` (32bpp, `BI_RGB`, bottom-up с переворотом
строк) + `CreateBitmap` AND-маски (1bpp, все нули — прозрачность задаёт альфа) +
`CreateIconIndirect`; обе маски удаляются `DeleteObject`, HICON — `DestroyIcon`.
Размер берётся из `GetSystemMetrics(SM_CXSMICON)` (это уже масштабированный под DPI размер;
на тестовой машине 250 % → 40 px), диапазон 16…64.

HICON-ы кешируются по ключу `layout|enabled|manualOnly`, кеш пересоздаётся при смене
размера иконки (переезд окна на монитор с другим DPI). Освобождение кеша — целиком в
`Dispose`, а не при каждой замене: оболочка может ещё держать прежний HICON.

**Баллун.** `NIF_INFO` + `szInfo`/`szInfoTitle`/`dwInfoFlags` (`NIIF_INFO`/`NIIF_WARNING`/
`NIIF_ERROR`), `uTimeoutOrVersion = 4000`. Публичный `ShowBalloon(..., TrayBalloonIcon)`,
enum живёт в `KeySwitch.App.Tray`.

**Таймер и потоки.** `SetTimer` на скрытом окне, `WM_TIMER` раз в 500 мс → опрос состояния
(`ForegroundTracker`, `AppSettings.Enabled`, `AutoSwitchService.IsStarted`,
`ExceptionStore.GetMode`). Все вызовы `Shell_NotifyIcon` идут из UI-потока (timer-сообщение
и `Dispatcher.BeginInvoke`), отдельных потоков нет.

**Тултип.** `szTip` — 128 символов (`NOTIFYICONDATAW` v4), строка обрезается до 127,
формат прежний: `KeySwitch · {RU|EN|—} · {статус}`.

**Dispose.** `NIM_DELETE` → `ClearIconCache` (`DestroyIcon` на каждый) → `KillTimer` →
`DestroyWindow` → `UnregisterClassW`.

## Замеры

Методика: `dotnet build KeySwitch.sln -c Release -v q`, тесты, `publish.ps1 -SkipTests`,
запуск `artifacts\publish\KeySwitch.exe`, `Get-Process KeySwitch` через 8 и 30 секунд.

| Вариант трея | exe | WS (8 с) | WS (30 с) | Private (8 с) |
| --- | --- | --- | --- | --- |
| WinForms `NotifyIcon` (было) | 150 826 530 B | 122 МБ | — | 115.6 МБ |
| WPF `HwndSource` + WPF-меню + RTB-иконки (промежуточный) | 150 834 722 B | 149.2 МБ | — | 151 МБ |
| **Win32 `Shell_NotifyIcon` + GDI-иконки (итог)** | 150 838 818 B | **109.0 МБ** | **109.1 МБ** | 113.5 МБ |

Размер exe почти не изменился: сборка self-contained single-file без trimming тащит весь
desktop-runtime, и 59 МБ WinForms в тривиальном приложении — это в основном DLL-ы, которые
в self-contained бандле и так лежат на диске; в рабочем наборе WinForms экономил ~13–18 МБ.

Повторные замеры на чистой машине дают 107–117 МБ WS в зависимости от того, какие страницы
ещё в общем кеше; роста после 30 секунд простоя нет (109.0 → 109.1).

Отдельно: после первого открытия окна настроек WS вырастает до ~195 МБ (WPF-окно
инициализирует milcore/Direct3D) и после закрытия остаётся ~190 МБ — это цена WPF-UI,
а не трея; в той же сессии WinForms-вариант вёл себя так же.

## Проверено автоматически

- `dotnet build KeySwitch.sln -c Release -v q` — 0 ошибок, 0 предупреждений.
- `dotnet test src\KeySwitch.Tests -c Release` — 352/352.
- `grep` по `src\KeySwitch.App`: нет `System.Windows.Forms`, `System.Drawing`,
  `UseWindowsForms`, `ToolTipIcon` (кроме комментариев).
- Лог `%APPDATA%\KeySwitch\logs\keyswitch.log`: `INFO Трей создан (Shell_NotifyIcon).`,
  исключений при старте нет, автопереключение стартует.
- `Shell_NotifyIconGetRect` для нашего HICON возвращает `S_OK` и валидный rect — значок
  реально зарегистрирован оболочкой.
- Окно меню (`#32768`) открывается по `WM_CONTEXTMENU` (visible), закрывается по
  `WM_CANCELMODE`.
- `WM_...` `NIN_SELECT` открывает окно «KeySwitch — настройки», `WM_CLOSE` его закрывает.
- Иконки всех состояний (16/24/32/48) отрендерены в PNG и просмотрены; HICON→PNG
  round-trip совпадает с исходным BGRA (alpha-mismatch = 0).

## Не проверено вручную

Клики мышью по значку в трее в сессии агента не воспроизвести, поэтому на ручную проверку
остаётся:

1. Правый клик по значку — меню появляется над курсором у края экрана, галочки
   соответствуют настройкам, меню закрывается кликом вне, пункты работают (в т.ч.
   «Запускать от администратора» перезапускает приложение с UAC).
2. Двойной левый клик и одиночный левый клик — открывают настройки.
3. Баллуны: предупреждение о повышенных правах окна и ошибка старта хука.
4. Поведение значка после перезапуска Explorer (пересоздаётся ли иконка).
5. Внешний вид значка в трее при 100 %, 150 %, 200 % масштабе (на машине агента 250 % —
   иконка 40 px, рядом с системными смотрится штатно).
6. Перетаскивание окна настроек между мониторами с разным DPI — иконка должна
   перерисоваться под новый `SM_CXSMICON`.
