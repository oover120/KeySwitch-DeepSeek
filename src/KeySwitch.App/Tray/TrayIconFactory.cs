using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KeySwitch.App.Tray;

/// <summary>
/// Иконка трея, нарисованная чистым Win32/GDI и отданная оболочке как HICON.
///
/// Ни System.Drawing, ни WPF здесь не участвуют: скруглённый квадрат, перечёркивание
/// и жёлтый бейдж считаются по пикселям (4×4 субпикселя на пиксель — так края остаются
/// гладкими на 16 px), а надпись «EN»/«RU» растеризует GDI в отдельную 32-bpp DIB-секцию
/// шрифтом Segoe UI Bold: яркость полученной маски становится альфой покрытия глифов,
/// поэтому сглаживание букв получается бесплатно.
///
/// Размер берётся из SM_CXSMICON (16 px при 100 %, 24 при 150 %, 32 при 200 %), иконка
/// рисуется ровно под текущий DPI — оболочке не приходится растягивать картинку.
/// </summary>
public static class TrayIconFactory
{
    private const byte EnR = 0x2F, EnG = 0x6F, EnB = 0xED;
    private const byte RuR = 0xD9, RuG = 0x53, RuB = 0x4F;
    private const byte NeutralR = 0x5C, NeutralG = 0x5C, NeutralB = 0x5C;
    private const byte DisabledR = 0x8A, DisabledG = 0x8A, DisabledB = 0x8A;
    private const byte BadgeR = 0xFF, BadgeG = 0xC8, BadgeB = 0x3D;
    private const byte BadgeBorderR = 0x5A, BadgeBorderG = 0x3C, BadgeBorderB = 0x00;
    private const int BadgeBorderAlpha = 0xF0;
    private const byte StrikeR = 0xF2, StrikeG = 0xF2, StrikeB = 0xF2;

    /// <summary>Сколько подвыборок на пиксель при расчёте покрытия фигур.</summary>
    private const int Subsamples = 4;

    /// <summary>SM_CXSMICON — размер мелкого значка в пикселях для текущего DPI.</summary>
    private const int SM_CXSMICON = 49;

    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    private const uint FW_BOLD = 700;
    private const uint DEFAULT_CHARSET = 1;
    private const uint OUT_TT_PRECIS = 4;
    private const uint CLIP_DEFAULT_PRECIS = 0;
    private const uint ANTIALIASED_QUALITY = 4;
    private const uint DEFAULT_PITCH = 0;
    private const uint FF_DONTCARE = 0;

    private const int TRANSPARENT = 1;
    private const uint DT_CENTER = 0x00000001;
    private const uint DT_VCENTER = 0x00000004;
    private const uint DT_SINGLELINE = 0x00000020;
    private const uint DT_NOPREFIX = 0x00000800;
    private const uint DT_NOCLIP = 0x00000100;

    private const string FontFace = "Segoe UI";

    /// <summary>
    /// Создаёт иконку трея. Возвращённый HICON принадлежит вызывающему коду
    /// и должен быть освобождён через <c>DestroyIcon</c>.
    /// </summary>
    /// <param name="layoutId">Идентификатор раскладки: "ru"/"en" (регистр и хвост вроде "ru-RU" не важны).</param>
    /// <param name="enabled">Включён ли автопереключатель (false — серая иконка с перечёркиванием).</param>
    /// <param name="manualOnly">Ручной режим — жёлтая точка в правом верхнем углу.</param>
    public static IntPtr Create(string layoutId, bool enabled, bool manualOnly)
    {
        (string code, byte r, byte g, byte b) = ResolveLayout(layoutId);
        int size = GetIconSize();

        byte[] pixels = Render(size, code, r, g, b, enabled, manualOnly);
        return CreateHIconFromBgra(pixels, size);
    }

    /// <summary>Иконка приложения/трея по умолчанию (EN, автопереключатель включён).</summary>
    public static IntPtr CreateDefault() => Create("en", enabled: true, manualOnly: false);

    /// <summary>
    /// Размер значка под текущий DPI. Приложение per-monitor-v2, поэтому SM_CXSMICON
    /// возвращает уже масштабированное значение. Диапазон ограничен 16…64 px.
    /// </summary>
    internal static int GetIconSize()
    {
        int size = GetSystemMetrics(SM_CXSMICON);
        return size <= 0 ? 16 : Math.Clamp(size, 16, 64);
    }

    private static (string Code, byte R, byte G, byte B) ResolveLayout(string layoutId)
    {
        if (!string.IsNullOrWhiteSpace(layoutId))
        {
            string id = layoutId.Trim().ToLowerInvariant();

            if (id.StartsWith("ru", StringComparison.Ordinal))
            {
                return ("RU", RuR, RuG, RuB);
            }

            if (id.StartsWith("en", StringComparison.Ordinal))
            {
                return ("EN", EnR, EnG, EnB);
            }
        }

        return ("?", NeutralR, NeutralG, NeutralB);
    }

    /// <summary>Рисует иконку и возвращает straight (не premultiplied) BGRA-пиксели, top-down.</summary>
    private static byte[] Render(int size, string code, byte r, byte g, byte b, bool enabled, bool manualOnly)
    {
        var pixels = new byte[size * size * 4];

        byte fillR = enabled ? r : DisabledR;
        byte fillG = enabled ? g : DisabledG;
        byte fillB = enabled ? b : DisabledB;

        // Скруглённый квадрат: покрытие считаем субпикселями, чтобы 16 px не «мылились».
        double inset = size <= 16 ? 0.5 : 1.0;
        double radius = size * 0.24;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double coverage = RoundedRectCoverage(x, y, inset, radius, size);
                if (coverage <= 0.0)
                {
                    continue;
                }

                byte alpha = ToByte(coverage);
                BlendOver(
                    pixels,
                    ((y * size) + x) * 4,
                    fillB,
                    fillG,
                    fillR,
                    alpha);
            }
        }

        if (enabled)
        {
            DrawCode(pixels, size, code, manualOnly);
        }

        if (!enabled)
        {
            DrawStrike(pixels, size);
        }

        if (manualOnly)
        {
            DrawManualBadge(pixels, size);
        }

        return pixels;
    }

    /// <summary>Покрытие (0…1) пикселя скруглённым квадратом по субпикселям.</summary>
    private static double RoundedRectCoverage(int x, int y, double inset, double radius, int size)
    {
        double left = inset;
        double top = inset;
        double right = size - inset;
        double bottom = size - inset;
        double innerLeft = left + radius;
        double innerRight = right - radius;
        double innerTop = top + radius;
        double innerBottom = bottom - radius;
        double radiusSquared = radius * radius;

        int hits = 0;

        for (int sy = 0; sy < Subsamples; sy++)
        {
            double py = y + ((sy + 0.5) / Subsamples);
            if (py < top || py > bottom)
            {
                continue;
            }

            for (int sx = 0; sx < Subsamples; sx++)
            {
                double px = x + ((sx + 0.5) / Subsamples);
                if (px < left || px > right)
                {
                    continue;
                }

                double cx = Math.Clamp(px, innerLeft, innerRight);
                double cy = Math.Clamp(py, innerTop, innerBottom);
                double dx = px - cx;
                double dy = py - cy;

                if ((dx * dx) + (dy * dy) <= radiusSquared)
                {
                    hits++;
                }
            }
        }

        return hits / (double)(Subsamples * Subsamples);
    }

    /// <summary>
    /// Рисует надпись через GDI в отдельную DIB-секцию и переносит её в иконку как альфу:
    /// яркость пикселя маски (белое по чёрному) = покрытие глифа.
    /// </summary>
    private static void DrawCode(byte[] pixels, int size, string code, bool manualOnly)
    {
        // Маску глифов растеризуем вчетверо крупнее и усредняем по 4×4: так буквы получаются
        // такими же гладкими, как при суперсэмплинге в WPF, и не «худеют» на 16 px.
        const int factor = 4;
        int maskSize = size * factor;

        // При жёлтой точке надпись сдвигаем влево-вниз и чуть уменьшаем: иначе на 16 px
        // бейдж наезжает на буквы.
        double maxWidth = maskSize * (manualOnly ? 0.74 : 0.86);
        // На мелких размерах кегль чуть больше: у GDI после усреднения штрихи «худеют»,
        // и буквы выглядят бледнее, чем рисовал WPF.
        double heightRatio = manualOnly
            ? (size <= 20 ? 0.40 : 0.44)
            : (size <= 20 ? 0.54 : 0.50);
        int shiftX = manualOnly ? (int)Math.Round(-maskSize * 0.10) : 0;
        int shiftY = manualOnly ? (int)Math.Round(maskSize * 0.02) : 0;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return;
        }

        IntPtr memoryDc = CreateCompatibleDC(screenDc);
        ReleaseDC(IntPtr.Zero, screenDc);
        if (memoryDc == IntPtr.Zero)
        {
            return;
        }

        var maskInfo = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = maskSize,
                biHeight = -maskSize, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
                biSizeImage = (uint)(maskSize * maskSize * 4),
            },
        };

        IntPtr maskBitmap = IntPtr.Zero;
        IntPtr previousBitmap = IntPtr.Zero;
        IntPtr font = IntPtr.Zero;
        IntPtr previousFont = IntPtr.Zero;

        try
        {
            maskBitmap = CreateDIBSection(IntPtr.Zero, ref maskInfo, DIB_RGB_COLORS, out IntPtr maskBits, IntPtr.Zero, 0);
            if (maskBitmap == IntPtr.Zero || maskBits == IntPtr.Zero)
            {
                return;
            }

            // DIB-секция создаётся нулевой — фон маски уже чёрный.
            previousBitmap = SelectObject(memoryDc, maskBitmap);
            SetBkMode(memoryDc, TRANSPARENT);
            SetTextColor(memoryDc, 0x00FFFFFF);

            // Подбираем кегль: «EN» шире «RU», на 16 px это критично.
            int height = Math.Max(5, (int)Math.Round(maskSize * heightRatio));
            int textWidth;
            while (true)
            {
                if (font != IntPtr.Zero)
                {
                    SelectObject(memoryDc, previousFont);
                    DeleteObject(font);
                }

                font = CreateFontW(
                    -height,
                    0,
                    0,
                    0,
                    FW_BOLD,
                    0,
                    0,
                    0,
                    DEFAULT_CHARSET,
                    OUT_TT_PRECIS,
                    CLIP_DEFAULT_PRECIS,
                    ANTIALIASED_QUALITY,
                    DEFAULT_PITCH | FF_DONTCARE,
                    FontFace);

                if (font == IntPtr.Zero)
                {
                    return;
                }

                previousFont = SelectObject(memoryDc, font);
                if (!GetTextExtentPoint32W(memoryDc, code, code.Length, out SIZE extent))
                {
                    return;
                }

                textWidth = extent.cx;
                if (textWidth <= maxWidth || height <= 5)
                {
                    break;
                }

                height = Math.Max(5, (int)Math.Floor(height * 0.94));
            }

            var rect = new RECT
            {
                left = shiftX,
                top = shiftY,
                right = maskSize + shiftX,
                bottom = maskSize + shiftY,
            };

            DrawTextW(memoryDc, code, code.Length, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_NOCLIP);

            int maskStride = maskSize * 4;
            var maskRow = new byte[maskStride];
            var coverage = new int[size * size];

            for (int y = 0; y < maskSize; y++)
            {
                Marshal.Copy(IntPtr.Add(maskBits, y * maskStride), maskRow, 0, maskStride);

                for (int x = 0; x < maskSize; x++)
                {
                    int i = x * 4;
                    // GDI пишет белым по чёрному; берём максимум каналов как покрытие.
                    int value = Math.Max(maskRow[i], Math.Max(maskRow[i + 1], maskRow[i + 2]));
                    if (value == 0)
                    {
                        continue;
                    }

                    coverage[((y / factor) * size) + (x / factor)] += value;
                }
            }

            int samples = factor * factor;

            for (int index = 0; index < coverage.Length; index++)
            {
                int value = coverage[index];
                if (value == 0)
                {
                    continue;
                }

                // Лёгкая гамма-коррекция: усреднённое покрытие даёт слишком бледные
                // полутона, из-за чего на 16 px буквы выглядят тоньше исходных.
                int averaged = (value + (samples / 2)) / samples;
                int alpha = (int)Math.Round(255.0 * Math.Pow(averaged / 255.0, 0.85));
                if (alpha <= 0)
                {
                    continue;
                }

                BlendOver(pixels, index * 4, 0xFF, 0xFF, 0xFF, (byte)Math.Min(255, alpha));
            }
        }
        finally
        {
            if (font != IntPtr.Zero)
            {
                if (previousFont != IntPtr.Zero)
                {
                    SelectObject(memoryDc, previousFont);
                }

                DeleteObject(font);
            }

            if (previousBitmap != IntPtr.Zero)
            {
                SelectObject(memoryDc, previousBitmap);
            }

            if (maskBitmap != IntPtr.Zero)
            {
                DeleteObject(maskBitmap);
            }

            DeleteDC(memoryDc);
        }
    }

    private static void DrawStrike(byte[] pixels, int size)
    {
        double halfWidth = Math.Max(1.5, size * 0.09) / 2.0;
        double x1 = size * 0.26, y1 = size * 0.74;
        double x2 = size * 0.74, y2 = size * 0.26;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int pixel = ((y * size) + x) * 4;
                if (pixels[pixel + 3] == 0)
                {
                    continue;
                }

                double coverage = SegmentCoverage(x, y, x1, y1, x2, y2, halfWidth);
                if (coverage <= 0.0)
                {
                    continue;
                }

                BlendOver(pixels, pixel, StrikeB, StrikeG, StrikeR, ToByte(coverage));
            }
        }
    }

    private static void DrawManualBadge(byte[] pixels, int size)
    {
        double radius = size * 0.135;
        double cx = size * 0.80;
        double cy = size * 0.20;
        double borderWidth = Math.Max(1.0, size * 0.05);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int pixel = ((y * size) + x) * 4;
                if (pixels[pixel + 3] == 0)
                {
                    continue;
                }

                double borderCoverage = DiscRingCoverage(x, y, cx, cy, radius, borderWidth);
                double fillCoverage = DiscCoverage(x, y, cx, cy, radius - (borderWidth / 2.0));

                if (borderCoverage <= 0.0 && fillCoverage <= 0.0)
                {
                    continue;
                }

                if (borderCoverage > 0.0)
                {
                    BlendOver(
                        pixels,
                        pixel,
                        BadgeBorderB,
                        BadgeBorderG,
                        BadgeBorderR,
                        ToByte(borderCoverage * (BadgeBorderAlpha / 255.0)));
                }

                if (fillCoverage > 0.0)
                {
                    BlendOver(pixels, pixel, BadgeB, BadgeG, BadgeR, ToByte(fillCoverage));
                }
            }
        }
    }

    private static double DiscCoverage(int x, int y, double cx, double cy, double radius)
    {
        if (radius <= 0.0)
        {
            return 0.0;
        }

        double radiusSquared = radius * radius;
        int hits = 0;

        for (int sy = 0; sy < Subsamples; sy++)
        {
            double py = y + ((sy + 0.5) / Subsamples);
            for (int sx = 0; sx < Subsamples; sx++)
            {
                double px = x + ((sx + 0.5) / Subsamples);
                double dx = px - cx;
                double dy = py - cy;
                if ((dx * dx) + (dy * dy) <= radiusSquared)
                {
                    hits++;
                }
            }
        }

        return hits / (double)(Subsamples * Subsamples);
    }

    private static double DiscRingCoverage(int x, int y, double cx, double cy, double radius, double borderWidth)
    {
        double outer = radius + (borderWidth / 2.0);
        double inner = radius - (borderWidth / 2.0);
        double outerSquared = outer * outer;
        double innerSquared = inner * inner;
        int hits = 0;

        for (int sy = 0; sy < Subsamples; sy++)
        {
            double py = y + ((sy + 0.5) / Subsamples);
            for (int sx = 0; sx < Subsamples; sx++)
            {
                double px = x + ((sx + 0.5) / Subsamples);
                double dx = px - cx;
                double dy = py - cy;
                double distanceSquared = (dx * dx) + (dy * dy);
                if (distanceSquared <= outerSquared && distanceSquared >= innerSquared)
                {
                    hits++;
                }
            }
        }

        return hits / (double)(Subsamples * Subsamples);
    }

    /// <summary>Покрытие (0…1) пикселя отрезком с круглыми концами.</summary>
    private static double SegmentCoverage(int x, int y, double x1, double y1, double x2, double y2, double halfWidth)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        double lengthSquared = (dx * dx) + (dy * dy);
        int hits = 0;

        for (int sy = 0; sy < Subsamples; sy++)
        {
            double py = y + ((sy + 0.5) / Subsamples);
            for (int sx = 0; sx < Subsamples; sx++)
            {
                double px = x + ((sx + 0.5) / Subsamples);

                double t = lengthSquared <= 0.0 ? 0.0 : (((px - x1) * dx) + ((py - y1) * dy)) / lengthSquared;
                t = Math.Clamp(t, 0.0, 1.0);

                double nearestX = x1 + (t * dx);
                double nearestY = y1 + (t * dy);
                double offsetX = px - nearestX;
                double offsetY = py - nearestY;

                if ((offsetX * offsetX) + (offsetY * offsetY) <= halfWidth * halfWidth)
                {
                    hits++;
                }
            }
        }

        return hits / (double)(Subsamples * Subsamples);
    }

    private static byte ToByte(double coverage)
    {
        int value = (int)Math.Round(coverage * 255.0);
        return value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)value;
    }

    /// <summary>
    /// Накладывает straight-цвет поверх уже записанного BGRA-пикселя (src-over).
    /// </summary>
    /// <param name="pixels">Массив BGRA, top-down.</param>
    /// <param name="index">Индекс байта B целевого пикселя.</param>
    private static void BlendOver(byte[] pixels, int index, byte srcB, byte srcG, byte srcR, byte srcA)
    {
        if (srcA == 0)
        {
            return;
        }

        int dstA = pixels[index + 3];

        if (srcA == 255 || dstA == 0)
        {
            pixels[index] = srcB;
            pixels[index + 1] = srcG;
            pixels[index + 2] = srcR;
            pixels[index + 3] = srcA;
            return;
        }

        int srcAlpha = srcA;
        int outAlpha = srcAlpha + ((dstA * (255 - srcAlpha)) / 255);
        if (outAlpha <= 0)
        {
            pixels[index] = 0;
            pixels[index + 1] = 0;
            pixels[index + 2] = 0;
            pixels[index + 3] = 0;
            return;
        }

        int weight = (dstA * (255 - srcAlpha)) / 255;

        pixels[index] = (byte)Math.Clamp(((srcB * srcAlpha) + (pixels[index] * weight)) / outAlpha, 0, 255);
        pixels[index + 1] = (byte)Math.Clamp(((srcG * srcAlpha) + (pixels[index + 1] * weight)) / outAlpha, 0, 255);
        pixels[index + 2] = (byte)Math.Clamp(((srcR * srcAlpha) + (pixels[index + 2] * weight)) / outAlpha, 0, 255);
        pixels[index + 3] = (byte)outAlpha;
    }

    /// <summary>
    /// Собирает HICON из top-down BGRA-пикселей: цветная DIB-секция 32bpp (DIB ждёт
    /// строки снизу вверх) плюс пустая AND-маска — прозрачность задаёт альфа-канал.
    /// </summary>
    private static IntPtr CreateHIconFromBgra(byte[] pixels, int size)
    {
        int stride = size * 4;
        if (pixels.Length < stride * size)
        {
            throw new ArgumentException("Недостаточно пикселей для иконки.", nameof(pixels));
        }

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
                biSizeImage = (uint)(stride * size),
            },
        };

        IntPtr colorBitmap = CreateDIBSection(IntPtr.Zero, ref info, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (colorBitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            if (colorBitmap != IntPtr.Zero)
            {
                DeleteObject(colorBitmap);
            }

            throw new Win32Exception(error, "CreateDIBSection не смог создать цветную маску иконки.");
        }

        IntPtr maskBitmap = IntPtr.Zero;

        try
        {
            // DIB-секция по умолчанию bottom-up: переворачиваем строки.
            for (int y = 0; y < size; y++)
            {
                Marshal.Copy(pixels, (size - 1 - y) * stride, IntPtr.Add(bits, y * stride), stride);
            }

            // AND-маска: 1bpp, все нули. Шаг строки выровнен по WORD.
            int maskStride = ((size + 15) / 16) * 2;
            var maskBits = new byte[maskStride * size];
            maskBitmap = CreateBitmap(size, size, 1, 1, maskBits);
            if (maskBitmap == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateBitmap не смог создать AND-маску иконки.");
            }

            var iconInfo = new ICONINFO
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = maskBitmap,
                hbmColor = colorBitmap,
            };

            IntPtr icon = CreateIconIndirect(ref iconInfo);
            if (icon == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateIconIndirect не смог собрать иконку.");
            }

            return icon;
        }
        finally
        {
            // CreateIconIndirect копирует битмапы, поэтому исходники освобождаем всегда.
            if (maskBitmap != IntPtr.Zero)
            {
                DeleteObject(maskBitmap);
            }

            DeleteObject(colorBitmap);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BITMAPINFO pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, byte[] lpBits);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFontW")]
    private static extern IntPtr CreateFontW(
        int cHeight,
        int cWidth,
        int cEscapement,
        int cOrientation,
        uint cWeight,
        uint bItalic,
        uint bUnderline,
        uint bStrikeOut,
        uint iCharSet,
        uint iOutPrecision,
        uint iClipPrecision,
        uint iQuality,
        uint iPitchAndFamily,
        string pszFaceName);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string lpString, int c, out SIZE psizl);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "DrawTextW")]
    private static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint uFormat);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }
}

/// <summary>
/// Вид значка в баллуне трея. Заменяет <c>System.Windows.Forms.ToolTipIcon</c>,
/// чтобы приложение не тянуло WinForms.
/// </summary>
public enum TrayBalloonIcon
{
    /// <summary>Обычное информационное сообщение.</summary>
    Info = 0,

    /// <summary>Предупреждение.</summary>
    Warning = 1,

    /// <summary>Ошибка.</summary>
    Error = 2,
}
