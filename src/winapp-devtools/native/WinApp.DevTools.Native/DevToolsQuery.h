// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include "DevToolsRead.h"
#include <algorithm>
#include <charconv>
#include <cmath>
#include <cwctype>
#include <limits>
#include <set>
#include <string_view>

namespace DevToolsQuery {

struct Predicate { std::wstring property, op, literal; };
enum class Verdict { Match, NoMatch, Unevaluated };
struct Evaluation { Verdict verdict; std::wstring reason; };

inline bool PropertyName(const std::wstring& name)
{
    if (name.empty() || name.size() > 256 || name.front() == L'.' || name.back() == L'.') return false;
    bool separator = false;
    for (wchar_t c : name) {
        if (c == L'.') { if (separator) return false; separator = true; }
        else {
            if (!(iswalnum(c) || c == L'_')) return false;
            separator = false;
        }
    }
    return true;
}

inline bool Parse(const std::wstring& text, Predicate& predicate, std::wstring& error)
{
    const auto start = text.find_first_of(L"=!<>*");
    if (text.size() > 4096 || start == std::wstring::npos || !PropertyName(text.substr(0, start))) {
        error = L"Expected Property<operator>Literal, for example FontSize>=20."; return false;
    }
    auto length = start + 1 < text.size() && text[start + 1] == L'=' ? 2u : 1u;
    const auto op = text.substr(start, length);
    if (op != L"==" && op != L"!=" && op != L"<" && op != L"<=" && op != L">" &&
        op != L">=" && op != L"*=") {
        error = L"Supported predicate operators: ==, !=, <, <=, >, >=, *=."; return false;
    }
    if ((text[start] == L'<' || text[start] == L'>') && start + length < text.size() &&
        std::wstring_view(L"=!<>*").find(text[start + length]) != std::wstring_view::npos) {
        error = L"Malformed ordered predicate operator. Use <, <=, >, or >= followed by a numeric literal."; return false;
    }
    predicate = {text.substr(0, start), op, text.substr(start + length)};
    return true;
}

inline std::wstring ShortType(const std::wstring& type)
{
    return type.substr(type.find_last_of(L'.') + 1);
}

inline bool Numeric(const std::wstring& type)
{
    const auto t = ShortType(type);
    return t == L"Double" || t == L"Single" || t == L"Int16" || t == L"Int32" || t == L"Int64" ||
        t == L"UInt16" || t == L"UInt32" || t == L"UInt64" || t == L"Byte" || t == L"SByte";
}

inline bool Number(const std::wstring& text, long double& value)
{
    if (text.empty()) return false;
    // XAML numeric literals use invariant decimal syntax, not locale grouping or hex.
    bool digit = false;
    for (wchar_t c : text) {
        if (c >= L'0' && c <= L'9') digit = true;
        else if (c != L'+' && c != L'-' && c != L'.' && c != L'e' && c != L'E') return false;
    }
    if (!digit) return false;
    std::string ascii;
    ascii.reserve(text.size());
    for (wchar_t c : text) ascii.push_back(static_cast<char>(c));
    const char* start = ascii.data() + (ascii.front() == '+' ? 1 : 0);
    double parsed = 0;
    const auto result = std::from_chars(start, ascii.data() + ascii.size(), parsed);
    value = parsed;
    return result.ptr == ascii.data() + ascii.size() && result.ec == std::errc{} && std::isfinite(parsed);
}

inline bool Integer(const std::wstring& text, bool unsignedType, unsigned long long& magnitude, bool& negative)
{
    if (text.empty()) return false;
    size_t pos = text.front() == L'+' || text.front() == L'-' ? 1 : 0;
    negative = text.front() == L'-';
    if (pos == text.size() || (unsignedType && negative)) return false;
    magnitude = 0;
    for (; pos < text.size(); ++pos) {
        if (text[pos] < L'0' || text[pos] > L'9') return false;
        const auto digit = static_cast<unsigned>(text[pos] - L'0');
        if (magnitude > ((std::numeric_limits<unsigned long long>::max)() - digit) / 10) return false;
        magnitude = magnitude * 10 + digit;
    }
    if (!unsignedType && magnitude > static_cast<unsigned long long>((std::numeric_limits<long long>::max)()) + (negative ? 1ull : 0ull))
        return false;
    if (!magnitude) negative = false;
    return true;
}

inline Evaluation Compare(const DevToolsReadProp* row, const Predicate& predicate)
{
    const auto unknown = [](const wchar_t* reason) { return Evaluation{Verdict::Unevaluated, reason}; };
    if (!row) return unknown(L"property-not-exposed");
    if (!row->valueState.empty() && row->valueState != L"binding") return {Verdict::Unevaluated, row->valueState};
    const auto type = ShortType(row->type);
    const bool equality = predicate.op == L"==" || predicate.op == L"!=";
    int order = 0;
    if (Numeric(type)) {
        if (predicate.op == L"*=") return unknown(L"contains-requires-string");
        if (type == L"Double" || type == L"Single") {
            long double left = 0, right = 0;
            if (!Number(row->value, left) || !Number(predicate.literal, right)) return unknown(L"finite-number-required");
            order = left < right ? -1 : left > right ? 1 : 0;
        } else {
            unsigned long long left = 0, right = 0; bool leftNegative = false, rightNegative = false;
            const bool unsignedType = type.front() == L'U' || type == L"Byte";
            if (!Integer(row->value, unsignedType, left, leftNegative) ||
                !Integer(predicate.literal, unsignedType, right, rightNegative)) return unknown(L"integer-required");
            order = leftNegative != rightNegative ? (leftNegative ? -1 : 1) :
                (left < right ? -1 : left > right ? 1 : 0) * (leftNegative ? -1 : 1);
        }
    } else if (type == L"String") {
        if (predicate.op == L"*=") return {row->value.find(predicate.literal) != std::wstring::npos
            ? Verdict::Match : Verdict::NoMatch, L""};
        if (!equality) return unknown(L"ordered-comparison-requires-number");
        order = row->value.compare(predicate.literal);
    } else if (type == L"Boolean") {
        if (!equality) return unknown(L"boolean-requires-equality");
        auto boolean = [](std::wstring text, bool& value) {
            for (auto& c : text) c = towlower(c);
            if (text == L"true" || text == L"1") { value = true; return true; }
            if (text == L"false" || text == L"0") { value = false; return true; }
            return false;
        };
        bool left = false, right = false;
        if (!boolean(row->value, left) || !boolean(predicate.literal, right)) return unknown(L"boolean-required");
        order = left == right ? 0 : 1;
    } else if (!row->enumValues.empty()) {
        if (!equality) return unknown(L"enum-requires-equality");
        if (std::find(row->enumValues.begin(), row->enumValues.end(), predicate.literal) == row->enumValues.end())
            return unknown(L"unknown-enum-member");
        order = row->value.compare(predicate.literal);
    } else return unknown(L"unsupported-scalar-type");
    const bool matched = predicate.op == L"==" ? order == 0 : predicate.op == L"!=" ? order != 0 :
        predicate.op == L"<" ? order < 0 : predicate.op == L"<=" ? order <= 0 :
        predicate.op == L">" ? order > 0 : order >= 0;
    return {matched ? Verdict::Match : Verdict::NoMatch, L""};
}

inline Evaluation Match(const std::vector<DevToolsReadProp>& rows, const std::vector<Predicate>& predicates)
{
    bool rejected = false;
    Evaluation unknown{Verdict::Match, L""};
    for (const auto& predicate : predicates) {
        const DevToolsReadProp* selected = nullptr;
        for (const auto& row : rows) if (row.name == predicate.property) {
            if (selected) return {Verdict::Unevaluated, L"ambiguous-property"};
            selected = &row;
        }
        const auto result = Compare(selected, predicate);
        if (result.verdict == Verdict::Unevaluated &&
            (unknown.verdict != Verdict::Unevaluated || result.reason < unknown.reason)) unknown = result;
        rejected |= result.verdict == Verdict::NoMatch;
    }
    // Finish scanning even after false: ambiguous metadata must still block the candidate.
    return rejected ? Evaluation{Verdict::NoMatch, L""} : unknown;
}
}
