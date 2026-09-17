// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Drawing;
using System.Windows.Forms;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

public sealed partial class UiaTestFixture
{
    /// <summary>Opt-in read-only fixtures must not activate even when first shown.</summary>
    private sealed class NonActivatingForm : Form
    {
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return parameters;
            }
        }
    }

    /// <summary>
    /// Adds a genuine RichEdit document. Formatting and selection are set on the owning STA,
    /// without focusing the control or activating its window.
    /// </summary>
    public RichTextBox AddTextAttributesDocument(bool mixed = false)
    {
        return OnUiThread(() =>
        {
            var document = new RichTextBox
            {
                Name = "textAttributesDocument",
                AccessibleName = "Formatted document",
                Text = "First run. Second run.",
                Location = new Point(20, 150),
                Size = new Size(420, 130),
                HideSelection = false,
                DetectUrls = false,
            };
            _form.Controls.Add(document);
            document.BringToFront();
            document.SelectAll();
            using (var font = new Font("Courier New", 15.5f, FontStyle.Bold | FontStyle.Italic | FontStyle.Strikeout))
            {
                document.SelectionFont = font;
            }
            document.SelectionColor = Color.FromArgb(12, 34, 56);
            if (mixed)
            {
                document.Select(11, document.TextLength - 11);
                using var font = new Font("Arial", 11.5f, FontStyle.Regular);
                document.SelectionFont = font;
                document.SelectionColor = Color.FromArgb(90, 80, 70);
            }
            document.Select(0, 1);
            return document;
        });
    }
}
