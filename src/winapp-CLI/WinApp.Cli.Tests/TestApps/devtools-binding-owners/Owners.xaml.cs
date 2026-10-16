// Copyright (c) Microsoft Corporation and Contributors.
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Controls;

namespace BindingOwnersFixture;

public sealed partial class OwnerPage : Page
{
    public string HeadingText => "Page source";
    public OwnerPage() => InitializeComponent();
}

public sealed partial class OwnerControl : UserControl
{
    public string HeadingText => "Control source";
    public OwnerControl() => InitializeComponent();
}
