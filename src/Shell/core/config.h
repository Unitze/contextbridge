// Command definition model, selection matching and launch planning.
// Pure C++ (no Win32), so all of the decision logic is unit-testable off Windows.
#pragma once

#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace cb {

enum ItemTypeMask : unsigned {
    kItemFile = 1u << 0,
    kItemFolder = 1u << 1,
    kItemFolderBackground = 1u << 2,
    kItemDrive = 1u << 3,
    kItemAll = kItemFile | kItemFolder | kItemFolderBackground | kItemDrive,
};

enum class MultiFileMode { Single, Each };
enum class WindowMode { Normal, Minimized, Maximized, Hidden };
enum class IconSource { None, Program, File };

struct Target {
    unsigned itemTypes = kItemFile;
    std::vector<std::wstring> extensions;  // normalized: lower-case with leading '.'; empty = any
    unsigned minSelection = 1;
    unsigned maxSelection = 0;  // 0 = unlimited
};

struct Action {
    std::wstring program;
    std::wstring arguments;
    std::wstring workingDirectory;
    MultiFileMode multiFileMode = MultiFileMode::Single;
    bool runAsAdmin = false;
    WindowMode window = WindowMode::Normal;
};

struct Icon {
    IconSource source = IconSource::Program;
    std::wstring path;
    int index = 0;
};

struct Command {
    std::wstring id;
    std::wstring title;
    std::wstring group;
    bool enabled = true;
    Target target;
    Action action;
    Icon icon;
};

struct Group {
    std::wstring id;
    std::wstring title;
    Icon icon;  // source None unless specified
};

struct Settings {
    std::wstring menuTitle = L"ContextBridge";
    bool logging = false;
};

struct Config {
    int version = 1;
    Settings settings;
    std::vector<Group> groups;
    std::vector<Command> commands;
};

constexpr int kSupportedVersion = 1;

struct LoadResult {
    std::shared_ptr<const Config> config;  // null on failure
    std::string error;
};

// Parses a UTF-8 JSON document. Any structural problem makes the whole load fail
// (a broken file hides every item rather than showing a partial menu).
LoadResult LoadConfig(const std::string& utf8Json);

// Title shown for a command; derives one from the program name when empty ("<name> で開く" for a
// Japanese Windows display language, "Open in <name>" otherwise).
std::wstring DisplayTitle(const Command& c, bool japaneseUi);

// ---- selection -------------------------------------------------------------

enum class ItemKind { File, Folder, Drive };

struct SelectedItem {
    std::wstring path;  // file-system path; empty if the item has none
    ItemKind kind = ItemKind::File;
};

struct Selection {
    bool background = false;           // invoked on a folder background
    std::wstring backgroundFolder;     // valid when background
    std::vector<SelectedItem> items;   // valid when !background
};

bool Matches(const Command& c, const Selection& s);

// ---- menu tree -------------------------------------------------------------

struct MenuNode {
    const Command* command = nullptr;  // leaf when non-null
    const Group* group = nullptr;      // flyout when non-null
    std::vector<const Command*> children;
};

// Static layout of the flyout (independent of the selection): enabled commands in
// definition order, with each group placed at the position of its first command.
std::vector<MenuNode> BuildLayout(const Config& cfg);

// Layout filtered by the selection; empty groups are omitted.
std::vector<MenuNode> BuildMenu(const Config& cfg, const Selection& s);

// ---- launching -------------------------------------------------------------

using EnvLookup = std::function<std::optional<std::wstring>(const std::wstring& name)>;

struct TemplateContext {
    std::vector<std::wstring> paths;
    std::wstring directory;
};

// Expands %1 %* %d %% and %ENV% references. %* quotes every path; a literal pair of
// quotes around %* ("%*") is absorbed so paths are never double-quoted.
std::wstring ExpandTemplate(const std::wstring& tmpl, const TemplateContext& ctx, const EnvLookup& env);

// Expands only %% and %ENV% references (used for action.program and icon.path so %1/%d/%* are never substituted).
std::wstring ExpandEnvOnly(const std::wstring& tmpl, const EnvLookup& env);

// Quotes one argument for CommandLineToArgvW-compatible parsing.
std::wstring QuoteArg(const std::wstring& arg);

struct LaunchSpec {
    std::wstring program;
    std::wstring arguments;
    std::wstring workingDirectory;
    bool runAsAdmin = false;
    WindowMode window = WindowMode::Normal;
};

std::vector<LaunchSpec> PlanLaunches(const Command& c, const Selection& s, const EnvLookup& env);

// Folder used for %d: background folder, a drive root itself, or the parent of the first item.
std::wstring ContextDirectory(const Selection& s);

}  // namespace cb
