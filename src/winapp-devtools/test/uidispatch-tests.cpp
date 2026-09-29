// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Deterministic coverage for concurrent DevTools UI-thread reads. The fake queue holds all three independent
// requests until they are simultaneously pending, then invokes them in reverse order. A process-global
// operation slot loses or cross-wires results under this schedule; per-request handlers preserve every payload.

#include "DevToolsUiDispatch.h"
#include "DevToolsProtocol.h"

#include <chrono>
#include <condition_variable>
#include <cstring>
#include <cstdio>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace {

int g_failures = 0;

void Check(bool condition, const char* message)
{
    if (condition) std::printf("  ok    %s\n", message);
    else { ++g_failures; std::printf("  FAIL  %s\n", message); }
}

using RawInvoke = HRESULT(STDMETHODCALLTYPE*)(void*);

class HeldDispatcher
{
public:
    static HRESULT Enqueue(void* context, IUnknown* handler, bool* enqueued)
    {
        if (!context || !handler || !enqueued) return E_INVALIDARG;
        auto* self = static_cast<HeldDispatcher*>(context);
        handler->AddRef();
        {
            std::lock_guard<std::mutex> lock(self->mutex_);
            self->handlers_.push_back(handler);
        }
        *enqueued = true;
        self->ready_.notify_all();
        return S_OK;
    }

    bool InvokeWhenAllQueued(size_t count, DWORD timeoutMs = 5000)
    {
        std::vector<IUnknown*> handlers;
        {
            std::unique_lock<std::mutex> lock(mutex_);
            if (!ready_.wait_for(lock, std::chrono::milliseconds(timeoutMs),
                                 [&] { return handlers_.size() == count; })) {
                handlers.swap(handlers_);
                lock.unlock();
                for (IUnknown* handler : handlers) handler->Release();
                return false;
            }
            handlers.swap(handlers_);
        }
        for (auto it = handlers.rbegin(); it != handlers.rend(); ++it) {
            void** vtable = *reinterpret_cast<void***>(*it);
            reinterpret_cast<RawInvoke>(vtable[3])(*it);
            (*it)->Release();
        }
        return true;
    }

private:
    std::mutex mutex_;
    std::condition_variable ready_;
    std::vector<IUnknown*> handlers_;
};

const char* DetailPayload(const std::string& method)
{
    if (method == "Property.get") {
        return R"({"handle":"101","authoredState":"available","props":[{"name":"Text","value":"ready","valueType":"String","editKind":"text","writeType":"String"}]})";
    }
    if (method == "Source.get") {
        return R"({"handle":101,"fileName":"ms-appx:///MainWindow.xaml","lineNumber":12,"columnNumber":5,"authoredState":"available","xaml":"<Button x:Name=\"CounterButton\" />"})";
    }
    if (method == "Layout.get") {
        return R"({"handle":"101","desired":{"w":320,"h":180},"render":{"w":320,"h":180},"actual":{"w":320,"h":180},"offset":{"x":10,"y":20},"parent":{"type":"Microsoft.UI.Xaml.Controls.Grid","handle":"100","childIndex":0,"childCount":1}})";
    }
    return nullptr;
}

std::string NarrowAscii(const std::wstring& value)
{
    std::string result;
    result.reserve(value.size());
    for (wchar_t character : value) result.push_back(static_cast<char>(character));
    return result;
}

std::string DetailResponse(const std::wstring& id, const std::string& method)
{
    const char* payload = DetailPayload(method);
    if (!payload) return {};
    const std::wstring widePayload(payload, payload + strlen(payload));
    const std::wstring response = DevToolsRpcResult(id, widePayload);
    return NarrowAscii(response);
}

void TestConcurrentIndependentPipeConnectionsKeepTheirOwnPayloads()
{
    std::printf("Independent named-pipe connections keep concurrent Property/Source/Layout payloads correlated\n");
    HeldDispatcher dispatcher;
    const std::string pipeName = "\\\\.\\pipe\\winapp-devtools-ui-dispatch-test-" +
                                 std::to_string(GetCurrentProcessId()) + "-" +
                                 std::to_string(GetTickCount64());
    constexpr size_t requestCount = 3;
    HANDLE serverPipes[requestCount]{};
    bool setupOk = true;
    for (size_t i = 0; i < requestCount; ++i) {
        serverPipes[i] = CreateNamedPipeA(
            pipeName.c_str(), PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_NOWAIT,
            static_cast<DWORD>(requestCount), 4096, 4096, 0, nullptr);
        if (serverPipes[i] == INVALID_HANDLE_VALUE) setupOk = false;
    }
    Check(setupOk, "three independent server pipe instances are created");
    if (!setupOk) {
        for (HANDLE pipe : serverPipes) if (pipe && pipe != INVALID_HANDLE_VALUE) CloseHandle(pipe);
        return;
    }

    HRESULT serverResults[requestCount]{ E_FAIL, E_FAIL, E_FAIL };
    bool dispatchCompleted[requestCount]{};
    std::thread servers[requestCount];
    for (size_t i = 0; i < requestCount; ++i) {
        servers[i] = std::thread([&, i] {
            const ULONGLONG deadline = GetTickCount64() + 5000;
            bool connected = false;
            while (!connected && GetTickCount64() < deadline) {
                if (ConnectNamedPipe(serverPipes[i], nullptr)) connected = true;
                else {
                    const DWORD error = GetLastError();
                    connected = error == ERROR_PIPE_CONNECTED;
                    if (!connected && error != ERROR_PIPE_LISTENING && error != ERROR_NO_DATA) break;
                    if (!connected) Sleep(1);
                }
            }
            char requestBuffer[256]{};
            DWORD read = 0;
            while (connected && !read && GetTickCount64() < deadline) {
                if (!ReadFile(serverPipes[i], requestBuffer, sizeof(requestBuffer) - 1, &read, nullptr)) {
                    const DWORD error = GetLastError();
                    if (error != ERROR_NO_DATA) break;
                }
                if (!read) Sleep(1);
            }
            if (!connected || !read) {
                serverResults[i] = E_FAIL;
            } else {
                std::string frame(requestBuffer, read);
                if (!frame.empty() && frame.back() == '\n') frame.pop_back();
                const DevToolsRpcRequest rpcRequest = DevToolsRpcParse(std::wstring(frame.begin(), frame.end()));
                auto response = std::make_shared<std::string>();
                serverResults[i] = DevToolsUiDispatch_Run(&dispatcher, HeldDispatcher::Enqueue, [rpcRequest, response] {
                    if (!rpcRequest.parsed || !rpcRequest.valid) return E_INVALIDARG;
                    *response = DetailResponse(rpcRequest.idRaw, NarrowAscii(rpcRequest.method));
                    if (response->empty()) return E_INVALIDARG;
                    return S_OK;
                }, 1000, &dispatchCompleted[i]);
                if (SUCCEEDED(serverResults[i]) && !response->empty()) {
                    DWORD written = 0;
                    WriteFile(serverPipes[i], response->data(), static_cast<DWORD>(response->size()), &written, nullptr);
                }
            }
            FlushFileBuffers(serverPipes[i]);
            DisconnectNamedPipe(serverPipes[i]);
            CloseHandle(serverPipes[i]);
        });
    }

    const char* requests[requestCount]{
        R"({"jsonrpc":"2.0","id":1,"method":"Property.get","params":{"handle":"101"}})" "\n",
        R"({"jsonrpc":"2.0","id":2,"method":"Source.get","params":{"handle":"101"}})" "\n",
        R"({"jsonrpc":"2.0","id":3,"method":"Layout.get","params":{"handle":"101"}})" "\n"
    };
    std::string responses[requestCount];
    bool clientOk[requestCount]{};
    std::thread clients[requestCount];
    for (size_t i = 0; i < requestCount; ++i) {
        clients[i] = std::thread([&, i] {
            const ULONGLONG deadline = GetTickCount64() + 5000;
            HANDLE pipe = INVALID_HANDLE_VALUE;
            while (pipe == INVALID_HANDLE_VALUE && GetTickCount64() < deadline) {
                pipe = CreateFileA(pipeName.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                   OPEN_EXISTING, 0, nullptr);
                if (pipe == INVALID_HANDLE_VALUE) Sleep(1);
            }
            if (pipe == INVALID_HANDLE_VALUE) return;
            DWORD mode = PIPE_READMODE_BYTE | PIPE_NOWAIT;
            if (!SetNamedPipeHandleState(pipe, &mode, nullptr, nullptr)) {
                CloseHandle(pipe);
                return;
            }
            DWORD written = 0;
            if (!WriteFile(pipe, requests[i], static_cast<DWORD>(strlen(requests[i])), &written, nullptr)) {
                CloseHandle(pipe);
                return;
            }
            char response[1024]{};
            DWORD read = 0;
            while (!read && GetTickCount64() < deadline) {
                if (!ReadFile(pipe, response, sizeof(response), &read, nullptr)) {
                    const DWORD error = GetLastError();
                    if (error != ERROR_NO_DATA) break;
                }
                if (!read) Sleep(1);
            }
            if (read) {
                responses[i].assign(response, read);
                clientOk[i] = true;
            }
            CloseHandle(pipe);
        });
    }

    Check(dispatcher.InvokeWhenAllQueued(3), "all three pipe requests reach the held dispatcher concurrently");
    for (auto& client : clients) client.join();
    for (auto& server : servers) server.join();

    Check(serverResults[0] == S_OK && serverResults[1] == S_OK && serverResults[2] == S_OK,
          "all three independently accepted requests complete");
    Check(dispatchCompleted[0] && dispatchCompleted[1] && dispatchCompleted[2],
          "all three callers own their completed payload state");
    Check(clientOk[0], "Property.get completes on its independent connection");
    Check(clientOk[1], "Source.get completes on its independent connection");
    Check(clientOk[2], "Layout.get completes on its independent connection");
    Check(responses[0] == DetailResponse(L"1", "Property.get"),
          "Property.get returns its complete correlated JSON-RPC payload");
    Check(responses[1] == DetailResponse(L"2", "Source.get"),
          "Source.get returns its complete correlated JSON-RPC payload");
    Check(responses[2] == DetailResponse(L"3", "Layout.get"),
          "Layout.get returns its complete correlated JSON-RPC payload");
}

void TestTimedOutPendingOperationIsCancelled()
{
    std::printf("A timed-out pending operation is inert when its dispatcher turn arrives later\n");
    HeldDispatcher dispatcher;
    bool ran = false;
    bool completed = true;
    const HRESULT hr = DevToolsUiDispatch_Run(&dispatcher, HeldDispatcher::Enqueue, [&] {
        ran = true;
        return S_OK;
    }, 1, &completed);
    Check(dispatcher.InvokeWhenAllQueued(1), "the cancelled handler still reaches its later dispatcher turn");
    Check(hr == HRESULT_FROM_WIN32(WAIT_TIMEOUT), "the bounded wait reports its timeout");
    Check(!completed, "a timeout does not transfer captured output ownership to the caller");
    Check(!ran, "the cancelled closure does not run after the caller has returned");
}

void TestTimedOutRunningOperationOwnsItsStateUntilCompletion()
{
    std::printf("A running operation owns its captured state after the bounded caller returns\n");
    HeldDispatcher dispatcher;
    std::mutex mutex;
    std::condition_variable changed;
    bool started = false;
    bool release = false;
    bool completed = false;
    auto payload = std::make_shared<std::string>();
    HRESULT hr = E_FAIL;
    bool operationCompleted = true;

    std::thread caller([&] {
        hr = DevToolsUiDispatch_Run(&dispatcher, HeldDispatcher::Enqueue, [&, payload] {
            {
                std::lock_guard<std::mutex> lock(mutex);
                started = true;
            }
            changed.notify_all();
            {
                std::unique_lock<std::mutex> lock(mutex);
                changed.wait(lock, [&] { return release; });
            }
            *payload = R"({"handle":"101","props":[{"name":"Text","value":"late"}]})";
            {
                std::lock_guard<std::mutex> lock(mutex);
                completed = true;
            }
            changed.notify_all();
            return S_OK;
        }, 20, &operationCompleted);
    });
    std::thread dispatcherThread([&] { dispatcher.InvokeWhenAllQueued(1); });

    {
        std::unique_lock<std::mutex> lock(mutex);
        changed.wait(lock, [&] { return started; });
    }
    caller.join();
    Check(hr == HRESULT_FROM_WIN32(WAIT_TIMEOUT), "the caller remains bounded after the handler has started");
    Check(!operationCompleted, "a running timeout keeps captured output ownership with the handler");
    Check(payload->empty(), "the caller does not receive a partial payload");
    {
        std::lock_guard<std::mutex> lock(mutex);
        release = true;
    }
    changed.notify_all();
    dispatcherThread.join();
    Check(completed, "the already-running closure completes without use-after-free");
    Check(*payload == R"({"handle":"101","props":[{"name":"Text","value":"late"}]})",
          "its captured payload state remains valid through completion");
}

} // namespace

int RunUiDispatchTests()
{
    std::printf("DevTools UI dispatch tests\n");
    TestConcurrentIndependentPipeConnectionsKeepTheirOwnPayloads();
    TestTimedOutPendingOperationIsCancelled();
    TestTimedOutRunningOperationOwnsItsStateUntilCompletion();
    return g_failures;
}
