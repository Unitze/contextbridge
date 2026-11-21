#include "strings.h"

#include <cstdint>
#include <cwctype>

namespace cb {

std::wstring Utf8ToWide(const std::string& s) {
    std::wstring out;
    out.reserve(s.size());
    size_t i = 0;
    const size_t n = s.size();
    auto emit = [&out](uint32_t cp) {
        if constexpr (sizeof(wchar_t) == 2) {
            if (cp >= 0x10000) {
                cp -= 0x10000;
                out += static_cast<wchar_t>(0xD800 + (cp >> 10));
                out += static_cast<wchar_t>(0xDC00 + (cp & 0x3FF));
                return;
            }
        }
        out += static_cast<wchar_t>(cp);
    };
    while (i < n) {
        unsigned char c = static_cast<unsigned char>(s[i]);
        uint32_t cp;
        int extra;
        if (c < 0x80) { cp = c; extra = 0; }
        else if ((c & 0xE0) == 0xC0) { cp = c & 0x1F; extra = 1; }
        else if ((c & 0xF0) == 0xE0) { cp = c & 0x0F; extra = 2; }
        else if ((c & 0xF8) == 0xF0) { cp = c & 0x07; extra = 3; }
        else { emit(0xFFFD); ++i; continue; }
        bool bad = false;
        for (int k = 1; k <= extra; ++k) {
            if (i + k >= n) { bad = true; break; }
            unsigned char cc = static_cast<unsigned char>(s[i + k]);
            if ((cc & 0xC0) != 0x80) { bad = true; break; }
            cp = (cp << 6) | (cc & 0x3F);
        }
        if (bad) { emit(0xFFFD); ++i; continue; }
        // Reject overlong encodings and surrogates.
        if ((extra == 1 && cp < 0x80) || (extra == 2 && cp < 0x800) || (extra == 3 && cp < 0x10000) ||
            cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) {
            emit(0xFFFD);
        } else {
            emit(cp);
        }
        i += extra + 1;
    }
    return out;
}

std::string WideToUtf8(const std::wstring& s) {
    std::string out;
    out.reserve(s.size());
    for (size_t i = 0; i < s.size(); ++i) {
        uint32_t cp = static_cast<uint32_t>(s[i]);
        if constexpr (sizeof(wchar_t) == 2) {
            if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < s.size()) {
                uint32_t lo = static_cast<uint32_t>(s[i + 1]);
                if (lo >= 0xDC00 && lo <= 0xDFFF) {
                    cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                    ++i;
                }
            }
        }
        if (cp >= 0xD800 && cp <= 0xDFFF) cp = 0xFFFD;
        if (cp < 0x80) {
            out += static_cast<char>(cp);
        } else if (cp < 0x800) {
            out += static_cast<char>(0xC0 | (cp >> 6));
            out += static_cast<char>(0x80 | (cp & 0x3F));
        } else if (cp < 0x10000) {
            out += static_cast<char>(0xE0 | (cp >> 12));
            out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (cp & 0x3F));
        } else {
            out += static_cast<char>(0xF0 | (cp >> 18));
            out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
            out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (cp & 0x3F));
        }
    }
    return out;
}

wchar_t FoldCase(wchar_t c) {
    if (c >= L'A' && c <= L'Z') return static_cast<wchar_t>(c - L'A' + L'a');
    if (c < 0x80) return c;
    // Locale-independent folding for common Latin-1 and fullwidth ASCII uppercase letters.
    if ((c >= 0x00C0 && c <= 0x00D6) || (c >= 0x00D8 && c <= 0x00DE)) return static_cast<wchar_t>(c + 0x20);
    if (c >= 0xFF21 && c <= 0xFF3A) return static_cast<wchar_t>(c - 0xFF21 + 0xFF41);
    return static_cast<wchar_t>(std::towlower(static_cast<wint_t>(c)));
}

bool EqualsIgnoreCase(const std::wstring& a, const std::wstring& b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        if (FoldCase(a[i]) != FoldCase(b[i])) return false;
    }
    return true;
}

bool EndsWithIgnoreCase(const std::wstring& s, const std::wstring& suffix) {
    if (suffix.size() > s.size()) return false;
    size_t off = s.size() - suffix.size();
    for (size_t i = 0; i < suffix.size(); ++i) {
        if (FoldCase(s[off + i]) != FoldCase(suffix[i])) return false;
    }
    return true;
}

static bool IsSep(wchar_t c) { return c == L'\\' || c == L'/'; }

std::wstring FileNameOf(const std::wstring& path) {
    size_t end = path.size();
    while (end > 0 && IsSep(path[end - 1])) --end;
    size_t start = end;
    while (start > 0 && !IsSep(path[start - 1]) && path[start - 1] != L':') --start;
    return path.substr(start, end - start);
}

std::wstring StemOf(const std::wstring& path) {
    std::wstring name = FileNameOf(path);
    size_t dot = name.rfind(L'.');
    if (dot == std::wstring::npos || dot == 0) return name;
    return name.substr(0, dot);
}

bool IsRootPath(const std::wstring& path) {
    // Drive roots: "C:", "C:\", "C:/"
    if ((path.size() == 2 || path.size() == 3) && path[1] == L':' &&
        ((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z'))) {
        return path.size() == 2 || IsSep(path[2]);
    }
    // UNC share roots: "\\server\share" or "\\server\share\" (both segments non-empty)
    if (path.size() > 2 && IsSep(path[0]) && IsSep(path[1])) {
        std::wstring rest = path.substr(2);
        while (!rest.empty() && IsSep(rest.back())) rest.pop_back();
        if (rest.empty() || IsSep(rest.front()) || rest.front() == L'?' || rest.front() == L'.') return false;
        size_t sepPos = std::wstring::npos;
        for (size_t i = 0; i < rest.size(); ++i) {
            if (IsSep(rest[i])) {
                if (sepPos != std::wstring::npos) return false;  // more than one separator
                sepPos = i;
            }
        }
        return sepPos != std::wstring::npos && sepPos > 0 && sepPos + 1 < rest.size();
    }
    return false;
}

std::wstring ParentOf(const std::wstring& path) {
    if (path.empty() || IsRootPath(path)) return L"";
    size_t end = path.size();
    while (end > 0 && IsSep(path[end - 1])) --end;
    size_t pos = end;
    while (pos > 0 && !IsSep(path[pos - 1])) --pos;
    if (pos == 0) {
        // "C:file" or bare name
        if (end >= 2 && path[1] == L':') return path.substr(0, 2) + L"\\";
        return L"";
    }
    std::wstring parent = path.substr(0, pos);  // includes trailing separator
    if (IsRootPath(parent)) {
        // Keep "C:\" form; normalize UNC root without trailing separator.
        if (parent.size() >= 2 && parent[1] == L':') return parent;
        while (!parent.empty() && IsSep(parent.back())) parent.pop_back();
        return parent;
    }
    while (parent.size() > 1 && IsSep(parent.back())) parent.pop_back();
    return parent;
}

std::wstring Trim(const std::wstring& s) {
    size_t b = 0, e = s.size();
    while (b < e && std::iswspace(static_cast<wint_t>(s[b]))) ++b;
    while (e > b && std::iswspace(static_cast<wint_t>(s[e - 1]))) --e;
    return s.substr(b, e - b);
}

}  // namespace cb
