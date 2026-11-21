// Process launching for selected commands. Runs ShellExecuteEx on a dedicated thread so a slow
// target (network share, UAC prompt, App Paths lookup) cannot freeze explorer.
#include "shell.h"

#include <shellapi.h>
#include <shlwapi.h>

#include <new>

namespace cb {

std::optional<std::wstring> EnvLookupWin(const std::wstring& name) {
    DWORD needed = GetEnvironmentVariableW(name.c_str(), nullptr, 0);
    if (needed == 0 || needed > 32768) return std::nullopt;
    std::wstring buf(needed, L'\0');
    DWORD n = GetEnvironmentVariableW(name.c_str(), buf.data(), needed);
    if (n == 0 || n >= needed) return std::nullopt;
    buf.resize(n);
    return buf;
}

static bool FileExists(const std::wstring& p) {
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

static std::wstring Unquote(const std::wstring& s) {
    if (s.size() >= 2 && s.front() == L'"' && s.back() == L'"') return s.substr(1, s.size() - 2);
    return s;
}

static std::wstring AppPathsLookupExact(const std::wstring& exe) {
    std::wstring key = L"Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\" + exe;
    for (HKEY root : {HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE}) {
        DWORD size = 0;
        if (RegGetValueW(root, key.c_str(), nullptr, RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ, nullptr, nullptr, &size) !=
                ERROR_SUCCESS ||
            size < sizeof(wchar_t) || size > 65536) {
            continue;
        }
        std::wstring buf(size / sizeof(wchar_t), L'\0');
        if (RegGetValueW(root, key.c_str(), nullptr, RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ, nullptr, buf.data(),
                         &size) == ERROR_SUCCESS) {
            while (!buf.empty() && buf.back() == L'\0') buf.pop_back();
            std::wstring v = Unquote(Trim(buf));
            if (FileExists(v)) return v;
        }
    }
    return L"";
}

static std::wstring AppPathsLookup(const std::wstring& exe) {
    std::wstring found = AppPathsLookupExact(exe);
    if (!found.empty()) return found;
    if (exe.find(L'.') == std::wstring::npos) {
        return AppPathsLookupExact(exe + L".exe");
    }
    return L"";
}

std::wstring ResolveProgramPath(const std::wstring& program) {
    std::wstring p = Unquote(Trim(ExpandEnvOnly(program, EnvLookupWin)));
    if (p.empty()) return L"";
    bool hasDir = p.find_first_of(L"\\/:") != std::wstring::npos;
    if (hasDir) return FileExists(p) ? p : L"";
    std::wstring viaAppPaths = AppPathsLookup(p);
    if (!viaAppPaths.empty()) return viaAppPaths;
    wchar_t buf[MAX_PATH * 4];
    DWORD n = SearchPathW(nullptr, p.c_str(), L".exe", _countof(buf), buf, nullptr);
    if (n > 0 && n < _countof(buf)) return buf;
    return L"";
}

std::wstring ResolveIconFilePath(const std::wstring& iconPath) {
    std::wstring p = Unquote(Trim(ExpandEnvOnly(iconPath, EnvLookupWin)));
    if (p.empty()) return L"";
    bool hasDir = p.find_first_of(L"\\/:") != std::wstring::npos;
    if (hasDir) return FileExists(p) ? p : L"";
    wchar_t buf[MAX_PATH * 4];
    DWORD n = SearchPathW(nullptr, p.c_str(), nullptr, _countof(buf), buf, nullptr);
    if (n > 0 && n < _countof(buf)) return buf;
    return L"";
}

namespace {

struct LaunchJob {
    std::vector<LaunchSpec> specs;
    std::wstring title;
    HMODULE self = nullptr;  // module reference held for the thread's lifetime
};

int ShowCmd(WindowMode m) {
    switch (m) {
        case WindowMode::Minimized: return SW_SHOWMINNOACTIVE;
        case WindowMode::Maximized: return SW_SHOWMAXIMIZED;
        case WindowMode::Hidden: return SW_HIDE;
        default: return SW_SHOWNORMAL;
    }
}

DWORD WINAPI LaunchThread(void* param) {
    LaunchJob* job = static_cast<LaunchJob*>(param);
    HRESULT hrInit = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
    try {
        for (const auto& s : job->specs) {
            SHELLEXECUTEINFOW sei{};
            sei.cbSize = sizeof(sei);
            sei.fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
            sei.lpVerb = s.runAsAdmin ? L"runas" : L"open";
            sei.lpFile = s.program.c_str();
            sei.lpParameters = s.arguments.empty() ? nullptr : s.arguments.c_str();
            DWORD wdAttr = s.workingDirectory.empty() ? INVALID_FILE_ATTRIBUTES
                                                      : GetFileAttributesW(s.workingDirectory.c_str());
            bool wdOk = wdAttr != INVALID_FILE_ATTRIBUTES && (wdAttr & FILE_ATTRIBUTE_DIRECTORY);
            sei.lpDirectory = wdOk ? s.workingDirectory.c_str() : nullptr;
            sei.nShow = ShowCmd(s.window);
            Log(L"launch: %ls %ls (cwd=%ls admin=%d)", s.program.c_str(), s.arguments.c_str(),
                s.workingDirectory.c_str(), s.runAsAdmin ? 1 : 0);
            if (!ShellExecuteExW(&sei)) {
                DWORD err = GetLastError();
                Log(L"launch failed: %lu", err);
                if (err == ERROR_CANCELLED) break;  // user declined UAC: do not prompt again per file
                wchar_t sys[512] = L"";
                FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, nullptr, err, 0, sys,
                               _countof(sys), nullptr);
                bool ja = PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_JAPANESE;
                std::wstring text = ja ? L"\u8d77\u52d5\u306b\u5931\u6557\u3057\u307e\u3057\u305f\u3002\n\n"
                                       : L"Failed to launch program.\n\n";
                text += s.program + L"\n\n" + sys;
                MessageBoxW(nullptr, text.c_str(), job->title.c_str(), MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
                break;
            }
        }
    } catch (...) {
    }
    if (SUCCEEDED(hrInit)) CoUninitialize();
    HMODULE self = job->self;
    delete job;
    // Drop our module reference and exit without returning into possibly-unloaded code.
    FreeLibraryAndExitThread(self, 0);  // does not return
}

}  // namespace

void LaunchAsync(std::vector<LaunchSpec> specs, std::wstring title) {
    if (specs.empty()) return;
    HMODULE self = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                            reinterpret_cast<LPCWSTR>(&LaunchThread), &self)) {
        return;
    }
    auto* job = new (std::nothrow) LaunchJob{std::move(specs), std::move(title), self};
    if (!job) {
        FreeLibrary(self);
        return;
    }
    HANDLE t = CreateThread(nullptr, 0, LaunchThread, job, 0, nullptr);
    if (!t) {
        delete job;
        FreeLibrary(self);
        return;
    }
    CloseHandle(t);
}

}  // namespace cb
