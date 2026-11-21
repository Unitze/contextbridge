#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <shobjidl.h>

#include <memory>
#include <optional>
#include <vector>
#include <string>

#include "../core/config.h"
#include "../core/strings.h"

namespace cb {

// {E3AC0490-1596-4CD9-8755-45988C3A5DC4}: files, folders and drives.
inline constexpr CLSID CLSID_ItemsCommand = {
    0xe3ac0490, 0x1596, 0x4cd9, {0x87, 0x55, 0x45, 0x98, 0x8c, 0x3a, 0x5d, 0xc4}};
// {B668E986-BDA4-4D6B-9511-F4BD8708A764}: folder background.
inline constexpr CLSID CLSID_BackgroundCommand = {
    0xb668e986, 0xbda4, 0x4d6b, {0x95, 0x11, 0xf4, 0xbd, 0x87, 0x08, 0xa7, 0x64}};

extern HMODULE g_module;
void ModuleAddRef();
void ModuleRelease();
LONG ModuleRefCount();

// ---- config cache (config_store.cpp) ----
std::wstring ConfigPath();
std::wstring DataDirectory();
// Returns the current config, reloading only if the file changed. Never throws; returns
// null when the file is missing, invalid, or fails the ownership check.
std::shared_ptr<const Config> CurrentConfig();

// ---- logging (log.cpp) ----
void Log(const wchar_t* fmt, ...);
void SetLoggingEnabled(bool enabled);

// ---- launching (launcher.cpp) ----
std::optional<std::wstring> EnvLookupWin(const std::wstring& name);
// Resolves a program for icon extraction (env vars, PATH, App Paths). Empty if not found.
std::wstring ResolveProgramPath(const std::wstring& program);
// Resolves an icon file path (env vars, unquoting, plus SearchPathW for bare DLL/ICO names).
std::wstring ResolveIconFilePath(const std::wstring& iconPath);
// Starts the planned processes on a worker thread so explorer's UI thread never blocks.
void LaunchAsync(std::vector<LaunchSpec> specs, std::wstring title);

HRESULT CreateRootCommand(bool background, REFIID riid, void** ppv);

}  // namespace cb
