// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml.Linq;

namespace WinApp.HotReload.Tests;

[TestClass]
public sealed class XamlCompilerTests
{
    [TestMethod]
    public void NamesAreTheElementsWithFieldsNotThoseInTemplates()
    {
        var document = XDocument.Parse("""
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <StackPanel x:Name="Panel">
                    <TextBox Name="NoteBox" />
                    <ListView>
                        <ListView.ItemTemplate>
                            <DataTemplate><TextBlock x:Name="ItemName" /></DataTemplate>
                        </ListView.ItemTemplate>
                    </ListView>
                </StackPanel>
            </Page>
            """);

        CollectionAssert.AreEquivalent(new[] { "Panel", "NoteBox" }, XamlCompiler.Names(document).ToArray());
    }

    [TestMethod]
    public void AnErrorInGeneratedCodeIsAtTheXamlLineThatMadeIt()
    {
        const string generated = """
            case 14: // ContactsPage.xaml line 21
            {
                this.WaveButton = global::WinRT.CastExtensions.As<global::Microsoft.UI.Xaml.Controls.Button>(target);
                ((global::Microsoft.UI.Xaml.Controls.Button)this.WaveButton).Click += this.OnWave;
            }
            """;

        Assert.AreEqual(21, XamlCompiler.XamlLine(generated, 4));
        Assert.IsNull(XamlCompiler.XamlLine("class Page {}", 1));
    }
}
