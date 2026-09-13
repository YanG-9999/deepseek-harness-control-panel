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

            VerifyBrowseButtonExists(form);
            VerifyLogToolbarExists(form);
        }
    }

    /// <summary>
    /// The log area needs its own controls. Without them the only way to share a
    /// failure was to drag-select the text box by hand, and there was no way to find a
    /// keyword in a long install log.
    /// </summary>
    private static void VerifyLogToolbarExists(Control form)
    {
        if (FindTextBox(form) == null)
            throw new InvalidOperationException("The log needs a search box (and the port needs a box).");

        foreach (string label in new[] { "下一个", "上一个", "导出日志", "清空" })
        {
            if (FindButtonByText(form, label) == null)
                throw new InvalidOperationException("The log toolbar is missing its '" + label + "' button.");
        }

        RichTextBox log = FindRichTextBox(form);
        if (log == null)
            throw new InvalidOperationException("The log text box was not found.");
        if (!log.ReadOnly)
            throw new InvalidOperationException("The log must stay read-only.");
    }

    private static TextBox FindTextBox(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            var box = child as TextBox;
            if (box != null)
                return box;
            TextBox nested = FindTextBox(child);
            if (nested != null)
                return nested;
        }
        return null;
    }

    private static RichTextBox FindRichTextBox(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            var box = child as RichTextBox;
            if (box != null)
                return box;
            RichTextBox nested = FindRichTextBox(child);
            if (nested != null)
                return nested;
        }
        return null;
    }

    /// <summary>
    /// The install-directory row previously had no picker: the handler existed but
    /// nothing was wired to it, so the only way to choose a directory was through the
    /// install flow. This asserts the control exists, is laid out, and sits where it
    /// belongs.
    ///
    /// On-screen visibility is deliberately not asserted here: Control.Visible is
    /// inherited, and this test never shows the form, so every control (including the
    /// form) reports false. Reachability is confirmed by driving the real window.
    /// </summary>
    private static void VerifyBrowseButtonExists(Control form)
    {
        Button browse = FindButtonByText(form, "浏览");
        if (browse == null)
            throw new InvalidOperationException("The install-directory row needs a reachable browse button.");
        if (browse.Width <= 0 || browse.Height <= 0)
            throw new InvalidOperationException(
                "The browse button must be laid out with a usable size, got " + browse.Size + ".");

        // It must sit on the install-directory row, which also carries the port control.
        TableLayoutPanel row = browse.Parent as TableLayoutPanel;
        if (row == null)
            throw new InvalidOperationException("The browse button must live in the install-directory row.");
        if (row.Controls.Count != 5)
            throw new InvalidOperationException(
                "The install-directory row must hold its label, the path, the browse button, and the port label and box, got " +
                row.Controls.Count + ".");
        if (browse.Dock != DockStyle.Fill)
            throw new InvalidOperationException("The browse button must fill its column, not auto-size past it.");
    }

    private static Button FindButtonByText(Control parent, string text)
    {
        foreach (Control child in parent.Controls)
        {
            var button = child as Button;
            if (button != null && String.Equals(button.Text, text, StringComparison.Ordinal))
                return button;
            Button nested = FindButtonByText(child, text);
            if (nested != null)
                return nested;
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
