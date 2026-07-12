// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#include "dynamicmethodunloadprofiler.h"

namespace
{
    const WCHAR* TargetMethodName = WCHAR("RuntimeAsyncProfilerUnloadTarget");
    const WCHAR* RuntimeProviderName = WCHAR("Microsoft-Windows-DotNETRuntime");
    constexpr UINT64 RuntimeJitKeyword = 0x10;
}

GUID DynamicMethodUnloadProfiler::GetClsid()
{
    GUID clsid = { 0xD57A7F32, 0x6B5F, 0x4D85, { 0x9C, 0x9B, 0x2A, 0x76, 0x4D, 0xF2, 0xC1, 0x51 } };
    return clsid;
}

HRESULT DynamicMethodUnloadProfiler::Initialize(IUnknown* pICorProfilerInfoUnk)
{
    HRESULT hr = Profiler::Initialize(pICorProfilerInfoUnk);
    if (FAILED(hr) || pCorProfilerInfo == nullptr)
    {
        return FAILED(hr) ? hr : E_FAIL;
    }

    hr = pICorProfilerInfoUnk->QueryInterface(
        __uuidof(ICorProfilerInfo12),
        reinterpret_cast<void**>(&_pCorProfilerInfo12));
    if (FAILED(hr))
    {
        printf("FAIL: failed to QI for ICorProfilerInfo12, hr=0x%x\n", hr);
        return hr;
    }

    hr = _pCorProfilerInfo12->SetEventMask2(
        COR_PRF_MONITOR_JIT_COMPILATION,
        COR_PRF_HIGH_MONITOR_DYNAMIC_FUNCTION_UNLOADS | COR_PRF_HIGH_MONITOR_EVENT_PIPE);
    if (FAILED(hr))
    {
        printf("FAIL: SetEventMask2 failed, hr=0x%x\n", hr);
        return hr;
    }

    COR_PRF_EVENTPIPE_PROVIDER_CONFIG providers[] =
    {
        { RuntimeProviderName, RuntimeJitKeyword, 5, nullptr }
    };

    hr = _pCorProfilerInfo12->EventPipeStartSession(
        static_cast<UINT32>(sizeof(providers) / sizeof(providers[0])),
        providers,
        false,
        &_session);
    if (FAILED(hr))
    {
        printf("FAIL: EventPipeStartSession failed, hr=0x%x\n", hr);
    }

    return hr;
}

HRESULT DynamicMethodUnloadProfiler::Shutdown()
{
    if (_pCorProfilerInfo12 != nullptr && _session != 0)
    {
        HRESULT stopResult = _pCorProfilerInfo12->EventPipeStopSession(_session);
        if (FAILED(stopResult))
        {
            printf("FAIL: EventPipeStopSession failed, hr=0x%x\n", stopResult);
            std::lock_guard<std::mutex> guard(_lock);
            _eventPipeFailures++;
        }
        _session = 0;
    }

    Profiler::Shutdown();

    bool passed;
    {
        std::lock_guard<std::mutex> guard(_lock);
        passed = _failedJitCount == 0 &&
            _eventPipeFailures == 0 &&
            _started.size() == 2 &&
            _finished == _started &&
            _unloaded == _started;

        for (FunctionID functionId : _started)
        {
            passed = passed &&
                _eventPipeLoaded.find(functionId) != _eventPipeLoaded.end() &&
                _eventPipeUnloaded.find(functionId) != _eventPipeUnloaded.end();
        }

        if (!passed)
        {
            printf(
                "DynamicMethodUnloadProfiler::Shutdown: FAIL: "
                "started=%zu finished=%zu unloaded=%zu eventLoads=%zu eventUnloads=%zu "
                "failedJit=%d eventPipeFailures=%d\n",
                _started.size(),
                _finished.size(),
                _unloaded.size(),
                _eventPipeLoaded.size(),
                _eventPipeUnloaded.size(),
                _failedJitCount,
                _eventPipeFailures);
        }
    }

    if (_pCorProfilerInfo12 != nullptr)
    {
        _pCorProfilerInfo12->Release();
        _pCorProfilerInfo12 = nullptr;
    }

    if (passed)
    {
        printf("PROFILER TEST PASSES\n");
    }

    fflush(stdout);
    return S_OK;
}

HRESULT DynamicMethodUnloadProfiler::DynamicMethodJITCompilationStarted(
    FunctionID functionId,
    BOOL fIsSafeToBlock,
    LPCBYTE ilHeader,
    ULONG cbILHeader)
{
    SHUTDOWNGUARD();

    if (IsTargetFunction(functionId))
    {
        std::lock_guard<std::mutex> guard(_lock);
        _started.insert(functionId);
    }

    return S_OK;
}

HRESULT DynamicMethodUnloadProfiler::DynamicMethodJITCompilationFinished(
    FunctionID functionId,
    HRESULT hrStatus,
    BOOL fIsSafeToBlock)
{
    SHUTDOWNGUARD();

    std::lock_guard<std::mutex> guard(_lock);
    if (_started.find(functionId) != _started.end())
    {
        if (SUCCEEDED(hrStatus))
        {
            _finished.insert(functionId);
        }
        else
        {
            _failedJitCount++;
        }
    }

    return S_OK;
}

HRESULT DynamicMethodUnloadProfiler::DynamicMethodUnloaded(FunctionID functionId)
{
    SHUTDOWNGUARD();

    std::lock_guard<std::mutex> guard(_lock);
    if (_started.find(functionId) != _started.end())
    {
        _unloaded.insert(functionId);
    }

    return S_OK;
}

HRESULT DynamicMethodUnloadProfiler::EventPipeEventDelivered(
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
    UINT_PTR stackFrames[])
{
    SHUTDOWNGUARD();

    if (GetOrAddProviderName(provider) != RuntimeProviderName)
    {
        return S_OK;
    }

    // EventPipe metadata names are versioned (for example, MethodLoad_V2).
    // Event IDs are stable across event versions and avoid name-shape coupling.
    bool isLoad = eventId == 141 || eventId == 143;
    bool isUnload = eventId == 142 || eventId == 144;
    if (!isLoad && !isUnload)
    {
        return S_OK;
    }

    if (cbEventData < sizeof(UINT64))
    {
        std::lock_guard<std::mutex> guard(_lock);
        _eventPipeFailures++;
        return S_OK;
    }

    ULONG offset = 0;
    FunctionID functionId = static_cast<FunctionID>(ReadFromBuffer<UINT64>(eventData, cbEventData, &offset));
    std::lock_guard<std::mutex> guard(_lock);
    if (isLoad)
    {
        _eventPipeLoaded.insert(functionId);
    }
    else
    {
        _eventPipeUnloaded.insert(functionId);
    }

    return S_OK;
}

bool DynamicMethodUnloadProfiler::IsTargetFunction(FunctionID functionId)
{
    ModuleID moduleId = 0;
    PCCOR_SIGNATURE signature = nullptr;
    ULONG signatureSize = 0;
    ULONG nameLength = 0;
    WCHAR name[STR_LENGTH] = {};

    HRESULT hr = pCorProfilerInfo->GetDynamicFunctionInfo(
        functionId,
        &moduleId,
        &signature,
        &signatureSize,
        STR_LENGTH,
        &nameLength,
        name);

    return SUCCEEDED(hr) && String(name) == TargetMethodName;
}

String DynamicMethodUnloadProfiler::GetOrAddProviderName(EVENTPIPE_PROVIDER provider)
{
    std::lock_guard<std::mutex> guard(_lock);
    auto iterator = _providerNameCache.find(provider);
    if (iterator == _providerNameCache.end())
    {
        WCHAR nameBuffer[LONG_LENGTH] = {};
        ULONG nameCount = 0;
        HRESULT hr = _pCorProfilerInfo12->EventPipeGetProviderInfo(
            provider,
            LONG_LENGTH,
            &nameCount,
            nameBuffer);
        if (FAILED(hr))
        {
            _eventPipeFailures++;
            return WCHAR("EventPipeGetProviderInfoFailed");
        }

        iterator = _providerNameCache.insert({ provider, String(nameBuffer) }).first;
    }

    return iterator->second;
}

