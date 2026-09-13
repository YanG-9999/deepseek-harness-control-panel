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
            // The approved design puts all nine actions on one row, so the panel must not
            // wrap. It previously wrapped, which is what the assertion here used to require.
            if (buttons.WrapContents)
                throw new InvalidOperationException("Button panel must keep every action on one row.");
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
    /// The version field holds a version number and nothing else. It used to be overwritten
    /// by the update-check result, and the same area once carried an auto-start switch.
    /// </summary>
    private static void VerifyVersionRowExists(Control form)
    {
        if (FindLabelByText(form, "Harness 版本") == null)
            throw new InvalidOperationException("The runtime-information card needs a version caption.");

        // The version itself carries the display prefix, so it reads as a version number.
        bool foundVersion = false;
        var all = new System.Collections.Generic.List<Control>();
        Collect(form, all);
        foreach (Control control in all)
        {
            if (control is CheckBox)
                throw new InvalidOperationException(
                    "The auto-start switch was removed on purpose; no checkbox belongs in the panel.");
            string text = control.Text ?? "";
            if (text.StartsWith("v", StringComparison.Ordinal) && text.Length > 1 &&
                Char.IsDigit(text[1]))
            {
                foundVersion = true;
            }
        }
        if (!foundVersion)
            throw new InvalidOperationException("The version field must show a version such as v0.1.5-rc.2.");
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

        // Two of the toolbar buttons are icon-only, with their label in a tooltip, which is
        // how the design presents the navigation pair; the rest carry their text.
        foreach (string label in new[] { "导出", "清空" })
        {
            if (FindButtonByText(form, label) == null)
                throw new InvalidOperationException("The log toolbar is missing its '" + label + "' button.");
        }
        foreach (string label in new[] { "上一个", "下一个" })
        {
            if (FindButtonByName(form, label) == null)
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
    /// The runtime-information card must carry the four facts, and the header must carry
    /// an editable port field.
    ///
    /// The browse button is gone for good: the directory can only be chosen before an
    /// install, and the install flow already opens its own picker. The port is editable
    /// again, which the approved design calls for.
    /// </summary>
    private static void VerifyInstallDirectoryRow(Control form)
    {
        if (FindLabelByText(form, "安装目录") == null)
            throw new InvalidOperationException("The runtime-information card needs an install-directory caption.");
        foreach (string caption in new[] { "状态", "运行状态", "Harness 版本" })
        {
            if (FindLabelByText(form, caption) == null)
                throw new InvalidOperationException("The runtime-information card is missing the '" + caption + "' caption.");
        }

        if (FindButtonByText(form, "浏览") != null)
            throw new InvalidOperationException(
                "The browse button was removed on purpose; the install flow owns directory selection.");

        // Exactly one text box belongs to the header: the editable port.
        TextBox port = FindTextBox(form);
        if (port == null)
            throw new InvalidOperationException("The header needs an editable port field.");

        int value;
        if (!Int32.TryParse(port.Text, out value) || !HarnessPortPolicy.IsValid(value))
            throw new InvalidOperationException("The port field must show a usable port, got '" + port.Text + "'.");
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

    /// <summary>
    /// Finds an icon-only button by its accessible name. Two toolbar buttons show only an
    /// icon, so this is how they are identified.
    /// </summary>
    private static Button FindButtonByName(Control parent, string name)
    {
        var all = new System.Collections.Generic.List<Control>();
        Collect(parent, all);
        foreach (Control control in all)
        {
            if (control.AccessibleName == name)
                return control as Button;
        }
        return null;
    }

    private static void Collect(Control parent, System.Collections.Generic.List<Control> into)
    {
        foreach (Control child in parent.Controls)
        {
            into.Add(child);
            Collect(child, into);
        }
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
