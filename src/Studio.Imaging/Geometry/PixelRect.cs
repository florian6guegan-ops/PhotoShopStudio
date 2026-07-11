namespace Studio.Imaging.Geometry;

public sealed record PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}
