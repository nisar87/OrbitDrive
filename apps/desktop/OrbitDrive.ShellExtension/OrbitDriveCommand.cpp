// OrbitDrive Explorer command (Windows 11 modern context menu): "Send to my phone via OrbitDrive" with one sub-command per paired receiver.
//
// Rules this file follows (docs: D6):
//  * Building the menu does no hashing, no network and no business logic. It reads one small local file (receivers.txt) that the OrbitDrive
//    agent keeps current, and it shows how old that information is instead of promising delivery.
//  * Invoking the command only hands the selection over: first through the agent's per-user named pipe, and if the app is not running,
//    through a per-user spool file (the app is started and sends it within five minutes, or the selection is discarded). Selected paths
//    travel as data in JSON. They are never placed on a command line and never passed to a shell.
//  * Folders are not supported yet and the command says so.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <objbase.h>
#include <strsafe.h>
#include <string>
#include <vector>
#include <cstdint>
#include <cstdlib>

// {7B2C3E8A-5D41-4C6F-9A07-1E3B5F9D2C64}
static const CLSID CLSID_OrbitDriveSend = { 0x7b2c3e8a, 0x5d41, 0x4c6f, { 0x9a, 0x07, 0x1e, 0x3b, 0x5f, 0x9d, 0x2c, 0x64 } };

static HMODULE g_module = nullptr;
static LONG g_objects = 0;

namespace {

std::wstring EnvVar(const wchar_t* name) {
    wchar_t buffer[2048];
    DWORD n = GetEnvironmentVariableW(name, buffer, ARRAYSIZE(buffer));
    return (n > 0 && n < ARRAYSIZE(buffer)) ? std::wstring(buffer, n) : std::wstring();
}

std::wstring ModuleDirectory() {
    wchar_t path[MAX_PATH * 2];
    DWORD n = GetModuleFileNameW(g_module, path, ARRAYSIZE(path));
    std::wstring full(path, n);
    size_t slash = full.find_last_of(L'\\');
    return slash == std::wstring::npos ? std::wstring() : full.substr(0, slash);
}

// Where the agent keeps its per-user state. Tests point this somewhere else with ORBITDRIVE_SHELL_STATE.
std::wstring StateDirectory() {
    std::wstring overridden = EnvVar(L"ORBITDRIVE_SHELL_STATE");
    if (!overridden.empty()) return overridden;
    std::wstring local = EnvVar(L"LOCALAPPDATA");
    return local.empty() ? std::wstring() : local + L"\\OrbitDrive\\agent";
}

std::string Utf8(const std::wstring& wide) {
    if (wide.empty()) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, wide.data(), (int)wide.size(), nullptr, 0, nullptr, nullptr);
    std::string out(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.data(), (int)wide.size(), out.data(), n, nullptr, nullptr);
    return out;
}
std::wstring Wide(const std::string& utf8) {
    if (utf8.empty()) return std::wstring();
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), (int)utf8.size(), nullptr, 0);
    std::wstring out(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8.data(), (int)utf8.size(), out.data(), n);
    return out;
}

// JSON string escaping for UTF-8 text (quotes, backslashes and control characters).
std::string JsonEscape(const std::string& in) {
    std::string out;
    out.reserve(in.size() + 8);
    for (unsigned char c : in) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20) { char buf[8]; StringCchPrintfA(buf, ARRAYSIZE(buf), "\\u%04x", c); out += buf; }
                else out += (char)c;
        }
    }
    return out;
}

struct Receiver { std::string id; std::wstring name; long long lastSeenMs; };
struct Receivers { std::vector<Receiver> list; long long writtenMs = 0; std::string pipe; bool ok = false; };

// receivers.txt:  line 1 "v1", line 2 written-at (unix ms), line 3 pipe name, then "id<TAB>lastSeenMs<TAB>name" per receiver.
Receivers ReadReceivers() {
    Receivers result;
    std::wstring dir = StateDirectory();
    if (dir.empty()) return result;
    HANDLE file = CreateFileW((dir + L"\\receivers.txt").c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return result;
    LARGE_INTEGER size{};
    std::string text;
    if (GetFileSizeEx(file, &size) && size.QuadPart > 0 && size.QuadPart <= 256 * 1024) {
        text.resize((size_t)size.QuadPart);
        DWORD read = 0;
        if (!ReadFile(file, text.data(), (DWORD)text.size(), &read, nullptr)) read = 0;
        text.resize(read);
    }
    CloseHandle(file);
    std::vector<std::string> lines;
    size_t start = 0;
    while (start <= text.size()) {
        size_t end = text.find('\n', start);
        if (end == std::string::npos) end = text.size();
        std::string line = text.substr(start, end - start);
        if (!line.empty() && line.back() == '\r') line.pop_back();
        lines.push_back(line);
        start = end + 1;
    }
    if (lines.size() < 3 || lines[0] != "v1") return result;
    result.writtenMs = _strtoi64(lines[1].c_str(), nullptr, 10);
    result.pipe = lines[2];
    for (size_t i = 3; i < lines.size() && result.list.size() < 32; ++i) {
        size_t t1 = lines[i].find('\t');
        size_t t2 = t1 == std::string::npos ? t1 : lines[i].find('\t', t1 + 1);
        if (t2 == std::string::npos) continue;
        Receiver r{ lines[i].substr(0, t1), Wide(lines[i].substr(t2 + 1)), _strtoi64(lines[i].substr(t1 + 1, t2 - t1 - 1).c_str(), nullptr, 10) };
        bool safe = !r.id.empty();
        for (char c : r.id) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-' || c == '_')) safe = false;
        if (safe && !r.name.empty()) result.list.push_back(r);
    }
    result.ok = true;
    return result;
}

long long NowMs() {
    FILETIME ft; GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER u; u.LowPart = ft.dwLowDateTime; u.HighPart = ft.dwHighDateTime;
    return (long long)((u.QuadPart - 116444736000000000ULL) / 10000ULL);
}

// "last seen 5 min ago": what the service reported, never a promise that a notification will arrive.
std::wstring SeenText(long long lastSeenMs) {
    if (lastSeenMs <= 0) return L"not seen yet";
    long long minutes = (NowMs() - lastSeenMs) / 60000;
    wchar_t buffer[64];
    if (minutes < 1) return L"seen just now";
    if (minutes < 90) { StringCchPrintfW(buffer, ARRAYSIZE(buffer), L"seen %lld min ago", minutes); return buffer; }
    if (minutes < 60 * 48) { StringCchPrintfW(buffer, ARRAYSIZE(buffer), L"seen %lld h ago", minutes / 60); return buffer; }
    StringCchPrintfW(buffer, ARRAYSIZE(buffer), L"seen %lld days ago", minutes / 1440);
    return buffer;
}

HRESULT SHStr(const std::wstring& text, PWSTR* out) { return SHStrDupW(text.c_str(), out); }

bool AnyFolder(IShellItemArray* items) {
    if (!items) return false;
    SFGAOF attributes = 0;
    return SUCCEEDED(items->GetAttributes(SIATTRIBFLAGS_OR, SFGAO_FOLDER, &attributes)) && (attributes & SFGAO_FOLDER) != 0;
}

std::vector<std::wstring> FilePaths(IShellItemArray* items) {
    std::vector<std::wstring> paths;
    DWORD count = 0;
    if (!items || FAILED(items->GetCount(&count))) return paths;
    for (DWORD i = 0; i < count && paths.size() < 500; ++i) {
        IShellItem* item = nullptr;
        if (FAILED(items->GetItemAt(i, &item))) continue;
        PWSTR name = nullptr;
        if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &name)) && name) { paths.emplace_back(name); CoTaskMemFree(name); }
        item->Release();
    }
    return paths;
}

std::string NewRequestId() {
    GUID g; CoCreateGuid(&g);
    char buffer[40];
    StringCchPrintfA(buffer, ARRAYSIZE(buffer), "%08lX%04X%04X%02X%02X%02X%02X%02X%02X%02X%02X", g.Data1, g.Data2, g.Data3, g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3], g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7]);
    return buffer;
}

// Sends one JSON line to the agent's per-user pipe. Quick: the agent answers on its own time and we do not wait for it.
bool SendOverPipe(const std::string& pipeName, const std::string& line) {
    if (pipeName.empty()) return false;
    std::wstring path = L"\\\\.\\pipe\\" + Wide(pipeName);
    if (!WaitNamedPipeW(path.c_str(), 250)) return false;   // no such pipe (the app is not running) fails at once
    HANDLE pipe = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    bool ok = WriteFile(pipe, line.data(), (DWORD)line.size(), &written, nullptr) && written == line.size();
    if (ok) FlushFileBuffers(pipe);
    CloseHandle(pipe);
    return ok;
}

// Fallback when the app is not running: a spool file the agent picks up (written whole, then renamed into place).
bool Spool(const std::string& json) {
    std::wstring dir = StateDirectory();
    if (dir.empty()) return false;
    std::wstring outbox = dir + L"\\outbox";
    SHCreateDirectoryExW(nullptr, outbox.c_str(), nullptr);
    std::wstring base = outbox + L"\\" + Wide(NewRequestId());
    HANDLE file = CreateFileW((base + L".tmp").c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    bool ok = WriteFile(file, json.data(), (DWORD)json.size(), &written, nullptr) && written == json.size();
    CloseHandle(file);
    if (!ok) { DeleteFileW((base + L".tmp").c_str()); return false; }
    return MoveFileExW((base + L".tmp").c_str(), (base + L".send").c_str(), MOVEFILE_REPLACE_EXISTING) != 0;
}

// Starts the OrbitDrive app (next to this DLL) in the background. No shell, no selected path anywhere near the command line.
void LaunchApp() {
    if (!EnvVar(L"ORBITDRIVE_SHELL_NOLAUNCH").empty()) return;
    std::wstring exe = ModuleDirectory() + L"\\OrbitDrive.exe";
    if (GetFileAttributesW(exe.c_str()) == INVALID_FILE_ATTRIBUTES) return;
    std::wstring command = L"\"" + exe + L"\" --background";
    STARTUPINFOW si{}; si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    if (CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW | DETACHED_PROCESS, nullptr, nullptr, &si, &pi)) { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
}

HRESULT Submit(const Receivers& receivers, const std::string& receiverId, const std::vector<std::wstring>& paths) {
    if (paths.empty()) return S_OK;
    // Batches keep every pipe message well below the agent's 64 KiB line limit.
    size_t index = 0;
    const std::string requestId = NewRequestId();
    bool all = true;
    int batch = 0;
    while (index < paths.size()) {
        std::string list;
        size_t used = 0;
        size_t first = index;
        while (index < paths.size()) {
            std::string one = "\"" + JsonEscape(Utf8(paths[index])) + "\"";
            if (index > first && used + one.size() + 1 > 40 * 1024) break;
            if (index > first) list += ",";
            list += one; used += one.size() + 1; ++index;
        }
        std::string rid = requestId + "-" + std::to_string(batch++);
        std::string args = "{\"receiverId\":\"" + receiverId + "\",\"paths\":[" + list + "],\"requestId\":\"" + rid + "\"}";
        std::string pipeLine = "{\"id\":\"1\",\"cmd\":\"send_files\",\"args\":" + args + "}\n";
        if (SendOverPipe(receivers.pipe, pipeLine)) continue;
        // The app is not running (or not answering): spool the selection and start the app.
        if (Spool(args + "\n")) LaunchApp(); else all = false;
    }
    return all ? S_OK : HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);
}

} // namespace

// ---------------------------------------------------------------------------------------------------------------------------------

template <class T> class ComBase : public T {
public:
    ComBase() : refs_(1) { InterlockedIncrement(&g_objects); }
    virtual ~ComBase() { InterlockedDecrement(&g_objects); }
    ULONG STDMETHODCALLTYPE AddRef() override { return (ULONG)InterlockedIncrement(&refs_); }
    ULONG STDMETHODCALLTYPE Release() override { LONG n = InterlockedDecrement(&refs_); if (n == 0) delete this; return (ULONG)n; }
private:
    LONG refs_;
};

class ReceiverCommand final : public ComBase<IExplorerCommand> {
public:
    ReceiverCommand(const Receivers& all, const Receiver& r) : all_(all), r_(r) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IExplorerCommand)) { *ppv = static_cast<IExplorerCommand*>(this); AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*, PWSTR* name) override { return SHStr(r_.name + L" (" + SeenText(r_.lastSeenMs) + L")", name); }
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*, PWSTR* icon) override { return SHStr(ModuleDirectory() + L"\\OrbitDrive.exe,0", icon); }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*, PWSTR* tip) override { *tip = nullptr; return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* guid) override { *guid = GUID_NULL; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override { *state = AnyFolder(items) ? ECS_DISABLED : ECS_ENABLED; return S_OK; }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray* items, IBindCtx*) override {
        if (AnyFolder(items)) return S_OK;   // folders are not supported yet; the menu entry is disabled for them
        return Submit(all_, r_.id, FilePaths(items));
    }
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* flags) override { *flags = ECF_DEFAULT; return S_OK; }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** e) override { *e = nullptr; return E_NOTIMPL; }
private:
    Receivers all_;
    Receiver r_;
};

class SubCommandEnum final : public ComBase<IEnumExplorerCommand> {
public:
    explicit SubCommandEnum(const Receivers& all) : all_(all), at_(0) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IEnumExplorerCommand)) { *ppv = static_cast<IEnumExplorerCommand*>(this); AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    HRESULT STDMETHODCALLTYPE Next(ULONG count, IExplorerCommand** commands, ULONG* fetched) override {
        ULONG got = 0;
        while (got < count && at_ < all_.list.size()) commands[got++] = new ReceiverCommand(all_, all_.list[at_++]);
        if (fetched) *fetched = got;
        return got == count ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE Skip(ULONG count) override { at_ += count; return at_ <= all_.list.size() ? S_OK : S_FALSE; }
    HRESULT STDMETHODCALLTYPE Reset() override { at_ = 0; return S_OK; }
    HRESULT STDMETHODCALLTYPE Clone(IEnumExplorerCommand** e) override { auto* copy = new SubCommandEnum(all_); copy->at_ = at_; *e = copy; return S_OK; }
private:
    Receivers all_;
    size_t at_;
};

class SendCommand final : public ComBase<IExplorerCommand>, public IObjectWithSite {
public:
    SendCommand() : site_(nullptr) {}
    ~SendCommand() override { if (site_) site_->Release(); }
    ULONG STDMETHODCALLTYPE AddRef() override { return ComBase<IExplorerCommand>::AddRef(); }
    ULONG STDMETHODCALLTYPE Release() override { return ComBase<IExplorerCommand>::Release(); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IExplorerCommand)) { *ppv = static_cast<IExplorerCommand*>(this); AddRef(); return S_OK; }
        if (riid == __uuidof(IObjectWithSite)) { *ppv = static_cast<IObjectWithSite*>(this); AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray* items, PWSTR* name) override {
        if (AnyFolder(items)) return SHStr(L"Send to my phone via OrbitDrive (folders are not supported yet)", name);
        Receivers r = ReadReceivers();
        if (r.list.empty()) return SHStr(L"Send to my phone via OrbitDrive (pair a phone first)", name);
        return SHStr(L"Send to my phone via OrbitDrive", name);
    }
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*, PWSTR* icon) override { return SHStr(ModuleDirectory() + L"\\OrbitDrive.exe,0", icon); }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*, PWSTR* tip) override { *tip = nullptr; return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* guid) override { *guid = CLSID_OrbitDriveSend; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override {
        *state = (AnyFolder(items) || ReadReceivers().list.empty()) ? ECS_DISABLED : ECS_ENABLED;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray*, IBindCtx*) override { return S_OK; }   // choosing a receiver is the action
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* flags) override { *flags = ECF_HASSUBCOMMANDS; return S_OK; }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** e) override {
        Receivers r = ReadReceivers();
        *e = new SubCommandEnum(r);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) override { if (site_) site_->Release(); site_ = site; if (site_) site_->AddRef(); return S_OK; }
    HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** ppv) override { return site_ ? site_->QueryInterface(riid, ppv) : E_FAIL; }
private:
    IUnknown* site_;
};

class Factory final : public ComBase<IClassFactory> {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IClassFactory)) { *ppv = static_cast<IClassFactory*>(this); AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto* command = new SendCommand();
        HRESULT hr = command->QueryInterface(riid, ppv);
        command->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) InterlockedIncrement(&g_objects); else InterlockedDecrement(&g_objects); return S_OK; }
};

extern "C" HRESULT STDAPICALLTYPE DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (clsid != CLSID_OrbitDriveSend) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new Factory();
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

extern "C" HRESULT STDAPICALLTYPE DllCanUnloadNow() { return g_objects == 0 ? S_OK : S_FALSE; }

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) { g_module = module; DisableThreadLibraryCalls(module); }
    return TRUE;
}
