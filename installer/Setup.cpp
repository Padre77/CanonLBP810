// SPDX-License-Identifier: GPL-2.0-or-later
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <objbase.h>
#include <filesystem>
#include <string>
#include <vector>
#include <stdexcept>

namespace fs = std::filesystem;
static const wchar_t* Title = L"Canon LBP-810 Native — Community Driver 0.2.0";

static void Check(bool value, const char* error) { if (!value) throw std::runtime_error(error); }

static fs::path ModulePath() {
    wchar_t buffer[32768];
    Check(GetModuleFileNameW(nullptr, buffer, 32768) != 0, "Cannot locate executable.");
    return buffer;
}

static fs::path Root() {
    PWSTR value = nullptr;
    Check(SUCCEEDED(SHGetKnownFolderPath(FOLDERID_ProgramFiles, 0, nullptr, &value)), "Cannot locate Program Files.");
    fs::path result = fs::path(value) / L"Canon LBP-810 Native";
    CoTaskMemFree(value);
    return result;
}

static fs::path Temporary() {
    wchar_t base[32768], id[40]; GUID guid{};
    Check(GetTempPathW(32768, base) != 0 && SUCCEEDED(CoCreateGuid(&guid)), "Cannot create temporary directory.");
    StringFromGUID2(guid, id, 40);
    fs::path stage = fs::path(base) / (std::wstring(L"LBP810-Setup-") + id);
    Check(fs::create_directory(stage), "Cannot create temporary directory.");
    return stage;
}

static void Resource(WORD id, const fs::path& target) {
    HRSRC found = FindResourceW(nullptr, MAKEINTRESOURCEW(id), RT_RCDATA);
    Check(found != nullptr, "Installer resource missing.");
    HGLOBAL loaded = LoadResource(nullptr, found);
    void* data = LockResource(loaded);
    DWORD size = SizeofResource(nullptr, found), written = 0;
    Check(data != nullptr && size != 0, "Invalid installer resource.");
    HANDLE file = CreateFileW(target.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    Check(file != INVALID_HANDLE_VALUE, "Cannot write installer resource.");
    bool ok = WriteFile(file, data, size, &written, nullptr) && written == size;
    CloseHandle(file);
    Check(ok, "Cannot write installer resource.");
}

static DWORD Run(const std::wstring& command, bool progress) {
    std::vector<wchar_t> buffer(command.begin(), command.end()); buffer.push_back(0);
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    Check(CreateProcessW(nullptr, buffer.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process), "Cannot launch installer helper.");
    HWND window = nullptr;
    if (progress) {
        window = CreateWindowExW(WS_EX_TOPMOST, L"STATIC", L"Выполняется установка/проверка LBP-810 Native.\nПожалуйста, дождитесь завершения…",
            WS_OVERLAPPED | WS_CAPTION | WS_VISIBLE | SS_CENTER, CW_USEDEFAULT, CW_USEDEFAULT, 570, 110, nullptr, nullptr, nullptr, nullptr);
    }
    while (WaitForSingleObject(process.hProcess, 100) == WAIT_TIMEOUT) {
        MSG message;
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
    }
    if (window) DestroyWindow(window);
    DWORD code = 1; GetExitCodeProcess(process.hProcess, &code);
    CloseHandle(process.hProcess); CloseHandle(process.hThread);
    return code;
}

static std::wstring Quote(const fs::path& value) { return L"\"" + value.wstring() + L"\""; }

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    fs::path stage;
    bool quiet = false;
    try {
        int argc; wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
        std::vector<std::wstring> args;
        for (int i = 1; i < argc; i++) args.emplace_back(argv[i]);
        LocalFree(argv);
        bool verify = false, worker = false;
        for (const auto& arg : args) { if (arg == L"/quiet") quiet = true; if (arg == L"/verify") verify = true; if (arg == L"/worker") worker = true; }
        SYSTEM_INFO info{}; GetNativeSystemInfo(&info);
        Check(info.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_AMD64, "This installer requires an x64 Windows computer.");
#ifdef UNINSTALLER
        if (!worker) {
            if (!quiet && MessageBoxW(nullptr, L"Удалить очередь, службу и файлы LBP-810 Native?\nДиагностические журналы будут сохранены.", Title, MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2) != IDYES) return 0;
            stage = Temporary();
            fs::path copied = stage / L"Uninstall.exe";
            Check(CopyFileW(ModulePath().c_str(), copied.c_str(), TRUE), "Cannot stage uninstaller.");
            std::wstring command = Quote(copied) + L" /worker /parent " + std::to_wstring(GetCurrentProcessId()) + (quiet ? L" /quiet" : L"");
            std::vector<wchar_t> line(command.begin(), command.end()); line.push_back(0);
            STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION process{};
            Check(CreateProcessW(nullptr, line.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process), "Cannot start uninstaller.");
            CloseHandle(process.hThread); CloseHandle(process.hProcess);
            return 0;
        }
        for (size_t i = 0; i + 1 < args.size(); i++) if (args[i] == L"/parent") {
            HANDLE parent = OpenProcess(SYNCHRONIZE, FALSE, std::stoul(args[i + 1]));
            if (parent) { WaitForSingleObject(parent, 30000); CloseHandle(parent); }
        }
        stage = ModulePath().parent_path();
        std::wstring action = L"Uninstall";
#else
        if (!quiet && !verify && MessageBoxW(nullptr,
            L"Установить драйвер Canon LBP-810 Native?\n\nA4, книжная и альбомная печать, Windows 10 22H2 x64 и новее.\n.NET включён. Принтер определяется автоматически по USB.\n\nЭто независимый проект, не официальный драйвер Canon.\nЛицензия GPL-2.0-or-later включена в пакет.",
            Title, MB_YESNO | MB_ICONQUESTION) != IDYES) return 0;
        stage = Temporary();
        Resource(101, stage / L"payload.zip");
        std::wstring action = verify ? L"Verify" : L"Install";
#endif
        Resource(102, stage / L"Bootstrap.ps1");
        wchar_t windows[32768]; Check(GetWindowsDirectoryW(windows, 32768) != 0, "Cannot locate Windows.");
        fs::path powershell = fs::path(windows) / L"System32/WindowsPowerShell/v1.0/powershell.exe";
        std::wstring command = Quote(powershell) + L" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(stage / L"Bootstrap.ps1") +
            L" -Action " + action + L" -Stage " + Quote(stage) + L" -InstallRoot " + Quote(Root());
        DWORD code = Run(command, !quiet);
        if (code != 0) {
            if (!quiet) MessageBoxW(nullptr, (L"Операция не завершена. Подробности:\n" + (stage / L"error.txt").wstring()).c_str(), Title, MB_OK | MB_ICONERROR);
            return static_cast<int>(code);
        }
#ifndef UNINSTALLER
        // This is exactly the GUID temporary directory created above by this process.
        std::error_code ignored; fs::remove_all(stage, ignored);
#endif
        if (!quiet) MessageBoxW(nullptr, verify ? L"Пакет и встроенный runtime проверены." :
#ifdef UNINSTALLER
            L"LBP-810 Native удалён. Журналы сохранены в ProgramData\\Lbp810Native.",
#else
            L"Готово. Выберите Canon LBP-810 Native в диалоге печати.\nСлужба запускается автоматически при загрузке Windows.",
#endif
            Title, MB_OK | MB_ICONINFORMATION);
        return 0;
    } catch (const std::exception& e) {
        if (!quiet) {
            std::string text = e.what(); std::wstring wide(text.begin(), text.end());
            MessageBoxW(nullptr, wide.c_str(), Title, MB_OK | MB_ICONERROR);
        }
        return 1;
    }
}
