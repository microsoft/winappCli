// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class CommentAuthoredIdentityTests
{
    private const string Root = """
        <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:local="using:MyApp">
        """;
    private string _root = "";

    [TestInitialize]
    public void Initialize() => _root = Directory.CreateTempSubdirectory("winapp-authored-comment-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private Comment Capture(string body, bool unique = true, string? containing = null)
    {
        var source = Root + "\n" + body + "\n</Page>";
        File.WriteAllText(Path.Combine(_root, "Page.xaml"), source);
        var selected = XamlCoordinateMap.SourceDeclarations(Encoding.UTF8.GetBytes(source))
            .First(declaration => declaration.Element.Name.LocalName == "TextBlock" &&
                (containing is null || declaration.Text.Contains(containing, StringComparison.Ordinal)));
        return new Comment
        {
            ProjectRoot = _root,
            Anchor = new CommentAnchor
            {
                SourceFile = "Page.xaml",
                SourceUri = "ms-appx:///Page.xaml",
                Line = selected.Line,
                Column = selected.Column,
                Identity = new CommentIdentity
                {
                    Type = "TextBlock",
                    Name = CommentAuthoredIdentity.Name(selected.Element),
                    Content = "Localized or live-edited runtime value",
                },
                Authored = CommentAuthoredIdentity.Capture(_root, "Page.xaml", selected, selected.Text, unique),
            },
        };
    }

    private CommentView View(Comment comment) => CommentViewBuilder.ToView(comment, new CommentAnchorResolver(), _root);

    [TestMethod]
    public void SecretAttribute_IsRedactedInTheStoreAndCandidates_AndStillAnchors()
    {
        const string authored = """<PasswordBox x:Name="SecretBox" Password="hunter2" PasswordChar="*" Header="Pw" />""";
        const string redacted = """<PasswordBox x:Name="SecretBox" Password="<redacted>" PasswordChar="*" Header="Pw" />""";
        var path = Path.Combine(_root, "Page.xaml");
        File.WriteAllText(path, Root + "\n" + authored + "\n</Page>");
        var declaration = CommentAuthoredIdentity.Read(path, _root).Single(item => item.Element.Name.LocalName == "PasswordBox");

        // The running app reports the declaration already redacted; an unredacted report anchors the same way.
        var captured = CommentAuthoredIdentity.Capture(_root, "Page.xaml", declaration, redacted, true);
        Assert.AreEqual(captured.Signature, CommentAuthoredIdentity.Capture(_root, "Page.xaml", declaration, authored, true).Signature);
        Assert.AreEqual(redacted, captured.Declaration);

        var comment = new Comment
        {
            ProjectRoot = _root,
            Anchor = new CommentAnchor
            {
                SourceFile = "Page.xaml",
                SourceUri = "ms-appx:///Page.xaml",
                Line = declaration.Line,
                Column = declaration.Column,
                Identity = new CommentIdentity { Type = "PasswordBox", Name = "SecretBox" },
                Authored = captured,
            },
        };
        var view = View(comment);
        Assert.IsTrue(view.AnchorConfirmed);
        Assert.AreEqual(redacted, view.Hits.Single().Text);
        StringAssert.DoesNotMatch(System.Text.Json.JsonSerializer.Serialize(new CommentStoreDocument { Comments = [comment] },
            CommentsJsonContext.Default.CommentStoreDocument), new System.Text.RegularExpressions.Regex("hunter2"));
    }

    [TestMethod]
    [DataRow("""<PasswordBox Password='hunter2'/>""", """<PasswordBox Password='<redacted>'/>""")]
    [DataRow("<PasswordBox\n  PasswordBox.Password = \"hunter2\"/>", "<PasswordBox\n  PasswordBox.Password = \"<redacted>\"/>")]
    [DataRow("""<PasswordBox Password="{x:Bind Secret}"/>""", """<PasswordBox Password="{x:Bind Secret}"/>""")]
    [DataRow("""<PasswordBox.Password>hunter2</PasswordBox.Password>""", """<PasswordBox.Password><redacted></PasswordBox.Password>""")]
    [DataRow("""<PasswordBox PasswordChar="#" PasswordRevealMode="Peek"/>""", """<PasswordBox PasswordChar="#" PasswordRevealMode="Peek"/>""")]
    public void RedactXaml_KeepsNamesAndBindings(string xaml, string expected)
        => Assert.AreEqual(expected, DevToolsSecrets.RedactXaml(xaml));

    [TestMethod]
    [DataRow("utf-8")]
    [DataRow("utf-16")]
    [DataRow("utf-16BE")]
    [DataRow("utf-32")]
    [DataRow("utf-32BE")]
    public void BomEncodedSource_PreservesCaptureAndMovedDeclaration(string encodingName)
    {
        const string target = """<TextBlock x:Name="Label" Text="Original" />""";
        var comment = Capture(target);
        var path = Path.Combine(_root, "Page.xaml");
        var source = File.ReadAllText(path).Replace(target, "\n\n" + target, StringComparison.Ordinal);
        File.WriteAllText(path, source, Encoding.GetEncoding(encodingName));
        var declaration = CommentAuthoredIdentity.Read(path, _root)
            .Single(item => item.Element.Name.LocalName == "TextBlock");
        var captured = CommentAuthoredIdentity.Capture(_root, "Page.xaml", declaration, target, true);
        Assert.AreEqual(comment.Anchor.Authored!.Signature, captured.Signature);
        var view = View(comment);
        Assert.IsTrue(view.AnchorConfirmed);
        Assert.AreEqual(comment.Anchor.Line + 2, view.Hits.Single().Line);
        Assert.AreEqual(target, comment.Anchor.Authored.Declaration);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InvalidEncoding_IsUnavailableEvidence_NotACommandFailure(bool unicode)
    {
        var comment = Capture("""<TextBlock Text="Original" />""");
        var path = Path.Combine(_root, "Page.xaml");
        File.WriteAllBytes(path, unicode ? [0xff, 0xfe, 0x3c] : [0xc0, 0xaf]);
        Assert.ThrowsExactly<InvalidDataException>(() => CommentAuthoredIdentity.Read(path, _root));
        var view = View(comment);
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.AreEqual(0, view.Hits.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BoundTextIsContextOnly_ExactAuthoredIdentitySurvivesLineMoves(bool reorder)
    {
        const string target = """<TextBlock Text="{x:Bind Vm.Title}" />""";
        var comment = Capture("<Grid x:Name=\"Panel\">\n" + target + "\n<Button Content=\"Other\" />\n</Grid>");
        var historical = comment.Anchor.Line;
        var original = File.ReadAllText(Path.Combine(_root, "Page.xaml"));
        var changed = reorder
            ? original.Replace(target + "\n<Button Content=\"Other\" />", "<Button Content=\"Other\" />\n" + target, StringComparison.Ordinal)
            : original.Replace(target, "\n\n" + target, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_root, "Page.xaml"), changed);
        var view = View(comment);
        Assert.IsTrue(view.AnchorConfirmed);
        Assert.IsFalse(view.RequiresConfirmation);
        Assert.AreEqual("strong", view.Hits.Single().Confidence);
        Assert.AreEqual(historical + (reorder ? 1 : 2), view.Hits[0].Line);
        Assert.AreEqual(historical, comment.Anchor.Line);
        Assert.AreEqual(target, comment.Anchor.Authored!.Declaration);
    }

    [TestMethod]
    public void ImplementedUnnamedTextEdit_KeepsItsTreePositionAsAStrongAnchor()
    {
        var comment = Capture("""<TextBlock Text="Original authored text" />""");
        var path = Path.Combine(_root, "Page.xaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("Original authored text", "Implemented text", StringComparison.Ordinal));
        var view = View(comment);
        Assert.IsTrue(view.AnchorConfirmed);
        Assert.IsFalse(view.RequiresConfirmation);
        Assert.AreEqual("treePath", view.Hits.Single().Via);
        StringAssert.Contains(view.Hits[0].Text, "Implemented text");
        StringAssert.Contains(comment.Anchor.Authored!.Declaration, "Original authored text");
    }

    [TestMethod]
    public void ImplementedEditOnAnUnnamedSibling_SurvivesRebuildWithoutTypeCandidates()
    {
        const string before = """
            <StackPanel>
                <TextBlock AutomationProperties.AutomationId="PageTitle" Text="WinUI Sample App" />
                <TextBlock AutomationProperties.AutomationId="PageSubtitle" Text="Used by tests" />
                <TextBlock FontSize="18" Text="Repro heading" />
                <StackPanel><TextBlock x:Name="CounterText" Text="Count: 0" /></StackPanel>
                <TextBlock x:Name="ResultText" Text="" />
            </StackPanel>
            """;
        var comment = Capture(before, containing: "Repro heading");
        var path = Path.Combine(_root, "Page.xaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            """<TextBlock FontSize="18" Text="Repro heading" />""",
            "<!-- one -->\n<!-- two -->\n<TextBlock FontSize=\"24\" Text=\"Repro heading\" />", StringComparison.Ordinal));
        var view = View(comment);
        Assert.IsTrue(view.AnchorConfirmed, string.Join(", ", view.Hits.Select(hit => $"{hit.Line} {hit.Via} {hit.Confidence}")));
        Assert.IsFalse(view.RequiresConfirmation);
        Assert.AreEqual(comment.Anchor.Line + 2, view.Hits.Single().Line);
        Assert.IsNull(CommentViewBuilder.AnchorHealthWarning(view));
    }

    [TestMethod]
    public void AnInsertedSiblingAtTheOldTreePositionIsNotAStrongAnchor()
    {
        var comment = Capture("""
            <StackPanel>
                <TextBlock FontSize="18" Text="Repro heading" />
            </StackPanel>
            """);
        var path = Path.Combine(_root, "Page.xaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            """<TextBlock FontSize="18" Text="Repro heading" />""",
            """<TextBlock Text="Inserted" /><TextBlock FontSize="24" Text="Repro heading" />""", StringComparison.Ordinal));
        var view = View(comment);
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.IsTrue(view.RequiresConfirmation);
        Assert.IsFalse(view.Hits.Any(hit => hit.Confidence == "strong"));
    }

    [TestMethod]
    public void IdenticalDeclarationsUnderAnonymousParents_DoNotUseTheOldLineToChoose()
    {
        var comment = Capture("""
            <Grid><TextBlock Text="Same" /></Grid>
            <Grid><TextBlock Text="Same" /></Grid>
            """);
        var view = View(comment);
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.IsTrue(view.Ambiguous);
        Assert.IsTrue(view.RequiresConfirmation);
        Assert.AreEqual(2, view.Candidates.Count);
    }

    [TestMethod]
    public void DuplicateNamesRequireTheirCapturedNamedAncestorScope()
    {
        var comment = Capture("""
            <Grid x:Name="First"><TextBlock x:Name="Label" Text="Same" /></Grid>
            <Grid x:Name="Second"><TextBlock x:Name="Label" Text="Same" /></Grid>
            """);
        var view = View(comment);
        Assert.IsTrue(view.AnchorConfirmed);
        Assert.AreEqual(comment.Anchor.Line, view.Hits.Single().Line);
        var path = Path.Combine(_root, "Page.xaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("x:Name=\"First\"", "x:Name=\"Renamed\"", StringComparison.Ordinal));
        view = View(comment);
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.IsTrue(view.RequiresConfirmation);
    }

    [TestMethod]
    public void TemplateDeclarationsRequireConfirmation_RepeatedRuntimeInstancesOfOneDeclarationDoNot()
    {
        var templated = Capture("""
            <DataTemplate x:DataType="local:Row">
                <TextBlock x:Name="Label" Text="{x:Bind Name}" />
            </DataTemplate>
            """);
        Assert.IsTrue(templated.Anchor.Authored!.Templated);
        Assert.IsFalse(View(templated).AnchorConfirmed);
        Assert.IsTrue(View(templated).RequiresConfirmation);
        var repeated = Capture("""<TextBlock Text="Same" />""", unique: false);
        var view = View(repeated);
        Assert.IsTrue(view.AnchorConfirmed, "One matching declaration is one place to edit, however often it is instantiated.");
        Assert.IsFalse(view.RequiresConfirmation);
        Assert.IsNull(CommentViewBuilder.AnchorHealthWarning(view));
    }

    [TestMethod]
    public void TypeOnlyCandidatesAreDroppedWhenTheDeclarationMatches()
    {
        var comment = Capture("""
            <Grid><TextBlock Text="Same" /></Grid>
            <Grid><TextBlock Text="Same" /></Grid>
            <TextBlock Text="Unrelated" />
            """);
        var view = View(comment);
        Assert.AreEqual(2, view.Hits.Count);
        Assert.IsTrue(view.Hits.All(hit => hit.Via == "declaration"), string.Join(", ", view.Hits.Select(hit => hit.Via)));
    }

    [TestMethod]
    public void HistoricalLocationIsShownOnlyWithoutACurrentMatch()
    {
        const string target = """<TextBlock x:Name="Label" Text="Same" />""";
        var comment = Capture(target);
        var path = Path.Combine(_root, "Page.xaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace(target, "\n\n" + target, StringComparison.Ordinal));
        Assert.IsNull(CommentViewBuilder.HistoricalLocation(View(comment)), "A moved but confirmed anchor has one location: the current one.");
        comment.Anchor.Authored = null;
        Assert.AreEqual($"Page.xaml:{comment.Anchor.Line}", CommentViewBuilder.HistoricalLocation(View(comment)));
        comment.Anchor.Line = null;
        Assert.AreEqual("Page.xaml", CommentViewBuilder.HistoricalLocation(View(comment)), "No line is not printed as an empty ':'.");
    }

    [TestMethod]
    public void MissingAuthoredXamlOrMovedSourceCannotProduceAStrongHit()
    {
        var comment = Capture("""<TextBlock x:Name="Label" Text="Same" />""");
        File.Move(Path.Combine(_root, "Page.xaml"), Path.Combine(_root, "Moved.xaml"));
        var view = View(comment);
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.IsTrue(view.RequiresConfirmation);
        Assert.AreEqual("Moved.xaml", view.Hits.Single().File);
        Assert.AreEqual("Page.xaml", comment.Anchor.SourceFile);
        File.Move(Path.Combine(_root, "Moved.xaml"), Path.Combine(_root, "Page.xaml"));
        comment.Anchor.Authored = null;
        view = View(comment);
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.IsTrue(view.RequiresConfirmation);
    }

    [TestMethod]
    public void MatchingShortTypeAndNameInAnotherNamespaceAreNotStrongIdentity()
    {
        var comment = Capture("""<TextBlock x:Name="Label" Text="Same" />""");
        var path = Path.Combine(_root, "Page.xaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("<TextBlock ", "<TextBlock xmlns=\"urn:foreign\" ", StringComparison.Ordinal));
        var view = View(comment);
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.IsTrue(view.RequiresConfirmation);
    }

    [TestMethod]
    public void CaptureRejectsChangedDeclaration_AndResolvesInheritedXamlNamespaces()
    {
        var comment = Capture("""<TextBlock x:Name="Label" Text="{x:Bind Vm.Title}" />""");
        var declaration = CommentAuthoredIdentity.Read(Path.Combine(_root, "Page.xaml"), _root)
            .Single(element => element.Element.Name.LocalName == "TextBlock");
        Assert.IsNotNull(comment.Anchor.Authored);
        Assert.ThrowsExactly<InvalidDataException>(() => CommentAuthoredIdentity.Capture(_root, "Page.xaml",
            declaration, declaration.Text.Replace("Vm.Title", "Vm.Other", StringComparison.Ordinal), true));
    }

    [TestMethod]
    public void MultilineDeclarationKeepsAttributeWhitespaceAndRejectsChangedLiteral()
    {
        var comment = Capture("<TextBlock\n    Text=\"{x:Bind Vm.Title,\n      Mode=OneWay}\" />");
        var declaration = CommentAuthoredIdentity.Read(Path.Combine(_root, "Page.xaml"), _root)
            .Single(element => element.Element.Name.LocalName == "TextBlock");
        var display = "<TextBlock\n  Text=\"{x:Bind Vm.Title,\n      Mode=OneWay}\" />";
        Assert.AreEqual(comment.Anchor.Authored!.Signature,
            CommentAuthoredIdentity.Capture(_root, "Page.xaml", declaration, display, true).Signature);
        Assert.ThrowsExactly<InvalidDataException>(() => CommentAuthoredIdentity.Capture(_root, "Page.xaml",
            declaration, display.Replace("      Mode", "  Mode", StringComparison.Ordinal), true));
    }

    [TestMethod]
    [DataRow("declaration")]
    [DataRow("scope")]
    [DataRow("structure")]
    [DataRow("type")]
    [DataRow("signature")]
    public void AuthoredMetadataHasBoundedFields(string field)
    {
        var identity = Capture("""<TextBlock Text="Original" />""").Anchor.Authored!;
        var oversized = new string('a', 64 * 1024 + 1);
        switch (field)
        {
            case "declaration": identity.Declaration = oversized; break;
            case "scope": identity.Scope = oversized; break;
            case "structure": identity.Structure = oversized; break;
            case "type": identity.Type = oversized; break;
            default: identity.Signature = new string('z', 64); break;
        }
        Assert.ThrowsExactly<InvalidDataException>(() => CommentAuthoredIdentity.Validate(identity));
    }

    [TestMethod]
    public void ANameChangedToAnAutomationIdIsOnlyASuggestion()
    {
        var comment = Capture("""<TextBlock x:Name="Label" Text="Same" />""");
        var path = Path.Combine(_root, "Page.xaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("x:Name=\"Label\"",
            "AutomationProperties.AutomationId=\"Label\"", StringComparison.Ordinal));
        var view = View(comment);
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.IsTrue(view.RequiresConfirmation);
        Assert.AreEqual("AutomationId", view.Hits.Single().Via);
    }

    [TestMethod]
    public void UnprefixedNamesQualifyTheTargetAndItsAncestorScope()
    {
        var comment = Capture("""<Grid Name="First"><TextBlock Name="Label" Text="Same" /></Grid>""");
        Assert.AreEqual("Label", comment.Anchor.Identity.Name);
        Assert.IsTrue(View(comment).AnchorConfirmed);
        var path = Path.Combine(_root, "Page.xaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("Name=\"First\"", "Name=\"Second\"", StringComparison.Ordinal));
        var view = View(comment);
        Assert.IsFalse(view.AnchorConfirmed);
        Assert.IsTrue(view.RequiresConfirmation);
    }
}
