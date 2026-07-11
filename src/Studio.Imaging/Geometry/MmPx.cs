namespace Studio.Imaging.Geometry;

public static class MmPx
{
    public const double MmPerInch = 25.4;

    public static int ToPixels(double mm, int dpi) => (int)Math.Round(mm / MmPerInch * dpi);

    public static double ToMm(int pixels, int dpi) => pixels * MmPerInch / dpi;
}
