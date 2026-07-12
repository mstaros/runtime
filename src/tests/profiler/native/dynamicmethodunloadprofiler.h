// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma once

#include "profiler.h"
#include "eventpipeprofiler/eventpipemetadatareader.h"

#include <map>
#include <mutex>
#include <set>

class DynamicMethodUnloadProfiler : public Profiler
{
public:
    DynamicMethodUnloadProfiler() :
        _pCorProfilerInfo12(nullptr),
        _session(0),
        _failedJitCount(0),
        _eventPipeFailures(0)
    {
    }

    static GUID GetClsid();

    HRESULT STDMETHODCALLTYPE Initialize(IUnknown* pICorProfilerInfoUnk) override;
    HRESULT STDMETHODCALLTYPE Shutdown() override;
    HRESULT STDMETHODCALLTYPE DynamicMethodJITCompilationStarted(
        FunctionID functionId,
        BOOL fIsSafeToBlock,
        LPCBYTE ilHeader,
        ULONG cbILHeader) override;
    HRESULT STDMETHODCALLTYPE DynamicMethodJITCompilationFinished(
        FunctionID functionId,
        HRESULT hrStatus,
        BOOL fIsSafeToBlock) override;
    HRESULT STDMETHODCALLTYPE DynamicMethodUnloaded(FunctionID functionId) override;
    HRESULT STDMETHODCALLTYPE EventPipeEventDelivered(
        EVENTPIPE_PROVIDER provider,
        DWORD eventId,
        DWORD eventVersion,
        ULONG cbMetadataBlob,
        LPCBYTE metadataBlob,
        ULONG cbEventData,
        LPCBYTE eventData,
        LPCGUID pActivityId,
        LPCGUID pRelatedActivityId,
        ThreadID eventThread,
        ULONG numStackFrames,
        UINT_PTR stackFrames[]) override;

private:
    bool IsTargetFunction(FunctionID functionId);
    String GetOrAddProviderName(EVENTPIPE_PROVIDER provider);
    EventPipeMetadataInstance GetOrAddMetadata(LPCBYTE metadataBlob, ULONG cbMetadataBlob);

    ICorProfilerInfo12* _pCorProfilerInfo12;
    EVENTPIPE_SESSION _session;
    std::mutex _lock;
    std::set<FunctionID> _started;
    std::set<FunctionID> _finished;
    std::set<FunctionID> _unloaded;
    std::set<FunctionID> _eventPipeLoaded;
    std::set<FunctionID> _eventPipeUnloaded;
    std::map<EVENTPIPE_PROVIDER, String> _providerNameCache;
    std::map<LPCBYTE, EventPipeMetadataInstance> _metadataCache;
    int _failedJitCount;
    int _eventPipeFailures;
};
