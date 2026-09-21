// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Windows.Forms;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

/// <summary>A live UIA fixture that does not take foreground focus when shown.</summary>
public sealed class NonActivatingTestForm : Form
{
    protected override bool ShowWithoutActivation => true;
}
