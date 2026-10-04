// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsUiDispatch.h"

#include <atomic>
#include <memory>
#include <new>

namespace {

struct __declspec(uuid("2E0872A9-4E29-5F14-B688-FB96D5F9D5F8")) IDispatcherQueueHandler : IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE Invoke() = 0;
};

enum class OperationStatus
{
    Pending,
    Running,
    Completed,
    Cancelled,
};

struct OperationState
{
    OperationState(std::function<HRESULT()> value)
        : done(CreateEventW(nullptr, TRUE, FALSE, nullptr)), fn(std::move(value))
    {
        if (!done) createError = GetLastError();
    }

    ~OperationState()
    {
        if (done) CloseHandle(done);
    }

    HANDLE done = nullptr;
    DWORD createError = ERROR_SUCCESS;
    std::function<HRESULT()> fn;
    HRESULT result = E_FAIL;
    std::atomic<OperationStatus> status{ OperationStatus::Pending };
};

class OperationHandler final : public IDispatcherQueueHandler
{
public:
    explicit OperationHandler(std::shared_ptr<OperationState> state)
        : state_(std::move(state))
    {
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override
    {
        if (!value) return E_POINTER;
        if (iid == IID_IUnknown || iid == __uuidof(IDispatcherQueueHandler) || iid == __uuidof(IAgileObject)) {
            *value = static_cast<IDispatcherQueueHandler*>(this);
            AddRef();
            return S_OK;
        }
        *value = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override
    {
        return refs_.fetch_add(1, std::memory_order_relaxed) + 1;
    }

    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG remaining = refs_.fetch_sub(1, std::memory_order_acq_rel) - 1;
        if (!remaining) delete this;
        return remaining;
    }

    HRESULT STDMETHODCALLTYPE Invoke() override
    {
        OperationStatus expected = OperationStatus::Pending;
        if (!state_->status.compare_exchange_strong(expected, OperationStatus::Running,
                                                    std::memory_order_acq_rel)) {
            return S_OK;
        }
        try {
            state_->result = state_->fn ? state_->fn() : E_FAIL;
        } catch (...) {
            state_->result = E_FAIL;
        }
        state_->fn = nullptr;
        state_->status.store(OperationStatus::Completed, std::memory_order_release);
        SetEvent(state_->done);
        return S_OK;
    }

private:
    std::atomic<ULONG> refs_{ 1 };
    std::shared_ptr<OperationState> state_;
};

} // namespace

// Worker waits on a UI-thread callback; timeout returns control without assuming the callback never arrives.
HRESULT DevToolsUiDispatch_Run(void* context, DevToolsUiEnqueueFn enqueue,
                          std::function<HRESULT()> operation, DWORD timeoutMs, bool* completed)
{
    if (completed) *completed = false;
    if (!context || !enqueue || !operation) return E_INVALIDARG;
    std::shared_ptr<OperationState> state;
    try {
        state = std::make_shared<OperationState>(std::move(operation));
    } catch (const std::bad_alloc&) {
        return E_OUTOFMEMORY;
    }
    if (!state->done) return HRESULT_FROM_WIN32(state->createError);

    auto* handler = new (std::nothrow) OperationHandler(state);
    if (!handler) return E_OUTOFMEMORY;
    bool enqueued = false;
    HRESULT hr = enqueue(context, handler, &enqueued);
    handler->Release();
    if (FAILED(hr) || !enqueued) {
        OperationStatus expected = OperationStatus::Pending;
        if (state->status.compare_exchange_strong(expected, OperationStatus::Cancelled,
                                                  std::memory_order_acq_rel)) {
            state->fn = nullptr;
        }
        return FAILED(hr) ? hr : E_FAIL;
    }

    const DWORD wait = WaitForSingleObject(state->done, timeoutMs);
    if (wait == WAIT_OBJECT_0 &&
        state->status.load(std::memory_order_acquire) == OperationStatus::Completed) {
        if (completed) *completed = true;
        return state->result;
    }
    if (state->status.load(std::memory_order_acquire) == OperationStatus::Completed) {
        if (completed) *completed = true;
        return state->result;
    }

    OperationStatus expected = OperationStatus::Pending;
    if (state->status.compare_exchange_strong(expected, OperationStatus::Cancelled,
                                              std::memory_order_acq_rel)) {
        state->fn = nullptr;
    }
    return wait == WAIT_FAILED ? HRESULT_FROM_WIN32(GetLastError())
                               : HRESULT_FROM_WIN32(WAIT_TIMEOUT);
}
