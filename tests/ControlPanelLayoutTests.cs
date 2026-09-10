using System;
using System.Windows.Forms;

public static class ControlPanelLayoutTests
{
    public static void Run()
    {
        using (var form = new ManagerForm())
        {
            FlowLayoutPanel buttons = FindFlowLayout(form);
            if (buttons == null)
                throw new InvalidOperationException("Button panel was not found.");
            if (!buttons.WrapContents)
                throw new InvalidOperationException("Button panel must wrap onto multiple rows.");
            if (buttons.AutoScroll)
                throw new InvalidOperationException("Button panel must not show scrollbars.");
            if (buttons.Controls.Count != 10)
                throw new InvalidOperationException("Expected 10 action buttons, got " + buttons.Controls.Count + ".");

            // Wrapped rows must stay inside the reserved height, or the last row is
            // clipped and the button becomes invisible.
            int rowCount = RequiredButtonRows(buttons);
            int required = rowCount * 34;
            if (rowCount > 2)
            {
                TableLayoutPanel main = FindTableLayout(form);
                if (main == null)
                    throw new InvalidOperationException("Main layout table was not found.");
                float reserved = main.RowStyles[4].Height;
                if (reserved < required)
                    throw new InvalidOperationException(
                        "The button row reserves only " + reserved + "px but needs " + required +
                        "px for " + rowCount + " wrapped rows.");
            }
        }
    }

    /// <summary>
    /// Counts the rows the wrapped buttons actually occupy, derived from their real
    /// preferred sizes rather than assumed.
    /// </summary>
    private static int RequiredButtonRows(FlowLayoutPanel buttons)
    {
        int containerWidth = buttons.ClientSize.Width;
        if (containerWidth <= 0)
            return 1;
        int rows = 1;
        int used = 0;
        foreach (Control control in buttons.Controls)
        {
            int width = control.PreferredSize.Width + control.Margin.Horizontal;
            if (used + width > containerWidth && used > 0)
            {
                rows++;
                used = 0;
            }
            used += width;
        }
        return rows;
    }

    private static TableLayoutPanel FindTableLayout(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            var table = child as TableLayoutPanel;
            if (table != null)
                return table;
            table = FindTableLayout(child);
            if (table != null)
                return table;
        }
        return null;
    }

    private static FlowLayoutPanel FindFlowLayout(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            var flow = child as FlowLayoutPanel;
            if (flow != null)
                return flow;
            flow = FindFlowLayout(child);
            if (flow != null)
                return flow;
        }
        return null;
    }
}
