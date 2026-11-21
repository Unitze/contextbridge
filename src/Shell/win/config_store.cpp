// Loads commands.json with a last-write-time cache (never block menu building on
// re-parsing an unchanged file) and an ownership check (a file that another
// non-admin principal can control must not drive process launches).
#include "shell.h"

#include <aclapi.h>
#include <sddl.h>
#include <shlobj.h>

#include <mutex>
#include <string>


namespace cb {

std::wstring DataDirectory() {
    PWSTR base = nullptr;
    std::wstring dir;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DONT_VERIFY, nullptr, &base)) && base) {
        dir = base;
        dir += L"\\ContextBridge";
    }
    CoTaskMemFree(base);
    return dir;
}

std::wstring ConfigPath() {
    std::wstring dir = DataDirectory();
    return dir.empty() ? dir : dir + L"\\commands.json";
}

namespace {

struct Cache {
    std::mutex lock;
    FILETIME lastWrite{};
    ULONGLONG size = ~0ull;
    ULONGLONG lastCheckTick = 0;
    bool checkedOnce = false;
    std::shared_ptr<const Config> config;
};

Cache& TheCache() {
    static Cache* c = new Cache();  // intentionally leaked: avoids destructor order issues at unload
    return *c;
}

bool SidIsTrusted(PSID sid, PSID currentUser) {
    if (!sid) return false;
    if (currentUser && EqualSid(sid, currentUser)) return true;
    BYTE sidBuf[SECURITY_MAX_SID_SIZE];
    DWORD sidLen = sizeof(sidBuf);
    if (CreateWellKnownSid(WinLocalSystemSid, nullptr, sidBuf, &sidLen) && EqualSid(sid, sidBuf)) return true;
    sidLen = sizeof(sidBuf);
    if (CreateWellKnownSid(WinBuiltinAdministratorsSid, nullptr, sidBuf, &sidLen) && EqualSid(sid, sidBuf))
        return true;
    return false;
}

bool CurrentUserSid(BYTE (&buf)[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE], PSID* outSid) {
    *outSid = nullptr;
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
    DWORD len = 0;
    bool ok = GetTokenInformation(token, TokenUser, buf, sizeof(buf), &len) != 0;
    CloseHandle(token);
    if (ok) *outSid = reinterpret_cast<TOKEN_USER*>(buf)->User.Sid;
    return ok;
}

// Verifies on the opened handle (avoiding TOCTOU) that:
//   1. Owner is the current user, SYSTEM, or BUILTIN\Administrators.
//   2. DACL is present and grants no write access to any other principal.
bool HandleSecurityIsTrusted(HANDLE h) {
    PSID owner = nullptr;
    PACL dacl = nullptr;
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (GetSecurityInfo(h, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, &owner, nullptr,
                        &dacl, nullptr, &sd) != ERROR_SUCCESS) {
        return false;
    }
    BYTE userBuf[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE];
    PSID userSid = nullptr;
    CurrentUserSid(userBuf, &userSid);

    bool trusted = SidIsTrusted(owner, userSid);
    if (trusted) {
        // A NULL DACL grants full access to Everyone; reject it.
        if (!dacl) {
            trusted = false;
        } else {
            constexpr ACCESS_MASK kWriteMask =
                FILE_WRITE_DATA | FILE_APPEND_DATA | WRITE_DAC | WRITE_OWNER | GENERIC_WRITE | GENERIC_ALL;
            ACL_SIZE_INFORMATION info{};
            if (!GetAclInformation(dacl, &info, sizeof(info), AclSizeInformation)) {
                trusted = false;
            } else {
                for (DWORD i = 0; i < info.AceCount; ++i) {
                    LPVOID acePtr = nullptr;
                    if (!GetAce(dacl, i, &acePtr)) {
                        trusted = false;
                        break;
                    }
                    auto* hdr = static_cast<PACE_HEADER>(acePtr);
                    if ((hdr->AceFlags & INHERIT_ONLY_ACE) != 0) continue;
                    if (hdr->AceType == ACCESS_ALLOWED_ACE_TYPE) {
                        auto* allow = static_cast<PACCESS_ALLOWED_ACE>(acePtr);
                        if ((allow->Mask & kWriteMask) != 0) {
                            PSID aceSid = reinterpret_cast<PSID>(&allow->SidStart);
                            if (!SidIsTrusted(aceSid, userSid)) {
                                trusted = false;
                                break;
                            }
                        }
                    }
                }
            }
        }
    }
    LocalFree(sd);
    return trusted;
}

bool LoadFromDisk(const std::wstring& path, std::shared_ptr<const Config>& outConfig, bool& outUntrusted) {
    outConfig.reset();
    outUntrusted = false;
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ | READ_CONTROL,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                           FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    if (!HandleSecurityIsTrusted(h)) {
        CloseHandle(h);
        outUntrusted = true;
        return false;
    }

    std::string data;
    LARGE_INTEGER size{};
    constexpr LONGLONG kMaxFileBytes = 8 * 1024 * 1024;
    if (GetFileSizeEx(h, &size) && size.QuadPart > 0 && size.QuadPart <= kMaxFileBytes) {
        const size_t total = static_cast<size_t>(size.QuadPart);
        data.resize(total);
        size_t offset = 0;
        while (offset < total) {
            DWORD chunk = 0;
            if (!ReadFile(h, data.data() + offset, static_cast<DWORD>(total - offset), &chunk, nullptr)) {
                offset = 0;
                break;
            }
            if (chunk == 0) break;
            offset += chunk;
        }
        data.resize(offset);
    } else if (size.QuadPart > kMaxFileBytes) {
        Log(L"config rejected: file exceeds 8 MiB");
    }
    CloseHandle(h);
    if (data.empty()) return false;
    auto r = LoadConfig(data);
    if (!r.config) {
        Log(L"config rejected: %hs", r.error.c_str());
        return false;
    }
    outConfig = std::move(r.config);
    return true;
}

}  // namespace

std::shared_ptr<const Config> CurrentConfig() {
    try {
        Cache& c = TheCache();
        std::lock_guard<std::mutex> guard(c.lock);
        // Explorer queries GetState many times while building one menu; coalesce stat calls.
        ULONGLONG now = GetTickCount64();
        if (c.checkedOnce && now - c.lastCheckTick < 250) return c.config;
        c.lastCheckTick = now;
        c.checkedOnce = true;

        std::wstring path = ConfigPath();
        WIN32_FILE_ATTRIBUTE_DATA fad{};
        if (path.empty() || !GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fad)) {
            c.config.reset();
            c.size = ~0ull;
            return nullptr;
        }
        ULONGLONG size = (static_cast<ULONGLONG>(fad.nFileSizeHigh) << 32) | fad.nFileSizeLow;
        if (c.size == size && CompareFileTime(&c.lastWrite, &fad.ftLastWriteTime) == 0) return c.config;
        c.lastWrite = fad.ftLastWriteTime;
        c.size = size;

        std::shared_ptr<const Config> loaded;
        bool untrusted = false;
        if (!LoadFromDisk(path, loaded, untrusted)) {
            if (untrusted) Log(L"config ignored: untrusted owner or writable ACL");
            c.config.reset();
            c.size = ~0ull;  // retry on the next check (e.g. mid-replace or permissions fixed)
            Log(L"config loaded: invalid");
            SetLoggingEnabled(false);
            return nullptr;
        }
        c.config = std::move(loaded);
        SetLoggingEnabled(c.config->settings.logging);
        Log(L"config loaded: ok");
        return c.config;
    } catch (...) {
        return nullptr;
    }
}

}  // namespace cb
