// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[JsonSerializable(typeof(UiElement))]
internal partial class QueryElementJsonContext : JsonSerializerContext;
