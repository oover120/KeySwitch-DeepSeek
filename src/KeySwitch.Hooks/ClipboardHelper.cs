using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeySwitch.Hooks;

/// <summary>
/// Буфер обмена: снимок/восстановление всех форматов, которые можно сериализовать
/// (текст, список файлов, изображение DIB, HTML/RTF), и простые обёртки над текстом.
/// <para>
/// Вызовы идут на выделенном STA-потоке с прокачкой сообщений: конвертация раскладки
/// происходит на потоке хука, и блокировать его ожиданием буфера нельзя.
/// </para>
/// <para>
/// Реализация — прямой Win32 (OpenClipboard/SetClipboardData), а не WPF
/// <c>System.Windows.Clipboard</c>: WPF-обёртка в этом окружении бросает
/// CLIPBRD_E_CANT_OPEN даже на собственном STA-потоке с OleInitialize, тогда как
/// сырые вызовы Win32 работают стабильно.
/// </para>
/// </summary>
public sealed class ClipboardHelper
{
    private const int OperationTimeoutMs = 3000;

    /// <summary>Буфер обмена может быть занят другим процессом — делаем несколько попыток.</summary>
    private const int OpenAttempts = 10;

    private const int OpenRetryDelayMs = 30;

    /// <summary>Верхний предел на один формат: защита от гигантских снимков в памяти/JSON.</summary>
    private const int MaxItemBytes = 8 * 1024 * 1024;

    private const string NameHtml = "HTML Format";
    private const string NameRtf = "Rich Text Format";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private BlockingCollection<WorkItem>? _queue;
    private Thread? _thread;

    /// <summary>
    /// Снимок буфера обмена в JSON: все форматы, которые можно сериализовать
    /// (текст, список файлов, изображение, HTML/RTF). null, если буфер пуст или недоступен.
    /// </summary>
    public string? CaptureAll() => Run(CaptureCore);

    /// <summary>Восстанавливает буфер из снимка <see cref="CaptureAll"/>; ошибки глотаются.</summary>
    public void RestoreAll(string? snapshot) => Run(() =>
    {
        RestoreCore(snapshot);
        return true;
    });

    /// <summary>Текст из буфера обмена или null, если текста нет.</summary>
    public string? GetText() => Run(GetTextCore);

    /// <summary>Записывает текст в буфер обмена; пустая строка очищает буфер.</summary>
    public void SetText(string text) => Run(() =>
    {
        SetTextCore(text);
        return true;
    });

    // ------------------------------------------------------------ STA-поток

    private T? Run<T>(Func<T?> work)
    {
        EnsureThread();

        var item = new WorkItem(() => work());
        BlockingCollection<WorkItem>? queue = _queue;
        if (queue is null || !TryEnqueue(queue, item))
            return default;

        if (!item.Done.Wait(OperationTimeoutMs))
            return default;

        if (item.Error is not null)
            return default;

        return item.Result is T typed ? typed : default;
    }

    private static bool TryEnqueue(BlockingCollection<WorkItem> queue, WorkItem item)
    {
        try
        {
            // Ограниченная очередь: если STA-поток завис на буфере, не копим запросы.
            return queue.TryAdd(item);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void EnsureThread()
    {
        lock (_gate)
        {
            if (_thread is { IsAlive: true })
                return;

            var queue = new BlockingCollection<WorkItem>(new ConcurrentQueue<WorkItem>(), 8);
            _queue = queue;

            var thread = new Thread(() => ThreadLoop(queue))
            {
                IsBackground = true,
                Name = "KeySwitch.Clipboard"
            };
            thread.SetApartmentState(ApartmentState.STA);
            _thread = thread;
            thread.Start();
        }
    }

    private static void ThreadLoop(BlockingCollection<WorkItem> queue)
    {
        // OLE-инициализация нужна приложениям, которые читают наши данные через IDataObject;
        // сам буфер обмена работает напрямую через Win32. RPC_E_CHANGED_MODE безвреден.
        _ = NativeMethods.OleInitialize(IntPtr.Zero);

        // Создаём очередь сообщений потока до первого запроса к буферу.
        NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);

        try
        {
            while (true)
            {
                if (queue.TryTake(out WorkItem? item, 20))
                {
                    Execute(item);
                    continue;
                }

                PumpMessages();
            }
        }
        finally
        {
            NativeMethods.OleUninitialize();
        }
    }

    /// <summary>Прокачка очереди сообщений STA-потока — её требуют OLE-вызовы.</summary>
    private static void PumpMessages()
    {
        while (NativeMethods.PeekMessage(out var msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    private static void Execute(WorkItem item)
    {
        try
        {
            item.Result = item.Work();
        }
        catch (Exception ex)
        {
            item.Error = ex;
        }
        finally
        {
            item.Done.Set();
        }
    }

    // ------------------------------------------------------------ снимок

    private static string? CaptureCore()
    {
        try
        {
            if (!OpenWithRetry())
                return null;

            try
            {
                var snapshot = new SnapshotModel { Version = 1 };

                // Текст: CF_UNICODETEXT покрывает и CF_TEXT — Windows конвертирует сам.
                if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
                {
                    string? text = ReadString(NativeMethods.CF_UNICODETEXT);
                    if (text is not null)
                    {
                        snapshot.Items.Add(new ItemModel
                        {
                            Format = NativeMethods.CF_UNICODETEXT,
                            Kind = KindText,
                            Text = text
                        });
                    }
                }

                // Список файлов.
                if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP))
                {
                    string[]? files = ReadFiles();
                    if (files is { Length: > 0 })
                    {
                        snapshot.Items.Add(new ItemModel
                        {
                            Format = NativeMethods.CF_HDROP,
                            Kind = KindFiles,
                            Values = files
                        });
                    }
                }

                // Изображение: DIBV5 информативнее, но принимаем и DIB.
                if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIBV5))
                {
                    AddBytesIfReasonable(snapshot, NativeMethods.CF_DIBV5, name: null, KindDib);
                }

                if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB))
                {
                    AddBytesIfReasonable(snapshot, NativeMethods.CF_DIB, name: null, KindDib);
                }

                // HTML и RTF — зарегистрированные форматы; сохраняем как есть.
                AddBytesIfReasonable(snapshot, Register(NameHtml), NameHtml, KindBytes);
                AddBytesIfReasonable(snapshot, Register(NameRtf), NameRtf, KindBytes);

                return snapshot.Items.Count == 0
                    ? null
                    : JsonSerializer.Serialize(snapshot, JsonOptions);
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void AddBytesIfReasonable(SnapshotModel snapshot, uint format, string? name, string kind)
    {
        if (format == 0 || !NativeMethods.IsClipboardFormatAvailable(format))
            return;

        byte[]? bytes = ReadBytes(format);
        if (bytes is not { Length: > 0 } || bytes.Length > MaxItemBytes)
            return;

        snapshot.Items.Add(new ItemModel
        {
            Format = format,
            Name = name,
            Kind = kind,
            Base64 = Convert.ToBase64String(bytes)
        });
    }

    // ------------------------------------------------------------ восстановление

    private static void RestoreCore(string? snapshot)
    {
        if (string.IsNullOrEmpty(snapshot))
            return;

        SnapshotModel? model;
        try
        {
            model = JsonSerializer.Deserialize<SnapshotModel>(snapshot, JsonOptions);
        }
        catch (Exception)
        {
            return;
        }

        if (model?.Items is not { Count: > 0 })
            return;

        try
        {
            if (!OpenWithRetry())
                return;

            try
            {
                if (!NativeMethods.EmptyClipboard())
                    return;

                foreach (ItemModel item in model.Items)
                {
                    try
                    {
                        RestoreItem(item);
                    }
                    catch (Exception)
                    {
                        // Восстанавливаем «что удалось», как и требует контракт.
                    }
                }
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        catch (Exception)
        {
            // Буфер занят другим процессом — не роняем приложение.
        }
    }

    private static void RestoreItem(ItemModel item)
    {
        switch (item.Kind)
        {
            case KindText when item.Text is not null:
                SetData(NativeMethods.CF_UNICODETEXT, AllocGlobalString(item.Text));
                break;

            case KindFiles when item.Values is { Length: > 0 }:
                SetData(NativeMethods.CF_HDROP, AllocGlobalBytes(BuildDropFiles(item.Values)));
                break;

            case KindDib when item.Base64 is not null && item.Format is NativeMethods.CF_DIB or NativeMethods.CF_DIBV5:
                SetData(item.Format, AllocGlobalBytes(Convert.FromBase64String(item.Base64)));
                break;

            case KindBytes when item.Base64 is not null:
                uint format = item.Format;
                if (format == 0 && !string.IsNullOrEmpty(item.Name))
                    format = Register(item.Name);
                if (format == 0)
                    break;
                SetData(format, AllocGlobalBytes(Convert.FromBase64String(item.Base64)));
                break;
        }
    }

    // ------------------------------------------------------------ текст

    private static string? GetTextCore()
    {
        try
        {
            if (!OpenWithRetry())
                return null;

            try
            {
                return NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT)
                    ? ReadString(NativeMethods.CF_UNICODETEXT)
                    : null;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void SetTextCore(string text)
    {
        try
        {
            if (!OpenWithRetry())
                return;

            try
            {
                if (!NativeMethods.EmptyClipboard())
                    return;

                if (!string.IsNullOrEmpty(text))
                    SetData(NativeMethods.CF_UNICODETEXT, AllocGlobalString(text));
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        catch (Exception)
        {
            // Буфер занят — не роняем приложение из-за этого.
        }
    }

    // ------------------------------------------------------------ чтение Win32

    /// <summary>Буфер обмена может быть кратко занят другим процессом — ждём до ~300 мс.</summary>
    private static bool OpenWithRetry()
    {
        for (int attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
                return true;

            Thread.Sleep(OpenRetryDelayMs);
        }

        return false;
    }

    private static string? ReadString(uint format)
    {
        IntPtr handle = NativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero)
            return null;

        IntPtr ptr = NativeMethods.GlobalLock(handle);
        if (ptr == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    private static byte[]? ReadBytes(uint format)
    {
        IntPtr handle = NativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero)
            return null;

        UIntPtr size = NativeMethods.GlobalSize(handle);
        long length = size.ToUInt64() > int.MaxValue ? 0 : (long)size.ToUInt64();
        if (length <= 0)
            return null;

        IntPtr ptr = NativeMethods.GlobalLock(handle);
        if (ptr == IntPtr.Zero)
            return null;

        try
        {
            var bytes = new byte[length];
            Marshal.Copy(ptr, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    private static string[]? ReadFiles()
    {
        // CF_HDROP — описатель списка файлов; памятью владеет буфер, освобождать не нужно,
        // DragQueryFile сам снимает блокировку на время чтения.
        IntPtr handle = NativeMethods.GetClipboardData(NativeMethods.CF_HDROP);
        if (handle == IntPtr.Zero)
            return null;

        uint count = NativeMethods.DragQueryFile(handle, uint.MaxValue, null, 0);
        if (count == 0 || count > 10_000)
            return null;

        var files = new List<string>((int)count);
        for (uint index = 0; index < count; index++)
        {
            uint length = NativeMethods.DragQueryFile(handle, index, null, 0);
            if (length == 0)
                continue;

            var buffer = new StringBuilder((int)length + 1);
            if (NativeMethods.DragQueryFile(handle, index, buffer, (uint)buffer.Capacity) > 0)
                files.Add(buffer.ToString());
        }

        return files.Count == 0 ? null : files.ToArray();
    }

    // ------------------------------------------------------------ запись Win32

    /// <summary>Отдаёт данные буферу; при успехе памятью владеет система, иначе освобождаем.</summary>
    private static void SetData(uint format, IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return;

        if (NativeMethods.SetClipboardData(format, handle) != IntPtr.Zero)
            return;

        NativeMethods.GlobalFree(handle);
    }

    private static IntPtr AllocGlobalString(string value)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(value + '\0');
        return AllocGlobalBytes(bytes);
    }

    private static IntPtr AllocGlobalBytes(byte[] bytes)
    {
        IntPtr handle = NativeMethods.GlobalAlloc(
            NativeMethods.GMEM_MOVEABLE, (UIntPtr)(uint)Math.Max(1, bytes.Length));
        if (handle == IntPtr.Zero)
            return IntPtr.Zero;

        IntPtr ptr = NativeMethods.GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
            return IntPtr.Zero;
        }

        try
        {
            if (bytes.Length > 0)
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }

        return handle;
    }

    /// <summary>Список путей в формате CF_HDROP: DROPFILES + пути UTF-16 с двойным нулём.</summary>
    private static byte[] BuildDropFiles(IReadOnlyList<string> files)
    {
        int headerSize = Marshal.SizeOf<NativeMethods.DROPFILES>();

        using var stream = new MemoryStream();
        var header = new byte[headerSize];
        BitConverter.GetBytes((uint)headerSize).CopyTo(header, 0);
        // pt = (0,0), fNC = 0; fWide = 1 — строки в UTF-16.
        BitConverter.GetBytes(1).CopyTo(header, headerSize - sizeof(int));
        stream.Write(header, 0, header.Length);

        foreach (string file in files)
        {
            byte[] path = Encoding.Unicode.GetBytes(file);
            stream.Write(path, 0, path.Length);
            stream.WriteByte(0);
            stream.WriteByte(0);
        }

        // Финальный пустой путь завершает список.
        stream.WriteByte(0);
        stream.WriteByte(0);

        return stream.ToArray();
    }

    /// <summary>Регистрирует формат, если он ещё не зарегистрирован системой (0 — ошибка).</summary>
    private static uint Register(string name)
    {
        try
        {
            return NativeMethods.RegisterClipboardFormat(name);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // ------------------------------------------------------------ типы

    private const string KindText = "text";
    private const string KindFiles = "files";
    private const string KindDib = "dib";
    private const string KindBytes = "bytes";

    private sealed class SnapshotModel
    {
        public int Version { get; set; }

        public List<ItemModel> Items { get; set; } = [];
    }

    private sealed class ItemModel
    {
        public uint Format { get; set; }

        public string? Name { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string? Text { get; set; }

        public string[]? Values { get; set; }

        public string? Base64 { get; set; }
    }

    private sealed class WorkItem
    {
        public WorkItem(Func<object?> work)
        {
            Work = work;
        }

        public Func<object?> Work { get; }

        public ManualResetEventSlim Done { get; } = new(false);

        public object? Result { get; set; }

        public Exception? Error { get; set; }
    }
}
