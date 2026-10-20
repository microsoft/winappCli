// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.UIAutomation.Tests;

[TestClass]
public class UiActionFlowDocumentTests
{
    private string _directory = null!;
    private static UiActionTarget Input => new() { AutomationId = "Input", ControlType = "Edit" };
    private static UiActionFlow Example => new()
    {
        ProcessName = "FlowDemo",
        InitialFocus = Input,
        Steps =
        [
            new() { Kind = "text", Target = Input, Parameter = "text1" },
            new() { Kind = "key", Target = Input, Keys = "tab", ElapsedMs = 100 },
            new() { Kind = "key", Target = new() { AutomationId = "Save", ControlType = "Button" }, Keys = "enter", ElapsedMs = 200 }
        ]
    };

    [TestInitialize]
    public void Initialize() => _directory = Directory.CreateTempSubdirectory("winapp-action-document-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public void SourceGeneratedRoundTripPreservesParametersWithoutValues()
    {
        var path = Path.Combine(_directory, "flow.json");
        UiActionFlowDocument.WriteNew(path, Example);
        var flow = UiActionFlowDocument.Read(path);
        Assert.AreEqual(1, flow.SchemaVersion);
        Assert.AreEqual(Input, flow.InitialFocus);
        CollectionAssert.AreEqual(Example.Steps.ToArray(), flow.Steps.ToArray());
        Assert.AreEqual("text1", flow.Steps[0].Parameter);
        Assert.IsNull(flow.Steps[0].Text);
        Assert.ThrowsExactly<IOException>(() => UiActionFlowDocument.WriteNew(path, Example));
    }

    [TestMethod]
    public void HandAuthoredStepsKeepTheirDeclaredDefaults()
    {
        var json = """
            {
              "schemaVersion": 1,
              "status": "recorded",
              "processName": "Demo",
              "steps": [
                {
                  "kind": "assert",
                  "target": { "automationId": "Status", "controlType": "Text" },
                  "property": "Name",
                  "expected": "Saved"
                }
              ]
            }
            """;
        var flow = System.Text.Json.JsonSerializer.Deserialize(
            Encoding.UTF8.GetBytes(json), UiActionDocumentJsonContext.Default.UiActionFlow)!;
        Assert.AreEqual(1, flow.Steps[0].ClickCount);
        Assert.AreEqual(0.5, flow.Steps[0].XFraction);
        Assert.AreEqual(0.5, flow.Steps[0].YFraction);
        UiActionFlowDocument.Validate(flow);
    }

    [TestMethod]
    public void LiteralTextAndEmptyReplacementArePreserved()
    {
        var flow = Example with
        {
            Steps =
            [
                new() { Kind = "text", Target = Input, Text = "one\r\ntwo \"three\"" },
                new() { Kind = "text", Target = Input, Text = "" }
            ]
        };
        var path = Path.Combine(_directory, "literal.json");
        UiActionFlowDocument.WriteNew(path, flow);
        var actual = UiActionFlowDocument.Read(path);
        CollectionAssert.AreEqual(flow.Steps.ToArray(), actual.Steps.ToArray());
    }

    [TestMethod]
    public void ApprovalAndAllParametersAreRequiredBeforeReplay()
    {
        Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.ValidateReplay(Example, new()));
        Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.ValidateReplay(Example, new() { AcceptRecording = true }));
        UiActionFlowDocument.ValidateReplay(Example, new()
        {
            AcceptRecording = true,
            Parameters = new Dictionary<string, string> { ["text1"] = "Test value" }
        });
        var error = Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.ValidateReplay(Example, new()
        {
            AcceptRecording = true,
            Parameters = new Dictionary<string, string> { ["extra"] = "DO-NOT-LOG-THIS" }
        }));
        Assert.IsFalse(error.Message.Contains("DO-NOT-LOG-THIS", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IncompleteFilesAreReadableButNeverReplayable()
    {
        var flow = Example with { Status = "incomplete", Steps = [], InitialFocus = null };
        var path = Path.Combine(_directory, "partial.json");
        UiActionFlowDocument.WriteNew(path, flow);
        var read = UiActionFlowDocument.Read(path);
        Assert.AreEqual("incomplete_recording", Assert.ThrowsExactly<UiActionException>(
            () => UiActionFlowDocument.ValidateReplay(read, new() { AcceptRecording = true })).Code);
    }

    [TestMethod]
    public void DuplicateUnknownAndWrongTypedFieldsAreRejected()
    {
        foreach (var json in new[]
        {
            """{"processName":"demo","processName":"other","steps":[]}""",
            """{"processName":"demo","status":"incomplete","startCommand":"malicious"}""",
            """{"processName":"demo","status":"incomplete","schemaVersion":"1"}""",
            """{"processName":"demo","status":"incomplete","warnings":null}""",
            """{"processName":"demo","status":"incomplete","steps":null}""",
            """{"processName":"demo","status":"incomplete","initialFocus":{"automationId":"A","AutomationId":"B","controlType":"Edit"}}""",
            """{"processName":"demo","status":"incomplete","steps":[{"kind":"key","target":{"automationId":"Input","controlType":"Edit"},"keys":"tab","shell":"not-allowed"}]}""",
            """{"processName":"demo","status":"incomplete"} trailing"""
        })
        {
            Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.Parse(Encoding.UTF8.GetBytes(json)));
        }
    }

    [TestMethod]
    public void UnsupportedVersionAndInvalidAnchorsAreRejected()
    {
        Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.Validate(Example with { SchemaVersion = 2 }));
        Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.Validate(Example with { Steps = [] }));
        Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.Validate(Example with { InitialFocus = null }, true));
        Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.Validate(Example with
        {
            InitialFocus = new() { Name = "input contains data", ControlType = "Edit" }
        }));
        Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.Validate(Example with
        {
            InitialFocus = new() { AutomationId = "Input", Name = "also a name", ControlType = "Edit" }
        }));
    }

    [TestMethod]
    [DataRow("win+l")]
    [DataRow("alt+tab")]
    [DataRow("ctrl+alt+delete")]
    [DataRow("alt+f4")]
    [DataRow("ctrl+shift+esc")]
    [DataRow("printscreen")]
    [DataRow("hello")]
    [DataRow("tab enter")]
    [DataRow("text=secret")]
    [DataRow("vk=0x41")]
    [DataRow("shift+vk=0x41")]
    [DataRow("ctrl+v")]
    [DataRow("ctrl+c")]
    [DataRow("ctrl+x")]
    [DataRow("shift+insert")]
    [DataRow("ctrl+vk=0x5B")]
    [DataRow("ctrl+vk=0x2C")]
    [DataRow("vk=0xAD")]
    [DataRow("vk=0x90")]
    [DataRow("vk=0xA2")]
    public void KeyActionsCannotSmuggleTextSystemOrClipboardInput(string keys)
    {
        var flow = Example with { Steps = [new() { Kind = "key", Target = Input, Keys = keys }] };
        Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.Validate(flow));
    }

    [TestMethod]
    [DataRow("tab")]
    [DataRow("shift+tab")]
    [DataRow("enter")]
    [DataRow("space")]
    [DataRow("ctrl+s")]
    [DataRow("f5")]
    [DataRow("down")]
    public void NavigationAndAppChordsAreAccepted(string keys)
    {
        UiActionFlowDocument.Validate(Example with { Steps = [new() { Kind = "key", Target = Input, Keys = keys }] });
    }

    [TestMethod]
    public void KindSpecificFieldsAndBoundsAreChecked()
    {
        foreach (var step in new UiActionStep[]
        {
            new() { Kind = "click", Target = Input, Button = "middle" },
            new() { Kind = "click", Target = Input, Button = "left", XFraction = double.NaN },
            new() { Kind = "click", Target = Input, Button = "left", ClickCount = 3 },
            new() { Kind = "wheel", Target = Input, WheelDelta = 0 },
            new() { Kind = "key", Target = Input, Keys = "tab", Text = "secret" },
            new() { Kind = "text", Target = Input, Text = "value", Parameter = "text1" },
            new() { Kind = "text", Target = Input, Parameter = "../secret" },
            new() { Kind = "text", Target = Input, Text = new('x', UiActionFlowDocument.MaximumTextLength + 1) },
            new() { Kind = "assert", Target = Input, Property = "Execute", Expected = "command" },
            new() { Kind = "run", Target = Input }
        })
        {
            Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.Validate(Example with { Steps = [step] }));
        }
    }

    [TestMethod]
    public void AssertionsAreExplicitAndDoNotBecomeRecordedActions()
    {
        var assertion = new UiActionStep { Kind = "assert", Target = Input, Property = "Value", Expected = "Done" };
        UiActionFlowDocument.Validate(Example with { Steps = [assertion] });
        Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.Validate(Example with
        {
            Steps = [assertion with { Expected = null }]
        }));
    }

    [TestMethod]
    public void OversizedInputsAreRejectedBeforeParsing()
    {
        Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.Parse(new byte[UiActionFlowDocument.MaximumFileBytes + 1]));
        Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.Validate(Example with
        {
            Steps = Enumerable.Repeat(Example.Steps[0], UiActionFlowDocument.MaximumSteps + 1).ToArray()
        }));
    }

    [TestMethod]
    public void TimestampsMustBeOrderedButAreNotReplayDelays()
    {
        Assert.ThrowsExactly<UiActionException>(() => UiActionFlowDocument.Validate(Example with
        {
            Steps = [Example.Steps[1], Example.Steps[0]]
        }));
        UiActionFlowDocument.Validate(Example with
        {
            Steps = [Example.Steps[0] with { ElapsedMs = 3600000 }]
        });
    }
}
