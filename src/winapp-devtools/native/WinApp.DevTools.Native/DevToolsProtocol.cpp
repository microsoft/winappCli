// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsProtocol.h"

#include <cwchar>
#include <cstdio>
#include <cstdlib>
#include <cstdint>

namespace {

struct Cursor {
    const wchar_t* p;
    const wchar_t* end;
    bool Eof() const { return p >= end; }
    wchar_t Peek() const { return p < end ? *p : L'\0'; }
    wchar_t Get() { return p < end ? *p++ : L'\0'; }
};

void SkipWs(Cursor& c) {
    while (!c.Eof()) {
        wchar_t ch = c.Peek();
        if (ch == L' ' || ch == L'\t' || ch == L'\n' || ch == L'\r') { c.p++; }
        else break;
    }
}

static const int kMaxParseDepth = 32;

bool ParseValue(Cursor& c, DevToolsJson& out, int depth);

bool ParseHex4(Cursor& c, unsigned& out) {
    out = 0;
    for (int i = 0; i < 4; ++i) {
        if (c.Eof()) return false;
        wchar_t ch = c.Get();
        out <<= 4;
        if (ch >= L'0' && ch <= L'9') out |= (unsigned)(ch - L'0');
        else if (ch >= L'a' && ch <= L'f') out |= (unsigned)(ch - L'a' + 10);
        else if (ch >= L'A' && ch <= L'F') out |= (unsigned)(ch - L'A' + 10);
        else return false;
    }
    return true;
}

bool ParseString(Cursor& c, std::wstring& out) {
    if (c.Get() != L'"') return false;
    for (;;) {
        if (c.Eof()) return false;
        wchar_t ch = c.Get();
        if (ch == L'"') return true;
        if (ch == L'\\') {
            if (c.Eof()) return false;
            wchar_t esc = c.Get();
            switch (esc) {
                case L'"':  out.push_back(L'"');  break;
                case L'\\': out.push_back(L'\\'); break;
                case L'/':  out.push_back(L'/');  break;
                case L'b':  out.push_back(L'\b'); break;
                case L'f':  out.push_back(L'\f'); break;
                case L'n':  out.push_back(L'\n'); break;
                case L'r':  out.push_back(L'\r'); break;
                case L't':  out.push_back(L'\t'); break;
                case L'u': {
                    unsigned cp = 0;
                    if (!ParseHex4(c, cp)) return false;
                    out.push_back((wchar_t)cp);
                    break;
                }
                default: return false; // invalid escape
            }
        } else {
            out.push_back(ch);
        }
    }
}

bool ParseNumber(Cursor& c, DevToolsJson& out) {
    const wchar_t* start = c.p;
    if (c.Peek() == L'-') c.p++;
    while (!c.Eof()) {
        wchar_t ch = c.Peek();
        if ((ch >= L'0' && ch <= L'9') || ch == L'+' || ch == L'-' ||
            ch == L'.' || ch == L'e' || ch == L'E') { c.p++; }
        else break;
    }
    if (c.p == start) return false;
    std::wstring text(start, (size_t)(c.p - start));
    out.type = DevToolsJsonType::Number;
    out.str  = text;                 // keep raw text for lossless echo of the id
    out.num  = wcstod(text.c_str(), nullptr);
    return true;
}

bool ParseObject(Cursor& c, DevToolsJson& out, int depth) {
    if (c.Get() != L'{') return false;
    out.type = DevToolsJsonType::Object;
    SkipWs(c);
    if (c.Peek() == L'}') { c.p++; return true; }
    for (;;) {
        SkipWs(c);
        if (c.Peek() != L'"') return false;
        std::wstring key;
        if (!ParseString(c, key)) return false;
        SkipWs(c);
        if (c.Get() != L':') return false;
        SkipWs(c);
        DevToolsJson val;
        if (!ParseValue(c, val, depth)) return false;
        if (!out.obj.emplace(key, std::move(val)).second) return false;
        SkipWs(c);
        wchar_t sep = c.Get();
        if (sep == L',') continue;
        if (sep == L'}') return true;
        return false;
    }
}

bool ParseArray(Cursor& c, DevToolsJson& out, int depth) {
    if (c.Get() != L'[') return false;
    out.type = DevToolsJsonType::Array;
    SkipWs(c);
    if (c.Peek() == L']') { c.p++; return true; }
    for (;;) {
        SkipWs(c);
        DevToolsJson val;
        if (!ParseValue(c, val, depth)) return false;
        out.arr.push_back(std::move(val));
        SkipWs(c);
        wchar_t sep = c.Get();
        if (sep == L',') continue;
        if (sep == L']') return true;
        return false;
    }
}

bool Literal(Cursor& c, const wchar_t* lit) {
    for (const wchar_t* q = lit; *q; ++q) {
        if (c.Eof() || c.Get() != *q) return false;
    }
    return true;
}

bool ParseValue(Cursor& c, DevToolsJson& out, int depth) {
    if (depth >= kMaxParseDepth) return false;   // too deeply nested: fail the parse, never the process
    SkipWs(c);
    if (c.Eof()) return false;
    wchar_t ch = c.Peek();
    switch (ch) {
        case L'{': return ParseObject(c, out, depth + 1);
        case L'[': return ParseArray(c, out, depth + 1);
        case L'"': out.type = DevToolsJsonType::String; return ParseString(c, out.str);
        case L't': if (!Literal(c, L"true"))  return false; out.type = DevToolsJsonType::Bool; out.b = true;  return true;
        case L'f': if (!Literal(c, L"false")) return false; out.type = DevToolsJsonType::Bool; out.b = false; return true;
        case L'n': if (!Literal(c, L"null"))  return false; out.type = DevToolsJsonType::Null; return true;
        default:   return ParseNumber(c, out);
    }
}

} // namespace

bool DevToolsJsonParse(const std::wstring& text, DevToolsJson& out) {
    Cursor c{ text.c_str(), text.c_str() + text.size() };
    DevToolsJson v;
    if (!ParseValue(c, v, 0)) return false;
    SkipWs(c);
    if (!c.Eof()) return false; // trailing garbage after the value
    out = std::move(v);
    return true;
}

bool DevToolsParseWireHandle(const std::wstring& text, bool allowZero, unsigned long long* value)
{
    if (!value || text.empty()) return false;
    unsigned long long parsed = 0;
    for (wchar_t c : text) {
        if (c < L'0' || c > L'9') return false;
        const unsigned digit = static_cast<unsigned>(c - L'0');
        if (parsed > (UINT64_MAX - digit) / 10) return false;
        parsed = parsed * 10 + digit;
    }
    if ((!allowZero && parsed == 0) || std::to_wstring(parsed) != text) return false;
    *value = parsed;
    return true;
}

bool DevToolsJson::HasKey(const std::wstring& key) const {
    return type == DevToolsJsonType::Object && obj.find(key) != obj.end();
}

const DevToolsJson* DevToolsJson::Find(const std::wstring& key) const {
    if (type != DevToolsJsonType::Object) return nullptr;
    auto it = obj.find(key);
    return it == obj.end() ? nullptr : &it->second;
}

std::wstring DevToolsJson::GetString(const std::wstring& key, const std::wstring& def) const {
    const DevToolsJson* v = Find(key);
    if (!v) return def;
    if (v->type == DevToolsJsonType::String) return v->str;
    if (v->type == DevToolsJsonType::Number) return v->str;            // raw number text (e.g. a numeric handle)
    if (v->type == DevToolsJsonType::Bool)   return v->b ? L"true" : L"false";
    return def;
}

long long DevToolsJson::GetInt(const std::wstring& key, long long def) const {
    const DevToolsJson* v = Find(key);
    if (!v) return def;
    if (v->type == DevToolsJsonType::Number) return (long long)v->num;
    if (v->type == DevToolsJsonType::String) {                          // accept a stringified integer
        wchar_t* endp = nullptr;
        long long n = wcstoll(v->str.c_str(), &endp, 10);
        if (endp && *endp == L'\0' && endp != v->str.c_str()) return n;
    }
    return def;
}

bool DevToolsJson::GetBool(const std::wstring& key, bool def) const {
    const DevToolsJson* v = Find(key);
    if (!v || v->type != DevToolsJsonType::Bool) return def;
    return v->b;
}

std::wstring DevToolsJsonEscape(const std::wstring& s) {
    std::wstring out;
    out.reserve(s.size() + 8);
    for (wchar_t ch : s) {
        switch (ch) {
            case L'"':  out += L"\\\""; break;
            case L'\\': out += L"\\\\"; break;
            case L'\b': out += L"\\b";  break;
            case L'\f': out += L"\\f";  break;
            case L'\n': out += L"\\n";  break;
            case L'\r': out += L"\\r";  break;
            case L'\t': out += L"\\t";  break;
            default:
                if (ch < 0x20) {                     // other control chars -> \u00XX
                    wchar_t buf[8];
                    _snwprintf_s(buf, _countof(buf), _TRUNCATE, L"\\u%04x", (unsigned)ch);
                    out += buf;
                } else {
                    out.push_back(ch);
                }
        }
    }
    return out;
}

std::wstring DevToolsJsonSerialize(const DevToolsJson& v) {
    switch (v.type) {
        case DevToolsJsonType::Null:   return L"null";
        case DevToolsJsonType::Bool:   return v.b ? L"true" : L"false";
        case DevToolsJsonType::Number: return v.str.empty() ? L"0" : v.str;
        case DevToolsJsonType::String: return L"\"" + DevToolsJsonEscape(v.str) + L"\"";
        case DevToolsJsonType::Array: {
            std::wstring out = L"[";
            for (size_t i = 0; i < v.arr.size(); ++i) { if (i) out += L','; out += DevToolsJsonSerialize(v.arr[i]); }
            out += L"]";
            return out;
        }
        case DevToolsJsonType::Object: {
            std::wstring out = L"{";
            bool first = true;
            for (const auto& kv : v.obj) {
                if (!first) out += L',';
                first = false;
                out += L"\"" + DevToolsJsonEscape(kv.first) + L"\":" + DevToolsJsonSerialize(kv.second);
            }
            out += L"}";
            return out;
        }
    }
    return L"null";
}

DevToolsRpcRequest DevToolsRpcParse(const std::wstring& line) {
    DevToolsRpcRequest req;
    DevToolsJson doc;
    if (!DevToolsJsonParse(line, doc)) return req;   // parsed=false
    req.parsed = true;
    if (!doc.IsObject()) return req;            // valid=false

    const DevToolsJson* method = doc.Find(L"method");
    if (!method || method->type != DevToolsJsonType::String || method->str.empty()) return req;
    req.method = method->str;

    const DevToolsJson* id = doc.Find(L"id");
    if (id) { req.hasId = true; req.idRaw = DevToolsJsonSerialize(*id); }

    const DevToolsJson* params = doc.Find(L"params");
    if (params) req.params = *params;           // typically an object; Null when absent

    req.valid = true;
    return req;
}

std::wstring DevToolsRpcResult(const std::wstring& idRaw, const std::wstring& resultJson) {
    return L"{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + L",\"result\":" +
           (resultJson.empty() ? std::wstring(L"null") : resultJson) + L"}";
}

std::wstring DevToolsRpcError(const std::wstring& idRaw, int code, const std::wstring& message,
                         const std::wstring& token, const std::wstring& dataExtra) {
    std::wstring out = L"{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + L",\"error\":{\"code\":" +
                       std::to_wstring(code) + L",\"message\":\"" + DevToolsJsonEscape(message) + L"\"";
    std::wstring data;
    if (!token.empty()) data += L"\"token\":\"" + DevToolsJsonEscape(token) + L"\"";
    if (!dataExtra.empty()) { if (!data.empty()) data += L','; data += dataExtra; }
    if (!data.empty()) out += L",\"data\":{" + data + L"}";
    out += L"}}";
    return out;
}

std::wstring DevToolsRpcEvent(const std::wstring& method, const std::wstring& paramsJson,
                         const std::wstring& originJson) {
    std::wstring params = paramsJson.empty() ? std::wstring(L"{}") : paramsJson;
    std::wstring origin = originJson.empty() ? std::wstring(L"null") : originJson;
    std::wstring merged = (params == L"{}")
        ? L"{\"origin\":" + origin + L"}"
        : L"{\"origin\":" + origin + L"," + params.substr(1); // drop the leading '{'
    return L"{\"jsonrpc\":\"2.0\",\"method\":\"" + DevToolsJsonEscape(method) + L"\",\"params\":" + merged + L"}";
}
