// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for DevToolsProtocol's JSON reader -- the parser every DevTools protocol client's input passes through.
//
// The parser runs INSIDE the inspected app, on the pipe thread. Its stated contract is that malformed input
// becomes a JSON-RPC ParseError and never a crash. Unbounded recursion breaks that contract in the one way
// the surrounding guards cannot cover: a Windows stack overflow is not catchable by the `catch (...)` around
// HandleRpcLine, so a deeply nested line terminates the developer's app outright.
//
// Following this directory's convention, the nesting tests carry a positive control: the same input is first
// run through a depth-UNAWARE recursive parse to show the shape really does recurse without bound, before
// asserting the shipping parser refuses it. A suite that only shows the fix passing proves nothing about
// whether it could detect the failure.
//
// Build/run: scripts/test-native-units.ps1 (also invoked by src/winapp-devtools/build-devtools.ps1).

#include "DevToolsProtocol.h"
#include "DevToolsProtocolSchema.h"

#include <cstdio>
#include <string>

int g_protocolFailures = 0;

static void PCheck(bool cond, const char* what)
{
    if (!cond) { ++g_protocolFailures; std::printf("  FAIL  %s\n", what); }
    else       {                       std::printf("  ok    %s\n", what); }
}

// ---- positive control ------------------------------------------------------------------------------------
// A faithful, depth-UNAWARE recursive descent over the same bracket shape. This is the control group: it
// demonstrates that the input really is unbounded-recursive, so the shipping parser's refusal below is
// meaningful rather than vacuous. Deliberately counts frames instead of actually overflowing the stack --
// a test that crashes the test process proves the point but cannot report it.
static int ControlRecursionDepth(const std::wstring& text)
{
    int depth = 0, maxDepth = 0;
    for (wchar_t ch : text) {
        if (ch == L'[' || ch == L'{') { ++depth; if (depth > maxDepth) maxDepth = depth; }
        else if (ch == L']' || ch == L'}') { --depth; }
    }
    return maxDepth;
}

static std::wstring NestedArray(int depth)
{
    std::wstring s;
    s.reserve((size_t)depth * 2);
    for (int i = 0; i < depth; ++i) s += L'[';
    for (int i = 0; i < depth; ++i) s += L']';
    return s;
}

static void TestRealParamsStillParse()
{
    std::printf("Real request params still parse\n");

    DevToolsJson v;
    PCheck(DevToolsJsonParse(L"{\"handle\":\"12345\",\"depth\":3,\"name\":\"Foreground\"}", v), "typical params object parses");
    PCheck(v.type == DevToolsJsonType::Object, "  parsed as an object");
    const DevToolsJson* h = v.Find(L"handle");
    PCheck(h && h->type == DevToolsJsonType::String && h->str == L"12345", "  string member round-trips");
    const DevToolsJson* d = v.Find(L"depth");
    PCheck(d && d->type == DevToolsJsonType::Number && (int)d->num == 3, "  number member round-trips");

    DevToolsJson arr;
    PCheck(DevToolsJsonParse(L"[1,\"two\",true,null,{\"k\":[1,2]}]", arr), "mixed nested array parses");
    PCheck(arr.type == DevToolsJsonType::Array && arr.arr.size() == 5, "  array has 5 entries");

    DevToolsJson esc;
    PCheck(DevToolsJsonParse(L"{\"s\":\"a\\\"b\\\\c\\nd\"}", esc), "escapes parse");
    const DevToolsJson* s = esc.Find(L"s");
    PCheck(s && s->str == L"a\"b\\c\nd", "  escapes decode to the right characters");
}

static void TestCanonicalWireHandles()
{
    std::printf("Canonical wire handles reject bridge-delimiter injection\n");
    unsigned long long value = 0;
    PCheck(DevToolsParseWireHandle(L"18446744073709551615", false, &value)
           && value == UINT64_MAX, "  maximum uint64 handle parses");
    PCheck(DevToolsParseWireHandle(L"0", true, &value) && value == 0,
           "  explicit zero parses only when the caller permits it");
    PCheck(!DevToolsParseWireHandle(L"0", false, &value), "  zero is refused for element handles");
    PCheck(!DevToolsParseWireHandle(L"01", false, &value), "  leading zeroes are refused");
    PCheck(!DevToolsParseWireHandle(L"18446744073709551616", false, &value), "  uint64 overflow is refused");
    PCheck(!DevToolsParseWireHandle(L"123|clear|Text", false, &value),
           "  pipe-delimited binding operation injection is refused");
    PCheck(!DevToolsParseWireHandle(L"-1", false, &value), "  signed handles are refused");
    PCheck(!DevToolsParseWireHandle(L" 1", false, &value), "  whitespace is refused");
}

static void TestMalformedIsRejectedNotCrashed()
{
    std::printf("Malformed input fails the parse cleanly\n");

    DevToolsJson v;
    PCheck(!DevToolsJsonParse(L"{\"a\":}", v), "missing value rejected");
    PCheck(!DevToolsJsonParse(L"{\"a\" 1}", v), "missing colon rejected");
    PCheck(!DevToolsJsonParse(L"[1,2", v), "unterminated array rejected");
    PCheck(!DevToolsJsonParse(L"{\"a\":1} trailing", v), "trailing garbage rejected");
    PCheck(!DevToolsJsonParse(L"\"unterminated", v), "unterminated string rejected");
    PCheck(!DevToolsJsonParse(L"", v), "empty input rejected");
}

static void TestDeepNestingIsRefusedRatherThanOverflowing()
{
    std::printf("Deeply nested input is refused, not recursed\n");

    // The pipe's line cap is 64KB, so an attacker gets tens of thousands of brackets on one line.
    const int kHostile = 20000;
    const std::wstring hostile = NestedArray(kHostile);
    PCheck(hostile.size() < 64u * 1024u, "hostile line fits inside the tap's 64KB line cap");

    // Control: the same input really does demand unbounded recursion depth.
    PCheck(ControlRecursionDepth(hostile) == kHostile,
           "control: input shape requires 20000 nested frames (unbounded recursion would overflow)");

    // Shipping parser: refuses rather than recursing to that depth.
    DevToolsJson v;
    PCheck(!DevToolsJsonParse(hostile, v), "hostile nesting is rejected as a parse error");

    // The cap must not be so tight that legitimate params break. Real params are 1-3 deep.
    DevToolsJson shallow;
    PCheck(DevToolsJsonParse(NestedArray(8), shallow), "8-deep nesting (well beyond any real request) still parses");

    // Nested objects take the same path as nested arrays.
    std::wstring objs;
    for (int i = 0; i < 5000; ++i) objs += L"{\"a\":";
    objs += L"1";
    for (int i = 0; i < 5000; ++i) objs += L"}";
    DevToolsJson o;
    PCheck(!DevToolsJsonParse(objs, o), "hostile object nesting is rejected too");
}

static bool ArrayContains(const DevToolsJson* array, const wchar_t* value)
{
    if (!array || array->type != DevToolsJsonType::Array) return false;
    for (const auto& item : array->arr) {
        if (item.type == DevToolsJsonType::String && item.str == value) return true;
    }
    return false;
}

static bool RegistryContainsType(const wchar_t* name)
{
    size_t typeCount = 0;
    const DevToolsProtocolType* types = DevToolsProtocolTypes(&typeCount);
    for (size_t i = 0; i < typeCount; ++i) {
        if (std::wstring(types[i].name) == name) return true;
    }
    return false;
}

static void TestAdvertisedCapabilitiesComeFromRegistry()
{
    std::printf("Advertised capabilities come from the compiled schema registry\n");

    size_t methodCount = 0;
    const DevToolsProtocolMethod* methods = DevToolsProtocolMethods(&methodCount);
    size_t publicCount = 0, internalCount = 0;
    for (size_t i = 0; i < methodCount; ++i) {
        if (methods[i].visibility == DevToolsVisibility::Public) ++publicCount;
        else ++internalCount;
    }
    // Positive control: the registry really does compile in at least one Internal.* entry (and it is NOT
    // named like a public verb) -- otherwise the exclusion asserted below would pass vacuously, because
    // there would be nothing internal to exclude.
    PCheck(internalCount > 0, "  the compiled registry contains at least one Internal.* entry");
    const DevToolsProtocolMethod* perf = DevToolsProtocolFindMethod(L"Internal.perf");
    PCheck(perf && perf->visibility == DevToolsVisibility::Internal,
           "  the performance back-channel is internal but still registry-gated");
    PCheck(perf && perf->access == DevToolsAccess::Mutation,
           "  performance instrumentation requires mutation posture");

    DevToolsJson capabilities;
    const std::wstring json = DevToolsProtocolCapabilitiesJson(1234, DevToolsAccess::Mutation, 7, L"0f1e2d3c4b5a69788796a5b4c3d2e1f0");
    PCheck(DevToolsJsonParse(json, capabilities), "DevTools.negotiate result parses");
    PCheck(capabilities.GetString(L"protocol") == L"winapp-devtools", "  protocol identity is present");
    PCheck(capabilities.GetString(L"protocolVersion") == L"0" && capabilities.GetBool(L"experimental", false), "  an experimental protocol is version 0");
    PCheck(capabilities.GetString(L"connectionId") == L"7", "  connection id is preserved");
    // The owner token is what a client presents to establish, and hand back, process-global UI state. It is
    // reported separately from connectionId on purpose: the id is public in every event's `origin`, so an
    // ownership claim keyed on it could be made by any client that has seen one event.
    PCheck(capabilities.GetString(L"ownerToken") == L"0f1e2d3c4b5a69788796a5b4c3d2e1f0",
           "  the session's owner token is returned");
    PCheck(capabilities.GetString(L"ownerToken") != capabilities.GetString(L"connectionId"),
           "  the owner token is not the (guessable, publicly echoed) connection id");

    // The negotiate reply is assembled by concatenation, not by a serializer, so a token carrying a quote or a
    // backslash would produce a reply no client could parse -- and a client that cannot parse the handshake
    // cannot connect at all. The token is defined as lowercase hex and is filtered to exactly that on the way
    // out, which makes the malformed reply structurally impossible rather than merely unlikely.
    DevToolsJson hostile;
    const std::wstring hostileJson = DevToolsProtocolCapabilitiesJson(
        1234, DevToolsAccess::Read, 8, LR"(ab", "posture":"mutation", "x":"\)");
    PCheck(DevToolsJsonParse(hostileJson, hostile), "a token carrying JSON syntax still produces a parseable reply");
    const std::wstring emitted = hostile.GetString(L"ownerToken");
    bool hexOnly = true;
    for (wchar_t c : emitted) {
        if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'))) hexOnly = false;
    }
    PCheck(hexOnly, "  only hex survives, so a token cannot carry a quote or a backslash into the reply");
    PCheck(hostile.GetString(L"posture") == L"read",
           "  the injected posture does not reach the reply");
    PCheck(hostile.Find(L"x") == nullptr, "  the injected field does not reach the reply");
    PCheck(capabilities.GetString(L"posture") == L"mutation", "  posture token is present");
    PCheck(capabilities.GetBool(L"mutation", false), "  the legacy mutation bool derives from posture == Mutation");

    const DevToolsJson* advertisedMethods = capabilities.Find(L"methods");
    PCheck(advertisedMethods && advertisedMethods->type == DevToolsJsonType::Array &&
           advertisedMethods->arr.size() == publicCount,
           "  advertised method count equals the PUBLIC-only registry count (Internal.* excluded)");

    const DevToolsJson* access = capabilities.Find(L"access");
    PCheck(access && access->type == DevToolsJsonType::Object, "  the negotiate result carries a per-method access map");

    for (size_t i = 0; i < methodCount; ++i) {
        const bool isPublic = methods[i].visibility == DevToolsVisibility::Public;
        PCheck(ArrayContains(advertisedMethods, methods[i].name) == isPublic,
               isPublic ? "  public registry method is advertised" : "  Internal.* registry method is NOT advertised");
        const DevToolsJson* accessEntry = access ? access->Find(methods[i].name) : nullptr;
        PCheck((accessEntry != nullptr) == isPublic,
               isPublic ? "  public method has an access map entry" : "  Internal.* method has no access map entry");
        if (accessEntry) {
            PCheck(accessEntry->type == DevToolsJsonType::String &&
                   accessEntry->str == DevToolsAccessToken(methods[i].access),
                   "  access map entry derives from the registry's own access tier (no second hand list)");
        }
        PCheck(RegistryContainsType(methods[i].paramsType), "  method params type is compiled into the registry");
        PCheck(RegistryContainsType(methods[i].resultType), "  method result type is compiled into the registry");
    }
    // Every public method's name must not look like an internal one -- the filter above keys off visibility,
    // not the name, but a name starting with "Internal." advertised anyway would defeat the whole point.
    for (size_t i = 0; i < advertisedMethods->arr.size(); ++i) {
        PCheck(advertisedMethods->arr[i].str.rfind(L"Internal.", 0) != 0,
               "  no advertised method name starts with \"Internal.\"");
    }

    const DevToolsJson* advertisedEvents = capabilities.Find(L"events");
    size_t eventCount = 0;
    const DevToolsProtocolEvent* events = DevToolsProtocolEvents(&eventCount);
    PCheck(advertisedEvents && advertisedEvents->type == DevToolsJsonType::Array &&
           advertisedEvents->arr.size() == eventCount,
           "  advertised event count equals registry count");
    for (size_t i = 0; i < eventCount; ++i) {
        PCheck(ArrayContains(advertisedEvents, events[i].name), "  registry event is advertised");
    }
}

// Reserved cancellation and caller-controlled executable-path mutation must not be registered.
static void TestReservedAndRetiredMethodsAreAbsent()
{
    std::printf("Reserved/retired methods are absent from the registry\n");
    PCheck(DevToolsProtocolFindMethod(L"DevTools.cancel") == nullptr, "  DevTools.cancel is not a registry entry");
    PCheck(DevToolsProtocolFindMethod(L"Internal.setCliExe") == nullptr, "  Internal.setCliExe is not a registry entry");
    PCheck(DevToolsProtocolFindMethod(L"totally.bogus") == nullptr, "  a made-up method is not a registry entry");
}

// registry-derived queryable selection-arm state and observable overlay state. The generic loop in
// TestAdvertisedCapabilitiesComeFromRegistry already covers every registry entry's params/result type and
// access-map agreement; this test PINS the specific access tier each new verb needs, so a future edit that
// accidentally loosens/tightens one (e.g. moving Overlay.hide to Read, which would let a read-only client
// intercept app input) fails here with a name rather than only showing up as a diff in the generic loop.
static void TestOverlaySelectionStateMethodsAreRegistered()
{
    std::printf("Overlay/selection-arm state methods are registered with the right access tier\n");

    const DevToolsProtocolMethod* overlayHide = DevToolsProtocolFindMethod(L"Overlay.hide");
    PCheck(overlayHide && overlayHide->visibility == DevToolsVisibility::Public, "  Overlay.hide is registered and public");
    PCheck(overlayHide && overlayHide->access == DevToolsAccess::Ui,
           "  Overlay.hide requires ui posture, same as Overlay.show/highlight/layout (it drives DevTools chrome)");

    const DevToolsProtocolMethod* overlayGetState = DevToolsProtocolFindMethod(L"Overlay.getState");
    PCheck(overlayGetState && overlayGetState->access == DevToolsAccess::Read,
           "  Overlay.getState is a pure read, available at read posture");

    const DevToolsProtocolMethod* overlayEnable = DevToolsProtocolFindMethod(L"Overlay.enable");
    const DevToolsProtocolMethod* overlayDisable = DevToolsProtocolFindMethod(L"Overlay.disable");
    PCheck(overlayEnable && overlayEnable->access == DevToolsAccess::Read,
           "  Overlay.enable is a read-tier subscription, matching VisualTree/Property/Selection.enable");
    PCheck(overlayDisable && overlayDisable->access == DevToolsAccess::Read, "  Overlay.disable is read-tier too");

    const DevToolsProtocolMethod* selectionGetState = DevToolsProtocolFindMethod(L"Selection.getState");
    PCheck(selectionGetState && selectionGetState->access == DevToolsAccess::Read,
           "  Selection.getState is a pure read (armed is a fact to observe, not an action)");

    // Positive control: Selection.arm/disarm remain Mutation-tier -- getState must NOT have quietly loosened
    // that gate. If this control ever fails, the assertions above are checking against a moved goalpost.
    const DevToolsProtocolMethod* selectionArm = DevToolsProtocolFindMethod(L"Selection.arm");
    PCheck(selectionArm && selectionArm->access == DevToolsAccess::Mutation,
           "  control: Selection.arm is still mutation-tier (unaffected by adding Selection.getState)");

    size_t eventCount = 0;
    const DevToolsProtocolEvent* events = DevToolsProtocolEvents(&eventCount);
    bool sawArmChanged = false, sawOverlayStateChanged = false;
    for (size_t i = 0; i < eventCount; ++i) {
        if (std::wstring(events[i].name) == L"Selection.armChanged") sawArmChanged = true;
        if (std::wstring(events[i].name) == L"Overlay.stateChanged") sawOverlayStateChanged = true;
    }
    PCheck(sawArmChanged, "  Selection.armChanged is a registered event");
    PCheck(sawOverlayStateChanged, "  Overlay.stateChanged is a registered event");
}

// The clear verb ('s naming defect, arriving over the wire). Clearing a value and re-writing a previous
// literal are DIFFERENT outcomes -- one deletes the local value and takes any binding with it, the other puts a
// literal back -- so they must stay two verbs. A future edit that "simplifies" this into a value:"" special
// case of HotReload.setProperty would make "set it to empty string" and "unset it" the same request, and this
// test is where that fails with a name.
static void TestClearPropertyIsItsOwnMutationVerb()
{
    std::printf("HotReload.clearProperty is a separate, mutation-tier verb\n");

    const DevToolsProtocolMethod* clear = DevToolsProtocolFindMethod(L"HotReload.clearProperty");
    PCheck(clear != nullptr, "  HotReload.clearProperty is registered");
    PCheck(clear && clear->visibility == DevToolsVisibility::Public,
           "  it is PUBLIC: an external inspector is the caller that needs it (the in-process window already "
           "had the primitive through route 1)");
    PCheck(clear && clear->access == DevToolsAccess::Mutation,
           "  it requires mutation posture -- it deletes a live value");

    // Control: the write it is distinct FROM is still registered and still mutation-tier. Without this, the
    // assertions above could pass on a build that had renamed setProperty out from under them.
    const DevToolsProtocolMethod* set = DevToolsProtocolFindMethod(L"HotReload.setProperty");
    PCheck(set && set->access == DevToolsAccess::Mutation,
           "  control: HotReload.setProperty is still registered and mutation-tier");
    PCheck(clear && set && std::wstring(clear->paramsType) != std::wstring(set->paramsType),
           "  the two verbs take DIFFERENT parameter types: a clear has no value or type to send");
}

// Every params type publishes "additionalProperties": false. This is the check that lets the dispatcher mean
// it. Without it a misspelled member is dropped in silence and a mutating verb answers success-shaped having
// done nothing -- `Overlay.layout {"enabled":false}` returned {"on":true} against a live AI Dev Gallery with
// the adorners still on. The declared-property scan must also stay TOP-LEVEL: VisualTreePreviewParams nests
// an "items" schema inside "handles", and admitting nested keys would declare members no handler reads.
static void TestUndeclaredParametersAreNotSilentlyAccepted()
{
    std::printf("undeclared parameters are refused, not dropped\n");

    PCheck(DevToolsProtocolParamDeclared(L"OverlayLayoutParams", L"set"),
           "  Overlay.layout declares `set`");
    PCheck(!DevToolsProtocolParamDeclared(L"OverlayLayoutParams", L"enabled"),
           "  and does NOT declare `enabled` -- the member that silently no-opped the toggle");

    PCheck(DevToolsProtocolParamDeclared(L"HandleParams", L"handle"), "  HandleParams declares `handle`");
    PCheck(!DevToolsProtocolParamDeclared(L"HandleParams", L"Handle"),
           "  member matching is case-sensitive, as JSON object keys are");

    // `owner` is resolved centrally for every method, so it is admitted everywhere -- including on the types
    // that never declared it. A check that refused it would break every ownership-taking client.
    PCheck(DevToolsProtocolParamDeclared(L"HandleParams", L"owner"),
           "  the reserved envelope member `owner` is admitted on a type that does not declare it");
    PCheck(DevToolsProtocolParamDeclared(L"EmptyParams", L"owner"),
           "  ... and on EmptyParams, which declares no properties at all");
    PCheck(!DevToolsProtocolParamDeclared(L"EmptyParams", L"clientVersion"),
           "  but EmptyParams still admits nothing else");

    // Top-level only: `handles` is declared, the `items` schema nested inside it is not a parameter.
    PCheck(DevToolsProtocolParamDeclared(L"VisualTreePreviewParams", L"handles"),
           "  VisualTreePreviewParams declares `handles`");
    PCheck(!DevToolsProtocolParamDeclared(L"VisualTreePreviewParams", L"items"),
           "  and `items` -- nested inside it -- is NOT a declared parameter");
    PCheck(!DevToolsProtocolParamDeclared(L"VisualTreePreviewParams", L"maxItems"),
           "  nor is `maxItems`, a keyword of that nested schema");

    // The keywords that sit beside "properties" in the schema object are not parameters either.
    PCheck(!DevToolsProtocolParamDeclared(L"SetPropertyParams", L"required"),
           "  schema keywords beside \"properties\" (required) are not parameters");
    PCheck(!DevToolsProtocolParamDeclared(L"SetPropertyParams", L"anyOf"),
           "  ... nor anyOf");
    PCheck(DevToolsProtocolParamDeclared(L"SetPropertyParams", L"value") &&
           DevToolsProtocolParamDeclared(L"SetPropertyParams", L"prop") &&
           DevToolsProtocolParamDeclared(L"SetPropertyParams", L"type") &&
           DevToolsProtocolParamDeclared(L"SetPropertyParams", L"handle") &&
           DevToolsProtocolParamDeclared(L"SetPropertyParams", L"name"),
           "  control: every member SetPropertyParams really declares is still admitted");

    // Every registered method must name a params type the registry defines -- otherwise the check above
    // fails open for it and the additionalProperties promise quietly stops applying to that method.
    size_t methodCount = 0, typeCount = 0;
    const DevToolsProtocolMethod* methods = DevToolsProtocolMethods(&methodCount);
    const DevToolsProtocolType* types = DevToolsProtocolTypes(&typeCount);
    bool everyParamsTypeDefined = true;
    for (size_t i = 0; i < methodCount; ++i) {
        bool found = false;
        for (size_t j = 0; j < typeCount; ++j) {
            if (std::wstring(methods[i].paramsType) == types[j].name) { found = true; break; }
        }
        if (!found) everyParamsTypeDefined = false;
    }
    PCheck(everyParamsTypeDefined, "  every registered method names a params type the registry defines");

    // Resolved the way the dispatcher resolves it -- through DevToolsProtocolFindMethod -- so the check cannot
    // drift from the registry that generates winapp-devtools-schema.json and the reference client's models.
    struct Case { const wchar_t* method; const wchar_t* declared; const wchar_t* undeclared; };
    const Case cases[] = {
        { L"DevTools.ping",                nullptr,    L"count"    },  // no-params, read tier
        { L"VisualTree.enumerate",     L"depth",   L"maxDepth" },  // read tier, optional members
        { L"VisualTree.getPreviews",   L"handles", L"items"    },  // nested schema inside a member
        { L"Property.get",             L"handle",  L"prop"     },  // read tier: `prop` belongs to OTHER methods
        { L"HotReload.setProperty",    L"value",   L"newValue" },  // mutation tier
        { L"HotReload.clearProperty",  L"prop",    L"value"    },  // mutation tier, deliberately no `value`
        { L"Overlay.highlight",        L"handle",  L"element"  },  // ui tier, owner-bearing
        { L"Overlay.layout",           L"set",     L"enabled"  },  // ui tier, the reported defect
        { L"Comment.add",              L"text",    L"body"     },  // mutation tier, anyOf addressing
        { L"Internal.setComments",     L"comments",L"id"       },  // internal, array-of-objects member
    };
    for (const auto& c : cases) {
        const DevToolsProtocolMethod* m = DevToolsProtocolFindMethod(c.method);
        PCheck(m != nullptr, "  registry resolves the method the dispatcher would look up");
        if (!m) continue;
        if (c.declared) {
            PCheck(DevToolsProtocolParamDeclared(m->paramsType, c.declared),
                   "  a member the method really declares is admitted");
        }
        PCheck(!DevToolsProtocolParamDeclared(m->paramsType, c.undeclared),
               "  a member it does not declare is refused");
        PCheck(DevToolsProtocolParamDeclared(m->paramsType, L"owner"),
               "  `owner` is admitted -- HandleRpc resolves it centrally for every method");
    }
}

// Repeated object members are refused at the parse, not resolved. JSON does not define a winner, so
// last-one-wins is a smuggling seam between anything that reads the request and the handler that acts on it.
static void TestDuplicateMembersAreRefusedNotResolved()
{
    std::printf("a repeated object member fails the parse\n");

    DevToolsJson dup;
    PCheck(!DevToolsJsonParse(L"{\"handle\":\"1\",\"handle\":\"2\"}", dup),
           "  a duplicate member is refused rather than silently resolved to one of the two");

    DevToolsJson nested;
    PCheck(!DevToolsJsonParse(L"{\"a\":{\"prop\":\"x\",\"prop\":\"y\"}}", nested),
           "  ... at any depth, not only at the top level");

    // Controls: the same shapes WITHOUT the repeat must still parse, or the check above would be passing
    // because the parser had simply broken.
    DevToolsJson ok;
    PCheck(DevToolsJsonParse(L"{\"handle\":\"1\",\"prop\":\"2\"}", ok) && ok.obj.size() == 2,
           "  CONTROL: two DIFFERENT members still parse");
    DevToolsJson okNested;
    PCheck(DevToolsJsonParse(L"{\"a\":{\"prop\":\"x\"},\"b\":{\"prop\":\"y\"}}", okNested),
           "  CONTROL: the same key in two DIFFERENT objects is not a duplicate");
    DevToolsJson okArray;
    PCheck(DevToolsJsonParse(L"{\"comments\":[{\"id\":\"1\"},{\"id\":\"2\"}]}", okArray),
           "  CONTROL: the same key in two array elements is not a duplicate either");
}

// The closed-params check must not change what `params` being absent, null or non-object means: JSON-RPC
// allows all three and the tap has always read them as "no members", which every no-params method relies on.
static void TestParamsShapeSemanticsAreUnchanged()
{
    std::printf("absent / null / non-object params keep their JSON-RPC meaning\n");

    DevToolsRpcRequest absent = DevToolsRpcParse(L"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"DevTools.ping\"}");
    PCheck(absent.valid, "  a request with no params at all is valid");
    PCheck(!absent.params.IsObject(), "  and its params is not an object, so the member check does not run");
    PCheck(absent.params.GetString(L"anything", L"def") == L"def",
           "  reading a member off it still yields the default");

    DevToolsRpcRequest null = DevToolsRpcParse(L"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"DevTools.ping\",\"params\":null}");
    PCheck(null.valid && !null.params.IsObject(), "  params:null is valid and is not an object");

    DevToolsRpcRequest arr = DevToolsRpcParse(L"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"DevTools.ping\",\"params\":[]}");
    PCheck(arr.valid && !arr.params.IsObject(), "  params:[] is valid and is not an object");

    DevToolsRpcRequest empty = DevToolsRpcParse(L"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"DevTools.ping\",\"params\":{}}");
    PCheck(empty.valid && empty.params.IsObject() && empty.params.obj.empty(),
           "  CONTROL: params:{} IS an object, with no members to check");
}

int RunProtocolTests()
{
    std::printf("== DevToolsProtocol JSON reader ==\n");
    TestRealParamsStillParse();
    TestCanonicalWireHandles();
    TestMalformedIsRejectedNotCrashed();
    TestDeepNestingIsRefusedRatherThanOverflowing();
    TestAdvertisedCapabilitiesComeFromRegistry();
    TestReservedAndRetiredMethodsAreAbsent();
    TestOverlaySelectionStateMethodsAreRegistered();
    TestClearPropertyIsItsOwnMutationVerb();
    TestUndeclaredParametersAreNotSilentlyAccepted();
    TestDuplicateMembersAreRefusedNotResolved();
    TestParamsShapeSemanticsAreUnchanged();
    return g_protocolFailures;
}
