using System;

// Compile with tests/TrayPopupRenderingTests.cs and reference a staged panel EXE.
// Exercises only the standalone view with sample data; never starts a service,
// reads credentials, or replaces the user's installed panel.
public static class RenderTrayPreview
{
    [STAThread]
    public static void Main()
    {
        TrayPopupRenderingTests.Run();
    }
}
