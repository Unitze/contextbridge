// IExplorerCommand tree:
//   Root ("ContextBridge", ECF_HASSUBCOMMANDS)
//     ├─ Command               (leaf)
//     └─ Group section         (separator + disabled header + commands; Windows 11 does not
//                               expand nested flyouts)
//          └─ Command          (leaf)
// The tree is built from a config snapshot; every node holds a reference to that snapshot so
// pointers into it stay valid even if the file is reloaded while a menu is open. Selection-
// dependent visibility is decided in GetState (non-matching items are hidden, not grayed out).
#include "shell.h"

#include <shlobj.h>
#include <shlwapi.h>

#include <cstring>
#include <mutex>
#include <new>
#include <vector>


namespace cb {

namespace {

// Deterministic non-null canonical GUID from a UTF-8 key (two independent 64-bit FNV-1a hashes).
GUID MakeCanonicalGuid(const std::wstring& key) {
    std::string utf8 = WideToUtf8(key);
    auto fnv = [&](uint64_t seed) {
        uint64_t h = seed;
        for (unsigned char b : utf8) {
            h ^= b;
            h *= 1099511628211ull;
        }
        return h;
    };
    uint64_t hi = fnv(14695981039346656037ull);
    uint64_t lo = fnv(14695981039346656037ull ^ 0x9e3779b97f4a7c15ull);
    unsigned char bytes[16];
    std::memcpy(bytes, &hi, 8);
    std::memcpy(bytes + 8, &lo, 8);
    // Mark as RFC 4122 variant, version 5 style.
    bytes[6] = static_cast<unsigned char>((bytes[6] & 0x0F) | 0x50);
    bytes[8] = static_cast<unsigned char>((bytes[8] & 0x3F) | 0x80);
    GUID g{};
    std::memcpy(&g, bytes, sizeof(g));
    return g;
}

// Shared by all nodes of one root instance. Owns the site reference so sub-commands can still
// query the hosting folder even if Explorer releases RootCommand before its children.
struct RootContext {
    bool background = false;
    std::mutex lock;
    IUnknown* site = nullptr;  // owned

    // Short-lived selection cache so Root + Group + every Leaf GetState during one menu build
    // does not re-enumerate IShellItemArray N times.
    const IShellItemArray* cachedItems = nullptr;
    DWORD cachedCount = 0;
    ULONGLONG cachedTick = 0;
    bool hasCache = false;
    Selection cachedSelection;

    ~RootContext() {
        if (site) site->Release();
        site = nullptr;
    }

    void SetSite(IUnknown* s) {
        std::lock_guard<std::mutex> guard(lock);
        if (s) s->AddRef();
        if (site) site->Release();
        site = s;
        hasCache = false;
    }

    IUnknown* AddRefSite() {
        std::lock_guard<std::mutex> guard(lock);
        if (site) site->AddRef();
        return site;
    }
};

HRESULT DupString(const std::wstring& s, LPWSTR* out) {
    if (!out) return E_POINTER;
    *out = nullptr;
    return SHStrDupW(s.c_str(), out);
}

// Folder currently shown by the browser hosting the menu; used for the background case
// when Explorer does not hand us an item array.
std::wstring FolderFromSite(IUnknown* site) {
    if (!site) return L"";
    std::wstring result;
    IServiceProvider* sp = nullptr;
    if (SUCCEEDED(site->QueryInterface(IID_PPV_ARGS(&sp)))) {
        IShellBrowser* browser = nullptr;
        if (SUCCEEDED(sp->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&browser)))) {
            IShellView* view = nullptr;
            if (SUCCEEDED(browser->QueryActiveShellView(&view))) {
                IFolderView* fv = nullptr;
                if (SUCCEEDED(view->QueryInterface(IID_PPV_ARGS(&fv)))) {
                    IShellItem* folder = nullptr;
                    if (SUCCEEDED(fv->GetFolder(IID_PPV_ARGS(&folder)))) {
                        PWSTR path = nullptr;
                        if (SUCCEEDED(folder->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path) result = path;
                        CoTaskMemFree(path);
                        folder->Release();
                    }
                    fv->Release();
                }
                view->Release();
            }
            browser->Release();
        }
        sp->Release();
    }
    return result;
}

constexpr DWORD kMaxItems = 10000;

Selection ReadSelectionDirect(RootContext& ctx, IShellItemArray* items) {
    Selection sel;
    if (ctx.background) {
        sel.background = true;
        if (items) {
            IShellItem* item = nullptr;
            if (SUCCEEDED(items->GetItemAt(0, &item))) {
                PWSTR path = nullptr;
                if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path) sel.backgroundFolder = path;
                CoTaskMemFree(path);
                item->Release();
            }
        }
        if (sel.backgroundFolder.empty()) {
            IUnknown* site = ctx.AddRefSite();
            if (site) {
                sel.backgroundFolder = FolderFromSite(site);
                site->Release();
            }
        }
        return sel;
    }
    if (!items) return sel;
    DWORD count = 0;
    if (FAILED(items->GetCount(&count)) || count == 0) return sel;
    if (count > kMaxItems) {
        sel.items.push_back({L"", ItemKind::File});  // too many: treat as unlaunchable
        return sel;
    }
    sel.items.reserve(count);
    for (DWORD i = 0; i < count; ++i) {
        IShellItem* item = nullptr;
        if (FAILED(items->GetItemAt(i, &item))) {
            sel.items.push_back({L"", ItemKind::File});
            continue;
        }
        SelectedItem si;
        PWSTR path = nullptr;
        if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path) si.path = path;
        CoTaskMemFree(path);
        SFGAOF attrs = 0;
        item->GetAttributes(SFGAO_FOLDER | SFGAO_STREAM | SFGAO_FILESYSTEM, &attrs);
        // Archives such as .zip report FOLDER|STREAM: they are files for our purposes.
        bool isFolder = (attrs & SFGAO_FOLDER) && !(attrs & SFGAO_STREAM);
        if (isFolder) si.kind = IsRootPath(si.path) ? ItemKind::Drive : ItemKind::Folder;
        else si.kind = ItemKind::File;
        sel.items.push_back(std::move(si));
        item->Release();
    }
    return sel;
}

Selection ReadSelectionCached(RootContext& ctx, IShellItemArray* items) {
    DWORD count = 0;
    if (items && FAILED(items->GetCount(&count))) count = 0;
    ULONGLONG now = GetTickCount64();
    {
        std::lock_guard<std::mutex> guard(ctx.lock);
        if (ctx.hasCache && ctx.cachedItems == items && ctx.cachedCount == count && now - ctx.cachedTick < 250) {
            return ctx.cachedSelection;
        }
    }
    Selection sel = ReadSelectionDirect(ctx, items);
    {
        std::lock_guard<std::mutex> guard(ctx.lock);
        ctx.cachedItems = items;
        ctx.cachedCount = count;
        ctx.cachedTick = now;
        ctx.cachedSelection = sel;
        ctx.hasCache = true;
    }
    return sel;
}

// Selection read by the most recent GetState (Explorer queries the root's state right before
// enumerating its sub-commands). False when there is none or it is stale.
bool CachedSelection(RootContext& ctx, Selection& out) {
    std::lock_guard<std::mutex> guard(ctx.lock);
    if (!ctx.hasCache || GetTickCount64() - ctx.cachedTick > 5000) return false;
    out = ctx.cachedSelection;
    return true;
}

std::wstring FormatIconResource(const std::wstring& path, int index) {
    if (path.empty()) return L"";
    std::wstring quoted = path.find(L',') != std::wstring::npos ? (L"\"" + path + L"\"") : path;
    return quoted + L"," + std::to_wstring(index);
}

std::wstring IconResource(const Icon& icon, const std::wstring& program) {
    switch (icon.source) {
        case IconSource::None:
            return L"";
        case IconSource::File: {
            std::wstring p = ResolveIconFilePath(icon.path);
            return FormatIconResource(p, icon.index);
        }
        case IconSource::Program:
        default: {
            std::wstring src = icon.path.empty() ? program : icon.path;
            std::wstring p = ResolveProgramPath(src);
            return FormatIconResource(p, icon.index);
        }
    }
}

// Common IUnknown/IExplorerCommand plumbing.
class CommandBase : public IExplorerCommand {
public:
    explicit CommandBase(std::shared_ptr<RootContext> ctx) : ctx_(std::move(ctx)) { ModuleAddRef(); }
    virtual ~CommandBase() { ModuleRelease(); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IExplorerCommand) {
            *ppv = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        return QueryExtra(riid, ppv);
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&refs_); }
    IFACEMETHODIMP_(ULONG) Release() override {
        LONG r = InterlockedDecrement(&refs_);
        if (r == 0) delete this;
        return r;
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override {
        if (tip) *tip = nullptr;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetCanonicalName(GUID* guid) override {
        if (!guid) return E_POINTER;
        *guid = GUID_NULL;
        return S_OK;
    }
    IFACEMETHODIMP Invoke(IShellItemArray*, IBindCtx*) override { return S_OK; }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** e) override {
        if (e) *e = nullptr;
        return E_NOTIMPL;
    }

protected:
    virtual HRESULT QueryExtra(REFIID, void** ppv) {
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    std::shared_ptr<RootContext> ctx_;

private:
    LONG refs_ = 1;
};

class CommandEnum final : public IEnumExplorerCommand {
public:
    explicit CommandEnum(std::vector<IExplorerCommand*> cmds) : cmds_(std::move(cmds)) { ModuleAddRef(); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IEnumExplorerCommand) {
            *ppv = static_cast<IEnumExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&refs_); }
    IFACEMETHODIMP_(ULONG) Release() override {
        LONG r = InterlockedDecrement(&refs_);
        if (r == 0) delete this;
        return r;
    }
    IFACEMETHODIMP Next(ULONG celt, IExplorerCommand** out, ULONG* fetched) override {
        if (!out) return E_POINTER;
        ULONG n = 0;
        while (n < celt && pos_ < cmds_.size()) {
            out[n] = cmds_[pos_++];
            out[n]->AddRef();
            ++n;
        }
        if (fetched) *fetched = n;
        return n == celt ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP Skip(ULONG celt) override {
        size_t remaining = cmds_.size() - pos_;
        if (celt > remaining) {
            pos_ = cmds_.size();
            return S_FALSE;
        }
        pos_ += celt;
        return S_OK;
    }
    IFACEMETHODIMP Reset() override {
        pos_ = 0;
        return S_OK;
    }
    IFACEMETHODIMP Clone(IEnumExplorerCommand** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        try {
            std::vector<IExplorerCommand*> copy = cmds_;
            for (auto* c : copy) c->AddRef();
            auto* e = new (std::nothrow) CommandEnum(std::move(copy));
            if (!e) {
                for (auto* c : copy) c->Release();
                return E_OUTOFMEMORY;
            }
            e->pos_ = pos_;
            *out = e;
            return S_OK;
        } catch (...) {
            return E_OUTOFMEMORY;
        }
    }

private:
    ~CommandEnum() {
        for (auto* c : cmds_) c->Release();
        ModuleRelease();
    }
    LONG refs_ = 1;
    std::vector<IExplorerCommand*> cmds_;  // owned references
    size_t pos_ = 0;
};

class LeafCommand final : public CommandBase {
public:
    LeafCommand(std::shared_ptr<RootContext> ctx, std::shared_ptr<const Config> cfg, const Command* cmd)
        : CommandBase(std::move(ctx)), cfg_(std::move(cfg)), cmd_(cmd) {}

    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override {
        if (!name) return E_POINTER;
        *name = nullptr;
        try {
            return DupString(DisplayTitle(*cmd_, PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_JAPANESE), name);
        } catch (...) {
            return E_FAIL;
        }
    }
    IFACEMETHODIMP GetCanonicalName(GUID* guid) override {
        if (!guid) return E_POINTER;
        try {
            *guid = MakeCanonicalGuid(L"cmd:" + cmd_->id);
            return S_OK;
        } catch (...) {
            *guid = GUID_NULL;
            return E_FAIL;
        }
    }
    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override {
        if (!icon) return E_POINTER;
        *icon = nullptr;
        try {
            std::wstring res = IconResource(cmd_->icon, cmd_->action.program);
            if (res.empty()) return E_NOTIMPL;
            return DupString(res, icon);
        } catch (...) {
            return E_FAIL;
        }
    }
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override {
        if (!state) return E_POINTER;
        *state = ECS_HIDDEN;
        try {
            if (Matches(*cmd_, ReadSelectionCached(*ctx_, items))) *state = ECS_ENABLED;
        } catch (...) {
        }
        return S_OK;
    }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override {
        if (!flags) return E_POINTER;
        *flags = ECF_DEFAULT;
        return S_OK;
    }
    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override {
        try {
            Selection sel = ReadSelectionDirect(*ctx_, items);
            if (!Matches(*cmd_, sel)) return S_OK;
            auto plans = PlanLaunches(*cmd_, sel, EnvLookupWin);
            Log(L"invoke %ls (%zu launch)", cmd_->id.c_str(), plans.size());
            LaunchAsync(std::move(plans), cfg_->settings.menuTitle);
        } catch (...) {
        }
        return S_OK;
    }

private:
    std::shared_ptr<const Config> cfg_;  // keeps cmd_ alive
    const Command* cmd_;
};

// Windows 11's new context menu only expands one level of sub-commands: an item with
// ECF_HASSUBCOMMANDS inside our flyout is drawn as a plain, inert entry. Groups are therefore
// rendered as sections of the ContextBridge flyout: a separator, a non-clickable header with the
// group's title and icon, then the group's commands.
class SeparatorCommand final : public CommandBase {
public:
    explicit SeparatorCommand(std::shared_ptr<RootContext> ctx) : CommandBase(std::move(ctx)) {}

    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override { return DupString(L"", name); }
    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override {
        if (icon) *icon = nullptr;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override {
        if (!state) return E_POINTER;
        *state = ECS_ENABLED;
        return S_OK;
    }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override {
        if (!flags) return E_POINTER;
        *flags = ECF_ISSEPARATOR;
        return S_OK;
    }
};

class GroupHeaderCommand final : public CommandBase {
public:
    GroupHeaderCommand(std::shared_ptr<RootContext> ctx, std::shared_ptr<const Config> cfg, const Group* group)
        : CommandBase(std::move(ctx)), cfg_(std::move(cfg)), group_(group) {}

    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override {
        if (!name) return E_POINTER;
        *name = nullptr;
        try {
            return DupString(group_->title, name);
        } catch (...) {
            return E_FAIL;
        }
    }
    IFACEMETHODIMP GetCanonicalName(GUID* guid) override {
        if (!guid) return E_POINTER;
        try {
            *guid = MakeCanonicalGuid(L"group:" + group_->id);
            return S_OK;
        } catch (...) {
            *guid = GUID_NULL;
            return E_FAIL;
        }
    }
    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override {
        if (!icon) return E_POINTER;
        *icon = nullptr;
        try {
            std::wstring res = IconResource(group_->icon, L"");
            if (res.empty()) return E_NOTIMPL;
            return DupString(res, icon);
        } catch (...) {
            return E_FAIL;
        }
    }
    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override {
        if (!state) return E_POINTER;
        *state = ECS_DISABLED;  // a label, not an action
        return S_OK;
    }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override {
        if (!flags) return E_POINTER;
        *flags = ECF_DEFAULT;
        return S_OK;
    }

private:
    std::shared_ptr<const Config> cfg_;  // keeps group_ alive
    const Group* group_;
};

class RootCommand final : public CommandBase, public IObjectWithSite {
public:
    explicit RootCommand(std::shared_ptr<RootContext> ctx) : CommandBase(std::move(ctx)) {}

    IFACEMETHODIMP_(ULONG) AddRef() override { return CommandBase::AddRef(); }
    IFACEMETHODIMP_(ULONG) Release() override { return CommandBase::Release(); }
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override { return CommandBase::QueryInterface(riid, ppv); }

    // IObjectWithSite
    IFACEMETHODIMP SetSite(IUnknown* site) override {
        try {
            ctx_->SetSite(site);
            return S_OK;
        } catch (...) {
            return E_FAIL;
        }
    }
    IFACEMETHODIMP GetSite(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        try {
            IUnknown* site = ctx_->AddRefSite();
            if (!site) return E_FAIL;
            HRESULT hr = site->QueryInterface(riid, ppv);
            site->Release();
            return hr;
        } catch (...) {
            return E_FAIL;
        }
    }

    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override {
        if (!name) return E_POINTER;
        *name = nullptr;
        try {
            auto cfg = Snapshot();
            return DupString(cfg ? cfg->settings.menuTitle : std::wstring(L"ContextBridge"), name);
        } catch (...) {
            return E_FAIL;
        }
    }
    IFACEMETHODIMP GetCanonicalName(GUID* guid) override {
        if (!guid) return E_POINTER;
        *guid = ctx_->background ? CLSID_BackgroundCommand : CLSID_ItemsCommand;
        return S_OK;
    }
    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override {
        if (!icon) return E_POINTER;
        *icon = nullptr;
        try {
            wchar_t path[MAX_PATH * 2];
            DWORD n = GetModuleFileNameW(g_module, path, _countof(path));
            if (n == 0 || n >= _countof(path)) return E_FAIL;
            return DupString(FormatIconResource(std::wstring(path, n), -101), icon);
        } catch (...) {
            return E_FAIL;
        }
    }
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override {
        if (!state) return E_POINTER;
        *state = ECS_HIDDEN;
        try {
            auto cfg = Snapshot();
            if (cfg && !BuildMenu(*cfg, ReadSelectionCached(*ctx_, items)).empty()) *state = ECS_ENABLED;
        } catch (...) {
        }
        return S_OK;
    }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override {
        if (!flags) return E_POINTER;
        *flags = ECF_HASSUBCOMMANDS;
        return S_OK;
    }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        std::vector<IExplorerCommand*> cmds;
        try {
            auto cfg = Snapshot();
            if (cfg) {
                // Root GetState has just read the selection; lay out only what will be visible so
                // section separators never double up around a group that would be hidden.
                Selection sel;
                std::vector<MenuNode> nodes =
                    CachedSelection(*ctx_, sel) ? BuildMenu(*cfg, sel) : BuildLayout(*cfg);
                auto add = [&](IExplorerCommand* c) {
                    if (c) cmds.push_back(c);
                };
                bool lastWasSeparator = true;  // no separator at the very top
                for (size_t i = 0; i < nodes.size(); ++i) {
                    const MenuNode& node = nodes[i];
                    if (node.command) {
                        add(new (std::nothrow) LeafCommand(ctx_, cfg, node.command));
                        lastWasSeparator = false;
                        continue;
                    }
                    if (!lastWasSeparator) add(new (std::nothrow) SeparatorCommand(ctx_));
                    add(new (std::nothrow) GroupHeaderCommand(ctx_, cfg, node.group));
                    for (const Command* child : node.children)
                        add(new (std::nothrow) LeafCommand(ctx_, cfg, child));
                    lastWasSeparator = false;
                    if (i + 1 < nodes.size()) {
                        add(new (std::nothrow) SeparatorCommand(ctx_));
                        lastWasSeparator = true;
                    }
                }
            }
            auto* e = new (std::nothrow) CommandEnum(std::move(cmds));
            if (!e) {
                for (auto* c : cmds) c->Release();
                return E_OUTOFMEMORY;
            }
            *out = e;
            return S_OK;
        } catch (...) {
            for (auto* c : cmds) c->Release();
            return E_FAIL;
        }
    }

protected:
    HRESULT QueryExtra(REFIID riid, void** ppv) override {
        if (riid == IID_IObjectWithSite) {
            *ppv = static_cast<IObjectWithSite*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

private:
    // Children capture the snapshot they were built from, so a reload while a menu is open
    // never invalidates pointers they hold.
    std::shared_ptr<const Config> Snapshot() { return CurrentConfig(); }
};

}  // namespace

HRESULT CreateRootCommand(bool background, REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    try {
        auto ctx = std::make_shared<RootContext>();
        ctx->background = background;
        auto* root = new (std::nothrow) RootCommand(std::move(ctx));
        if (!root) return E_OUTOFMEMORY;
        HRESULT hr = root->QueryInterface(riid, ppv);
        root->Release();
        return hr;
    } catch (...) {
        return E_OUTOFMEMORY;
    }
}

}  // namespace cb
