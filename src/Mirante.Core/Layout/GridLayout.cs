namespace Mirante.Core.Layout;

public readonly record struct GridSize(int Rows, int Columns);

public static class GridLayout
{
    public const double DefaultTileAspect = 16.0 / 9.0;

    /// <summary>
    /// Picks the rows × columns that make the tiles as large as possible inside the
    /// available area. A portrait monitor therefore stacks cameras vertically, a wide
    /// one side by side.
    /// </summary>
    public static GridSize Best(int count, double width, double height, double tileAspect = DefaultTileAspect)
    {
        if (count <= 0)
        {
            return new GridSize(0, 0);
        }

        if (width <= 0 || height <= 0)
        {
            return Square(count);
        }

        var best = Square(count);
        var bestArea = 0.0;
        for (var columns = 1; columns <= count; columns++)
        {
            var rows = (int)Math.Ceiling(count / (double)columns);
            var tileWidth = Math.Min(width / columns, height / rows * tileAspect);
            var area = tileWidth * tileWidth / tileAspect;
            if (area > bestArea + 0.5)
            {
                bestArea = area;
                best = new GridSize(rows, columns);
            }
        }

        return best;
    }

    private static GridSize Square(int count)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        return new GridSize((int)Math.Ceiling(count / (double)columns), columns);
    }
}
