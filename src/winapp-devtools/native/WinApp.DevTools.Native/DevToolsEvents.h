// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <windows.h>
#include <cstdint>
#include <string>

// Subscribable domains. DevTools, HotReload, Source and Resource have no events and return bit 0.
enum DevToolsDomainBit : uint32_t {
    DevToolsDomain_VisualTree = 1u << 0,
    DevToolsDomain_Property   = 1u << 1,
    DevToolsDomain_Selection  = 1u << 2,
    DevToolsDomain_Focus      = 1u << 3,
    DevToolsDomain_Overlay    = 1u << 4,   // Overlay.stateChanged (toolbar visibility/highlight/layout adorners)
};

enum DevToolsCoalesce {
    DevToolsCoalesce_None,       // never coalesced or dropped (used for responses)
    DevToolsCoalesce_KeepNewest, // replace any queued entry with the same coalesceKey (selection/focus: latest wins)
};

struct DevToolsConn;

enum DevToolsDomainChange {
    DevToolsDomain_NoChange,
    DevToolsDomain_FirstEnabled,
    DevToolsDomain_LastDisabled,
    DevToolsDomain_ConnectionGone,
};

DevToolsConn* DevToolsEvents_Register(HANDLE pipe);

void DevToolsEvents_Retain(DevToolsConn* conn);
void DevToolsEvents_Release(DevToolsConn* conn);

uint32_t DevToolsEvents_Unregister(DevToolsConn* conn);

void DevToolsEvents_EnqueueResponse(DevToolsConn* conn, const std::wstring& line);

uint32_t DevToolsEvents_DomainBit(const std::wstring& domain);

DevToolsDomainChange DevToolsEvents_SetDomain(DevToolsConn* conn, uint32_t domainBit, bool on);

bool DevToolsEvents_DomainEnabled(DevToolsConn* conn, uint32_t domainBit);

uint32_t DevToolsEvents_DomainSubscriberCount(uint32_t domainBit);

uint64_t DevToolsEvents_ConnId(DevToolsConn* conn);

std::wstring DevToolsEvents_MarkNegotiated(DevToolsConn* conn);

uint64_t DevToolsEvents_ResolveOwnerToken(const std::wstring& token);

bool DevToolsEvents_IsOwnerLive(uint64_t connId);

void DevToolsEvents_Broadcast(uint32_t domainBit, const std::wstring& eventLine,
                         DevToolsCoalesce policy, const std::wstring& coalesceKey);

void DevToolsEvents_BroadcastTreeDelta(const std::wstring& deltaLine);

#ifdef WINAPP_DEVTOOLS_EVENTS_FAULT_INJECTION
enum class DevToolsEventsStage { OwnerToken, RegistryInsert, BroadcastEnqueue };
DevToolsEventsStage& DevToolsEventsFaultStage();
long& DevToolsEventsFaultBudget();
long& DevToolsEventsFaultsTaken();
enum class DevToolsEventsTestStage { BeforeWriterLoop, BeforeWrite, AfterCancel };
using DevToolsEventsTestHook = void (*)(DevToolsEventsTestStage, HANDLE);
DevToolsEventsTestHook& DevToolsEventsScheduleHook();
#endif
