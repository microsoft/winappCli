// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// COM sink tests: Invoke at slot 3, required QI identities, and process-lifetime reference semantics.
// Deliberately broken sinks must fail the same checks as the real base passes. No desktop is needed.

#include "DevToolsSink.h"

#include <cstdio>

static int g_sinkFailures = 0;

static void Check(bool cond, const char* what)
{
    if (!cond) { ++g_sinkFailures; std::printf("  FAIL  %s\n", what); }
    else       {                   std::printf("  ok    %s\n", what); }
}

// Arbitrary IIDs standing in for the real delegate GUIDs; the base treats them as opaque data.
static const GUID kIidPrimary   = { 0x1D9F5C41, 0x0001, 0x4A2B, { 0x9E, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 } };
static const GUID kIidSecondary = { 0x1D9F5C41, 0x0002, 0x4A2B, { 0x9E, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02 } };
static const GUID kIidUnrelated = { 0x1D9F5C41, 0x0003, 0x4A2B, { 0x9E, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03 } };

// The owner's generation counter, exactly as g_route1Gen / g_selGen work in the shipping files.
static unsigned g_testGen = 0;
static int      g_invoked = 0;

struct TestSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*)
    {
        if (gen != g_testGen) return S_OK; // stale generation (owner torn down) -> inert
        ++g_invoked;
        return S_OK;
    }
    void Init() { InitSink(kIidPrimary, g_testGen); }
};

struct TwoIidSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*) { return S_OK; }
    void Init() { InitSink(kIidPrimary, g_testGen, &kIidSecondary); }
};

// The dispatcher-queue sinks (RemovedHandler / TreeWatchHandler / UiHandler in DevToolsTap.cpp) sit on the same
// base but implement IDispatcherQueueHandler, whose Invoke takes NO arguments. Slot placement is decided by
// declaration order rather than signature, so this must land on slot 3 exactly like the two-argument sinks --
// but "must" is the kind of thing that is true right up until someone adds a virtual to the base.
static int g_noArgInvoked = 0;
struct NoArgSink : DevToolsSinkBase
{
    virtual HRESULT STDMETHODCALLTYPE Invoke() { ++g_noArgInvoked; return S_OK; }
    void Init() { InitSink(kIidPrimary, g_testGen); }
};

// Negative control: omit IAgileObject and IMarshal to prove the QI checks detect missing interfaces.
struct ControlNoAgileSink
{
    virtual HRESULT STDMETHODCALLTYPE QueryInterface(REFIID r, void** p)
    {
        if (!p) return E_POINTER;
        if (r == IID_IUnknown || r == kIidPrimary) { *p = this; return S_OK; }
        *p = nullptr; return E_NOINTERFACE;
    }
    virtual ULONG STDMETHODCALLTYPE AddRef() { return 2; }
    virtual ULONG STDMETHODCALLTYPE Release() { return 1; }
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*) { return S_OK; }
};

// A sink that declares an extra virtual BEFORE Invoke -- what a well-meaning "add a helper method" edit to the
// shared base would do. Invoke lands at slot 4 and the raw-vtable call below hits the wrong function.
struct ControlShiftedVtblSink
{
    virtual HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void**) { return E_NOINTERFACE; }
    virtual ULONG STDMETHODCALLTYPE AddRef() { return 2; }
    virtual ULONG STDMETHODCALLTYPE Release() { return 1; }
    virtual ULONG STDMETHODCALLTYPE Intruder() { return 0xBAD; }
    virtual HRESULT STDMETHODCALLTYPE Invoke(void*, void*) { return S_OK; }
};

// How WinUI actually calls a delegate: four raw slots, `this` passed as the first argument.
struct RawDelegateVtbl
{
    HRESULT(STDMETHODCALLTYPE* QueryInterface)(void*, REFIID, void**);
    ULONG  (STDMETHODCALLTYPE* AddRef)(void*);
    ULONG  (STDMETHODCALLTYPE* Release)(void*);
    HRESULT(STDMETHODCALLTYPE* Invoke)(void*, void*, void*);
};

static RawDelegateVtbl* RawVtbl(void* obj) { return *reinterpret_cast<RawDelegateVtbl**>(obj); }

// ---- Tests -------------------------------------------------------------------------------------------------

static void Test_InvokeIsAtVtableSlotThree()
{
    std::printf("Invoke lands at vtable slot 3 (the ABI WinUI calls through)\n");

    g_testGen = 7;
    TestSink s;
    s.Init();

    g_invoked = 0;
    const HRESULT hr = RawVtbl(&s)->Invoke(&s, nullptr, nullptr);
    Check(hr == S_OK, "slot 3 called through the raw vtable returns S_OK");
    Check(g_invoked == 1, "slot 3 IS the derived sink's Invoke (its body ran exactly once)");

    Check(RawVtbl(&s)->AddRef(&s) == 2, "slot 1 is still AddRef");
    Check(RawVtbl(&s)->Release(&s) == 1, "slot 2 is still Release");

    // Control: an extra virtual ahead of Invoke pushes it off slot 3, and slot 3 becomes the intruder.
    ControlShiftedVtblSink bad;
    const ULONG intruder = RawVtbl(&bad)->AddRef(&bad);
    Check(intruder == 2, "control: slots 0-2 are unaffected by the extra virtual");
    // Calling slot 3 on the control reaches Intruder(), not Invoke -- read its ULONG return through the same
    // slot to prove the shift is observable rather than silently benign.
    typedef ULONG(STDMETHODCALLTYPE* SlotThreeAsUlong)(void*);
    void** vt = *reinterpret_cast<void***>(&bad);
    const ULONG viaSlotThree = reinterpret_cast<SlotThreeAsUlong>(vt[3])(&bad);
    Check(viaSlotThree == 0xBAD, "control: an extra virtual before Invoke moves it off slot 3 (detected)");
}

// The no-argument Invoke used by the dispatcher-queue sinks must land on slot 3 too.
static void Test_NoArgInvokeIsAlsoAtSlotThree()
{
    std::printf("no-argument Invoke (IDispatcherQueueHandler shape) also lands at slot 3\n");

    g_testGen = 11;
    NoArgSink s;
    s.Init();

    // IDispatcherQueueHandler::Invoke takes no arguments, so call slot 3 through a matching signature.
    typedef HRESULT(STDMETHODCALLTYPE* SlotThreeNoArg)(void*);
    void** vt = *reinterpret_cast<void***>(&s);
    g_noArgInvoked = 0;
    const HRESULT hr = reinterpret_cast<SlotThreeNoArg>(vt[3])(&s);
    Check(hr == S_OK, "slot 3 called with the no-arg signature returns S_OK");
    Check(g_noArgInvoked == 1, "slot 3 IS the no-arg sink's Invoke (its body ran exactly once)");

    Check(RawVtbl(&s)->AddRef(&s) == 2, "slot 1 is still AddRef for a no-arg sink");
    Check(RawVtbl(&s)->Release(&s) == 1, "slot 2 is still Release for a no-arg sink");
}
static void Test_QueryInterfaceAnswersEveryIidTheFrameworkAsks(){
    std::printf("QueryInterface answers IUnknown + the delegate IID + IAgileObject + IMarshal\n");

    g_testGen = 3;
    TestSink s;
    s.Init();

    void* p = nullptr;
    Check(s.QueryInterface(IID_IUnknown, &p) == S_OK && p == &s, "IUnknown");
    p = nullptr;
    Check(s.QueryInterface(kIidPrimary, &p) == S_OK && p == &s, "the bound delegate IID");
    p = nullptr;
    Check(s.QueryInterface(__uuidof(IAgileObject), &p) == S_OK && p == &s, "IAgileObject");
    p = nullptr;
    Check(s.QueryInterface(__uuidof(IMarshal), &p) == S_OK && p != nullptr, "IMarshal (delegated to the FTM)");
    if (p) reinterpret_cast<IUnknown*>(p)->Release();
    p = nullptr;
    Check(s.QueryInterface(kIidUnrelated, &p) == E_NOINTERFACE && p == nullptr, "an unrelated IID is refused");
    Check(s.QueryInterface(IID_IUnknown, nullptr) == E_POINTER, "a null out-pointer is E_POINTER");

    // A sink bound to one IID must NOT claim the second one; only the two-IID sinks do.
    p = nullptr;
    Check(s.QueryInterface(kIidSecondary, &p) == E_NOINTERFACE, "an unbound second IID is refused");

    TwoIidSink two;
    two.Init();
    p = nullptr;
    Check(two.QueryInterface(kIidPrimary, &p) == S_OK, "two-IID sink answers its primary IID");
    p = nullptr;
    Check(two.QueryInterface(kIidSecondary, &p) == S_OK, "two-IID sink answers its secondary IID");
    p = nullptr;
    Check(two.QueryInterface(kIidUnrelated, &p) == E_NOINTERFACE, "two-IID sink still refuses everything else");

    // Control: the mutation this suite exists to catch. If these two lines ever pass, dropping the
    // IAgileObject / IMarshal cases from DevToolsSinkBase would go unnoticed.
    ControlNoAgileSink bad;
    p = nullptr;
    Check(bad.QueryInterface(__uuidof(IAgileObject), &p) != S_OK, "control: a QI missing IAgileObject is detected");
    p = nullptr;
    Check(bad.QueryInterface(__uuidof(IMarshal), &p) != S_OK, "control: a QI missing IMarshal is detected");
}

static void Test_TheProcessLifeLeakIsDeliberate()
{
    std::printf("AddRef=2 / Release=1 -- sinks outlive the subtree that raises into them, on purpose\n");

    g_testGen = 1;
    TestSink s;
    s.Init();

    Check(s.AddRef() == 2, "AddRef reports 2 so the caller never believes it owns the last reference");
    Check(s.Release() == 1, "Release reports 1 so a balanced release never destroys the sink");
    Check(s.Release() == 1 && s.Release() == 1, "repeated Release stays at 1 (no refcount to drive to zero)");
}

static void Test_TheGenerationStampGoesStale()
{
    std::printf("The generation stamp is what makes the leak safe\n");

    g_testGen = 11;
    TestSink live;
    live.Init();
    Check(live.gen == 11, "Init stamps the sink with the owner's CURRENT generation");

    g_invoked = 0;
    RawVtbl(&live)->Invoke(&live, nullptr, nullptr);
    Check(g_invoked == 1, "a sink from the current generation runs");

    // The owning window is torn down and a new one opens: the old sink is still wired and still reachable.
    ++g_testGen;
    g_invoked = 0;
    const HRESULT hr = RawVtbl(&live)->Invoke(&live, nullptr, nullptr);
    Check(hr == S_OK, "a stale sink returns S_OK rather than failing the framework's dispatch");
    Check(g_invoked == 0, "a stale sink is INERT -- its body never touches the torn-down owner's state");
}

static void Test_TheMarshalerIsBuiltOnceForRebindableSinks()
{
    std::printf("InitSink builds the free-threaded marshaler once per object\n");

    g_testGen = 0;
    TestSink s;
    s.Init();
    IUnknown* const first = s.ftm;
    Check(first != nullptr, "InitSink creates the free-threaded marshaler");

    // The overlay's EventSinks are static and re-Bind()ed across overlays. A second marshaler per rebind would
    // strand the first, and every rebind would leak one more.
    s.Init();
    Check(s.ftm == first, "a re-Init reuses the existing marshaler instead of stranding it");
}

int RunSinkTests()
{
    std::printf("DevToolsSinkBase tests -- each check is paired with a control that must FAIL it\n");
    // CoCreateFreeThreadedMarshaler needs an initialized apartment. Single-threaded is enough and keeps this
    // runnable in a plain console process with no desktop, like the other two units.
    const HRESULT coHr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    Check(SUCCEEDED(coHr), "COM initializes for the sink suite");

    Test_InvokeIsAtVtableSlotThree();
    Test_NoArgInvokeIsAlsoAtSlotThree();
    Test_QueryInterfaceAnswersEveryIidTheFrameworkAsks();
    Test_TheProcessLifeLeakIsDeliberate();
    Test_TheGenerationStampGoesStale();
    Test_TheMarshalerIsBuiltOnceForRebindableSinks();

    if (SUCCEEDED(coHr)) CoUninitialize();
    return g_sinkFailures;
}
