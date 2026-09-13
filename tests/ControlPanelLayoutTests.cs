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

            VerifyInstallDirectoryRow(form);
            VerifyLogToolbarExists(form);
            VerifyVersionRowExists(form);
        }
    }

    /// <summary>
    /// The version row shows the installed version and the auto-start switch. The version
    /// must be a version number: it used to be overwritten by the update-check result.
    /// </summary>
    private static void VerifyVersionRowExists(Control form)
    {
        TableLayoutPanel row = FindRowContainingLabel(form, "Harness 版本");
        if (row == null)
            throw new InvalidOperationException("The version row was not found.");
        if (row.Controls.Count != 3)
            throw new InvalidOperationException(
                "The version row must hold the label, the version, and the auto-start switch, got " + row.Controls.Count + ".");

        bool hasCheckBox = false;
        foreach (Control child in row.Controls)
        {
            if (child is CheckBox) hasCheckBox = true;
        }
        if (!hasCheckBox)
            throw new InvalidOperationException("The auto-start switch belongs on the version row.");
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

        foreach (string label in new[] { "下一个", "上一个", "导出", "清空" })
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
    /// <summary>
    /// The install-directory row must carry the path and a read-only port.
    ///
    /// Both the browse button and the port editor were removed deliberately. The
    /// directory can only be chosen before an install and the install flow already opens
    /// its own picker; the port follows the Harness default, and an editable box only
    /// invited a change that would not affect an already-running instance.
    /// </summary>
    private static void VerifyInstallDirectoryRow(Control form)
    {
        TableLayoutPanel row = FindRowContainingLabel(form, "安装目录");
        if (row == null)
            throw new InvalidOperationException("The install-directory row was not found.");
        if (row.Controls.Count != 4)
            throw new InvalidOperationException(
                "The install-directory row must hold the label, the path, the port label, and the port value, got " +
                row.Controls.Count + ".");

        if (FindButtonByText(form, "浏览") != null)
            throw new InvalidOperationException(
                "The browse button was removed on purpose; the install flow owns directory selection.");

        // The port must be shown but not editable.
        foreach (Control child in row.Controls)
        {
            if (child is TextBox)
            {
                throw new InvalidOperationException(
                    "The install-directory row must not carry an editable field; the port is display only.");
            }
        }

        Label portValue = FindLabelByText(row, HarnessPortPolicy.DefaultPort.ToString());
        if (portValue == null)
            throw new InvalidOperationException(
                "The row must show the port in use, expected '" + HarnessPortPolicy.DefaultPort + "'.");
    }

    /// <summary>Finds a label with the given text anywhere under the parent.</summary>
    private static Label FindLabelByText(Control parent, string text)
    {
        foreach (Control child in parent.Controls)
        {
            var label = child as Label;
            if (label != null && String.Equals(label.Text, text, StringComparison.Ordinal))
                return label;
            Label nested = FindLabelByText(child, text);
            if (nested != null) return nested;
        }
        return null;
    }

    /// <summary>Finds the table that contains a label with the given text.</summary>
    private static TableLayoutPanel FindRowContainingLabel(Control parent, string text)
    {
        foreach (Control child in parent.Controls)
        {
            var label = child as Label;
            if (label != null && String.Equals(label.Text, text, StringComparison.Ordinal))
            {
                var row = label.Parent as TableLayoutPanel;
                if (row != null) return row;
            }
            TableLayoutPanel nested = FindRowContainingLabel(child, text);
            if (nested != null) return nested;
        }
        return null;
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
