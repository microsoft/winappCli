// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Property classifier/serializer tests compare literal and resource-authored rows together:
// identical Local precedence must not erase their different authored origins.

#include "DevToolsRead.h"

#include <cstdio>
#include <string>
#include "DevToolsTestCheck.h"

static bool Contains(const std::wstring& hay, const wchar_t* needle)
{
    return hay.find(needle) != std::wstring::npos;
}

// ---------------------------------------------------------------------------------------------------------
// authoredKind
// ---------------------------------------------------------------------------------------------------------

static void Test_ClassifyAuthored()
{
    CheckEqW(DevToolsRead_ClassifyAuthored(L"{ThemeResource TextFillColorSecondaryBrush}"), L"themeResource",
              "a {ThemeResource} is classified as one");
    CheckEqW(DevToolsRead_ClassifyAuthored(L"{StaticResource CountToText}"), L"staticResource",
              "a {StaticResource} is classified as one");
    CheckEqW(DevToolsRead_ClassifyAuthored(L"{x:Bind Vm.Title, Mode=OneWay}"), L"xBind",
              "an {x:Bind} keeps its x: prefix and is NOT collapsed into {Binding}");
    CheckEqW(DevToolsRead_ClassifyAuthored(L"{Binding Title, Mode=TwoWay}"), L"binding",
              "a runtime {Binding} is classified as one");
    CheckEqW(DevToolsRead_ClassifyAuthored(L"{TemplateBinding Padding}"), L"templateBinding",
              "a {TemplateBinding} is classified as one");
    CheckEqW(DevToolsRead_ClassifyAuthored(L"{x:Null}"), L"customMarkup",
              "markup we have no specific handling for stays OUT of the literal bucket");
    CheckEqW(DevToolsRead_ClassifyAuthored(L"theme brush"), L"literal",
              "plain text is a literal");
    CheckEqW(DevToolsRead_ClassifyAuthored(L"#FF0000FF"), L"literal",
              "a hex colour is a literal");
    CheckEqW(DevToolsRead_ClassifyAuthored(L""), L"",
              "nothing authored classifies as nothing, not as a literal");
    // The XAML markup ESCAPE. "{}" exists so an author can write a value whose first character is a brace,
    // so it is the one string where treating a leading '{' as markup is provably wrong.
    CheckEqW(DevToolsRead_ClassifyAuthored(L"{}{NotMarkup}"), L"literal",
              "the {} markup escape is a literal, not a markup extension");
    CheckEqW(DevToolsRead_ClassifyAuthored(L"  {ThemeResource X}"), L"themeResource",
              "leading whitespace does not hide the markup");
}

// Compare both origin cases together to reject a hardcoded classification.
static void Test_TheRowNoLongerContradictsItself()
{
    // Both rows report valueSource "Local" -- and the runtime is not lying. A resolved {ThemeResource} IS
    // applied as a local value. That is exactly why a pill derived from valueSource labels them identically
    // while their origin lines say different things.
    const std::wstring themeSource = L"Local";
    const std::wstring localSource = L"Local";
    Check(themeSource == localSource,
           "PREMISE: valueSource cannot tell a {ThemeResource} row from a literal row -- both say Local");

    const std::wstring themeKind = DevToolsRead_ClassifyAuthored(L"{ThemeResource TextFillColorSecondaryBrush}");
    const std::wstring localKind = DevToolsRead_ClassifyAuthored(L"#FF0000FF");
    Check(themeKind != localKind,
           "CONTROL: authoredKind DOES tell them apart -- the two rows must not report the same origin");
    CheckEqW(themeKind, L"themeResource", "the {ThemeResource} row's origin is the resource, not Local");
    CheckEqW(localKind, L"literal",       "the literal row's origin is the literal");
}

// ---------------------------------------------------------------------------------------------------------
// writeType, derived from the DECLARED type instead of a 20-name table
// ---------------------------------------------------------------------------------------------------------

static void Test_DeriveWriteType()
{
    CheckEqW(DevToolsRead_DeriveWriteType(L"Windows.Foundation.Double", false), L"Double",
              "a Double-declared property is written as a Double");
    CheckEqW(DevToolsRead_DeriveWriteType(L"Windows.Foundation.String", false), L"String", "String");
    CheckEqW(DevToolsRead_DeriveWriteType(L"Windows.Foundation.Boolean", false), L"Boolean", "Boolean");
    CheckEqW(DevToolsRead_DeriveWriteType(L"Microsoft.UI.Xaml.Thickness", false), L"Thickness", "Thickness");
    CheckEqW(DevToolsRead_DeriveWriteType(L"Microsoft.UI.Xaml.CornerRadius", false), L"CornerRadius", "CornerRadius");
    // A Brush-declared property is written by CONSTRUCTING a solid brush, never by recolouring the brush
    // already attached -- which may be the app's single shared instance of a theme brush.
    CheckEqW(DevToolsRead_DeriveWriteType(L"Microsoft.UI.Xaml.Media.Brush", false), L"SolidColorBrush",
              "a Brush-declared property writes a NEW SolidColorBrush");
    CheckEqW(DevToolsRead_DeriveWriteType(L"Microsoft.UI.Xaml.Media.SolidColorBrush", false), L"SolidColorBrush",
              "so does one already holding a SolidColorBrush");
    CheckEqW(DevToolsRead_DeriveWriteType(L"Microsoft.UI.Xaml.Visibility", true), L"Visibility",
              "an enum writes by member name, under its short type name");
    // The conservative half. A type with no XAML string parser we have exercised reports NO write type, which
    // renders the row read-only -- rather than offering an editor whose write silently does nothing.
    CheckEqW(DevToolsRead_DeriveWriteType(L"Microsoft.UI.Xaml.Controls.ControlTemplate", false), L"",
              "a type no literal can construct is reported read-only, not guessed at");
    CheckEqW(DevToolsRead_DeriveWriteType(L"", false), L"", "no declared type, no write type");

    // The REGRESSION this replaces. The old map answered by NAME, so it knew Width and knew nothing else; a
    // pane that now shows every property would have rendered all of them read-only.
    Check(!DevToolsRead_DeriveWriteType(L"Windows.Foundation.Double", false).empty() &&
           !DevToolsRead_DeriveWriteType(L"Microsoft.UI.Xaml.Thickness", false).empty(),
           "CONTROL: a property the old 20-name table never heard of is still editable when its type is");
}

// ---------------------------------------------------------------------------------------------------------
// expandable complex values, and when their children may be written
// ---------------------------------------------------------------------------------------------------------

static void Test_ExpandableKind()
{
    CheckEqW(DevToolsRead_ExpandableKind(L"Microsoft.UI.Xaml.Media.SolidColorBrush"), L"brush",
              "a solid brush opens as a brush");
    CheckEqW(DevToolsRead_ExpandableKind(L"Microsoft.UI.Xaml.Media.CompositeTransform"), L"transform",
              "a CompositeTransform opens as a transform -- nine flat doubles, not a tree");
    CheckEqW(DevToolsRead_ExpandableKind(L"Microsoft.UI.Xaml.Media.ScaleTransform"), L"transform", "ScaleTransform");
    // A template opens another tree; a transform exposes supported leaf values.
    CheckEqW(DevToolsRead_ExpandableKind(L"Microsoft.UI.Xaml.Controls.ControlTemplate"), L"",
              "a ControlTemplate does not expand -- opening it yields a tree, not leaf values");
    CheckEqW(DevToolsRead_ExpandableKind(L"Windows.Foundation.Double"), L"", "a Double has no structure to open");
}

static void Test_ChildEditingIsGatedOnOwnership()
{
    Check(DevToolsRead_ChildEditingIsSafe(L"Local", L"literal"),
           "a locally-authored value's children are this element's to edit");
    Check(!DevToolsRead_ChildEditingIsSafe(L"Local", L"themeResource"),
           "a {ThemeResource} brush is SHARED -- editing its Color in place would recolour the whole app");
    Check(!DevToolsRead_ChildEditingIsSafe(L"Local", L"staticResource"),
           "so is a {StaticResource} one");
    Check(!DevToolsRead_ChildEditingIsSafe(L"Style", L"literal"),
           "a value handed out by a style is shared with every element using that style");
    Check(!DevToolsRead_ChildEditingIsSafe(L"Default", L""),
           "so is the type default");
}

// ---------------------------------------------------------------------------------------------------------
// the serializer
// ---------------------------------------------------------------------------------------------------------

static DevToolsReadProp MakeProp(const wchar_t* name, const wchar_t* value, const wchar_t* type)
{
    DevToolsReadProp p;
    p.name = name; p.value = value; p.type = type; p.editKind = L"text";
    return p;
}

static void Test_SerializeEmitsAuthoredKindWithAuthored()
{
    std::vector<DevToolsReadProp> props;
    DevToolsReadProp fg = MakeProp(L"Foreground", L"#9E000000", L"Microsoft.UI.Xaml.Media.SolidColorBrush");
    fg.editKind = L"color";
    fg.source = L"Local";
    fg.authored = L"{ThemeResource TextFillColorSecondaryBrush}";
    fg.authoredKind = DevToolsRead_ClassifyAuthored(fg.authored);
    props.push_back(fg);

    const std::wstring json = DevToolsRead_SerializeProps(42, props, L"available");
    Check(Contains(json, L"\"authored\":\"{ThemeResource TextFillColorSecondaryBrush}\""),
           "the authored expression is on the wire");
    Check(Contains(json, L"\"authoredKind\":\"themeResource\""),
           "and so is its classification -- a client must not have to re-parse the expression");
    Check(Contains(json, L"\"valueSource\":\"Local\""),
           "valueSource is still reported honestly: the runtime really did apply it locally");
}

static void Test_SerializeOmitsAuthoredKindWhenNothingWasAuthored()
{
    std::vector<DevToolsReadProp> props;
    props.push_back(MakeProp(L"Width", L"Auto", L"Windows.Foundation.Double"));
    const std::wstring json = DevToolsRead_SerializeProps(1, props, L"available");
    Check(!Contains(json, L"authoredKind"),
           "absence stays meaningful: no authored value means no authoredKind key at all");
}

static void Test_SerializeUnsetIsAStateNotAValue()
{
    // The old row put the string "Auto" in a box that only accepts a Double, so the pane displayed a
    // value no user input could ever restore.
    std::vector<DevToolsReadProp> props;
    DevToolsReadProp w = MakeProp(L"Width", L"Auto", L"Windows.Foundation.Double");
    w.editKind = L"number";
    w.writeType = L"Double";
    w.valueState = L"unset";
    props.push_back(w);
    const std::wstring json = DevToolsRead_SerializeProps(1, props, L"available");
    Check(Contains(json, L"\"valueState\":\"unset\""),
           "an unset Double is reported as a STATE, so a surface can render a placeholder");
    Check(Contains(json, L"\"writeType\":\"Double\""),
           "and it stays writable -- unset is not read-only");
}

static void Test_SerializeChildren()
{
    std::vector<DevToolsReadProp> props;
    DevToolsReadProp fg = MakeProp(L"Foreground", L"#FF0000FF", L"Microsoft.UI.Xaml.Media.SolidColorBrush");
    fg.editKind = L"color";
    fg.source = L"Local";

    DevToolsReadProp kind = MakeProp(L"Kind", L"SolidColorBrush", L"Windows.Foundation.String");
    DevToolsReadProp color = MakeProp(L"Color", L"#FF0000FF", L"Windows.UI.Color");
    color.editKind = L"color"; color.writeType = L"Color";
    DevToolsReadProp op = MakeProp(L"Opacity", L"1", L"Windows.Foundation.Double");
    op.editKind = L"number"; op.writeType = L"Double";
    fg.children.push_back(kind);
    fg.children.push_back(color);
    fg.children.push_back(op);
    props.push_back(fg);

    const std::wstring json = DevToolsRead_SerializeProps(7, props, L"available");
    Check(Contains(json, L"\"children\":["), "an expandable value carries its children");
    Check(Contains(json, L"\"name\":\"Color\""), "the leaf a user actually edits is one of them");
    Check(Contains(json, L"\"name\":\"Opacity\""), "so is Opacity");
    // A child row is an ORDINARY row. If it were a cut-down shape, a client would need a second renderer for
    // the inside of a brush -- which is how the pane ended up with a type name in a textbox and two dropdowns
    // beneath it in the first place.
    Check(Contains(json, L"\"name\":\"Color\",\"value\":\"#FF0000FF\",\"valueType\":\"Windows.UI.Color\",")
           && Contains(json, L"\"editKind\":\"color\""),
           "a child is serialized with the SAME row anatomy as a top-level row");

    // The value we were flattening to a type name is now the row you open, and the type name is not the value.
    Check(!Contains(json, L"\"value\":\"{SolidColorBrush}\""),
           "CONTROL: the brush's value is a colour, never the literal token {SolidColorBrush}");
}

static void Test_SerializeStaysValidJsonOnOneLine()
{
    std::vector<DevToolsReadProp> props;
    DevToolsReadProp p = MakeProp(L"Text", L"line\none\t\"quoted\"", L"Windows.Foundation.String");
    p.authored = L"{Binding Path=\"A\"}";
    p.authoredKind = DevToolsRead_ClassifyAuthored(p.authored);
    DevToolsReadProp child = MakeProp(L"Color", L"a\\b", L"Windows.UI.Color");
    p.children.push_back(child);
    props.push_back(p);
    const std::wstring json = DevToolsRead_SerializeProps(1, props, L"available");
    Check(json.find(L'\n') == std::wstring::npos && json.find(L'\r') == std::wstring::npos,
           "the payload stays on ONE wire line even with newlines in a value and a nested child");
    Check(Contains(json, L"\\\"quoted\\\""), "quotes inside a value are escaped");
    Check(Contains(json, L"a\\\\b"), "so are backslashes inside a CHILD's value");
}

static void Test_SerializeChainCarriesConfirmedDeclaration()
{
    std::vector<DevToolsReadProp> props;
    DevToolsReadProp p = MakeProp(L"Background", L"#FF0067C0", L"Microsoft.UI.Xaml.Media.SolidColorBrush");
    DevToolsReadChainEntry style{ L"Style", L"#FF0067C0", L"Microsoft.UI.Xaml.Controls.Button",
        L"ms-appx:///App.xaml", 14, L"App.xaml", 12, true };
    DevToolsReadChainEntry fallback{ L"Default", L"", L"", L"", 0, L"", 0, false };
    p.chain = { style, fallback };
    props.push_back(p);
    const std::wstring json = DevToolsRead_SerializeProps(1, props, L"available");
    Check(Contains(json, L"\"file\":\"ms-appx:///App.xaml\",\"line\":14,\"authoredFileName\":\"App.xaml\",\"authoredLineNumber\":12"),
           "a chain source keeps its runtime position and adds its confirmed declaration start");
    Check(Contains(json, L"{\"source\":\"Default\",\"value\":\"\",\"winner\":false}"),
           "a source without a confirmed declaration adds nothing");
}

// Core properties are an ordering hint, not a read filter.
static void Test_CorePropsAreAnOrderingHintNotAReadGate()
{
    Check(DevToolsRead_IsCoreProp(L"Foreground"), "Foreground is core");
    Check(DevToolsRead_IsCoreProp(L"RenderTransform"),
           "RenderTransform is core -- it was absent from the payload entirely, not merely un-expandable");
    Check(!DevToolsRead_IsCoreProp(L"CharacterSpacing"),
           "CharacterSpacing is not core -- and must still reach the pane, which is the point of dropping the gate");
}

// Multi-field types and primary expression arguments.

static void Test_FieldLabelsAreMeasuredNotAssumed()
{
    CheckEqW(std::to_wstring(DevToolsRead_FieldLabels(L"Microsoft.UI.Xaml.Thickness").size()), L"4",
              "a Thickness is four fields");
    CheckEqW(DevToolsRead_FieldLabels(L"Microsoft.UI.Xaml.Thickness")[0], L"Left", "named, not indexed");
    CheckEqW(std::to_wstring(DevToolsRead_FieldLabels(L"Windows.Foundation.Point").size()), L"2", "a Point is two");
    CheckEqW(std::to_wstring(DevToolsRead_FieldLabels(L"Windows.Foundation.Numerics.Vector3").size()), L"3",
              "a Vector3 is three -- the type round 2 never mentioned and which outnumbers Point 4:1");
    CheckEqW(DevToolsRead_FieldLabels(L"Windows.Foundation.Numerics.Vector3")[2], L"Z", "and its third field is Z");
    CheckEqW(std::to_wstring(DevToolsRead_FieldLabels(L"Microsoft.UI.Xaml.CornerRadius").size()), L"4", "CornerRadius");

    // Size is not a comma-separated writable field type; Rect is not an included editor type.
    Check(DevToolsRead_FieldLabels(L"Windows.Foundation.Size").empty(),
           "CONTROL: Size is NOT a field editor -- its value is 'WxH' and a write to it fails");
    Check(DevToolsRead_FieldLabels(L"Windows.Foundation.Rect").empty(),
           "CONTROL: Rect is not built for -- it appears on no element measured");
    Check(DevToolsRead_FieldLabels(L"Windows.Foundation.Numerics.Matrix4x4").empty(),
           "CONTROL: a Matrix4x4 arrives as nested braces, not a flat row of boxes");
    Check(DevToolsRead_FieldLabels(L"Windows.Foundation.Double").empty(), "a Double is one number, not a field list");
}

static void Test_PointAndVector3AreWritable_OnAMeasuredRoundTrip()
{
    // Both were reported read-only while the write demonstrably worked -- ~195 rows per element refused an
    // editor that would have committed. Added on a watched round-trip, not on their shape.
    CheckEqW(DevToolsRead_DeriveWriteType(L"Windows.Foundation.Point", false), L"Point",
              "Point writes -- RenderTransformOrigin took '0.25,0.75' and read back changed");
    CheckEqW(DevToolsRead_DeriveWriteType(L"Windows.Foundation.Numerics.Vector3", false), L"Vector3",
              "Vector3 writes -- Translation took '1,2,3' and read back changed");
    // The control: the same measurement REFUSED Size, so it must stay read-only. Without this, "add every
    // struct that has commas" would pass.
    CheckEqW(DevToolsRead_DeriveWriteType(L"Windows.Foundation.Size", false), L"",
              "CONTROL: Size stays read-only -- writing RenderSize returns an error, as it should");
}

static void Test_AuthoredKeyIsWhatTheChipShowsBesideIt()
{
    CheckEqW(DevToolsRead_AuthoredKey(L"{ThemeResource TextFillColorSecondaryBrush}"), L"TextFillColorSecondaryBrush",
              "a resource row shows its KEY, not the whole markup");
    CheckEqW(DevToolsRead_AuthoredKey(L"{StaticResource CardPadding}"), L"CardPadding", "static resource key");
    CheckEqW(DevToolsRead_AuthoredKey(L"{x:Bind Vm.Title, Mode=OneWay}"), L"Vm.Title",
              "an x:Bind shows its PATH; Mode describes the expression rather than naming it");
    CheckEqW(DevToolsRead_AuthoredKey(L"{Binding Title}"), L"Title", "a binding shows its path");
    CheckEqW(DevToolsRead_AuthoredKey(L"{Binding Path=Title}"), L"Title",
              "Path= is an assignment; the reader wants the value on its right");
    // A comma INSIDE a nested extension must not truncate the argument.
    CheckEqW(DevToolsRead_AuthoredKey(L"{Binding Count, Converter={StaticResource X}}"), L"Count",
              "a nested extension's comma does not cut the path short");
    CheckEqW(DevToolsRead_AuthoredKey(L"{x:Null}"), L"",
              "markup with no argument has no key -- the chip stands alone rather than inventing one");
    CheckEqW(DevToolsRead_AuthoredKey(L"{Binding ElementName=Box, Path=Text}"), L"",
              "two named arguments have no single key, and guessing one would be worse than none");
    CheckEqW(DevToolsRead_AuthoredKey(L"0,0,8,0"), L"",
              "CONTROL: a literal has no key -- its value is already in the editor beside the line");
}

static void Test_SerializeFields()
{
    std::vector<DevToolsReadProp> props;
    DevToolsReadProp p = MakeProp(L"CenterPoint", L"0,0,0", L"Windows.Foundation.Numerics.Vector3");
    p.editKind = L"fields";
    p.fields = DevToolsRead_FieldLabels(p.type);
    p.writeType = L"Vector3";
    props.push_back(p);
    const std::wstring json = DevToolsRead_SerializeProps(3, props, L"available");
    Check(Contains(json, L"\"editKind\":\"fields\""), "the multi-field kind is general, not named after Thickness");
    Check(Contains(json, L"\"fields\":[\"X\",\"Y\",\"Z\"]"),
           "the LABELS ride on the wire -- '0,0,0' cannot say whether it is a Vector3 or an RGB triple");

    std::vector<DevToolsReadProp> plain;
    plain.push_back(MakeProp(L"Opacity", L"1", L"Windows.Foundation.Double"));
    Check(!Contains(DevToolsRead_SerializeProps(3, plain, L"available"), L"fields"),
           "CONTROL: a single-value row carries no field labels");
}

// Parent layout facts.

static void Test_LayoutParentContext()
{
    DevToolsReadLayout L;
    L.handle = 9;
    L.haveRender = true;  L.renderW = 1288; L.renderH = 37.6f;
    L.haveOffset = true;  L.offsetX = 40;   L.offsetY = 40;
    L.haveInParent = true; L.inParentX = 40; L.inParentY = 40;
    L.parentType = L"Microsoft.UI.Xaml.Controls.StackPanel";
    L.parentOrientation = L"Vertical";
    L.parentPadding = L"40,40,40,40";
    L.parentSpacing = L"8";
    L.haveParentBox = true; L.parentW = 1368; L.parentH = 831;
    L.childIndex = 0; L.childCount = 3;

    const std::wstring json = DevToolsRead_SerializeLayout(L);
    Check(Contains(json, L"\"orientation\":\"Vertical\""), "the parent's orientation is on the wire");
    Check(Contains(json, L"\"padding\":\"40,40,40,40\""), "and its padding -- the number that explains the offset");
    Check(Contains(json, L"\"spacing\":\"8\""), "and its spacing");
    Check(Contains(json, L"\"childIndex\":0") && Contains(json, L"\"childCount\":3"),
           "and where this element sits among its siblings, which is what makes 'no spacing applies' sayable");
    // inParent is NOT a duplicate of offset. The root-space number cannot be compared against the parent's
    // own padding, which is the thing that usually explains it.
    Check(Contains(json, L"\"inParent\":{\"x\":40,\"y\":40}"),
           "the offset INSIDE the parent is reported separately from the root-space one");
    Check(Contains(json, L"\"offset\":{\"x\":40,\"y\":40}"), "and the root-space one is still there");
}

static void Test_LayoutOmitsWhatItCouldNotRead()
{
    // Absence is meaningful here exactly as it is on a property row: a parent that is not a panel HAS no
    // Orientation, and emitting a default would be indistinguishable from one somebody set.
    DevToolsReadLayout L;
    L.handle = 1;
    L.haveRender = true; L.renderW = 10; L.renderH = 10;
    L.parentType = L"Microsoft.UI.Xaml.Controls.Border";
    const std::wstring json = DevToolsRead_SerializeLayout(L);
    Check(!Contains(json, L"orientation"), "CONTROL: a parent with no Orientation reports none");
    Check(!Contains(json, L"spacing"),     "CONTROL: nor a Spacing it does not have");
    Check(!Contains(json, L"childIndex"),  "CONTROL: an unknown sibling position is omitted, not reported as 0");
    Check(!Contains(json, L"inParent"),    "CONTROL: an unread in-parent offset is omitted, not reported as 0,0");
    Check(Contains(json, L"\"type\":\"Microsoft.UI.Xaml.Controls.Border\""), "the parent itself is still named");
}

static void Test_LayoutGridPlacementOnlyWhenThereIsOne()
{
    DevToolsReadLayout g;
    g.handle = 2; g.parentType = L"Microsoft.UI.Xaml.Controls.Grid";
    g.gridRow = L"2"; g.gridColumn = L"1"; g.gridColumnSpan = L"3";
    Check(Contains(DevToolsRead_SerializeLayout(g), L"\"grid\":{\"row\":\"2\",\"column\":\"1\""),
           "an element in a Grid reports its cell");
    Check(Contains(DevToolsRead_SerializeLayout(g), L"\"columnSpan\":\"3\""), "including a span");

    // EVERY element carries these attached properties. A child of a StackPanel has them all at 0, and
    // printing a cell there would send a reader looking for a Grid that is not in the tree.
    DevToolsReadLayout s;
    s.handle = 3; s.parentType = L"Microsoft.UI.Xaml.Controls.StackPanel";
    Check(!Contains(DevToolsRead_SerializeLayout(s), L"grid"),
           "CONTROL: an element with no Grid placement reports none rather than a row of zeroes");
}

// Strip assembly qualification before taking a short type name; version dots are not namespace dots.
static void Test_ShortTypeNameSurvivesAnAssemblyQualifiedName()
{
    CheckEqW(DevToolsRead_ShortTypeName(L"winui_binding_fixture.FixtureViewModel"), L"FixtureViewModel",
              "a plain XAML type name reduces to its class");
    CheckEqW(DevToolsRead_ShortTypeName(
                  L"winui_binding_fixture.UnknownToXamlViewModel, winui-binding-fixture, Version=1.0.0.0, "
                  L"Culture=neutral, PublicKeyToken=null"),
              L"UnknownToXamlViewModel",
              "an ASSEMBLY-QUALIFIED CLR name reduces to its class, not to a fragment of its version");
    // The regression itself, stated as the thing that must NOT come back.
    Check(DevToolsRead_ShortTypeName(
               L"winui_binding_fixture.UnknownToXamlViewModel, winui-binding-fixture, Version=1.0.0.0, "
               L"Culture=neutral, PublicKeyToken=null").find(L"PublicKeyToken") == std::wstring::npos,
           "and never carries the assembly qualification into the row");

    // WinRT names have no comma at all, so the cut must be a no-op for every type the pane already showed.
    CheckEqW(DevToolsRead_ShortTypeName(L"Microsoft.UI.Xaml.Media.SolidColorBrush"), L"SolidColorBrush",
              "CONTROL: an ordinary WinRT type is unchanged");
    CheckEqW(DevToolsRead_ShortTypeName(L"Windows.Foundation.IReference`1<String>"), L"IReference`1<String>",
              "a projected generic keeps its argument list");
    // The generic argument carries its OWN namespace and its own assembly qualification. Neither belongs to
    // the outer type, and a depth-blind scan would return "String]]" or cut the name in half.
    CheckEqW(DevToolsRead_ShortTypeName(
                  L"System.Collections.Generic.List`1[[System.String, mscorlib, Version=4.0.0.0]], mscorlib, "
                  L"Version=4.0.0.0"),
              L"List`1[[System.String, mscorlib, Version=4.0.0.0]]",
              "a CLR generic is cut at the OUTER qualification, not inside its argument list");
}

// One label for where a row's value came from, used by `get-property` (through the wire's `origin`) and, compact, by
// the DevTools window's pill. What the developer authored on this element wins over the precedence slot: the runtime
// applies a {ThemeResource} as a plain local value, so the slot alone would say "Local".
static void Test_OriginLabel()
{
    auto full = [](const wchar_t* kind, const wchar_t* authored, const wchar_t* key, const wchar_t* binding, const wchar_t* source) {
        return DevToolsRead_OriginLabel(kind, authored, key, binding, source, false);
    };
    auto pill = [](const wchar_t* kind, const wchar_t* source) {
        return DevToolsRead_OriginLabel(kind, L"", L"", L"", source, true);
    };
    CheckEqW(full(L"themeResource", L"{ThemeResource TextFillColorSecondaryBrush}", L"TextFillColorSecondaryBrush", L"", L"Local"),
              L"ThemeResource TextFillColorSecondaryBrush", "a theme resource names its key");
    CheckEqW(full(L"themeResource", L"{ThemeResource}", L"", L"", L"Local"), L"ThemeResource", "a theme resource without a key");
    CheckEqW(full(L"staticResource", L"{StaticResource CardBrush}", L"CardBrush", L"", L"Local"),
              L"StaticResource CardBrush", "a static resource names its key");
    CheckEqW(full(L"literal", L"4", L"", L"", L"Local"), L"Local", "a literal authored here is a local value");
    CheckEqW(full(L"", L"", L"", L"", L"Local"), L"Local", "nothing authored reads as the precedence slot");
    CheckEqW(full(L"xBind", L"{x:Bind ViewModel.CanSave}", L"", L"", L"Local"), L"{x:Bind ViewModel.CanSave}", "an x:Bind reads as authored");
    CheckEqW(full(L"xBind", L"", L"", L"", L"Local"), L"x:Bind", "an x:Bind without its markup");
    CheckEqW(full(L"binding", L"", L"", L"{Binding Title}", L"Local"), L"{Binding Title}", "a binding falls back to the live binding");
    CheckEqW(full(L"binding", L"", L"", L"", L"Local"), L"Binding", "a binding with neither");
    CheckEqW(full(L"templateBinding", L"", L"", L"", L"Local"), L"TemplateBinding", "a template binding");
    CheckEqW(full(L"customMarkup", L"", L"", L"", L"Local"), L"Markup", "other markup");
    CheckEqW(full(L"", L"", L"", L"{Binding CanSave}", L"local"), L"{Binding CanSave}", "a bound value is the binding, not its slot");
    CheckEqW(full(L"", L"", L"", L"", L""), L"", "no origin at all");
    CheckEqW(pill(L"themeResource", L"Local"), L"Theme resource", "the pill says theme resource");
    CheckEqW(pill(L"staticResource", L"Local"), L"Static resource", "the pill says static resource");
    CheckEqW(pill(L"xBind", L"Local"), L"x:Bind", "the pill says x:Bind");
    CheckEqW(pill(L"binding", L"Local"), L"Binding", "the pill says Binding");
    CheckEqW(pill(L"templateBinding", L"Local"), L"TemplateBinding", "the pill says TemplateBinding");
    CheckEqW(pill(L"customMarkup", L"Local"), L"Markup", "the pill says Markup");
    CheckEqW(pill(L"literal", L"Style"), L"Style", "otherwise the pill is the precedence slot");

    DevToolsReadProp themed;
    themed.name = L"Foreground"; themed.value = L"#C5FFFFFF"; themed.type = L"SolidColorBrush"; themed.source = L"Local";
    themed.authored = L"{ThemeResource TextFillColorSecondaryBrush}"; themed.authoredKind = L"themeResource";
    themed.authoredKey = L"TextFillColorSecondaryBrush";
    Check(Contains(DevToolsRead_SerializeProps(1, { themed }, L""), L"\"origin\":\"ThemeResource TextFillColorSecondaryBrush\""),
           "Property.get rows carry the label as origin");
}

int RunReadTests()
{
    std::printf("DevToolsRead tests -- the property payload's classifiers and serializer\n");
    Test_ClassifyAuthored();
    Test_OriginLabel();
    Test_TheRowNoLongerContradictsItself();
    Test_DeriveWriteType();
    Test_ExpandableKind();
    Test_ChildEditingIsGatedOnOwnership();
    Test_SerializeEmitsAuthoredKindWithAuthored();
    Test_SerializeOmitsAuthoredKindWhenNothingWasAuthored();
    Test_SerializeUnsetIsAStateNotAValue();
    Test_SerializeChildren();
    Test_SerializeStaysValidJsonOnOneLine();
    Test_SerializeChainCarriesConfirmedDeclaration();
    Test_CorePropsAreAnOrderingHintNotAReadGate();
    Test_FieldLabelsAreMeasuredNotAssumed();
    Test_PointAndVector3AreWritable_OnAMeasuredRoundTrip();
    Test_AuthoredKeyIsWhatTheChipShowsBesideIt();
    Test_SerializeFields();
    Test_LayoutParentContext();
    Test_LayoutOmitsWhatItCouldNotRead();
    Test_LayoutGridPlacementOnlyWhenThereIsOne();
    Test_ShortTypeNameSurvivesAnAssemblyQualifiedName();
    return g_testFailures;
}
