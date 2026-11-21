// Opt-in diagnostics (settings.logging). Off by default; never throws.
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <mutex>

#include "shell.h"

namespace cb {

static std::atomic<bool> g_logging{false};

void SetLoggingEnabled(bool enabled) { g_logging = enabled; }

void Log(const wchar_t* fmt, ...) {
    if (!g_logging) return;
    static std::mutex* m = new std::mutex();  // intentionally leaked like TheCache() to avoid unload-order issues
    try {
        wchar_t msg[1024];
        va_list ap;
        va_start(ap, fmt);
        _vsnwprintf_s(msg, _countof(msg), _TRUNCATE, fmt, ap);
        va_end(ap);

        std::wstring dir = DataDirectory();
        if (dir.empty()) return;
        dir += L"\\logs";
        CreateDirectoryW(dir.c_str(), nullptr);
        std::wstring path = dir + L"\\shell.log";

        SYSTEMTIME st;
        GetLocalTime(&st);
        wchar_t line[1200];
        int n = _snwprintf_s(line, _countof(line), _TRUNCATE, L"%04u-%02u-%02u %02u:%02u:%02u.%03u [%lu] %ls\r\n",
                             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                             GetCurrentThreadId(), msg);
        std::wstring lineStr;
        if (n < 0) {
            lineStr = line;
            if (lineStr.size() < 2 || lineStr.compare(lineStr.size() - 2, 2, L"\r\n") != 0) lineStr += L"\r\n";
        } else {
            lineStr.assign(line, static_cast<size_t>(n));
        }
        std::string utf8 = WideToUtf8(lineStr);

        std::lock_guard<std::mutex> guard(*m);
        HANDLE h = CreateFileW(path.c_str(), FILE_APPEND_DATA,
                               FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS,
                               FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) return;
        LARGE_INTEGER size{};
        if (GetFileSizeEx(h, &size) && size.QuadPart > 4 * 1024 * 1024) {
            // Keep the log bounded: rotate safely even when another process holds the file open.
            CloseHandle(h);
            std::wstring oldPath = dir + L"\\shell.log.old";
            MoveFileExW(path.c_str(), oldPath.c_str(), MOVEFILE_REPLACE_EXISTING);
            h = CreateFileW(path.c_str(), FILE_APPEND_DATA,
                            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS,
                            FILE_ATTRIBUTE_NORMAL, nullptr);
            if (h == INVALID_HANDLE_VALUE) return;
        }
        DWORD written = 0;
        WriteFile(h, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
        CloseHandle(h);
    } catch (...) {
    }
}

}  // namespace cb
