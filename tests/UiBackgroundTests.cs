using System;
using System.Drawing;

/// <summary>
/// Covers the guarded page gradient behind the cards.
///
/// The unguarded version took the panel down with a crash dialog. A window with no client
/// area still receives the erase message, and LinearGradientBrush refuses an empty
/// rectangle, so painting threw. It came out of a paint message, which is why the window
/// looked perfectly healthy underneath the modal error.
/// </summary>
public static class UiBackgroundTests
{
    public static void Run()
    {
        VerifyEmptyRectanglesAreNoOps();
        VerifyNormalRectangleStillPaints();
        Console.WriteLine("UI background tests passed.");
    }

    /// <summary>
    /// Every degenerate size a window or a collapsed layout can hand over. None of these
    /// has any area to fill, so painting has to do nothing rather than throw.
    /// </summary>
    private static void VerifyEmptyRectanglesAreNoOps()
    {
        using (var bitmap = new Bitmap(8, 8))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            Paint(graphics, new Rectangle(0, 0, 0, 0));
            Paint(graphics, new Rectangle(0, 0, 8, 0));
            Paint(graphics, new Rectangle(0, 0, 0, 8));
            Paint(graphics, new Rectangle(0, 0, -5, 8));
            Paint(graphics, new Rectangle(0, 0, 8, -5));
        }
    }

    /// <summary>
    /// The guard must not have turned the background into a no-op, and the page has to be
    /// the colour the style declares. It used to be a vertical gradient, and that gradient
    /// turned out to be the panel's slowest operation by two orders of magnitude - see
    /// UiBackground.Paint - so the flat colour is deliberate, not an oversight.
    /// </summary>
    private static void VerifyNormalRectangleStillPaints()
    {
        using (var bitmap = new Bitmap(16, 16))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
                UiBackground.Paint(graphics, new Rectangle(0, 0, 16, 16));

            foreach (Point point in new[] { new Point(0, 0), new Point(8, 8), new Point(15, 15) })
            {
                Color pixel = bitmap.GetPixel(point.X, point.Y);
                if (pixel.A != 255)
                    throw new InvalidOperationException(
                        "The page background must be filled, got alpha " + pixel.A + " at " + point + ".");
                if (pixel.ToArgb() != UiStyle.WindowBackground.ToArgb())
                    throw new InvalidOperationException(
                        "The page background must be the page colour, got " + pixel + " at " + point + ".");
            }
        }
    }

    private static void Paint(Graphics graphics, Rectangle bounds)
    {
        try
        {
            UiBackground.Paint(graphics, bounds);
        }
        catch (ArgumentException error)
        {
            throw new InvalidOperationException(
                "Painting " + bounds + " must be a no-op instead of an exception: " + error.Message);
        }
    }
}
