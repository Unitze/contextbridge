// Windows-only smoke test for ContextBridge.Shell.dll: loads the DLL like Explorer would
// (DllGetClassObject → IExplorerCommand), feeds it real IShellItemArrays and checks titles,
// visibility, sub-command enumeration and an actual launch.
//
// Usage: cb_shell_smoke <path-to-ContextBridge.Shell.dll>
// It temporarily replaces %LOCALAPPDATA%\ContextBridge\commands.json (restored afterwards).
#ifndef UNICODE
#define UNICODE
#endif
#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace fs = std::filesystem;

static const CLSID CLSID_Items = {0xe3ac0490, 0x1596, 0x4cd9, {0x87, 0x55, 0x45, 0x98, 0x8c, 0x3a, 0x5d, 0xc4}};
static const CLSID CLSID_Background = {0xb668e986, 0xbda4, 0x4d6b, {0x95, 0x11, 0xf4, 0xbd, 0x87, 0x08, 0xa7, 0x64}};

static int g_failed = 0;
#define EXPECT(cond)                                                              \
    do {                                                                          \
        if (!(cond)) {                                                            \
            ++g_failed;                                                           \
            std::fprintf(stderr, "%s:%d: FAILED: %s\n", __FILE__, __LINE__, #cond); \
        }                                                                         \
    } while (0)

using GetClassObjectFn = HRESULT(__stdcall*)(REFCLSID, REFIID, void**);

static IExplorerCommand* Create(GetClassObjectFn fn, REFCLSID clsid) {
    IClassFactory* cf = nullptr;
    if (FAILED(fn(clsid, IID_IClassFactory, reinterpret_cast<void**>(&cf)))) return nullptr;
    IExplorerCommand* cmd = nullptr;
    cf->CreateInstance(nullptr, IID_IExplorerCommand, reinterpret_cast<void**>(&cmd));
    cf->Release();
    return cmd;
}

static IShellItemArray* ArrayOf(const std::vector<fs::path>& paths) {
    std::vector<PIDLIST_ABSOLUTE> pidls;
    for (auto& p : paths) {
        PIDLIST_ABSOLUTE pidl = nullptr;
        if (SUCCEEDED(SHParseDisplayName(p.c_str(), nullptr, &pidl, 0, nullptr))) pidls.push_back(pidl);
    }
    IShellItemArray* arr = nullptr;
    SHCreateShellItemArrayFromIDLists(static_cast<UINT>(pidls.size()),
                                      const_cast<PCIDLIST_ABSOLUTE_ARRAY>(pidls.data()), &arr);
    for (auto p : pidls) CoTaskMemFree(p);
    return arr;
}

static std::wstring Title(IExplorerCommand* c, IShellItemArray* a) {
    PWSTR t = nullptr;
    std::wstring r;
    if (SUCCEEDED(c->GetTitle(a, &t)) && t) r = t;
    CoTaskMemFree(t);
    return r;
}

static EXPCMDSTATE State(IExplorerCommand* c, IShellItemArray* a) {
    EXPCMDSTATE s = ECS_DISABLED;
    c->GetState(a, TRUE, &s);
    return s;
}

static std::vector<IExplorerCommand*> Children(IExplorerCommand* c) {
    std::vector<IExplorerCommand*> out;
    IEnumExplorerCommand* e = nullptr;
    if (FAILED(c->EnumSubCommands(&e)) || !e) return out;
    IExplorerCommand* item = nullptr;
    ULONG fetched = 0;
    while (e->Next(1, &item, &fetched) == S_OK && fetched == 1) out.push_back(item);
    e->Release();
    return out;
}

static std::string Utf8(const std::wstring& w) {
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string s(n > 0 ? n - 1 : 0, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, s.data(), n, nullptr, nullptr);
    return s;
}

static std::string JsonEscape(const std::string& s) {
    std::string o;
    for (char c : s) {
        if (c == '\\' || c == '"') o += '\\';
        o += c;
    }
    return o;
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) {
        std::fprintf(stderr, "usage: cb_shell_smoke <ContextBridge.Shell.dll>\n");
        return 2;
    }
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    wchar_t local[MAX_PATH];
    GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
    fs::path dataDir = fs::path(local) / L"ContextBridge";
    fs::path cfgPath = dataDir / L"commands.json";
    fs::path backup = dataDir / L"commands.json.smoke-backup";
    fs::create_directories(dataDir);
    bool hadConfig = fs::exists(cfgPath);
    if (hadConfig) fs::copy_file(cfgPath, backup, fs::copy_options::overwrite_existing);

    fs::path work = fs::temp_directory_path() / L"cb smoke テスト";
    struct RestoreGuard {
        bool hadConfig;
        fs::path cfgPath, backup, work;
        ~RestoreGuard() {
            std::error_code ec;
            if (hadConfig) {
                fs::copy_file(backup, cfgPath, fs::copy_options::overwrite_existing, ec);
                fs::remove(backup, ec);
            } else {
                fs::remove(cfgPath, ec);
            }
            fs::remove_all(work, ec);
        }
    } guard{hadConfig, cfgPath, backup, work};

    fs::remove_all(work);
    fs::create_directories(work / L"sub");
    std::ofstream(work / L"a.txt") << "a";
    std::ofstream(work / L"b.md") << "b";
    std::ofstream(work / L"c.png") << "c";
    fs::path marker = work / L"launched.txt";

    std::string cfg = R"({
  "version": 1,
  "settings": { "menuTitle": "Smoke", "logging": true },
  "groups": [ { "id": "g", "title": "Group" } ],
  "commands": [
    { "id": "text", "title": "Text only", "group": "g",
      "target": { "itemTypes": ["file"], "extensions": [".txt", ".md"] },
      "action": { "program": "%SystemRoot%\\System32\\cmd.exe",
                  "arguments": "/c echo %* > \")" + JsonEscape(Utf8(marker.wstring())) + R"(\"", "window": "hidden" } },
    { "id": "folder", "title": "Folder only",
      "target": { "itemTypes": ["folder", "folderBackground"] },
      "action": { "program": "cmd.exe" } },
    { "id": "off", "title": "Disabled", "enabled": false,
      "target": { "itemTypes": ["*"] }, "action": { "program": "cmd.exe" } }
  ]
})";
    std::ofstream(cfgPath, std::ios::binary) << cfg;

    HMODULE dll = LoadLibraryW(argv[1]);
    EXPECT(dll != nullptr);
    if (!dll) return 1;
    auto getClass = reinterpret_cast<GetClassObjectFn>(GetProcAddress(dll, "DllGetClassObject"));
    EXPECT(getClass != nullptr);
    EXPECT(GetProcAddress(dll, "DllCanUnloadNow") != nullptr);

    IExplorerCommand* root = Create(getClass, CLSID_Items);
    EXPECT(root != nullptr);
    if (root) {
        IShellItemArray* txt = ArrayOf({work / L"a.txt", work / L"b.md"});
        IShellItemArray* png = ArrayOf({work / L"c.png"});
        IShellItemArray* mixed = ArrayOf({work / L"a.txt", work / L"c.png"});
        IShellItemArray* folder = ArrayOf({work / L"sub"});

        EXPECT(Title(root, txt) == L"Smoke");
        EXPECT(State(root, txt) == ECS_ENABLED);
        EXPECT(State(root, png) == ECS_HIDDEN);    // nothing matches .png
        EXPECT(State(root, mixed) == ECS_HIDDEN);  // .png in the selection disqualifies "Text only"
        EXPECT(State(root, folder) == ECS_ENABLED);

        // Groups are flattened into sections (Windows 11 does not expand nested flyouts), and the
        // flyout is laid out for the selection read by the preceding root GetState.
        EXPECT(State(root, txt) == ECS_ENABLED);
        auto top = Children(root);
        EXPECT(top.size() == 2);  // header "Group" + "Text only" ("Folder only" / disabled omitted)
        if (top.size() == 2) {
            EXPECT(Title(top[0], txt) == L"Group");
            EXPECT(State(top[0], txt) == ECS_DISABLED);  // header is a label
            EXPECT(Title(top[1], txt) == L"Text only");
            EXPECT(State(top[1], txt) == ECS_ENABLED);
            PWSTR icon = nullptr;
            EXPECT(SUCCEEDED(top[1]->GetIcon(txt, &icon)));
            CoTaskMemFree(icon);
            EXPECT(SUCCEEDED(top[1]->Invoke(txt, nullptr)));
            for (int i = 0; i < 100 && !fs::exists(marker); ++i) Sleep(100);
            EXPECT(fs::exists(marker));
            if (fs::exists(marker)) {
                std::ifstream in(marker);
                std::string content((std::istreambuf_iterator<char>(in)), {});
                std::printf("launch output: %s\n", content.c_str());
                EXPECT(content.find("a.txt") != std::string::npos);
                EXPECT(content.find("b.md") != std::string::npos);
            }
        }
        for (auto c : top) c->Release();

        EXPECT(State(root, folder) == ECS_ENABLED);
        auto forFolder = Children(root);
        EXPECT(forFolder.size() == 1);
        if (forFolder.size() == 1) EXPECT(Title(forFolder[0], folder) == L"Folder only");
        for (auto c : forFolder) c->Release();

        // Broken config → everything hidden, nothing thrown.
        Sleep(300);  // config stat calls are coalesced for 250 ms
        std::ofstream(cfgPath, std::ios::binary | std::ios::trunc) << "{ broken";
        Sleep(50);
        EXPECT(State(root, txt) == ECS_HIDDEN);

        for (auto a : {txt, png, mixed, folder}) if (a) a->Release();
        root->Release();
    }

    IExplorerCommand* bg = Create(getClass, CLSID_Background);
    EXPECT(bg != nullptr);
    if (bg) bg->Release();

    if (dll) FreeLibrary(dll);
    std::printf(g_failed ? "SMOKE FAILED (%d)\n" : "SMOKE OK\n", g_failed);
    CoUninitialize();
    return g_failed ? 1 : 0;
}
