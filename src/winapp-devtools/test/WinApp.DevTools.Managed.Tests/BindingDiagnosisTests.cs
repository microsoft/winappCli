// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
public sealed class BindingDiagnosisTests
{
    private sealed class Source
    {
        public string Value => "healthy";
        public string StaleTitle => "NEW-NOT-NOTIFIED";
        public Source? Child => null;
        public string Explodes => throw new InvalidOperationException("getter failed");
    }

    private sealed class Converter(object? parameter, string language, bool throws) : IValueConverter
    {
        public object Convert(object value, Type targetType, object actualParameter, string actualLanguage)
        {
            if (throws || !Equals(parameter, actualParameter) || language != actualLanguage)
                throw new InvalidOperationException("Incorrect converter context");
            return value;
        }

        public object ConvertBack(object value, Type targetType, object actualParameter, string actualLanguage)
            => throw new NotSupportedException();
    }

    private static string Walk(object? parameter, string language, IValueConverter? converter,
        string path = nameof(Source.Value), object? source = null, string mode = "OneWay", string kind = "{Binding}")
        => BindingDiagnosis.Evaluate(path, source ?? new Source(), "Source", kind, mode,
            typeof(string), "Text", () => "healthy", converter, parameter, language);

    [TestMethod]
    [DataRow("2", "fr-FR")]
    [DataRow("2", "")]
    [DataRow(null, "de-DE")]
    [DataRow(null, "")]
    public void WalkerUsesActualConverterContext(string? parameter, string language)
    {
        var converter = new Converter(parameter, language, false);
        Assert.AreEqual("healthy", converter.Convert("healthy", typeof(string), parameter!, language));
        using var result = JsonDocument.Parse(Walk(parameter, language, converter));
        Assert.AreEqual("evaluated", result.RootElement.GetProperty("state").GetString(), result.RootElement.ToString());
    }

    [TestMethod]
    public void ActualConverterExceptionRemainsAFault()
    {
        using var result = JsonDocument.Parse(Walk("2", "fr-FR", new Converter("2", "fr-FR", true)));
        Assert.AreEqual("threw", result.RootElement.GetProperty("state").GetString());
    }

    [TestMethod]
    public void NoConverterStillWalksTheSource()
    {
        using var result = JsonDocument.Parse(Walk(null, "", null));
        Assert.AreEqual("evaluated", result.RootElement.GetProperty("state").GetString());
    }

    [TestMethod]
    [DataRow("People[1].Title", "{Binding}")]
    [DataRow("FormatTitle(Vm.Title)", "{x:Bind}")]
    [DataRow("(Grid.Row)", "{Binding}")]
    [DataRow("Vm..Title", "{x:Bind}")]
    public void UnsupportedSyntaxNeverInventsAMissingMember(string path, string kind)
    {
        using var result = JsonDocument.Parse(Walk(null, "", null, path, kind: kind));
        Assert.AreEqual("unavailable", result.RootElement.GetProperty("state").GetString());
        Assert.AreEqual(path, result.RootElement.GetProperty("path").GetString());
        StringAssert.Contains(result.RootElement.GetProperty("reason").GetString()!, "syntax");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(".")]
    public void PathlessConverterStillExecutes(string path)
    {
        using var result = JsonDocument.Parse(Walk("2", "fr-FR", new Converter("2", "fr-FR", true), path, 7));
        Assert.AreEqual("threw", result.RootElement.GetProperty("state").GetString());
    }

    [TestMethod]
    [DataRow("OneWay", "{Binding}")]
    [DataRow("OneWay", "{x:Bind}")]
    [DataRow("OneTime", "{Binding}")]
    [DataRow("OneTime", "{x:Bind}")]
    public void ValueObservationsDoNotClaimFreshnessOrMissingNotifications(string mode, string kind)
    {
        using var result = JsonDocument.Parse(Walk(null, "", null, nameof(Source.StaleTitle), mode: mode, kind: kind));
        var answer = result.RootElement;
        Assert.AreEqual("evaluated", answer.GetProperty("state").GetString());
        Assert.AreEqual("NEW-NOT-NOTIFIED", answer.GetProperty("sourceValue").GetString());
        Assert.AreEqual("healthy", answer.GetProperty("targetValue").GetString());
        StringAssert.Contains(answer.GetProperty("reason").GetString()!, "not checked");
    }

    [TestMethod]
    [DataRow("Missing", "bad-segment")]
    [DataRow("Child.Value", "null-link")]
    [DataRow("Explodes", "threw")]
    public void SimplePathFaultsRemainUseful(string path, string state)
    {
        using var result = JsonDocument.Parse(Walk(null, "", null, path));
        Assert.AreEqual(state, result.RootElement.GetProperty("state").GetString());
    }

    private sealed class FormattingConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) => $"formatted:{value}";
        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
    }

    [TestMethod]
    public void PathlessConverterObservationsKeepInputAndOutputSeparate()
    {
        using var result = JsonDocument.Parse(Walk(null, "", new FormattingConverter(), "", 7));
        Assert.AreEqual("evaluated", result.RootElement.GetProperty("state").GetString());
        Assert.AreEqual("7", result.RootElement.GetProperty("sourceValue").GetString());
        Assert.AreEqual("formatted:7", result.RootElement.GetProperty("resolvedValue").GetString());
        Assert.AreEqual("healthy", result.RootElement.GetProperty("targetValue").GetString());
    }

    [TestMethod]
    public void UnreadableTargetIsDisclosedWithoutChangingPathObservation()
    {
        using var result = JsonDocument.Parse(BindingDiagnosis.Evaluate("Value", new Source(), "Source", "{Binding}", "OneWay",
            typeof(string), "Text", () => throw new InvalidOperationException("target unavailable")));
        Assert.AreEqual("evaluated", result.RootElement.GetProperty("state").GetString());
        StringAssert.Contains(result.RootElement.GetProperty("targetUnavailable").GetString()!, "target unavailable");
        Assert.IsFalse(result.RootElement.TryGetProperty("targetValue", out _));
    }

    [TestMethod]
    public void NullAndDifferentClrTypeRemainObservationsNotDeliveryFailures()
    {
        using var nullResult = JsonDocument.Parse(BindingDiagnosis.Evaluate("", null, "Source", "{Binding}", "OneTime",
            typeof(string), "Text", () => "TargetNullValue"));
        Assert.AreEqual("evaluated", nullResult.RootElement.GetProperty("state").GetString());
        Assert.AreEqual("(null)", nullResult.RootElement.GetProperty("sourceValue").GetString());
        using var typeResult = JsonDocument.Parse(Walk(null, "", null, "", 7));
        Assert.AreEqual("silent", typeResult.RootElement.GetProperty("state").GetString());
        Assert.AreEqual("Int32", typeResult.RootElement.GetProperty("resolvedType").GetString());
        Assert.AreEqual("healthy", typeResult.RootElement.GetProperty("targetValue").GetString());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(".")]
    public void PathlessSourceStillReceivesTypeEvaluation(string path)
    {
        using var result = JsonDocument.Parse(Walk(null, "", null, path, "whole-source"));
        Assert.AreEqual("evaluated", result.RootElement.GetProperty("state").GetString());
        Assert.AreEqual("whole-source", result.RootElement.GetProperty("sourceValue").GetString());
    }
}
