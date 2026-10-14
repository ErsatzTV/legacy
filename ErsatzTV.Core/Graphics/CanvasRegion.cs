using System.Globalization;

namespace ErsatzTV.Core.Graphics;

public record CanvasRegion(int X, int Y, int Width, int Height)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X},{Y},{Width},{Height}");

    public static Option<CanvasRegion> Parse(string value)
    {
        string[] parts = (value ?? string.Empty).Split(',');
        if (parts.Length != 4)
        {
            return None;
        }

        var numbers = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return None;
            }
        }

        return numbers[2] > 0 && numbers[3] > 0
            ? new CanvasRegion(numbers[0], numbers[1], numbers[2], numbers[3])
            : None;
    }
}
