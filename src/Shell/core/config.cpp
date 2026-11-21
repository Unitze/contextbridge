#include "config.h"

#include <algorithm>
#include <climits>
#include <cmath>
#include <cstdint>
#include <set>

#include "json.h"
#include "strings.h"

namespace cb {

namespace {

using json::Value;

struct Loader {
    std::string error;

    bool Fail(const std::string& msg) {
        if (error.empty()) error = msg;
        return false;
    }

    // Optional string member; wrong type is an error, null/absent yields fallback.
    bool Str(const Value& obj, const char* key, std::wstring& out, bool required = false) {
        const Value& v = obj[key];
        if (v.isString()) {
            out = Utf8ToWide(v.asString());
            return true;
        }
        if (v.isNull()) {
            if (required) return Fail(std::string("missing '") + key + "'");
            return true;
        }
        return Fail(std::string("'") + key + "' must be a string");
    }

    bool Bool(const Value& obj, const char* key, bool& out) {
        const Value& v = obj[key];
        if (v.isBool()) { out = v.asBool(); return true; }
        if (v.isNull()) return true;
        return Fail(std::string("'") + key + "' must be a boolean");
    }

    bool UInt(const Value& obj, const char* key, unsigned& out) {
        const Value& v = obj[key];
        if (v.isNull()) return true;
        if (!v.isNumber()) return Fail(std::string("'") + key + "' must be a number");
        double d = v.asNumber();
        if (d < 0 || d > 1e6 || d != static_cast<double>(static_cast<unsigned>(d))) {
            return Fail(std::string("'") + key + "' must be a non-negative integer");
        }
        out = static_cast<unsigned>(d);
        return true;
    }

    bool Int(const Value& obj, const char* key, int& out) {
        const Value& v = obj[key];
        if (v.isNull()) return true;
        if (!v.isNumber()) return Fail(std::string("'") + key + "' must be a number");
        double d = v.asNumber();
        if (!std::isfinite(d) || d < static_cast<double>(INT_MIN) || d > static_cast<double>(INT_MAX) ||
            std::floor(d) != d) {
            return Fail(std::string("'") + key + "' must be a 32-bit integer");
        }
        out = static_cast<int>(d);
        return true;
    }

    bool ParseIcon(const Value& v, Icon& icon, IconSource defaultSource) {
        icon.source = defaultSource;
        if (v.isNull()) return true;
        if (!v.isObject()) return Fail("'icon' must be an object");
        std::wstring src;
        if (!Str(v, "source", src)) return false;
        if (src.empty()) {
            // keep default
        } else if (src == L"program") {
            icon.source = IconSource::Program;
        } else if (src == L"file") {
            icon.source = IconSource::File;
        } else if (src == L"none") {
            icon.source = IconSource::None;
        } else {
            return Fail("unknown icon.source");
        }
        if (!Str(v, "path", icon.path)) return false;
        if (!Int(v, "index", icon.index)) return false;
        return true;
    }

    bool ParseItemTypes(const Value& v, unsigned& mask) {
        if (v.isNull()) return true;  // default: file
        if (!v.isArray()) return Fail("'itemTypes' must be an array");
        if (v.items().empty()) return Fail("'itemTypes' must not be empty");
        mask = 0;
        for (const auto& t : v.items()) {
            if (!t.isString()) return Fail("'itemTypes' entries must be strings");
            const std::string& s = t.asString();
            if (s == "file") mask |= kItemFile;
            else if (s == "folder") mask |= kItemFolder;
            else if (s == "folderBackground") mask |= kItemFolderBackground;
            else if (s == "drive") mask |= kItemDrive;
            else if (s == "*") mask |= kItemAll;
            else return Fail("unknown itemType '" + s + "'");
        }
        return true;
    }

    bool ParseTarget(const Value& v, Target& t) {
        if (v.isNull()) return true;
        if (!v.isObject()) return Fail("'target' must be an object");
        if (!ParseItemTypes(v["itemTypes"], t.itemTypes)) return false;
        const Value& ext = v["extensions"];
        if (ext.isArray()) {
            bool anyExt = false;
            for (const auto& e : ext.items()) {
                if (!e.isString()) return Fail("'extensions' entries must be strings");
                if (anyExt) continue;
                std::wstring w = Trim(Utf8ToWide(e.asString()));
                if (w.empty()) continue;
                if (w == L"*" || w == L".*") {  // explicit "any"
                    t.extensions.clear();
                    anyExt = true;
                    continue;
                }
                if (w[0] != L'.') w.insert(w.begin(), L'.');
                for (auto& ch : w) ch = FoldCase(ch);
                t.extensions.push_back(std::move(w));
            }
        } else if (!ext.isNull()) {
            return Fail("'extensions' must be an array");
        }
        if (!UInt(v, "minSelection", t.minSelection)) return false;
        if (!UInt(v, "maxSelection", t.maxSelection)) return false;
        if (t.maxSelection != 0 && t.maxSelection < t.minSelection) {
            return Fail("maxSelection is smaller than minSelection");
        }
        return true;
    }

    bool ParseAction(const Value& v, Action& a) {
        if (!v.isObject()) return Fail("'action' must be an object");
        if (!Str(v, "program", a.program, true)) return false;
        if (Trim(a.program).empty()) return Fail("'action.program' is empty");
        if (!Str(v, "arguments", a.arguments)) return false;
        if (!Str(v, "workingDirectory", a.workingDirectory)) return false;
        std::wstring mode;
        if (!Str(v, "multiFileMode", mode)) return false;
        if (mode.empty() || mode == L"single") a.multiFileMode = MultiFileMode::Single;
        else if (mode == L"each") a.multiFileMode = MultiFileMode::Each;
        else return Fail("unknown multiFileMode");
        if (!Bool(v, "runAsAdmin", a.runAsAdmin)) return false;
        std::wstring win;
        if (!Str(v, "window", win)) return false;
        if (win.empty() || win == L"normal") a.window = WindowMode::Normal;
        else if (win == L"minimized") a.window = WindowMode::Minimized;
        else if (win == L"maximized") a.window = WindowMode::Maximized;
        else if (win == L"hidden") a.window = WindowMode::Hidden;
        else return Fail("unknown window mode");
        return true;
    }

    bool ParseCommand(const Value& v, Command& c) {
        if (!v.isObject()) return Fail("command must be an object");
        if (!Str(v, "id", c.id, true)) return false;
        if (Trim(c.id).empty()) return Fail("command id is empty");
        if (!Str(v, "title", c.title)) return false;
        if (!Str(v, "group", c.group)) return false;
        if (!Bool(v, "enabled", c.enabled)) return false;
        if (!ParseTarget(v["target"], c.target)) return false;
        if (!ParseAction(v["action"], c.action)) return false;
        if (!ParseIcon(v["icon"], c.icon, IconSource::Program)) return false;
        return true;
    }

    std::shared_ptr<Config> Run(const Value& root) {
        if (!root.isObject()) { Fail("root must be an object"); return nullptr; }
        auto cfg = std::make_shared<Config>();
        const Value& ver = root["version"];
        if (!ver.isNumber()) { Fail("'version' is missing"); return nullptr; }
        double vd = ver.asNumber();
        if (!std::isfinite(vd) || std::floor(vd) != vd || vd < 1 || vd > kSupportedVersion) {
            Fail("unsupported version");
            return nullptr;
        }
        cfg->version = static_cast<int>(vd);
        const Value& st = root["settings"];
        if (st.isObject()) {
            std::wstring title;
            if (!Str(st, "menuTitle", title)) return nullptr;
            if (!Trim(title).empty()) cfg->settings.menuTitle = title;
            if (!Bool(st, "logging", cfg->settings.logging)) return nullptr;
        } else if (!st.isNull()) {
            Fail("'settings' must be an object");
            return nullptr;
        }

        std::set<std::wstring> groupIds;
        const Value& groups = root["groups"];
        if (!groups.isNull() && !groups.isArray()) { Fail("'groups' must be an array"); return nullptr; }
        for (const auto& g : groups.items()) {
            Group grp;
            if (!g.isObject()) { Fail("group must be an object"); return nullptr; }
            if (!Str(g, "id", grp.id, true) || !Str(g, "title", grp.title)) return nullptr;
            if (Trim(grp.id).empty()) { Fail("group id is empty"); return nullptr; }
            if (!groupIds.insert(grp.id).second) { Fail("duplicate group id"); return nullptr; }
            if (grp.title.empty()) grp.title = grp.id;
            if (!ParseIcon(g["icon"], grp.icon, IconSource::None)) return nullptr;
            cfg->groups.push_back(std::move(grp));
        }

        std::set<std::wstring> cmdIds;
        const Value& cmds = root["commands"];
        if (!cmds.isNull() && !cmds.isArray()) { Fail("'commands' must be an array"); return nullptr; }
        for (const auto& c : cmds.items()) {
            Command cmd;
            if (!ParseCommand(c, cmd)) return nullptr;
            if (!cmdIds.insert(cmd.id).second) { Fail("duplicate command id"); return nullptr; }
            // A reference to an unknown group is tolerated: the command goes to the top level.
            if (!cmd.group.empty() && !groupIds.count(cmd.group)) cmd.group.clear();
            cfg->commands.push_back(std::move(cmd));
        }
        return cfg;
    }
};

bool IsSep(wchar_t c) { return c == L'\\' || c == L'/'; }

}  // namespace

LoadResult LoadConfig(const std::string& utf8Json) {
    LoadResult r;
    try {
        auto parsed = json::Parse(utf8Json);
        if (!parsed.ok) {
            r.error = "JSON: " + parsed.error + " at offset " + std::to_string(parsed.offset);
            return r;
        }
        Loader loader;
        auto cfg = loader.Run(parsed.value);
        if (!cfg) {
            r.error = loader.error;
            return r;
        }
        r.config = std::move(cfg);
    } catch (...) {
        r.config.reset();
        r.error = "internal error";
    }
    return r;
}

std::wstring DisplayTitle(const Command& c, bool japaneseUi) {
    std::wstring t = Trim(c.title);
    if (!t.empty()) return t;
    std::wstring stem = StemOf(c.action.program);
    if (stem.empty()) return c.id;
    if (!japaneseUi) return L"Open in " + stem;
    return stem + L" \u3067\u958b\u304f";  // "<name> で開く"
}

// ---- matching ----------------------------------------------------------------

static bool ExtensionMatches(const Target& t, const std::wstring& path) {
    if (t.extensions.empty()) return true;
    std::wstring name = FileNameOf(path);
    for (const auto& ext : t.extensions) {
        // Allow multi-part extensions such as ".tar.gz" by suffix comparison,
        // but require that the suffix is not the whole name (".gitignore" has no extension
        // under Windows rules, yet users expect ".gitignore" to match it, so allow equality).
        if (EndsWithIgnoreCase(name, ext)) return true;
    }
    return false;
}

bool Matches(const Command& c, const Selection& s) {
    if (!c.enabled) return false;
    const Target& t = c.target;
    if (s.background) {
        if (!(t.itemTypes & kItemFolderBackground)) return false;
        if (s.backgroundFolder.empty()) return false;
        return t.minSelection <= 1;  // the background counts as a single target
    }
    size_t n = s.items.size();
    if (n == 0) return false;
    if (n < t.minSelection) return false;
    if (t.maxSelection != 0 && n > t.maxSelection) return false;
    for (const auto& item : s.items) {
        if (item.path.empty()) return false;  // virtual items cannot be launched
        switch (item.kind) {
            case ItemKind::File:
                if (!(t.itemTypes & kItemFile)) return false;
                if (!ExtensionMatches(t, item.path)) return false;
                break;
            case ItemKind::Folder:
                if (!(t.itemTypes & kItemFolder)) return false;
                break;
            case ItemKind::Drive:
                if (!(t.itemTypes & kItemDrive)) return false;
                break;
        }
    }
    return true;
}

std::vector<MenuNode> BuildLayout(const Config& cfg) {
    std::vector<MenuNode> top;
    std::vector<size_t> groupSlot(cfg.groups.size(), SIZE_MAX);
    for (const auto& cmd : cfg.commands) {
        if (!cmd.enabled) continue;
        size_t gi = 0;
        if (!cmd.group.empty()) {
            while (gi < cfg.groups.size() && cfg.groups[gi].id != cmd.group) ++gi;
        } else {
            gi = cfg.groups.size();
        }
        if (gi == cfg.groups.size()) {
            MenuNode n;
            n.command = &cmd;
            top.push_back(std::move(n));
            continue;
        }
        if (groupSlot[gi] == SIZE_MAX) {
            MenuNode n;
            n.group = &cfg.groups[gi];
            groupSlot[gi] = top.size();
            top.push_back(std::move(n));
        }
        top[groupSlot[gi]].children.push_back(&cmd);
    }
    return top;
}

std::vector<MenuNode> BuildMenu(const Config& cfg, const Selection& s) {
    std::vector<MenuNode> out;
    for (auto& node : BuildLayout(cfg)) {
        if (node.command) {
            if (Matches(*node.command, s)) out.push_back(std::move(node));
            continue;
        }
        MenuNode g;
        g.group = node.group;
        for (const Command* c : node.children) {
            if (Matches(*c, s)) g.children.push_back(c);
        }
        if (!g.children.empty()) out.push_back(std::move(g));
    }
    return out;
}

// ---- templates -----------------------------------------------------------------

std::wstring QuoteArg(const std::wstring& arg) {
    // Always quote: paths are passed quoted by convention and many apps expect it.
    std::wstring out = L"\"";
    size_t backslashes = 0;
    for (wchar_t c : arg) {
        if (c == L'\\') {
            ++backslashes;
            out += c;
        } else if (c == L'"') {
            // 'backslashes' were already emitted above; add 'backslashes' more to double them, plus 1 to escape '"'.
            out.append(backslashes + 1, L'\\');
            out += L'"';
            backslashes = 0;
        } else {
            backslashes = 0;
            out += c;
        }
    }
    out.append(backslashes, L'\\');  // double trailing backslashes before closing quote
    out += L'"';
    return out;
}

static bool IsEnvNameChar(wchar_t c) {
    return (c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') ||
           c == L'_' || c == L'(' || c == L')' || c == L'-' || c == L'.' || c == L'{' || c == L'}';
}

// Appends a value that is substituted for %1 / %d / %ENV%. When the template wraps it in quotes
// ("%1") and the value ends in backslashes (e.g. "C:\"), the backslashes would escape the
// closing quote, so they are doubled.
static void AppendRaw(std::wstring& out, const std::wstring& value, bool followedByQuote) {
    out += value;
    if (followedByQuote) {
        size_t n = 0;
        for (size_t i = value.size(); i > 0 && value[i - 1] == L'\\'; --i) ++n;
        out.append(n, L'\\');
    }
}

std::wstring ExpandEnvOnly(const std::wstring& t, const EnvLookup& env) {
    std::wstring out;
    out.reserve(t.size() + 32);
    size_t i = 0;
    const size_t n = t.size();
    while (i < n) {
        wchar_t c = t[i];
        if (c != L'%' || i + 1 >= n) {
            out += c;
            ++i;
            continue;
        }
        if (t[i + 1] == L'%') {
            out += L'%';
            i += 2;
            continue;
        }
        size_t j = i + 1;
        while (j < n && IsEnvNameChar(t[j])) ++j;
        if (j < n && t[j] == L'%' && j > i + 1 && env) {
            std::wstring name = t.substr(i + 1, j - i - 1);
            if (auto v = env(name)) {
                out += *v;
                i = j + 1;
                continue;
            }
        }
        out += c;
        ++i;
    }
    return out;
}

std::wstring ExpandTemplate(const std::wstring& t, const TemplateContext& ctx, const EnvLookup& env) {
    std::wstring out;
    out.reserve(t.size() + 64);
    size_t i = 0;
    const size_t n = t.size();
    while (i < n) {
        wchar_t c = t[i];
        if (c == L'"' && i + 3 < n && t[i + 1] == L'%' && t[i + 2] == L'*' && t[i + 3] == L'"') {
            // "%*" -> quoted list without extra outer quotes
            for (size_t k = 0; k < ctx.paths.size(); ++k) {
                if (k) out += L' ';
                out += QuoteArg(ctx.paths[k]);
            }
            i += 4;
            continue;
        }
        if (c != L'%' || i + 1 >= n) {
            out += c;
            ++i;
            continue;
        }
        wchar_t d = t[i + 1];
        bool nextIsQuote = (i + 2 < n && t[i + 2] == L'"');
        if (d == L'%') {
            out += L'%';
            i += 2;
        } else if (d == L'1') {
            AppendRaw(out, ctx.paths.empty() ? ctx.directory : ctx.paths[0], nextIsQuote);
            i += 2;
        } else if (d == L'*') {
            for (size_t k = 0; k < ctx.paths.size(); ++k) {
                if (k) out += L' ';
                out += QuoteArg(ctx.paths[k]);
            }
            i += 2;
        } else if (d == L'd') {
            // %d must not swallow an environment variable like %DATA% or an undefined %data%:
            // when %d...% forms a closed env-var reference, treat it strictly as an env lookup.
            size_t j = i + 1;
            while (j < n && IsEnvNameChar(t[j])) ++j;
            bool isEnvRef = (j < n && t[j] == L'%' && j > i + 2);
            if (isEnvRef) {
                if (env) {
                    std::wstring name = t.substr(i + 1, j - i - 1);
                    if (auto v = env(name)) {
                        bool afterEnvQuote = (j + 1 < n && t[j + 1] == L'"');
                        AppendRaw(out, *v, afterEnvQuote);
                        i = j + 1;
                        continue;
                    }
                }
                // Keep undefined %d...% literal untouched instead of expanding %d.
                out.append(t, i, j - i + 1);
                i = j + 1;
                continue;
            }
            AppendRaw(out, ctx.directory, nextIsQuote);
            i += 2;
        } else {
            size_t j = i + 1;
            while (j < n && IsEnvNameChar(t[j])) ++j;
            if (j < n && t[j] == L'%' && j > i + 1 && env) {
                std::wstring name = t.substr(i + 1, j - i - 1);
                if (auto v = env(name)) {
                    bool afterEnvQuote = (j + 1 < n && t[j + 1] == L'"');
                    AppendRaw(out, *v, afterEnvQuote);
                    i = j + 1;
                    continue;
                }
            }
            out += c;  // leave unknown sequences untouched
            ++i;
        }
    }
    return out;
}

static std::wstring DirectoryForItem(const SelectedItem& item) {
    if (item.kind == ItemKind::Drive) return item.path;
    std::wstring p = ParentOf(item.path);
    return p.empty() ? item.path : p;
}

std::wstring ContextDirectory(const Selection& s) {
    if (s.background) return s.backgroundFolder;
    if (s.items.empty()) return L"";
    return DirectoryForItem(s.items.front());
}

static std::wstring Unquote(const std::wstring& s) {
    if (s.size() >= 2 && s.front() == L'"' && s.back() == L'"') return s.substr(1, s.size() - 2);
    return s;
}

std::vector<LaunchSpec> PlanLaunches(const Command& c, const Selection& s, const EnvLookup& env) {
    std::vector<LaunchSpec> out;
    struct Batch {
        std::vector<std::wstring> paths;
        std::wstring dir;
    };
    std::vector<Batch> batches;
    if (s.background) {
        batches.push_back({{s.backgroundFolder}, s.backgroundFolder});
    } else if (c.action.multiFileMode == MultiFileMode::Each) {
        for (const auto& it : s.items) batches.push_back({{it.path}, DirectoryForItem(it)});
    } else {
        std::vector<std::wstring> all;
        for (const auto& it : s.items) all.push_back(it.path);
        batches.push_back({std::move(all), ContextDirectory(s)});
    }
    for (auto& batch : batches) {
        TemplateContext ctx{batch.paths, batch.dir};
        LaunchSpec spec;
        // action.program only expands environment variables (never %1/%d/%*).
        spec.program = Unquote(Trim(ExpandEnvOnly(c.action.program, env)));
        spec.arguments = ExpandTemplate(c.action.arguments, ctx, env);
        std::wstring wd = Trim(c.action.workingDirectory);
        spec.workingDirectory = wd.empty() ? batch.dir : Unquote(Trim(ExpandTemplate(wd, ctx, env)));
        while (spec.workingDirectory.size() > 3 && IsSep(spec.workingDirectory.back()))
            spec.workingDirectory.pop_back();
        spec.runAsAdmin = c.action.runAsAdmin;
        spec.window = c.action.window;
        out.push_back(std::move(spec));
    }
    return out;
}

}  // namespace cb
