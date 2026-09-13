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
    /// The guard must not have turned the background into a no-op: a normal window still
    /// gets the vertical page gradient.
    /// </summary>
    private static void VerifyNormalRectangleStillPaints()
    {
        using (var bitmap = new Bitmap(16, 16))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
                UiBackground.Paint(graphics, new Rectangle(0, 0, 16, 16));

            Color top = bitmap.GetPixel(8, 0);
            Color bottom = bitmap.GetPixel(8, 15);
            if (top.A != 255)
                throw new InvalidOperationException("The page background must be filled, got alpha " + top.A + ".");
            if (top.R == bottom.R && top.G == bottom.G && top.B == bottom.B)
                throw new InvalidOperationException("The page background must keep its vertical gradient.");
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
