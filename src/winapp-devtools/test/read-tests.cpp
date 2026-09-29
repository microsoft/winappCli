// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Property classifier/serializer tests compare literal and resource-authored rows together:
// identical Local precedence must not erase their different authored origins.

#include "DevToolsRead.h"

#include <cstdio>
#include <string>

static int g_readFailures = 0;

static void RCheck(bool cond, const char* what)
{
    if (!cond) { ++g_readFailures; std::printf("  FAIL  %s\n", what); }
    else       {                   std::printf("  ok    %s\n", what); }
}

static void RCheckEqW(const std::wstring& got, const std::wstring& want, const char* what)
{
    if (got != want) { ++g_readFailures; std::printf("  FAIL  %s (want \"%ls\", got \"%ls\")\n", what, want.c_str(), got.c_str()); }
    else             {                   std::printf("  ok    %s (\"%ls\")\n", what, got.c_str()); }
}

static bool Contains(const std::wstring& hay, const wchar_t* needle)
{
    return hay.find(needle) != std::wstring::npos;
}

// ---------------------------------------------------------------------------------------------------------
// authoredKind
// ---------------------------------------------------------------------------------------------------------

static void Test_ClassifyAuthored()
{
    RCheckEqW(DevToolsRead_ClassifyAuthored(L"{ThemeResource TextFillColorSecondaryBrush}"), L"themeResource",
              "a {ThemeResource} is classified as one");
    RCheckEqW(DevToolsRead_ClassifyAuthored(L"{StaticResource CountToText}"), L"staticResource",
              "a {StaticResource} is classified as one");
    RCheckEqW(DevToolsRead_ClassifyAuthored(L"{x:Bind Vm.Title, Mode=OneWay}"), L"xBind",
              "an {x:Bind} keeps its x: prefix and is NOT collapsed into {Binding}");
    RCheckEqW(DevToolsRead_ClassifyAuthored(L"{Binding Title, Mode=TwoWay}"), L"binding",
              "a runtime {Binding} is classified as one");
    RCheckEqW(DevToolsRead_ClassifyAuthored(L"{TemplateBinding Padding}"), L"templateBinding",
              "a {TemplateBinding} is classified as one");
    RCheckEqW(DevToolsRead_ClassifyAuthored(L"{x:Null}"), L"customMarkup",
              "markup we have no specific handling for stays OUT of the literal bucket");
    RCheckEqW(DevToolsRead_ClassifyAuthored(L"theme brush"), L"literal",
              "plain text is a literal");
    RCheckEqW(DevToolsRead_ClassifyAuthored(L"#FF0000FF"), L"literal",
              "a hex colour is a literal");
    RCheckEqW(DevToolsRead_ClassifyAuthored(L""), L"",
              "nothing authored classifies as nothing, not as a literal");
    // The XAML markup ESCAPE. "{}" exists so an author can write a value whose first character is a brace,
    // so it is the one string where treating a leading '{' as markup is provably wrong.
    RCheckEqW(DevToolsRead_ClassifyAuthored(L"{}{NotMarkup}"), L"literal",
              "the {} markup escape is a literal, not a markup extension");
    RCheckEqW(DevToolsRead_ClassifyAuthored(L"  {ThemeResource X}"), L"themeResource",
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
    RCheck(themeSource == localSource,
           "PREMISE: valueSource cannot tell a {ThemeResource} row from a literal row -- both say Local");

    const std::wstring themeKind = DevToolsRead_ClassifyAuthored(L"{ThemeResource TextFillColorSecondaryBrush}");
    const std::wstring localKind = DevToolsRead_ClassifyAuthored(L"#FF0000FF");
    RCheck(themeKind != localKind,
           "CONTROL: authoredKind DOES tell them apart -- the two rows must not report the same origin");
    RCheckEqW(themeKind, L"themeResource", "the {ThemeResource} row's origin is the resource, not Local");
    RCheckEqW(localKind, L"literal",       "the literal row's origin is the literal");
}

// ---------------------------------------------------------------------------------------------------------
// writeType, derived from the DECLARED type instead of a 20-name table
// ---------------------------------------------------------------------------------------------------------

static void Test_DeriveWriteType()
{
    RCheckEqW(DevToolsRead_DeriveWriteType(L"Windows.Foundation.Double", false), L"Double",
              "a Double-declared property is written as a Double");
    RCheckEqW(DevToolsRead_DeriveWriteType(L"Windows.Foundation.String", false), L"String", "String");
    RCheckEqW(DevToolsRead_DeriveWriteType(L"Windows.Foundation.Boolean", false), L"Boolean", "Boolean");
    RCheckEqW(DevToolsRead_DeriveWriteType(L"Microsoft.UI.Xaml.Thickness", false), L"Thickness", "Thickness");
    RCheckEqW(DevToolsRead_DeriveWriteType(L"Microsoft.UI.Xaml.CornerRadius", false), L"CornerRadius", "CornerRadius");
    // A Brush-declared property is written by CONSTRUCTING a solid brush, never by recolouring the brush
    // already attached -- which may be the app's single shared instance of a theme brush.
    RCheckEqW(DevToolsRead_DeriveWriteType(L"Microsoft.UI.Xaml.Media.Brush", false), L"SolidColorBrush",
              "a Brush-declared property writes a NEW SolidColorBrush");
    RCheckEqW(DevToolsRead_DeriveWriteType(L"Microsoft.UI.Xaml.Media.SolidColorBrush", false), L"SolidColorBrush",
              "so does one already holding a SolidColorBrush");
    RCheckEqW(DevToolsRead_DeriveWriteType(L"Microsoft.UI.Xaml.Visibility", true), L"Visibility",
              "an enum writes by member name, under its short type name");
    // The conservative half. A type with no XAML string parser we have exercised reports NO write type, which
    // renders the row read-only -- rather than offering an editor whose write silently does nothing.
    RCheckEqW(DevToolsRead_DeriveWriteType(L"Microsoft.UI.Xaml.Controls.ControlTemplate", false), L"",
              "a type no literal can construct is reported read-only, not guessed at");
    RCheckEqW(DevToolsRead_DeriveWriteType(L"", false), L"", "no declared type, no write type");

    // The REGRESSION this replaces. The old map answered by NAME, so it knew Width and knew nothing else; a
    // pane that now shows every property would have rendered all of them read-only.
    RCheck(!DevToolsRead_DeriveWriteType(L"Windows.Foundation.Double", false).empty() &&
           !DevToolsRead_DeriveWriteType(L"Microsoft.UI.Xaml.Thickness", false).empty(),
           "CONTROL: a property the old 20-name table never heard of is still editable when its type is");
}

// ---------------------------------------------------------------------------------------------------------
// expandable complex values, and when their children may be written
// ---------------------------------------------------------------------------------------------------------

static void Test_ExpandableKind()
{
    RCheckEqW(DevToolsRead_ExpandableKind(L"Microsoft.UI.Xaml.Media.SolidColorBrush"), L"brush",
              "a solid brush opens as a brush");
    RCheckEqW(DevToolsRead_ExpandableKind(L"Microsoft.UI.Xaml.Media.CompositeTransform"), L"transform",
              "a CompositeTransform opens as a transform -- nine flat doubles, not a tree");
    RCheckEqW(DevToolsRead_ExpandableKind(L"Microsoft.UI.Xaml.Media.ScaleTransform"), L"transform", "ScaleTransform");
    // A template opens another tree; a transform exposes supported leaf values.
    RCheckEqW(DevToolsRead_ExpandableKind(L"Microsoft.UI.Xaml.Controls.ControlTemplate"), L"",
              "a ControlTemplate does not expand -- opening it yields a tree, not leaf values");
    RCheckEqW(DevToolsRead_ExpandableKind(L"Windows.Foundation.Double"), L"", "a Double has no structure to open");
}

static void Test_ChildEditingIsGatedOnOwnership()
{
    RCheck(DevToolsRead_ChildEditingIsSafe(L"Local", L"literal"),
           "a locally-authored value's children are this element's to edit");
    RCheck(!DevToolsRead_ChildEditingIsSafe(L"Local", L"themeResource"),
           "a {ThemeResource} brush is SHARED -- editing its Color in place would recolour the whole app");
    RCheck(!DevToolsRead_ChildEditingIsSafe(L"Local", L"staticResource"),
           "so is a {StaticResource} one");
    RCheck(!DevToolsRead_ChildEditingIsSafe(L"Style", L"literal"),
           "a value handed out by a style is shared with every element using that style");
    RCheck(!DevToolsRead_ChildEditingIsSafe(L"Default", L""),
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
    RCheck(Contains(json, L"\"authored\":\"{ThemeResource TextFillColorSecondaryBrush}\""),
           "the authored expression is on the wire");
    RCheck(Contains(json, L"\"authoredKind\":\"themeResource\""),
           "and so is its classification -- a client must not have to re-parse the expression");
    RCheck(Contains(json, L"\"valueSource\":\"Local\""),
           "valueSource is still reported honestly: the runtime really did apply it locally");
}

static void Test_SerializeOmitsAuthoredKindWhenNothingWasAuthored()
{
    std::vector<DevToolsReadProp> props;
    props.push_back(MakeProp(L"Width", L"Auto", L"Windows.Foundation.Double"));
    const std::wstring json = DevToolsRead_SerializeProps(1, props, L"available");
    RCheck(!Contains(json, L"authoredKind"),
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
    RCheck(Contains(json, L"\"valueState\":\"unset\""),
           "an unset Double is reported as a STATE, so a surface can render a placeholder");
    RCheck(Contains(json, L"\"writeType\":\"Double\""),
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
    RCheck(Contains(json, L"\"children\":["), "an expandable value carries its children");
    RCheck(Contains(json, L"\"name\":\"Color\""), "the leaf a user actually edits is one of them");
    RCheck(Contains(json, L"\"name\":\"Opacity\""), "so is Opacity");
    // A child row is an ORDINARY row. If it were a cut-down shape, a client would need a second renderer for
    // the inside of a brush -- which is how the pane ended up with a type name in a textbox and two dropdowns
    // beneath it in the first place.
    RCheck(Contains(json, L"\"name\":\"Color\",\"value\":\"#FF0000FF\",\"valueType\":\"Windows.UI.Color\",")
           && Contains(json, L"\"editKind\":\"color\""),
           "a child is serialized with the SAME row anatomy as a top-level row");

    // The value we were flattening to a type name is now the row you open, and the type name is not the value.
    RCheck(!Contains(json, L"\"value\":\"{SolidColorBrush}\""),
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
    RCheck(json.find(L'\n') == std::wstring::npos && json.find(L'\r') == std::wstring::npos,
           "the payload stays on ONE wire line even with newlines in a value and a nested child");
    RCheck(Contains(json, L"\\\"quoted\\\""), "quotes inside a value are escaped");
    RCheck(Contains(json, L"a\\\\b"), "so are backslashes inside a CHILD's value");
}

// Core properties are an ordering hint, not a read filter.
static void Test_CorePropsAreAnOrderingHintNotAReadGate()
{
    RCheck(DevToolsRead_IsCoreProp(L"Foreground"), "Foreground is core");
    RCheck(DevToolsRead_IsCoreProp(L"RenderTransform"),
           "RenderTransform is core -- it was absent from the payload entirely, not merely un-expandable");
    RCheck(!DevToolsRead_IsCoreProp(L"CharacterSpacing"),
           "CharacterSpacing is not core -- and must still reach the pane, which is the point of dropping the gate");
}

// Multi-field types and primary expression arguments.

static void Test_FieldLabelsAreMeasuredNotAssumed()
{
    RCheckEqW(std::to_wstring(DevToolsRead_FieldLabels(L"Microsoft.UI.Xaml.Thickness").size()), L"4",
              "a Thickness is four fields");
    RCheckEqW(DevToolsRead_FieldLabels(L"Microsoft.UI.Xaml.Thickness")[0], L"Left", "named, not indexed");
    RCheckEqW(std::to_wstring(DevToolsRead_FieldLabels(L"Windows.Foundation.Point").size()), L"2", "a Point is two");
    RCheckEqW(std::to_wstring(DevToolsRead_FieldLabels(L"Windows.Foundation.Numerics.Vector3").size()), L"3",
              "a Vector3 is three -- the type round 2 never mentioned and which outnumbers Point 4:1");
    RCheckEqW(DevToolsRead_FieldLabels(L"Windows.Foundation.Numerics.Vector3")[2], L"Z", "and its third field is Z");
    RCheckEqW(std::to_wstring(DevToolsRead_FieldLabels(L"Microsoft.UI.Xaml.CornerRadius").size()), L"4", "CornerRadius");

    // Size is not a comma-separated writable field type; Rect is not an included editor type.
    RCheck(DevToolsRead_FieldLabels(L"Windows.Foundation.Size").empty(),
           "CONTROL: Size is NOT a field editor -- its value is 'WxH' and a write to it fails");
    RCheck(DevToolsRead_FieldLabels(L"Windows.Foundation.Rect").empty(),
           "CONTROL: Rect is not built for -- it appears on no element measured");
    RCheck(DevToolsRead_FieldLabels(L"Windows.Foundation.Numerics.Matrix4x4").empty(),
           "CONTROL: a Matrix4x4 arrives as nested braces, not a flat row of boxes");
    RCheck(DevToolsRead_FieldLabels(L"Windows.Foundation.Double").empty(), "a Double is one number, not a field list");
}

static void Test_PointAndVector3AreWritable_OnAMeasuredRoundTrip()
{
    // Both were reported read-only while the write demonstrably worked -- ~195 rows per element refused an
    // editor that would have committed. Added on a watched round-trip, not on their shape.
    RCheckEqW(DevToolsRead_DeriveWriteType(L"Windows.Foundation.Point", false), L"Point",
              "Point writes -- RenderTransformOrigin took '0.25,0.75' and read back changed");
    RCheckEqW(DevToolsRead_DeriveWriteType(L"Windows.Foundation.Numerics.Vector3", false), L"Vector3",
              "Vector3 writes -- Translation took '1,2,3' and read back changed");
    // The control: the same measurement REFUSED Size, so it must stay read-only. Without this, "add every
    // struct that has commas" would pass.
    RCheckEqW(DevToolsRead_DeriveWriteType(L"Windows.Foundation.Size", false), L"",
              "CONTROL: Size stays read-only -- writing RenderSize returns an error, as it should");
}

static void Test_AuthoredKeyIsWhatTheChipShowsBesideIt()
{
    RCheckEqW(DevToolsRead_AuthoredKey(L"{ThemeResource TextFillColorSecondaryBrush}"), L"TextFillColorSecondaryBrush",
              "a resource row shows its KEY, not the whole markup");
    RCheckEqW(DevToolsRead_AuthoredKey(L"{StaticResource CardPadding}"), L"CardPadding", "static resource key");
    RCheckEqW(DevToolsRead_AuthoredKey(L"{x:Bind Vm.Title, Mode=OneWay}"), L"Vm.Title",
              "an x:Bind shows its PATH; Mode describes the expression rather than naming it");
    RCheckEqW(DevToolsRead_AuthoredKey(L"{Binding Title}"), L"Title", "a binding shows its path");
    RCheckEqW(DevToolsRead_AuthoredKey(L"{Binding Path=Title}"), L"Title",
              "Path= is an assignment; the reader wants the value on its right");
    // A comma INSIDE a nested extension must not truncate the argument.
    RCheckEqW(DevToolsRead_AuthoredKey(L"{Binding Count, Converter={StaticResource X}}"), L"Count",
              "a nested extension's comma does not cut the path short");
    RCheckEqW(DevToolsRead_AuthoredKey(L"{x:Null}"), L"",
              "markup with no argument has no key -- the chip stands alone rather than inventing one");
    RCheckEqW(DevToolsRead_AuthoredKey(L"{Binding ElementName=Box, Path=Text}"), L"",
              "two named arguments have no single key, and guessing one would be worse than none");
    RCheckEqW(DevToolsRead_AuthoredKey(L"0,0,8,0"), L"",
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
    RCheck(Contains(json, L"\"editKind\":\"fields\""), "the multi-field kind is general, not named after Thickness");
    RCheck(Contains(json, L"\"fields\":[\"X\",\"Y\",\"Z\"]"),
           "the LABELS ride on the wire -- '0,0,0' cannot say whether it is a Vector3 or an RGB triple");

    std::vector<DevToolsReadProp> plain;
    plain.push_back(MakeProp(L"Opacity", L"1", L"Windows.Foundation.Double"));
    RCheck(!Contains(DevToolsRead_SerializeProps(3, plain, L"available"), L"fields"),
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
    RCheck(Contains(json, L"\"orientation\":\"Vertical\""), "the parent's orientation is on the wire");
    RCheck(Contains(json, L"\"padding\":\"40,40,40,40\""), "and its padding -- the number that explains the offset");
    RCheck(Contains(json, L"\"spacing\":\"8\""), "and its spacing");
    RCheck(Contains(json, L"\"childIndex\":0") && Contains(json, L"\"childCount\":3"),
           "and where this element sits among its siblings, which is what makes 'no spacing applies' sayable");
    // inParent is NOT a duplicate of offset. The root-space number cannot be compared against the parent's
    // own padding, which is the thing that usually explains it.
    RCheck(Contains(json, L"\"inParent\":{\"x\":40,\"y\":40}"),
           "the offset INSIDE the parent is reported separately from the root-space one");
    RCheck(Contains(json, L"\"offset\":{\"x\":40,\"y\":40}"), "and the root-space one is still there");
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
    RCheck(!Contains(json, L"orientation"), "CONTROL: a parent with no Orientation reports none");
    RCheck(!Contains(json, L"spacing"),     "CONTROL: nor a Spacing it does not have");
    RCheck(!Contains(json, L"childIndex"),  "CONTROL: an unknown sibling position is omitted, not reported as 0");
    RCheck(!Contains(json, L"inParent"),    "CONTROL: an unread in-parent offset is omitted, not reported as 0,0");
    RCheck(Contains(json, L"\"type\":\"Microsoft.UI.Xaml.Controls.Border\""), "the parent itself is still named");
}

static void Test_LayoutGridPlacementOnlyWhenThereIsOne()
{
    DevToolsReadLayout g;
    g.handle = 2; g.parentType = L"Microsoft.UI.Xaml.Controls.Grid";
    g.gridRow = L"2"; g.gridColumn = L"1"; g.gridColumnSpan = L"3";
    RCheck(Contains(DevToolsRead_SerializeLayout(g), L"\"grid\":{\"row\":\"2\",\"column\":\"1\""),
           "an element in a Grid reports its cell");
    RCheck(Contains(DevToolsRead_SerializeLayout(g), L"\"columnSpan\":\"3\""), "including a span");

    // EVERY element carries these attached properties. A child of a StackPanel has them all at 0, and
    // printing a cell there would send a reader looking for a Grid that is not in the tree.
    DevToolsReadLayout s;
    s.handle = 3; s.parentType = L"Microsoft.UI.Xaml.Controls.StackPanel";
    RCheck(!Contains(DevToolsRead_SerializeLayout(s), L"grid"),
           "CONTROL: an element with no Grid placement reports none rather than a row of zeroes");
}

// Strip assembly qualification before taking a short type name; version dots are not namespace dots.
static void Test_ShortTypeNameSurvivesAnAssemblyQualifiedName()
{
    RCheckEqW(DevToolsRead_ShortTypeName(L"winui_binding_fixture.FixtureViewModel"), L"FixtureViewModel",
              "a plain XAML type name reduces to its class");
    RCheckEqW(DevToolsRead_ShortTypeName(
                  L"winui_binding_fixture.UnknownToXamlViewModel, winui-binding-fixture, Version=1.0.0.0, "
                  L"Culture=neutral, PublicKeyToken=null"),
              L"UnknownToXamlViewModel",
              "an ASSEMBLY-QUALIFIED CLR name reduces to its class, not to a fragment of its version");
    // The regression itself, stated as the thing that must NOT come back.
    RCheck(DevToolsRead_ShortTypeName(
               L"winui_binding_fixture.UnknownToXamlViewModel, winui-binding-fixture, Version=1.0.0.0, "
               L"Culture=neutral, PublicKeyToken=null").find(L"PublicKeyToken") == std::wstring::npos,
           "and never carries the assembly qualification into the row");

    // WinRT names have no comma at all, so the cut must be a no-op for every type the pane already showed.
    RCheckEqW(DevToolsRead_ShortTypeName(L"Microsoft.UI.Xaml.Media.SolidColorBrush"), L"SolidColorBrush",
              "CONTROL: an ordinary WinRT type is unchanged");
    RCheckEqW(DevToolsRead_ShortTypeName(L"Windows.Foundation.IReference`1<String>"), L"IReference`1<String>",
              "a projected generic keeps its argument list");
    // The generic argument carries its OWN namespace and its own assembly qualification. Neither belongs to
    // the outer type, and a depth-blind scan would return "String]]" or cut the name in half.
    RCheckEqW(DevToolsRead_ShortTypeName(
                  L"System.Collections.Generic.List`1[[System.String, mscorlib, Version=4.0.0.0]], mscorlib, "
                  L"Version=4.0.0.0"),
              L"List`1[[System.String, mscorlib, Version=4.0.0.0]]",
              "a CLR generic is cut at the OUTER qualification, not inside its argument list");
}

int RunReadTests()
{
    std::printf("DevToolsRead tests -- the property payload's classifiers and serializer\n");
    Test_ClassifyAuthored();
    Test_TheRowNoLongerContradictsItself();
    Test_DeriveWriteType();
    Test_ExpandableKind();
    Test_ChildEditingIsGatedOnOwnership();
    Test_SerializeEmitsAuthoredKindWithAuthored();
    Test_SerializeOmitsAuthoredKindWhenNothingWasAuthored();
    Test_SerializeUnsetIsAStateNotAValue();
    Test_SerializeChildren();
    Test_SerializeStaysValidJsonOnOneLine();
    Test_CorePropsAreAnOrderingHintNotAReadGate();
    Test_FieldLabelsAreMeasuredNotAssumed();
    Test_PointAndVector3AreWritable_OnAMeasuredRoundTrip();
    Test_AuthoredKeyIsWhatTheChipShowsBesideIt();
    Test_SerializeFields();
    Test_LayoutParentContext();
    Test_LayoutOmitsWhatItCouldNotRead();
    Test_LayoutGridPlacementOnlyWhenThereIsOne();
    Test_ShortTypeNameSurvivesAnAssemblyQualifiedName();
    return g_readFailures;
}
