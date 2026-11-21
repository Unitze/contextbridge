// COM entry points for ContextBridge.Shell.dll. Activated by Windows Explorer through
// the sparse package's windows.comServer declaration (SurrogateServer).
#include <new>

#include "shell.h"

namespace cb {

HMODULE g_module = nullptr;
static volatile LONG g_refs = 0;

void ModuleAddRef() { InterlockedIncrement(&g_refs); }
void ModuleRelease() { InterlockedDecrement(&g_refs); }
LONG ModuleRefCount() { return g_refs; }

namespace {

class ClassFactory final : public IClassFactory {
public:
    explicit ClassFactory(bool background) : background_(background) { ModuleAddRef(); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
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
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        return CreateRootCommand(background_, riid, ppv);
    }
    IFACEMETHODIMP LockServer(BOOL lock) override {
        if (lock) ModuleAddRef(); else ModuleRelease();
        return S_OK;
    }

private:
    ~ClassFactory() { ModuleRelease(); }
    LONG refs_ = 1;
    bool background_;
};

}  // namespace
}  // namespace cb

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        cb::g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    bool background;
    if (clsid == cb::CLSID_ItemsCommand) background = false;
    else if (clsid == cb::CLSID_BackgroundCommand) background = true;
    else return CLASS_E_CLASSNOTAVAILABLE;
    auto* f = new (std::nothrow) cb::ClassFactory(background);
    if (!f) return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

extern "C" HRESULT __stdcall DllCanUnloadNow() {
    return cb::ModuleRefCount() == 0 ? S_OK : S_FALSE;
}
