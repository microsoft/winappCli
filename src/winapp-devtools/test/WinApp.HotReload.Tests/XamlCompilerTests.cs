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

    [TestMethod]
    public void Pass1RunsOnlyForChangesItsCodeIsMadeFrom()
    {
        static string Xaml(string body, string page = "MainPage") => $"""
            <Page x:Class="App.{page}" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <StackPanel x:Name="Panel">{body}</StackPanel>
            </Page>
            """;
        static string? Shape(string body) => XamlCompiler.Pass1Shape(Xaml(body));
        var shape = Shape("""<TextBlock Text="Hello" />""");

        foreach (var same in new[] { """<TextBlock Text="Bye" Margin="8" />""", """<TextBlock Text="Hello" /><Button Click="OnClick" />""" })
        {
            Assert.AreEqual(shape, Shape(same), same);
        }
        foreach (var changed in new[] { """<TextBlock x:Name="Greeting" />""", """<Button Name="Panel2" />""", """<TextBlock Text="{x:Bind Title}" />""",
            """<TextBlock x:Load="False" />""", """<TextBlock><TextBlock.Text>{x:Bind Title}</TextBlock.Text></TextBlock>""" })
        {
            Assert.AreNotEqual(shape, Shape(changed), changed);
        }
        Assert.AreNotEqual(shape, XamlCompiler.Pass1Shape(Xaml("""<TextBlock Text="Hello" />""", "OtherPage")));
        Assert.IsNull(XamlCompiler.Pass1Shape("<Page"));
    }
}
