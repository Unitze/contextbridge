// Unit tests for the platform-independent handler core. Build with CMake target
// `cb_core_tests` (works on Linux, macOS and Windows).
#include <cstdio>
#include <fstream>
#include <map>
#include <sstream>
#include <string>

#include "../../src/Shell/core/config.h"
#include "../../src/Shell/core/json.h"
#include "../../src/Shell/core/strings.h"

using namespace cb;

static int g_failed = 0;
static int g_passed = 0;

#define CHECK(cond)                                                              \
    do {                                                                         \
        if (cond) {                                                              \
            ++g_passed;                                                          \
        } else {                                                                 \
            ++g_failed;                                                          \
            std::fprintf(stderr, "%s:%d: CHECK failed: %s\n", __FILE__, __LINE__, #cond); \
        }                                                                        \
    } while (0)

#define CHECK_EQ_W(a, b)                                                                     \
    do {                                                                                     \
        std::wstring _a = (a), _b = (b);                                                     \
        if (_a == _b) {                                                                      \
            ++g_passed;                                                                      \
        } else {                                                                             \
            ++g_failed;                                                                      \
            std::fprintf(stderr, "%s:%d: expected [%s] got [%s]\n", __FILE__, __LINE__,      \
                         WideToUtf8(_b).c_str(), WideToUtf8(_a).c_str());                    \
        }                                                                                    \
    } while (0)

static EnvLookup TestEnv() {
    static std::map<std::wstring, std::wstring> vars = {
        {L"LOCALAPPDATA", L"C:\\Users\\me\\AppData\\Local"},
        {L"ProgramFiles", L"C:\\Program Files"},
        {L"DATA", L"D:\\data"},
    };
    return [](const std::wstring& name) -> std::optional<std::wstring> {
        for (auto& kv : vars) {
            if (EqualsIgnoreCase(kv.first, name)) return kv.second;
        }
        return std::nullopt;
    };
}

static std::shared_ptr<const Config> MustLoad(const std::string& json) {
    auto r = LoadConfig(json);
    if (!r.config) std::fprintf(stderr, "load error: %s\n", r.error.c_str());
    return r.config;
}

static Selection Files(std::initializer_list<const wchar_t*> paths) {
    Selection s;
    for (auto p : paths) s.items.push_back({p, ItemKind::File});
    return s;
}

static void TestJson() {
    auto r = json::Parse("\xEF\xBB\xBF{\"a\": [1, 2.5, -3e2], \"b\": \"x\\u3042\\ud83d\\ude00\", \"c\": null}");
    CHECK(r.ok);
    CHECK(r.value["a"].items().size() == 3);
    CHECK(r.value["a"].items()[2].asNumber() == -300);
    CHECK(r.value["b"].asString() == "x\xE3\x81\x82\xF0\x9F\x98\x80");
    CHECK(r.value["c"].isNull());
    CHECK(!json::Parse("{\"a\": 1,}").ok);
    CHECK(!json::Parse("[1 2]").ok);
    CHECK(!json::Parse("\"unterminated").ok);
    CHECK(!json::Parse("{} x").ok);
    CHECK(!json::Parse("01").ok);
    std::string deep(200, '[');
    CHECK(!json::Parse(deep).ok);
    CHECK(!json::Parse("").ok);
    // Lone high surrogate followed by a valid surrogate pair must emit U+FFFD + U+1F600 (never raw surrogate bytes).
    auto sur = json::Parse("\"\\uD800\\uD83D\\uDE00\"");
    CHECK(sur.ok);
    CHECK(sur.value.asString() == "\xEF\xBF\xBD\xF0\x9F\x98\x80");
}

static void TestStrings() {
    CHECK_EQ_W(Utf8ToWide("\xE3\x81\x82" "b"), L"\u3042b");
    CHECK(WideToUtf8(L"\u3042") == "\xE3\x81\x82");
    CHECK_EQ_W(FileNameOf(L"C:\\a\\b.txt"), L"b.txt");
    CHECK_EQ_W(StemOf(L"C:\\Tools\\example.exe"), L"example");
    CHECK_EQ_W(StemOf(L".gitignore"), L".gitignore");
    CHECK_EQ_W(ParentOf(L"C:\\a\\b.txt"), L"C:\\a");
    CHECK_EQ_W(ParentOf(L"C:\\b.txt"), L"C:\\");
    CHECK_EQ_W(ParentOf(L"C:\\"), L"");
    CHECK_EQ_W(ParentOf(L"\\\\srv\\share\\x.txt"), L"\\\\srv\\share");
    CHECK(IsRootPath(L"D:\\"));
    CHECK(IsRootPath(L"\\\\srv\\share"));
    CHECK(!IsRootPath(L"\\\\srv\\share\\dir"));
    CHECK(!IsRootPath(L"\\\\\\a"));
    CHECK(!IsRootPath(L"\\\\srv\\"));
    CHECK(EndsWithIgnoreCase(L"README.MD", L".md"));
}

static const char* kSample = R"({
  "version": 1,
  "groups": [ { "id": "editors", "title": "エディタで開く" } ],
  "commands": [
    {
      "id": "open-with-example",
      "title": "Example で開く",
      "group": "editors",
      "enabled": true,
      "target": { "itemTypes": ["file"], "extensions": [".txt", "md"], "minSelection": 1, "maxSelection": 20 },
      "action": { "program": "C:\\Tools\\example.exe", "arguments": "\"%*\"", "workingDirectory": "%d",
                  "multiFileMode": "single", "runAsAdmin": false, "window": "normal" },
      "icon": { "source": "program", "path": null, "index": 0 }
    },
    {
      "id": "terminal-here",
      "title": "",
      "target": { "itemTypes": ["folder", "folderBackground", "drive"] },
      "action": { "program": "%LOCALAPPDATA%\\Microsoft\\WindowsApps\\wt.exe", "arguments": "-d \"%d\"" }
    },
    {
      "id": "disabled",
      "enabled": false,
      "target": { "itemTypes": ["*"] },
      "action": { "program": "x.exe" }
    },
    {
      "id": "each",
      "group": "editors",
      "target": { "itemTypes": ["file"], "minSelection": 2 },
      "action": { "program": "y.exe", "arguments": "--open \"%1\"", "multiFileMode": "each" }
    }
  ]
})";

static void TestLoad() {
    auto cfg = MustLoad(kSample);
    CHECK(cfg != nullptr);
    if (!cfg) return;
    CHECK(cfg->commands.size() == 4);
    CHECK(cfg->groups.size() == 1);
    CHECK_EQ_W(cfg->groups[0].title, L"\u30a8\u30c7\u30a3\u30bf\u3067\u958b\u304f");
    CHECK(cfg->commands[0].target.extensions.size() == 2);
    CHECK_EQ_W(cfg->commands[0].target.extensions[1], L".md");
    CHECK(cfg->commands[1].target.itemTypes == (kItemFolder | kItemFolderBackground | kItemDrive));
    CHECK_EQ_W(cfg->settings.menuTitle, L"ContextBridge");
    CHECK_EQ_W(DisplayTitle(cfg->commands[1], true), L"wt \u3067\u958b\u304f");
    CHECK_EQ_W(DisplayTitle(cfg->commands[1], false), L"Open in wt");
    CHECK_EQ_W(DisplayTitle(cfg->commands[0], false), cfg->commands[0].title);

    // Structural errors fail the whole file.
    CHECK(!LoadConfig("{\"version\":1,\"commands\":[{\"id\":\"a\"}]}").config);
    CHECK(!LoadConfig("{\"version\":2}").config);
    CHECK(!LoadConfig("{\"commands\":[]}").config);
    CHECK(!LoadConfig(R"({"version":1,"commands":[{"id":"a","action":{"program":"x"}},{"id":"a","action":{"program":"y"}}]})").config);
    CHECK(!LoadConfig(R"({"version":1,"commands":[{"id":"a","target":{"itemTypes":["bogus"]},"action":{"program":"x"}}]})").config);
    CHECK(!LoadConfig(R"({"version":1,"commands":[{"id":"a","target":{"minSelection":3,"maxSelection":2},"action":{"program":"x"}}]})").config);
    CHECK(!LoadConfig("not json").config);
    // Unknown group reference is tolerated (moved to top level).
    auto c2 = MustLoad(R"({"version":1,"commands":[{"id":"a","group":"nope","action":{"program":"x"}}]})");
    CHECK(c2 && c2->commands[0].group.empty());
    // Settings
    auto c3 = MustLoad(R"({"version":1,"settings":{"menuTitle":"Open with…"},"commands":[]})");
    CHECK(c3 && c3->settings.menuTitle == L"Open with\u2026");
}

static void TestMatching() {
    auto cfg = MustLoad(kSample);
    if (!cfg) return;
    const auto& example = cfg->commands[0];
    const auto& term = cfg->commands[1];
    const auto& disabled = cfg->commands[2];

    CHECK(Matches(example, Files({L"C:\\a\\b.TXT"})));
    CHECK(Matches(example, Files({L"C:\\a\\b.txt", L"C:\\a\\c.md"})));
    CHECK(!Matches(example, Files({L"C:\\a\\b.txt", L"C:\\a\\c.png"})));
    CHECK(!Matches(example, Files({L"C:\\a\\b.png"})));
    Selection mixed = Files({L"C:\\a\\b.txt"});
    mixed.items.push_back({L"C:\\a\\dir", ItemKind::Folder});
    CHECK(!Matches(example, mixed));
    CHECK(!Matches(disabled, Files({L"C:\\x"})));

    Selection bg;
    bg.background = true;
    bg.backgroundFolder = L"C:\\work";
    CHECK(Matches(term, bg));
    CHECK(!Matches(example, bg));

    Selection drive;
    drive.items.push_back({L"D:\\", ItemKind::Drive});
    CHECK(Matches(term, drive));
    CHECK(!Matches(example, drive));

    // selection count limits
    Selection many;
    for (int i = 0; i < 21; ++i) many.items.push_back({L"C:\\f" + std::to_wstring(i) + L".txt", ItemKind::File});
    CHECK(!Matches(example, many));

    // virtual item without a path
    Selection virt;
    virt.items.push_back({L"", ItemKind::File});
    CHECK(!Matches(example, virt));
}

static void TestMenu() {
    auto cfg = MustLoad(kSample);
    if (!cfg) return;
    auto menu = BuildMenu(*cfg, Files({L"C:\\a.txt", L"C:\\b.txt"}));
    // example + each are both in "editors"; group appears once
    CHECK(menu.size() == 1);
    CHECK(menu.size() == 1 && menu[0].group && menu[0].children.size() == 2);

    Selection bg;
    bg.background = true;
    bg.backgroundFolder = L"C:\\work";
    menu = BuildMenu(*cfg, bg);
    CHECK(menu.size() == 1 && menu[0].command && menu[0].command->id == L"terminal-here");

    menu = BuildMenu(*cfg, Files({L"C:\\a.png"}));
    CHECK(menu.empty());

    // ordering: group placed at the position of its first visible command
    auto cfg2 = MustLoad(R"({"version":1,"groups":[{"id":"g","title":"G"}],"commands":[
      {"id":"a","action":{"program":"a"}},
      {"id":"b","group":"g","action":{"program":"b"}},
      {"id":"c","action":{"program":"c"}},
      {"id":"d","group":"g","action":{"program":"d"}}]})");
    if (!cfg2) return;
    menu = BuildMenu(*cfg2, Files({L"C:\\x.txt"}));
    CHECK(menu.size() == 3);
    if (menu.size() == 3) {
        CHECK(menu[0].command && menu[0].command->id == L"a");
        CHECK(menu[1].group && menu[1].children.size() == 2);
        CHECK(menu[2].command && menu[2].command->id == L"c");
    }
}

static void TestTemplates() {
    auto env = TestEnv();
    TemplateContext ctx{{L"C:\\a b\\x.txt", L"C:\\y.md"}, L"C:\\a b"};
    CHECK_EQ_W(ExpandTemplate(L"%1", ctx, env), L"C:\\a b\\x.txt");
    CHECK_EQ_W(ExpandTemplate(L"%*", ctx, env), L"\"C:\\a b\\x.txt\" \"C:\\y.md\"");
    // a template that wraps %* in quotes must not produce double quotes
    CHECK_EQ_W(ExpandTemplate(L"\"%*\"", ctx, env), L"\"C:\\a b\\x.txt\" \"C:\\y.md\"");
    CHECK_EQ_W(ExpandTemplate(L"-d \"%d\"", ctx, env), L"-d \"C:\\a b\"");
    CHECK_EQ_W(ExpandTemplate(L"100%%", ctx, env), L"100%");
    CHECK_EQ_W(ExpandTemplate(L"%LOCALAPPDATA%\\x", ctx, env), L"C:\\Users\\me\\AppData\\Local\\x");
    CHECK_EQ_W(ExpandTemplate(L"%DATA%\\x", ctx, env), L"D:\\data\\x");
    CHECK_EQ_W(ExpandTemplate(L"%UNKNOWN%", ctx, env), L"%UNKNOWN%");
    CHECK_EQ_W(ExpandTemplate(L"50% off %d", ctx, env), L"50% off C:\\a b");
    // %d in quotes for a drive root must not escape the closing quote
    TemplateContext root{{L"D:\\"}, L"D:\\"};
    CHECK_EQ_W(ExpandTemplate(L"\"%d\"", root, env), L"\"D:\\\\\"");
    CHECK_EQ_W(ExpandTemplate(L"%*", root, env), L"\"D:\\\\\"");
    CHECK_EQ_W(ExpandTemplate(L"%d", root, env), L"D:\\");
    // a path containing '%' is substituted literally, never re-expanded
    TemplateContext pct{{L"C:\\100%LOCALAPPDATA%.txt"}, L"C:\\"};
    CHECK_EQ_W(ExpandTemplate(L"\"%1\"", pct, env), L"\"C:\\100%LOCALAPPDATA%.txt\"");
    CHECK_EQ_W(QuoteArg(L"a\"b"), L"\"a\\\"b\"");
    CHECK_EQ_W(QuoteArg(L"a\\\"b"), L"\"a\\\\\\\"b\"");
    // Undefined %d...% env var must remain literal instead of expanding %d.
    CHECK_EQ_W(ExpandTemplate(L"%d_unset%", ctx, env), L"%d_unset%");
    // ExpandEnvOnly never expands %1 / %d / %*.
    CHECK_EQ_W(ExpandEnvOnly(L"%LOCALAPPDATA%\\%1\\%d", env), L"C:\\Users\\me\\AppData\\Local\\%1\\%d");
}

static void TestLaunch() {
    auto cfg = MustLoad(kSample);
    if (!cfg) return;
    auto env = TestEnv();
    auto plans = PlanLaunches(cfg->commands[0], Files({L"C:\\w\\a.txt", L"C:\\w\\b.md"}), env);
    CHECK(plans.size() == 1);
    CHECK_EQ_W(plans[0].program, L"C:\\Tools\\example.exe");
    CHECK_EQ_W(plans[0].arguments, L"\"C:\\w\\a.txt\" \"C:\\w\\b.md\"");
    CHECK_EQ_W(plans[0].workingDirectory, L"C:\\w");

    plans = PlanLaunches(cfg->commands[3], Files({L"C:\\w1\\a.txt", L"C:\\w2\\b.txt"}), env);
    CHECK(plans.size() == 2);
    CHECK_EQ_W(plans[0].workingDirectory, L"C:\\w1");
    CHECK_EQ_W(plans[1].arguments, L"--open \"C:\\w2\\b.txt\"");
    CHECK_EQ_W(plans[1].workingDirectory, L"C:\\w2");  // each-mode uses each item's own parent folder

    // Program must not expand %1 / %d; quoted workingDirectory is unquoted.
    auto secCfg = MustLoad(R"({"version":1,"commands":[{"id":"s","action":{"program":"%1","workingDirectory":"\"%d\""}}]})");
    CHECK(secCfg != nullptr);
    if (secCfg) {
        auto sp = PlanLaunches(secCfg->commands[0], Files({L"C:\\w\\evil.exe"}), env);
        CHECK_EQ_W(sp[0].program, L"%1");
        CHECK_EQ_W(sp[0].workingDirectory, L"C:\\w");
    }

    Selection bg;
    bg.background = true;
    bg.backgroundFolder = L"C:\\work";
    plans = PlanLaunches(cfg->commands[1], bg, env);
    CHECK(plans.size() == 1);
    CHECK_EQ_W(plans[0].program, L"C:\\Users\\me\\AppData\\Local\\Microsoft\\WindowsApps\\wt.exe");
    CHECK_EQ_W(plans[0].arguments, L"-d \"C:\\work\"");

    Selection folder;
    folder.items.push_back({L"C:\\work\\sub", ItemKind::Folder});
    CHECK_EQ_W(ContextDirectory(folder), L"C:\\work");
}

// Shared vectors with the C# test-suite so both implementations agree.
static void TestSharedVectors(const char* path) {
    std::ifstream f(path, std::ios::binary);
    if (!f) {
        std::fprintf(stderr, "shared vectors not found: %s\n", path);
        CHECK(false);
        return;
    }
    std::stringstream ss;
    ss << f.rdbuf();
    auto r = json::Parse(ss.str());
    CHECK(r.ok);
    auto env = TestEnv();
    for (const auto& v : r.value["templates"].items()) {
        TemplateContext ctx;
        for (const auto& p : v["paths"].items()) ctx.paths.push_back(Utf8ToWide(p.asString()));
        ctx.directory = Utf8ToWide(v["directory"].asString());
        std::wstring got = ExpandTemplate(Utf8ToWide(v["template"].asString()), ctx, env);
        CHECK_EQ_W(got, Utf8ToWide(v["expected"].asString()));
    }
    for (const auto& v : r.value["configs"].items()) {
        bool ok = LoadConfig(v["json"].asString()).config != nullptr;
        if (ok != v["valid"].asBool()) {
            std::fprintf(stderr, "config vector mismatch: %s\n", v["name"].asString().c_str());
        }
        CHECK(ok == v["valid"].asBool());
    }
}

int main(int argc, char** argv) {
    TestJson();
    TestStrings();
    TestLoad();
    TestMatching();
    TestMenu();
    TestTemplates();
    TestLaunch();
    TestSharedVectors(argc > 1 ? argv[1] : "tests/vectors/vectors.json");
    std::printf("%d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
