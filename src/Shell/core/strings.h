// Portable string helpers (no Win32 dependency so the core can be unit-tested anywhere).
#pragma once

#include <string>

namespace cb {

std::wstring Utf8ToWide(const std::string& s);
std::string WideToUtf8(const std::wstring& s);

wchar_t FoldCase(wchar_t c);
bool EqualsIgnoreCase(const std::wstring& a, const std::wstring& b);
bool EndsWithIgnoreCase(const std::wstring& s, const std::wstring& suffix);

// Path helpers that understand both '\\' and '/'.
std::wstring FileNameOf(const std::wstring& path);
std::wstring StemOf(const std::wstring& path);          // file name without last extension
std::wstring ParentOf(const std::wstring& path);        // "" when there is no parent
bool IsRootPath(const std::wstring& path);              // "C:\", "C:", "\\server\share"

std::wstring Trim(const std::wstring& s);

}  // namespace cb
