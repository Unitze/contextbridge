#include "json.h"

#include <cmath>
#include <cstdlib>
#include <locale>
#include <sstream>

namespace cb::json {

const std::string Value::empty_;
const std::vector<Value> Value::emptyArray_;
const std::vector<std::pair<std::string, Value>> Value::emptyObject_;

const Value& Value::operator[](const std::string& key) const {
    static const Value null;
    if (!isObject()) return null;
    // Last duplicate wins, matching common JSON parsers (System.Text.Json is configurable; we document this).
    for (auto it = object_.rbegin(); it != object_.rend(); ++it) {
        if (it->first == key) return it->second;
    }
    return null;
}

bool Value::has(const std::string& key) const {
    if (!isObject()) return false;
    for (const auto& m : object_) {
        if (m.first == key) return true;
    }
    return false;
}

namespace {

class Parser {
public:
    explicit Parser(const std::string& s) : s_(s) {}

    ParseResult Run() {
        ParseResult r;
        if (s_.size() > kMaxInputBytes) {
            r.error = "input too large";
            return r;
        }
        // Skip UTF-8 BOM.
        if (s_.size() >= 3 && (unsigned char)s_[0] == 0xEF && (unsigned char)s_[1] == 0xBB &&
            (unsigned char)s_[2] == 0xBF) {
            pos_ = 3;
        }
        SkipWs();
        Value v;
        if (!ParseValue(v, 0)) {
            r.error = error_;
            r.offset = pos_;
            return r;
        }
        SkipWs();
        if (pos_ != s_.size()) {
            r.error = "trailing characters";
            r.offset = pos_;
            return r;
        }
        r.ok = true;
        r.value = std::move(v);
        return r;
    }

private:
    const std::string& s_;
    size_t pos_ = 0;
    std::string error_;

    bool Fail(const char* msg) {
        if (error_.empty()) error_ = msg;
        return false;
    }

    void SkipWs() {
        while (pos_ < s_.size()) {
            char c = s_[pos_];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') {
                ++pos_;
            } else {
                break;
            }
        }
    }

    bool Consume(char c) {
        if (pos_ < s_.size() && s_[pos_] == c) {
            ++pos_;
            return true;
        }
        return false;
    }

    bool Literal(const char* lit) {
        size_t i = 0;
        while (lit[i]) {
            if (pos_ + i >= s_.size() || s_[pos_ + i] != lit[i]) return Fail("invalid literal");
            ++i;
        }
        pos_ += i;
        return true;
    }

    bool ParseValue(Value& out, int depth) {
        if (depth > kMaxDepth) return Fail("nesting too deep");
        if (pos_ >= s_.size()) return Fail("unexpected end of input");
        char c = s_[pos_];
        switch (c) {
            case '{': return ParseObject(out, depth);
            case '[': return ParseArray(out, depth);
            case '"': {
                std::string str;
                if (!ParseString(str)) return false;
                out = Value::makeString(std::move(str));
                return true;
            }
            case 't':
                if (!Literal("true")) return false;
                out = Value::makeBool(true);
                return true;
            case 'f':
                if (!Literal("false")) return false;
                out = Value::makeBool(false);
                return true;
            case 'n':
                if (!Literal("null")) return false;
                out = Value::makeNull();
                return true;
            default:
                if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(out);
                return Fail("unexpected character");
        }
    }

    bool ParseObject(Value& out, int depth) {
        ++pos_;  // '{'
        std::vector<std::pair<std::string, Value>> members;
        SkipWs();
        if (Consume('}')) {
            out = Value::makeObject(std::move(members));
            return true;
        }
        for (;;) {
            SkipWs();
            if (pos_ >= s_.size() || s_[pos_] != '"') return Fail("expected object key");
            std::string key;
            if (!ParseString(key)) return false;
            SkipWs();
            if (!Consume(':')) return Fail("expected ':'");
            SkipWs();
            Value v;
            if (!ParseValue(v, depth + 1)) return false;
            members.emplace_back(std::move(key), std::move(v));
            SkipWs();
            if (Consume(',')) continue;
            if (Consume('}')) break;
            return Fail("expected ',' or '}'");
        }
        out = Value::makeObject(std::move(members));
        return true;
    }

    bool ParseArray(Value& out, int depth) {
        ++pos_;  // '['
        std::vector<Value> items;
        SkipWs();
        if (Consume(']')) {
            out = Value::makeArray(std::move(items));
            return true;
        }
        for (;;) {
            SkipWs();
            Value v;
            if (!ParseValue(v, depth + 1)) return false;
            items.push_back(std::move(v));
            SkipWs();
            if (Consume(',')) continue;
            if (Consume(']')) break;
            return Fail("expected ',' or ']'");
        }
        out = Value::makeArray(std::move(items));
        return true;
    }

    static void AppendUtf8(std::string& out, uint32_t cp) {
        if (cp < 0x80) {
            out += static_cast<char>(cp);
        } else if (cp < 0x800) {
            out += static_cast<char>(0xC0 | (cp >> 6));
            out += static_cast<char>(0x80 | (cp & 0x3F));
        } else if (cp < 0x10000) {
            out += static_cast<char>(0xE0 | (cp >> 12));
            out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (cp & 0x3F));
        } else {
            out += static_cast<char>(0xF0 | (cp >> 18));
            out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
            out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (cp & 0x3F));
        }
    }

    bool ParseHex4(uint32_t& out) {
        if (pos_ + 4 > s_.size()) return Fail("truncated \\u escape");
        out = 0;
        for (int i = 0; i < 4; ++i) {
            char c = s_[pos_++];
            out <<= 4;
            if (c >= '0' && c <= '9') out |= (c - '0');
            else if (c >= 'a' && c <= 'f') out |= (c - 'a' + 10);
            else if (c >= 'A' && c <= 'F') out |= (c - 'A' + 10);
            else return Fail("invalid \\u escape");
        }
        return true;
    }

    bool ParseString(std::string& out) {
        ++pos_;  // opening quote
        while (pos_ < s_.size()) {
            unsigned char c = static_cast<unsigned char>(s_[pos_]);
            if (c == '"') {
                ++pos_;
                return true;
            }
            if (c < 0x20) return Fail("control character in string");
            if (c != '\\') {
                out += static_cast<char>(c);
                ++pos_;
                continue;
            }
            ++pos_;
            if (pos_ >= s_.size()) break;
            char e = s_[pos_++];
            switch (e) {
                case '"': out += '"'; break;
                case '\\': out += '\\'; break;
                case '/': out += '/'; break;
                case 'b': out += '\b'; break;
                case 'f': out += '\f'; break;
                case 'n': out += '\n'; break;
                case 'r': out += '\r'; break;
                case 't': out += '\t'; break;
                case 'u': {
                    uint32_t cp;
                    if (!ParseHex4(cp)) return false;
                    for (;;) {
                        if (cp >= 0xD800 && cp <= 0xDBFF) {
                            if (pos_ + 2 <= s_.size() && s_[pos_] == '\\' && s_[pos_ + 1] == 'u') {
                                pos_ += 2;
                                uint32_t lo;
                                if (!ParseHex4(lo)) return false;
                                if (lo >= 0xDC00 && lo <= 0xDFFF) {
                                    cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                                    AppendUtf8(out, cp);
                                    break;
                                }
                                // Lone high surrogate followed by another \uXXXX: emit replacement and re-evaluate lo.
                                AppendUtf8(out, 0xFFFD);
                                cp = lo;
                                continue;
                            }
                            AppendUtf8(out, 0xFFFD);
                            break;
                        }
                        if (cp >= 0xDC00 && cp <= 0xDFFF) {
                            AppendUtf8(out, 0xFFFD);
                        } else {
                            AppendUtf8(out, cp);
                        }
                        break;
                    }
                    break;
                }
                default:
                    return Fail("invalid escape");
            }
        }
        return Fail("unterminated string");
    }

    bool ParseNumber(Value& out) {
        size_t start = pos_;
        if (s_[pos_] == '-') ++pos_;
        if (pos_ >= s_.size()) return Fail("invalid number");
        if (s_[pos_] == '0') {
            ++pos_;
        } else if (s_[pos_] >= '1' && s_[pos_] <= '9') {
            while (pos_ < s_.size() && s_[pos_] >= '0' && s_[pos_] <= '9') ++pos_;
        } else {
            return Fail("invalid number");
        }
        if (pos_ < s_.size() && s_[pos_] == '.') {
            ++pos_;
            if (pos_ >= s_.size() || s_[pos_] < '0' || s_[pos_] > '9') return Fail("invalid number");
            while (pos_ < s_.size() && s_[pos_] >= '0' && s_[pos_] <= '9') ++pos_;
        }
        if (pos_ < s_.size() && (s_[pos_] == 'e' || s_[pos_] == 'E')) {
            ++pos_;
            if (pos_ < s_.size() && (s_[pos_] == '+' || s_[pos_] == '-')) ++pos_;
            if (pos_ >= s_.size() || s_[pos_] < '0' || s_[pos_] > '9') return Fail("invalid number");
            while (pos_ < s_.size() && s_[pos_] >= '0' && s_[pos_] <= '9') ++pos_;
        }
        std::string token = s_.substr(start, pos_ - start);
        // Locale-independent parse (host process may have a non-C LC_NUMERIC locale).
        std::istringstream iss(token);
        iss.imbue(std::locale::classic());
        double d = 0;
        if (!(iss >> d) || !iss.eof() || !std::isfinite(d)) return Fail("number out of range");
        out = Value::makeNumber(d);
        return true;
    }
};

}  // namespace

ParseResult Parse(const std::string& utf8) {
    try {
        return Parser(utf8).Run();
    } catch (...) {
        ParseResult r;
        r.error = "internal error";
        return r;
    }
}

}  // namespace cb::json
