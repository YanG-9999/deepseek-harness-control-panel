using System;
using System.Windows.Forms;

public static class ControlPanelLayoutTests
{
    public static void Run()
    {
        using (var form = new ManagerForm())
        {
            VerifyActionButtons(form);

            VerifyInstallDirectoryRow(form);
            VerifyInfoCardBottomClearance(form);
            VerifyLogToolbarExists(form);
            VerifyVersionRowExists(form);
        }
    }

    private static void VerifyInfoCardBottomClearance(Form form)
    {
        form.Show();
        Application.DoEvents();

        Label caption = FindLabelByText(form, "Harness 版本");
        if (caption == null)
            throw new InvalidOperationException("The version caption was not found.");

        Control value = null;
        foreach (Control child in caption.Parent.Controls)
        {
            if (child != caption)
            {
                value = child;
                break;
            }
        }
        Control card = caption.Parent;
        while (card != null && !(card is UiCardPanel))
            card = card.Parent;
        if (value == null || card == null)
            throw new InvalidOperationException("The information card layout was not found.");

        System.Drawing.Point topLeft = card.PointToClient(value.PointToScreen(System.Drawing.Point.Empty));
        int clearance = card.ClientSize.Height - topLeft.Y - value.Height;
        if (clearance < UiMetrics.CardPaddingY)
        {
            throw new InvalidOperationException(
                "The information fields are too close to the card bottom: " + clearance + "px.");
        }
    }

    private static void VerifyActionButtons(Control form)
    {
        foreach (string label in new[] {
            "安装", "卸载", "启动", "重启", "停止",
            "检查更新", "打开页面", "扫描", "打开目录" })
        {
            if (FindButtonByText(form, label) == null)
                throw new InvalidOperationException("The action grid is missing its '" + label + "' button.");
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
    /// failure was to drag-select the text box by hand.
    ///
    /// The search box and the previous/next pair were removed on purpose: they took a
    /// whole row for a rarely used feature. Only the two actions that earn their space
    /// remain, so this asserts they are present and that nothing re-adds the rest.
    /// </summary>
    private static void VerifyLogToolbarExists(Control form)
    {
        foreach (string label in new[] { "导出", "清空" })
        {
            Button button = FindButtonByText(form, label);
            if (button == null)
                throw new InvalidOperationException("The log toolbar is missing its '" + label + "' button.");
            UiFlatButton flat = button as UiFlatButton;
            if (flat == null || button.ClientSize.Width < UiMeasure.MeasureToolbarButtonWidth(label, flat.Icon))
                throw new InvalidOperationException("The log toolbar clips its '" + label + "' button.");
        }
        foreach (string removed in new[] { "上一个", "下一个" })
        {
            if (FindButtonByText(form, removed) != null)
                throw new InvalidOperationException(
                    "The log toolbar must not carry the removed '" + removed + "' button.");
        }
        if (FindTextBox(form) != null)
            throw new InvalidOperationException("The log search box was removed on purpose.");

        RichTextBox log = FindRichTextBox(form);
        if (log == null)
            throw new InvalidOperationException("The log text box was not found.");
        if (!log.ReadOnly)
            throw new InvalidOperationException("The log must stay read-only.");
    }

    /// <summary>
    /// Any text box left on the form. The port is a plain label now and the log search
    /// box is gone, so this should find nothing; it stays as the guard for that.
    /// </summary>
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
    /// The runtime-information card must carry the four facts, and the header must show
    /// Harness's fixed default port.
    ///
    /// The browse button is gone for good: the directory can only be chosen before an
    /// install, and the install flow already opens its own picker.
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

        Label port = FindLabelByText(form, HarnessPortPolicy.DefaultPort.ToString());
        if (port == null)
            throw new InvalidOperationException("The header must display the Harness default port.");
        if (port.CanSelect)
            throw new InvalidOperationException("The displayed port must not behave like an input control.");
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

}
