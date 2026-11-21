// Minimal, dependency-free JSON reader used by the shell handler.
// Designed for in-process use inside explorer.exe: never throws out of Parse(),
// bounds nesting depth and input size, and accepts an optional UTF-8 BOM.
#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <string>
#include <vector>

namespace cb::json {

class Value {
public:
    enum class Type { Null, Bool, Number, String, Array, Object };

    Value() = default;

    Type type() const { return type_; }
    bool isNull() const { return type_ == Type::Null; }
    bool isBool() const { return type_ == Type::Bool; }
    bool isNumber() const { return type_ == Type::Number; }
    bool isString() const { return type_ == Type::String; }
    bool isArray() const { return type_ == Type::Array; }
    bool isObject() const { return type_ == Type::Object; }

    bool asBool(bool fallback = false) const { return isBool() ? bool_ : fallback; }
    double asNumber(double fallback = 0) const { return isNumber() ? number_ : fallback; }
    // UTF-8 encoded string content.
    const std::string& asString() const { return isString() ? string_ : empty_; }

    const std::vector<Value>& items() const { return isArray() ? array_ : emptyArray_; }

    // Object member lookup. Returns a shared Null value when absent.
    const Value& operator[](const std::string& key) const;
    bool has(const std::string& key) const;
    // Members in document order.
    const std::vector<std::pair<std::string, Value>>& members() const {
        return isObject() ? object_ : emptyObject_;
    }

    static Value makeNull() { return Value(); }
    static Value makeBool(bool b) { Value v; v.type_ = Type::Bool; v.bool_ = b; return v; }
    static Value makeNumber(double d) { Value v; v.type_ = Type::Number; v.number_ = d; return v; }
    static Value makeString(std::string s) { Value v; v.type_ = Type::String; v.string_ = std::move(s); return v; }
    static Value makeArray(std::vector<Value> a) { Value v; v.type_ = Type::Array; v.array_ = std::move(a); return v; }
    static Value makeObject(std::vector<std::pair<std::string, Value>> o) {
        Value v; v.type_ = Type::Object; v.object_ = std::move(o); return v;
    }

private:
    Type type_ = Type::Null;
    bool bool_ = false;
    double number_ = 0;
    std::string string_;
    std::vector<Value> array_;
    std::vector<std::pair<std::string, Value>> object_;

    static const std::string empty_;
    static const std::vector<Value> emptyArray_;
    static const std::vector<std::pair<std::string, Value>> emptyObject_;
};

struct ParseResult {
    bool ok = false;
    Value value;
    std::string error;   // human readable, for logs only
    size_t offset = 0;   // byte offset of the error
};

constexpr size_t kMaxInputBytes = 8 * 1024 * 1024;
constexpr int kMaxDepth = 64;

ParseResult Parse(const std::string& utf8);

}  // namespace cb::json
