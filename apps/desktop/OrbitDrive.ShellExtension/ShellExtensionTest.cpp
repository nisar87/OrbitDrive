// Drives the real OrbitDriveShell.dll through its COM interfaces exactly as Explorer would (menu text, state, sub-commands, Invoke
// with a real IShellItemArray), without Explorer. This proves the component's logic; it does NOT prove that the Windows 11 menu shows
// it (that needs the package identity installed on a real machine: docs/evidence AWAITING_DEVICE_TEST).
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <cstdio>
#include <string>
#include <vector>
#include <thread>

static int g_failures = 0;
#define CHECK(condition, ...) do { if (!(condition)) { ++g_failures; std::printf("FAIL line %d: %s  ", __LINE__, #condition); std::printf(__VA_ARGS__); std::printf("\n"); } else { std::printf("ok   %s\n", #condition); } } while (0)

static std::string Utf8(const std::wstring& w) { int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), nullptr, 0, nullptr, nullptr); std::string o(n, 0); WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), o.data(), n, nullptr, nullptr); return o; }
static std::string JsonEscape(const std::string& in) { std::string o; for (char c : in) { if (c == '\\') o += "\\\\"; else if (c == '"') o += "\\\""; else o += c; } return o; }
static std::wstring Title(IExplorerCommand* c, IShellItemArray* items) { PWSTR t = nullptr; if (FAILED(c->GetTitle(items, &t)) || !t) return L"<failed>"; std::wstring s = t; CoTaskMemFree(t); return s; }
static void WriteFileText(const std::wstring& path, const std::string& text) { HANDLE f = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr); DWORD w; WriteFile(f, text.data(), (DWORD)text.size(), &w, nullptr); CloseHandle(f); }
static std::string ReadFileText(const std::wstring& path) { HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr); if (f == INVALID_HANDLE_VALUE) return ""; char buf[65536]; DWORD r = 0; ReadFile(f, buf, sizeof buf, &r, nullptr); CloseHandle(f); return std::string(buf, r); }
static std::vector<std::wstring> Spooled(const std::wstring& outbox) { std::vector<std::wstring> v; WIN32_FIND_DATAW d; HANDLE h = FindFirstFileW((outbox + L"\\*.send").c_str(), &d); if (h == INVALID_HANDLE_VALUE) return v; do v.push_back(outbox + L"\\" + d.cFileName); while (FindNextFileW(h, &d)); FindClose(h); return v; }

static IShellItemArray* ItemsFor(const std::vector<std::wstring>& paths) {
    std::vector<PIDLIST_ABSOLUTE> ids;
    for (auto& p : paths) { PIDLIST_ABSOLUTE id = nullptr; if (SUCCEEDED(SHParseDisplayName(p.c_str(), nullptr, &id, 0, nullptr)) && id) ids.push_back(id); }
    IShellItemArray* array = nullptr;
    if (FAILED(SHCreateShellItemArrayFromIDLists((UINT)ids.size(), (PCIDLIST_ABSOLUTE_ARRAY)ids.data(), &array))) array = nullptr;
    for (auto id : ids) CoTaskMemFree(id);
    return array;
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) { std::printf("usage: ShellExtensionTest <OrbitDriveShell.dll>\n"); return 2; }
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    wchar_t temp[MAX_PATH]; GetTempPathW(MAX_PATH, temp);
    std::wstring root = std::wstring(temp) + L"orbit-shell-test-" + std::to_wstring(GetTickCount64());
    std::wstring state = root + L"\\state", files = root + L"\\files", outbox = state + L"\\outbox";
    SHCreateDirectoryExW(nullptr, state.c_str(), nullptr); SHCreateDirectoryExW(nullptr, files.c_str(), nullptr);
    SetEnvironmentVariableW(L"ORBITDRIVE_SHELL_STATE", state.c_str());
    SetEnvironmentVariableW(L"ORBITDRIVE_SHELL_NOLAUNCH", L"1");

    std::wstring pipeName = L"orbitdrive-shell-test-" + std::to_wstring(GetTickCount64());
    FILETIME ft; GetSystemTimeAsFileTime(&ft); ULARGE_INTEGER u; u.LowPart = ft.dwLowDateTime; u.HighPart = ft.dwHighDateTime;
    long long now = (long long)((u.QuadPart - 116444736000000000ULL) / 10000ULL);
    auto writeReceivers = [&](const std::string& extra) {
        WriteFileText(state + L"\\receivers.txt", "v1\n" + std::to_string(now) + "\n" + Utf8(pipeName) + "\nabc-123\t" + std::to_string(now - 5 * 60000) + "\tMy Phone\ndef_456\t0\tTablet 'two' \"q\"\n" + extra);
    };
    writeReceivers("bad id\"; calc\t5\tEvil\n");

    std::vector<std::wstring> paths = { files + L"\\a b.txt", files + L"\\quote ' and unicode é 文件.txt", files + L"\\semi;colon & amp $(x).txt" };
    for (auto& p : paths) WriteFileText(p, "x");
    // The temp folder may be spelled with 8.3 short names; Explorer hands over the long, real path, so expect that.
    for (auto& p : paths) { wchar_t longPath[MAX_PATH * 2]; DWORD n = GetLongPathNameW(p.c_str(), longPath, ARRAYSIZE(longPath)); if (n > 0 && n < ARRAYSIZE(longPath)) p.assign(longPath, n); }
    SHCreateDirectoryExW(nullptr, (files + L"\\a folder").c_str(), nullptr);

    HMODULE dll = LoadLibraryW(argv[1]);
    CHECK(dll != nullptr, "cannot load %ls (error %lu)", argv[1], GetLastError());
    if (!dll) return 1;
    auto get = (HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**))GetProcAddress(dll, "DllGetClassObject");
    CHECK(get != nullptr, "DllGetClassObject not exported");
    CHECK(GetProcAddress(dll, "DllCanUnloadNow") != nullptr, "DllCanUnloadNow not exported");
    const CLSID clsid = { 0x7b2c3e8a, 0x5d41, 0x4c6f, { 0x9a, 0x07, 0x1e, 0x3b, 0x5f, 0x9d, 0x2c, 0x64 } };
    IClassFactory* factory = nullptr;
    CHECK(SUCCEEDED(get(clsid, __uuidof(IClassFactory), (void**)&factory)), "class factory");
    CLSID wrong = clsid; wrong.Data1 ^= 1; void* none = nullptr;
    CHECK(get(wrong, __uuidof(IClassFactory), &none) == CLASS_E_CLASSNOTAVAILABLE, "an unknown class id is refused");
    IExplorerCommand* command = nullptr;
    CHECK(SUCCEEDED(factory->CreateInstance(nullptr, __uuidof(IExplorerCommand), (void**)&command)), "create the command");
    if (!command) return 1;

    IShellItemArray* selection = ItemsFor(paths);
    CHECK(selection != nullptr, "build a real shell item array");
    CHECK(Title(command, selection) == L"Send to my phone via OrbitDrive", "title: %ls", Title(command, selection).c_str());
    EXPCMDFLAGS flags = 0; command->GetFlags(&flags);
    CHECK((flags & ECF_HASSUBCOMMANDS) != 0, "has sub-commands");
    EXPCMDSTATE st = ECS_HIDDEN; command->GetState(selection, FALSE, &st);
    CHECK(st == ECS_ENABLED, "enabled for files");

    IEnumExplorerCommand* e = nullptr;
    CHECK(SUCCEEDED(command->EnumSubCommands(&e)) && e, "enumerate receivers");
    IExplorerCommand* subs[8] = {}; ULONG got = 0; e->Next(8, subs, &got);
    CHECK(got == 2, "two valid receivers (the one with an unsafe id is skipped), got %lu", got);
    CHECK(Title(subs[0], selection) == L"My Phone (seen 5 min ago)", "receiver title: %ls", Title(subs[0], selection).c_str());
    CHECK(Title(subs[1], selection) == L"Tablet 'two' \"q\" (not seen yet)", "receiver title: %ls", Title(subs[1], selection).c_str());

    // 1) The app is running: the selection arrives over the per-user pipe as JSON data.
    std::string received;
    HANDLE pipe = CreateNamedPipeW((L"\\\\.\\pipe\\" + pipeName).c_str(), PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_WAIT, 1, 65536, 65536, 0, nullptr);
    CHECK(pipe != INVALID_HANDLE_VALUE, "create test pipe");
    std::thread reader([&] {
        if (ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED) {
            char buf[8192]; DWORD r;
            while (ReadFile(pipe, buf, sizeof buf, &r, nullptr) && r) { received.append(buf, r); if (received.find('\n') != std::string::npos) break; }
        }
    });
    HRESULT hr = subs[0]->Invoke(selection, nullptr);
    reader.join(); CloseHandle(pipe);
    CHECK(SUCCEEDED(hr), "invoke over the pipe");
    CHECK(received.find("\"cmd\":\"send_files\"") != std::string::npos, "command: %s", received.c_str());
    CHECK(received.find("\"receiverId\":\"abc-123\"") != std::string::npos, "receiver id");
    for (auto& p : paths) CHECK(received.find("\"" + JsonEscape(Utf8(p)) + "\"") != std::string::npos, "path carried as escaped JSON data: %ls", p.c_str());
    CHECK(Spooled(outbox).empty(), "nothing was spooled when the pipe worked");

    // 2) The app is not running: the selection is spooled whole, and no program is started in the test.
    hr = subs[1]->Invoke(selection, nullptr);
    CHECK(SUCCEEDED(hr), "invoke without the app");
    auto spooled = Spooled(outbox);
    CHECK(spooled.size() == 1, "one spool file, got %zu", spooled.size());
    if (!spooled.empty()) {
        std::string body = ReadFileText(spooled[0]);
        CHECK(body.find("\"receiverId\":\"def_456\"") != std::string::npos, "spool names the receiver");
        for (auto& p : paths) CHECK(body.find("\"" + JsonEscape(Utf8(p)) + "\"") != std::string::npos, "spool carries the path");
        CHECK(body.find("\"cmd\"") == std::string::npos, "the spool holds the arguments only");
    }

    // 3) Folders are not supported: the entry says so, is disabled and sends nothing.
    std::vector<std::wstring> withFolder = { paths[0], files + L"\\a folder" };
    IShellItemArray* folderSelection = ItemsFor(withFolder);
    command->GetState(folderSelection, FALSE, &st);
    CHECK(st == ECS_DISABLED, "disabled when a folder is selected");
    CHECK(Title(command, folderSelection).find(L"folders are not supported yet") != std::wstring::npos, "truthful title: %ls", Title(command, folderSelection).c_str());
    subs[0]->Invoke(folderSelection, nullptr);
    CHECK(Spooled(outbox).size() == 1, "a folder selection sends nothing");

    // 4) No paired receiver (no list yet): the command is disabled and tells the user what to do.
    DeleteFileW((state + L"\\receivers.txt").c_str());
    command->GetState(selection, FALSE, &st);
    CHECK(st == ECS_DISABLED, "disabled without receivers");
    CHECK(Title(command, selection).find(L"pair a phone first") != std::wstring::npos, "title says to pair first");
    // 5) Garbage in the list is ignored, not trusted.
    WriteFileText(state + L"\\receivers.txt", "v2\n1\nx\nabc\t1\tName\n");
    command->GetState(selection, FALSE, &st);
    CHECK(st == ECS_DISABLED, "an unknown list version is ignored");

    for (auto s : subs) if (s) s->Release();
    if (e) e->Release();
    if (selection) selection->Release();
    if (folderSelection) folderSelection->Release();
    command->Release(); factory->Release();
    std::printf("%s (%d failure%s)\n", g_failures ? "SHELL EXTENSION TEST FAILED" : "SHELL EXTENSION TEST PASSED", g_failures, g_failures == 1 ? "" : "s");
    return g_failures ? 1 : 0;
}
