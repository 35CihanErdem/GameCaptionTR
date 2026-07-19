namespace GameCaptionTR.Models;

public sealed class CaptureRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public bool IsValid => Width >= 40 && Height >= 20;

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}
