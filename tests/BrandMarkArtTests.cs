using System;
using System.Drawing;
using System.IO;

/// <summary>
/// Covers the frame the header logo is drawn from.
///
/// Two regressions lived here and both are invisible until someone looks at the window:
/// the mark came from the 32 px associated icon and was stretched into a blurry tile, and
/// a PNG icon frame decoded through System.Drawing.Icon answers with a bitmap whose
/// pixels belong to the icon handle, so they collapse into noise once that icon is
/// disposed. The checks below fail for both, so neither can come back unnoticed.
/// </summary>
public static class BrandMarkArtTests
{
    public static void Run()
    {
        using (Image mark = IconFramePolicy.DecodeLargestFrame(EmbeddedIcon()))
        {
            if (mark == null)
                throw new InvalidOperationException("The embedded icon must decode to an image.");
            if (mark.Width < 128 || mark.Height < 128)
                throw new InvalidOperationException(
                    "The mark must come from a large frame, not the 32 px one: got " +
                    mark.Width + "x" + mark.Height + ".");
            VerifyMarkIsArtwork(mark);
        }
        VerifyRejectsUnusableInput();
        Console.WriteLine("Brand mark art tests passed.");
    }

    /// <summary>
    /// The mark is a rounded tile: transparent at the corners, opaque across the middle,
    /// and flat enough that whole areas share a colour.
    /// </summary>
    private static void VerifyMarkIsArtwork(Image mark)
    {
        using (var bitmap = new Bitmap(mark))
        {
            if (Alpha(bitmap, 0, 0) > 16 ||
                Alpha(bitmap, bitmap.Width - 1, 0) > 16 ||
                Alpha(bitmap, 0, bitmap.Height - 1) > 16 ||
                Alpha(bitmap, bitmap.Width - 1, bitmap.Height - 1) > 16)
                throw new InvalidOperationException("The rounded tile must leave the corners transparent.");
            if (Alpha(bitmap, bitmap.Width / 2, bitmap.Height / 2) < 200)
                throw new InvalidOperationException("The middle of the tile must be opaque.");

            // A frame decoded as the wrong format keeps roughly this outline but fills it
            // with per-pixel noise, which is exactly what the corner and centre samples
            // above cannot see.
            int row = bitmap.Height / 2;
            int jumps = 0;
            for (int x = 1; x < bitmap.Width; x++)
            {
                if (ChannelDelta(bitmap.GetPixel(x - 1, row), bitmap.GetPixel(x, row)) > 60)
                    jumps++;
            }
            double density = (double)jumps / (bitmap.Width - 1);
            if (density > 0.15)
                throw new InvalidOperationException(
                    "The mark must be flat artwork, not noise: " + density.ToString("P1") +
                    " of one row changes sharply.");
        }
    }

    /// <summary>
    /// Anything unusable has to decode to nothing rather than throw: the caller falls
    /// back to the associated icon on a null result.
    /// </summary>
    private static void VerifyRejectsUnusableInput()
    {
        if (IconFramePolicy.DecodeLargestFrame(null) != null)
            throw new InvalidOperationException("A missing icon must decode to nothing.");
        if (IconFramePolicy.DecodeLargestFrame(new byte[] { 1, 2, 3 }) != null)
            throw new InvalidOperationException("A truncated icon must decode to nothing.");
        if (IconFramePolicy.DecodeLargestFrame(new byte[22]) != null)
            throw new InvalidOperationException("An icon with an empty frame must decode to nothing.");
        if (IconFramePolicy.IsPng(new byte[] { 0x89, 0x50 }))
            throw new InvalidOperationException("A short buffer is not a PNG.");
        if (IconFramePolicy.IsPng(null))
            throw new InvalidOperationException("A missing buffer is not a PNG.");
    }

    private static int Alpha(Bitmap bitmap, int x, int y)
    {
        return bitmap.GetPixel(x, y).A;
    }

    private static int ChannelDelta(Color left, Color right)
    {
        return Math.Max(
            Math.Max(Math.Abs(left.R - right.R), Math.Abs(left.G - right.G)),
            Math.Abs(left.B - right.B));
    }

    /// <summary>
    /// The icon the panel embeds with /resource. Reading it back also proves build.ps1 and
    /// the drawing code agree on the name: a rename in one place would otherwise drop the
    /// mark back to the soft associated icon without any other symptom.
    /// </summary>
    private static byte[] EmbeddedIcon()
    {
        using (Stream stream = typeof(IconFramePolicy).Assembly
            .GetManifestResourceStream("DeepSeekHarness.ico"))
        {
            if (stream == null)
                throw new InvalidOperationException(
                    "The multi-frame icon must be embedded as DeepSeekHarness.ico by build.ps1.");
            var buffer = new byte[stream.Length];
            int read = 0;
            while (read < buffer.Length)
            {
                int chunk = stream.Read(buffer, read, buffer.Length - read);
                if (chunk <= 0)
                    break;
                read += chunk;
            }
            if (read != buffer.Length)
                throw new InvalidOperationException("The embedded icon must be readable in full.");
            return buffer;
        }
    }
}
