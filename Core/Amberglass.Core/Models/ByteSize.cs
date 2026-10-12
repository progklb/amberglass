using System.Globalization;

namespace Amberglass.Core.Models;

/// <summary>Human-readable byte counts, used for both file sizes and cache totals.</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {Units[unit]}");
    }

    public static long FromGigabytes(double gigabytes) => (long)(gigabytes * 1024 * 1024 * 1024);

    public static double ToGigabytes(long bytes) => bytes / (double)(1024 * 1024 * 1024);
}
