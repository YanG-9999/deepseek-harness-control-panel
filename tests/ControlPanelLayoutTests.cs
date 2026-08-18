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
            if (buttons.Controls.Count != 9)
                throw new InvalidOperationException("Expected 9 action buttons, got " + buttons.Controls.Count + ".");
        }
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
