// Native plugin for Unity Agent Client UI Window Sync
// Monitors Unity Editor window position changes via WinEventHook and publishes to shared memory
// This allows the Tauri window to follow Unity window even during drag operations

#include <windows.h>
#include <stdio.h>
#include <cstdint>

// Shared memory layout (must match C# CodelyWindowSyncSharedMemory)
const int IPC_SIZE = 64;
const uint32_t IPC_MAGIC = 0x31495543; // "CUI1" little-endian
const uint32_t IPC_VERSION = 1;
const uint32_t FLAG_VISIBLE = 1u << 0;
const uint32_t FLAG_ACTIVE = 1u << 1;

// Offsets
const int OFFSET_MAGIC = 0;
const int OFFSET_VERSION = 4;
const int OFFSET_SEQ = 8;
const int OFFSET_X = 12;
const int OFFSET_Y = 16;
const int OFFSET_W = 20;
const int OFFSET_H = 24;
const int OFFSET_RESERVED = 28;
const int OFFSET_FLAGS = 32;
const int OFFSET_OWNER = 40;

// Global state
static HWND g_targetWindow = NULL;
static HANDLE g_mmFile = NULL;
static void* g_mmView = NULL;
static HWINEVENTHOOK g_eventHook = NULL;
static uint32_t g_seq = 0;
static bool g_initialized = false;
static CRITICAL_SECTION g_cs;

// Initialize shared memory
static bool InitializeSharedMemory(LPCWSTR mappingName)
{
    if (g_mmView != NULL) return true; // Already initialized

    // Open or create the file mapping
    g_mmFile = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, mappingName);
    if (g_mmFile == NULL)
    {
        // Try to create it
        g_mmFile = CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            NULL,
            PAGE_READWRITE,
            0,
            IPC_SIZE,
            mappingName
        );
        if (g_mmFile == NULL)
        {
            return false;
        }
    }

    // Map the view
    g_mmView = MapViewOfFile(g_mmFile, FILE_MAP_ALL_ACCESS, 0, 0, IPC_SIZE);
    if (g_mmView == NULL)
    {
        CloseHandle(g_mmFile);
        g_mmFile = NULL;
        return false;
    }

    // Initialize header if needed
    uint32_t* magic = (uint32_t*)((uint8_t*)g_mmView + OFFSET_MAGIC);
    uint32_t* version = (uint32_t*)((uint8_t*)g_mmView + OFFSET_VERSION);
    if (*magic != IPC_MAGIC || *version != IPC_VERSION)
    {
        *magic = IPC_MAGIC;
        *version = IPC_VERSION;
        g_seq = 0;
        *(uint32_t*)((uint8_t*)g_mmView + OFFSET_SEQ) = 0;
        *(int32_t*)((uint8_t*)g_mmView + OFFSET_X) = 0;
        *(int32_t*)((uint8_t*)g_mmView + OFFSET_Y) = 0;
        *(int32_t*)((uint8_t*)g_mmView + OFFSET_W) = 0;
        *(int32_t*)((uint8_t*)g_mmView + OFFSET_H) = 0;
        *(int32_t*)((uint8_t*)g_mmView + OFFSET_RESERVED) = 0;
        *(uint32_t*)((uint8_t*)g_mmView + OFFSET_FLAGS) = 0;
        *(int64_t*)((uint8_t*)g_mmView + OFFSET_OWNER) = 0;
        FlushViewOfFile(g_mmView, IPC_SIZE);
    }
    else
    {
        // Read current sequence
        g_seq = *(uint32_t*)((uint8_t*)g_mmView + OFFSET_SEQ);
    }

    return true;
}

// Cleanup shared memory
static void CleanupSharedMemory()
{
    if (g_mmView != NULL)
    {
        UnmapViewOfFile(g_mmView);
        g_mmView = NULL;
    }
    if (g_mmFile != NULL)
    {
        CloseHandle(g_mmFile);
        g_mmFile = NULL;
    }
}

// Write rect to shared memory
static void WriteRect(int x, int y, int w, int h, bool visible, bool active, int64_t owner)
{
    if (g_mmView == NULL) return;

    EnterCriticalSection(&g_cs);

    // Write data first
    *(int32_t*)((uint8_t*)g_mmView + OFFSET_X) = x;
    *(int32_t*)((uint8_t*)g_mmView + OFFSET_Y) = y;
    *(int32_t*)((uint8_t*)g_mmView + OFFSET_W) = w;
    *(int32_t*)((uint8_t*)g_mmView + OFFSET_H) = h;

    uint32_t flags = (visible ? FLAG_VISIBLE : 0) | (active ? FLAG_ACTIVE : 0);
    *(uint32_t*)((uint8_t*)g_mmView + OFFSET_FLAGS) = flags;
    *(int64_t*)((uint8_t*)g_mmView + OFFSET_OWNER) = owner;

    // Update sequence last (two-phase commit)
    g_seq++;
    *(uint32_t*)((uint8_t*)g_mmView + OFFSET_SEQ) = g_seq;

    LeaveCriticalSection(&g_cs);
}

// Window event callback
static void CALLBACK WinEventProc(
    HWINEVENTHOOK hWinEventHook,
    DWORD event,
    HWND hwnd,
    LONG idObject,
    LONG idChild,
    DWORD dwEventThread,
    DWORD dwmsEventTime)
{
    if (g_targetWindow == NULL || hwnd != g_targetWindow) return;

    // Get window rect
    RECT rect;
    if (!GetWindowRect(g_targetWindow, &rect)) return;

    // Convert to screen coordinates
    int x = rect.left;
    int y = rect.top;
    int w = rect.right - rect.left;
    int h = rect.bottom - rect.top;

    // Check if window is visible
    BOOL isVisible = IsWindowVisible(g_targetWindow);

    // Write to shared memory
    // Note: We don't know the exact visibility/active state from here,
    // so we just publish the rect and let C# side handle visibility logic
    int64_t owner = (int64_t)g_targetWindow;
    WriteRect(x, y, w, h, isVisible != FALSE, true, owner);
}

// Exported function: start monitoring
extern "C" __declspec(dllexport) int codely_ws_start(int64_t hwnd, const char* mappingName)
{
    if (g_initialized) return 0; // Already running

    // Initialize critical section
    InitializeCriticalSection(&g_cs);

    // Convert mapping name to wide string
    wchar_t wideName[512];
    MultiByteToWideChar(CP_UTF8, 0, mappingName, -1, wideName, 512);

    // Initialize shared memory
    if (!InitializeSharedMemory(wideName))
    {
        DeleteCriticalSection(&g_cs);
        return 1; // Error
    }

    g_targetWindow = (HWND)hwnd;

    // Set up WinEventHook for window location changes
    // Use WINEVENT_OUTOFCONTEXT to avoid deadlocks during UI operations
    g_eventHook = SetWinEventHook(
        EVENT_OBJECT_LOCATIONCHANGE,
        EVENT_OBJECT_LOCATIONCHANGE,
        NULL,
        WinEventProc,
        0,
        0,
        WINEVENT_OUTOFCONTEXT
    );

    if (g_eventHook == NULL)
    {
        CleanupSharedMemory();
        DeleteCriticalSection(&g_cs);
        return 1; // Error
    }

    // Initial rect write
    RECT rect;
    if (GetWindowRect(g_targetWindow, &rect))
    {
        BOOL isVisible = IsWindowVisible(g_targetWindow);
        WriteRect(
            rect.left, rect.top,
            rect.right - rect.left, rect.bottom - rect.top,
            isVisible != FALSE, true,
            (int64_t)g_targetWindow
        );
    }

    g_initialized = true;
    return 0; // Success
}

// Exported function: stop monitoring
extern "C" __declspec(dllexport) void codely_ws_stop()
{
    if (!g_initialized) return;

    if (g_eventHook != NULL)
    {
        UnhookWinEvent(g_eventHook);
        g_eventHook = NULL;
    }

    CleanupSharedMemory();

    DeleteCriticalSection(&g_cs);

    g_targetWindow = NULL;
    g_initialized = false;
}

// DLL entry point
BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
        // Initialize
        InitializeCriticalSection(&g_cs);
        break;
    case DLL_PROCESS_DETACH:
        // Cleanup
        if (g_initialized)
        {
            codely_ws_stop();
        }
        DeleteCriticalSection(&g_cs);
        break;
    }
    return TRUE;
}