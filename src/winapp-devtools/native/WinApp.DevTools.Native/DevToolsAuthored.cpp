// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include <windows.h>
#include <xmllite.h>
#include <bcrypt.h>

#pragma comment(lib, "xmllite.lib")
#pragma comment(lib, "bcrypt.lib")

#include <algorithm>
#include <cstring>
#include <map>
#include <memory>
#include <set>
#include <string>
#include <vector>

#include "DevToolsAuthored.h"
#include "DevToolsSourcePath.h"
#include "DevToolsProtocol.h"

namespace {

std::wstring g_sourceRoot;
unsigned long long g_buildTimeUnix = 0;
std::wstring g_coordinateError;
std::map<std::wstring, std::wstring> g_coordinateExclusions;
std::set<std::wstring> g_excludedSourcePaths;
struct CoordinateFile
{
    std::wstring source, sourceHash;
    std::vector<DevToolsJson> elements;
    bool payloadVerified = false;
    bool advisory = false;
    bool likely = false;
    std::wstring evidence;
    std::shared_ptr<void> payloadLease;
    std::vector<std::shared_ptr<void>> referencedLeases;
};
std::map<std::wstring, CoordinateFile> g_coordinates;

std::wstring HashBytes(const std::vector<char>& bytes)
{
    unsigned char hash[32]{};
    if (BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0,
        reinterpret_cast<PUCHAR>(const_cast<char*>(bytes.data())), static_cast<ULONG>(bytes.size()),
        hash, sizeof(hash)) < 0) return L"";
    std::wstring result;
    constexpr wchar_t hex[] = L"0123456789ABCDEF";
    for (auto byte : hash) { result += hex[byte >> 4]; result += hex[byte & 15]; }
    return result;
}

bool ReadBytes(const std::wstring& path, size_t maximum, std::vector<char>* bytes, std::shared_ptr<void>* retained = nullptr,
    DWORD share = FILE_SHARE_READ)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, share, nullptr, OPEN_EXISTING, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER length{};
    bool ok = GetFileSizeEx(file, &length) && length.QuadPart > 0 &&
        static_cast<unsigned long long>(length.QuadPart) <= maximum;
    if (ok) {
        bytes->resize(static_cast<size_t>(length.QuadPart));
        DWORD read = 0;
        ok = ReadFile(file, bytes->data(), static_cast<DWORD>(bytes->size()), &read, nullptr) && read == bytes->size();
    }
    if (ok && retained) { retained->reset(file, [](void* handle) { CloseHandle(handle); }); }
    else { CloseHandle(file); }
    return ok;
}

std::wstring ResourceKey(std::wstring resource)
{
    if (resource.rfind(L"ms-appx:///", 0) == 0) resource.erase(0, 11);
    while (!resource.empty() && (resource.front() == L'/' || resource.front() == L'\\')) resource.erase(0, 1);
    for (auto& c : resource) { c = c == L'\\' ? L'/' : static_cast<wchar_t>(towlower(c)); }
    return resource;
}

struct CachedFile
{
    bool ok = false;
    bool stale = false;
    std::wstring hash;
    std::vector<std::wstring> lines;
};
std::map<std::wstring, CachedFile> g_files;
SRWLOCK g_lock = SRWLOCK_INIT;

// Adapt SourceInfo's app-relative spellings to the shared canonical source resolver.
std::wstring UriToDiskPath(const std::wstring& uri)
{
    if (g_sourceRoot.empty() || uri.empty()) return L"";
    std::wstring rel = uri;
    const wchar_t* kPrefix = L"ms-appx:///";
    if (rel.rfind(kPrefix, 0) == 0) rel = rel.substr(wcslen(kPrefix));
    while (!rel.empty() && (rel[0] == L'/' || rel[0] == L'\\')) rel.erase(0, 1);
    if (rel.empty()) return L"";
    std::replace(rel.begin(), rel.end(), L'\\', L'/');
    std::wstring path;
    return DevToolsSourcePath_ResolveExisting(g_sourceRoot, kPrefix + rel, &path) ? path : L"";
}

unsigned long long LastWriteUnix(const std::wstring& path)
{
    WIN32_FILE_ATTRIBUTE_DATA fad{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fad)) return 0;
    ULARGE_INTEGER li{};
    li.LowPart = fad.ftLastWriteTime.dwLowDateTime;
    li.HighPart = fad.ftLastWriteTime.dwHighDateTime;
    if (li.QuadPart < 116444736000000000ULL) return 0;
    return (li.QuadPart - 116444736000000000ULL) / 10000000ULL;
}

bool ReadLines(const std::wstring& path, std::vector<std::wstring>* out, std::wstring* hash)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(h, &size) || size.QuadPart <= 0 || size.QuadPart > (4 << 20)) { CloseHandle(h); return false; }
    std::vector<char> buf((size_t)size.QuadPart);
    DWORD read = 0;
    BOOL ok = ReadFile(h, buf.data(), (DWORD)buf.size(), &read, nullptr);
    CloseHandle(h);
    if (!ok) return false;
    buf.resize(read);
    *hash = HashBytes(buf);

    std::wstring text;
    if (buf.size() >= 2 && (unsigned char)buf[0] == 0xFF && (unsigned char)buf[1] == 0xFE) {
        const size_t wcharCount = (buf.size() - 2) / sizeof(wchar_t);
        text.resize(wcharCount);
        if (wcharCount != 0) {
            std::memcpy(text.data(), buf.data() + 2, wcharCount * sizeof(wchar_t));
        }
    } else {
        size_t off = (buf.size() >= 3 && (unsigned char)buf[0] == 0xEF && (unsigned char)buf[1] == 0xBB &&
                      (unsigned char)buf[2] == 0xBF) ? 3 : 0;
        int need = MultiByteToWideChar(CP_UTF8, 0, buf.data() + off, (int)(buf.size() - off), nullptr, 0);
        if (need <= 0) return false;
        text.resize((size_t)need);
        MultiByteToWideChar(CP_UTF8, 0, buf.data() + off, (int)(buf.size() - off), text.data(), need);
    }

    out->clear();
    std::wstring cur;
    for (wchar_t c : text) {
        if (c == L'\n') { out->push_back(cur); cur.clear(); }
        else if (c != L'\r') cur.push_back(c);
    }
    out->push_back(cur);
    return true;
}

const CachedFile& GetFile(const std::wstring& path)
{
    {
        AcquireSRWLockShared(&g_lock);
        auto it = g_files.find(path);
        if (it != g_files.end()) { const CachedFile& f = it->second; ReleaseSRWLockShared(&g_lock); return f; }
        ReleaseSRWLockShared(&g_lock);
    }
    CachedFile f;
    f.ok = ReadLines(path, &f.lines, &f.hash);
    if (f.ok && g_buildTimeUnix) {
        unsigned long long w = LastWriteUnix(path);
        f.stale = (w != 0 && w > g_buildTimeUnix + 2);   // 2s slack for filesystem timestamp granularity
    }
    AcquireSRWLockExclusive(&g_lock);
    auto res = g_files.emplace(path, std::move(f));
    const CachedFile& ref = res.first->second;
    ReleaseSRWLockExclusive(&g_lock);
    return ref;
}

std::wstring XmlUnescape(const std::wstring& s)
{
    std::wstring out;
    out.reserve(s.size());
    for (size_t i = 0; i < s.size(); ) {
        if (s[i] == L'&') {
            if (s.compare(i, 5, L"&amp;") == 0)       { out += L'&';  i += 5; continue; }
            if (s.compare(i, 4, L"&lt;") == 0)        { out += L'<';  i += 4; continue; }
            if (s.compare(i, 4, L"&gt;") == 0)        { out += L'>';  i += 4; continue; }
            if (s.compare(i, 6, L"&quot;") == 0)      { out += L'"';  i += 6; continue; }
            if (s.compare(i, 6, L"&apos;") == 0)      { out += L'\''; i += 6; continue; }
        }
        out += s[i++];
    }
    return out;
}

bool OpensElementTag(const std::wstring& l, size_t c)
{
    if (c + 1 >= l.size()) return false;
    const wchar_t n = l[c + 1];
    return iswalpha(n) != 0 || n == L'_';
}

// Join the element's OPENING TAG, starting from the '<' that opens it and ending at its unquoted '>'.
std::wstring ElementSpan(const std::vector<std::wstring>& lines, unsigned int line, unsigned int column)
{
    if (line == 0 || line > lines.size()) return L"";
    const size_t target = (size_t)line - 1;
    const size_t kBack = 40, kForward = 40;
    const size_t from = target >= kBack ? target - kBack : 0;

    // '<' cannot appear before the first tag's '>' closes it.
    size_t openLine = 0, openCol = 0;
    bool open = false;
    wchar_t quote = 0;
    for (size_t i = from; i < target; ++i) {
        const std::wstring& l = lines[i];
        for (size_t c = 0; c < l.size(); ++c) {
            const wchar_t ch = l[c];
            if (quote != 0) { if (ch == quote) quote = 0; continue; }
            if (ch == L'"' || ch == L'\'') { quote = ch; continue; }
            if (ch == L'<') { open = OpensElementTag(l, c); openLine = i; openCol = c; }
            else if (ch == L'>') open = false;
        }
        quote = 0;   // an attribute value never spans a line break in practice; do not carry a stray quote
    }

    size_t startLine = target, startCol = 0;
    {
        const std::wstring& l = lines[target];
        std::vector<size_t> starts;
        quote = 0;
        for (size_t c = 0; c < l.size(); ++c) {
            const wchar_t ch = l[c];
            if (quote != 0) { if (ch == quote) quote = 0; continue; }
            if (ch == L'"' || ch == L'\'') { quote = ch; continue; }
            if (l.compare(c, 4, L"<!--") == 0) {
                const size_t end = l.find(L"-->", c + 4);
                if (end == std::wstring::npos) break;
                c = end + 2;
                continue;
            }
            if (ch == L'<' && OpensElementTag(l, c)) starts.push_back(c);
        }
        if (starts.empty()) {
            if (!open) return L"";
            startLine = openLine; startCol = openCol;
        } else if (column == 0) {
            if (starts.size() != 1) return L"";
            startCol = starts.front();
        } else {
            const size_t position = static_cast<size_t>(column) - 1;
            bool found = false;
            for (const size_t candidate : starts) {
                if (candidate > position) break;
                quote = 0;
                size_t end = candidate;
                for (; end < l.size(); ++end) {
                    const wchar_t ch = l[end];
                    if (quote) { if (ch == quote) quote = 0; continue; }
                    if (ch == L'"' || ch == L'\'') quote = ch;
                    else if (ch == L'>') break;
                }
                if (position <= end) { startCol = candidate; found = true; break; }
            }
            if (!found) return L"";
        }
    }

    std::wstring span;
    quote = 0;
    for (size_t i = startLine; i < lines.size() && i < startLine + kForward; ++i) {
        const std::wstring& l = lines[i];
        const size_t begin = (i == startLine) ? startCol : 0;
        std::wstring piece = l.substr(begin);
        if (i != startLine && quote == 0) {
            size_t lead = 0;
            while (lead < piece.size() && (piece[lead] == L' ' || piece[lead] == L'\t')) ++lead;
            piece = std::wstring(2, L' ') + piece.substr(lead);
        }
        for (size_t c = 0; c < piece.size(); ++c) {
            const wchar_t ch = piece[c];
            if (quote != 0) { if (ch == quote) quote = 0; continue; }
            if (ch == L'"' || ch == L'\'') { quote = ch; continue; }
            if (ch == L'>') { span += piece.substr(0, c + 1); return span; }
        }
        span += piece;
        if (i + 1 < lines.size()) span += L'\n';
    }
    return L""; // unterminated markup is not an available declaration
}

bool FindAttribute(const std::wstring& span, const std::wstring& prop, std::wstring* out)
{
    size_t pos = 0;
    while ((pos = span.find(prop, pos)) != std::wstring::npos) {
        const size_t end = pos + prop.size();
        const bool leftOk = (pos == 0) || (!iswalnum(span[pos - 1]) && span[pos - 1] != L'_' && span[pos - 1] != L'.' && span[pos - 1] != L':');
        size_t j = end;
        while (j < span.size() && iswspace(span[j])) ++j;
        const bool rightOk = (j < span.size() && span[j] == L'=');
        if (leftOk && rightOk) {
            ++j;
            while (j < span.size() && iswspace(span[j])) ++j;
            if (j < span.size() && (span[j] == L'"' || span[j] == L'\'')) {
                const wchar_t quote = span[j++];
                size_t close = span.find(quote, j);
                if (close != std::wstring::npos) { *out = XmlUnescape(span.substr(j, close - j)); return true; }
            }
            return false;
        }
        pos = end;
    }
    return false;
}

// An x:Class root declares the runtime subclass, not the Page/UserControl tag's base type.
// Read the declaration's namespace URI so an alias works and a lookalike attribute does not.
bool DeclaresRuntimeClass(const std::wstring& span, const std::wstring& type)
{
    const std::wstring xml = L"\xFEFF" + span;
    IStream* stream = nullptr;
    IXmlReader* reader = nullptr;
    bool matches = false;
    if (SUCCEEDED(CreateStreamOnHGlobal(nullptr, TRUE, &stream))) {
        const ULONG bytes = static_cast<ULONG>(xml.size() * sizeof(wchar_t));
        ULONG written = 0;
        LARGE_INTEGER zero{};
        if (SUCCEEDED(stream->Write(xml.data(), bytes, &written)) && written == bytes &&
            SUCCEEDED(stream->Seek(zero, STREAM_SEEK_SET, nullptr)) &&
            SUCCEEDED(CreateXmlReader(__uuidof(IXmlReader), reinterpret_cast<void**>(&reader), nullptr)) &&
            SUCCEEDED(reader->SetProperty(XmlReaderProperty_DtdProcessing, DtdProcessing_Prohibit)) &&
            SUCCEEDED(reader->SetInput(stream))) {
            XmlNodeType node{};
            if (reader->Read(&node) == S_OK && node == XmlNodeType_Element &&
                reader->MoveToAttributeByName(L"Class", L"http://schemas.microsoft.com/winfx/2006/xaml") == S_OK) {
                const wchar_t* value = nullptr;
                UINT length = 0;
                if (SUCCEEDED(reader->GetValue(&value, &length)))
                    matches = type == std::wstring(value, length);
            }
        }
    }
    if (reader) reader->Release();
    if (stream) stream->Release();
    return matches;
}

} // namespace

const wchar_t* DevToolsAuthored_StateToken(DevToolsAuthoredState s)
{
    switch (s) {
        case DevToolsAuthoredState::Available:    return L"available";
        case DevToolsAuthoredState::NoSourceInfo: return L"noSourceInfo";
        case DevToolsAuthoredState::NoFile:       return L"noFile";
        case DevToolsAuthoredState::Stale:        return L"stale";
        case DevToolsAuthoredState::Unavailable:  return L"unavailable";
        case DevToolsAuthoredState::UnverifiedBuild: return L"unverifiedBuild";
        case DevToolsAuthoredState::Likely: return L"likely";
    }
    return L"noSourceInfo";
}

void DevToolsAuthored_Init(const std::wstring& sourceRoot, unsigned long long buildTimeUnix)
{
    AcquireSRWLockExclusive(&g_lock);
    g_sourceRoot = sourceRoot;
    while (!g_sourceRoot.empty() && (g_sourceRoot.back() == L'\\' || g_sourceRoot.back() == L'/'))
        g_sourceRoot.pop_back();
    g_buildTimeUnix = buildTimeUnix;
    g_files.clear();
    g_coordinates.clear();
    g_coordinateError.clear();
    g_coordinateExclusions.clear();
    g_excludedSourcePaths.clear();
    ReleaseSRWLockExclusive(&g_lock);
}

bool DevToolsAuthored_HasSourceRoot()
{
    AcquireSRWLockShared(&g_lock);
    const bool bound = !g_sourceRoot.empty();
    ReleaseSRWLockShared(&g_lock);
    return bound;
}

bool DevToolsAuthored_IsProjectSource(const std::wstring& fileUri)
{
    return !UriToDiskPath(fileUri).empty();
}

void DevToolsAuthored_InitCoordinates(const std::wstring& inventoryPath, const std::wstring& inventoryHash,
    const std::wstring& payloadRoot)
{
    if (inventoryPath.empty() && inventoryHash.empty()) return;
    g_coordinateError = L"The launch source inventory could not be verified.";
    std::vector<char> bytes;
    // The CLI holds the inventory open for write with delete-on-close, so it must be opened with full sharing.
    if (!ReadBytes(inventoryPath, 16 << 20, &bytes, nullptr, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE) ||
        inventoryHash.size() != 64 || HashBytes(bytes) != inventoryHash) return;
    int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes.data(), static_cast<int>(bytes.size()), nullptr, 0);
    if (size <= 0) return;
    std::wstring text(static_cast<size_t>(size), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes.data(), static_cast<int>(bytes.size()), text.data(), size);
    DevToolsJson inventory;
    if (!DevToolsJsonParse(text, inventory) || !inventory.IsObject()) return;
    const auto error = inventory.GetString(L"coordinateError");
    if (!error.empty()) { g_coordinateError = error; return; }
    std::map<std::wstring, std::wstring> exclusions;
    std::set<std::wstring> excludedPaths;
    if (const auto excluded = inventory.Find(L"coordinateExclusions");
        excluded && excluded->type != DevToolsJsonType::Null) {
        if (excluded->type != DevToolsJsonType::Array || excluded->arr.size() > 2048) return;
        for (const auto& entry : excluded->arr) {
            const auto resource = ResourceKey(entry.GetString(L"resource"));
            const auto reason = entry.GetString(L"reason");
            if (resource.size() < 6 || resource.substr(resource.size() - 5) != L".xaml" || reason.empty() ||
                !exclusions.emplace(resource, reason).second) return;
            const auto path = UriToDiskPath(entry.GetString(L"source"));
            if (!path.empty()) excludedPaths.insert(ResourceKey(path));
        }
    }
    const auto files = inventory.Find(L"coordinates");
    if (!files || files->type == DevToolsJsonType::Null) {
        g_coordinateExclusions = std::move(exclusions);
        g_excludedSourcePaths = std::move(excludedPaths);
        g_coordinateError.clear();
        return;
    }
    if (files->type != DevToolsJsonType::Array || files->arr.size() > 2048) return;
    std::map<std::wstring, CoordinateFile> admitted;
    std::shared_ptr<void> priLease;
    std::wstring verifiedPriHash;
    for (const auto& entry : files->arr) {
        CoordinateFile file;
        file.source = entry.GetString(L"source");
        std::replace(file.source.begin(), file.source.end(), L'\\', L'/');
        file.sourceHash = entry.GetString(L"sourceHash");
        file.advisory = inventory.GetBool(L"coordinatesAdvisory", false);
        file.likely = entry.GetString(L"attribution") == L"likely";
        file.evidence = entry.GetString(L"evidence");
        const auto resource = ResourceKey(entry.GetString(L"resource"));
        if (exclusions.count(resource)) return;
        const auto elements = entry.Find(L"elements");
        std::wstring sourcePath;
        if (file.sourceHash.size() != 64 || (!file.likely && entry.GetString(L"xbfHash").size() != 64) ||
            !DevToolsSourcePath_ResolveExisting(g_sourceRoot, L"ms-appx:///" + file.source, &sourcePath) ||
            !elements || elements->type != DevToolsJsonType::Array || elements->arr.size() > 4096 ||
            resource.size() < 6 || resource.substr(resource.size() - 5) != L".xaml") return;
        for (const auto& element : elements->arr) {
            for (const auto field : { L"line", L"endLine", L"column" }) {
                const auto value = element.Find(field);
                if (!value || value->type != DevToolsJsonType::Number || value->num < 1 || value->num > (2 << 20) ||
                    value->num != static_cast<unsigned int>(value->num)) return;
            }
            if (element.GetString(L"type").empty() ||
                element.GetInt(L"endLine", 0) < element.GetInt(L"line", 0)) return;
        }
        file.elements = elements->arr;
        if (file.likely) {
            if (file.evidence.empty()) return;
            if (!admitted.emplace(resource, std::move(file)).second) return;
            continue;
        }
        std::wstring xbfPath;
        std::vector<char> xbf;
        const auto priHash = entry.GetString(L"priHash");
        if (!priHash.empty()) {
            if (priHash.size() != 64) return;
            if (!priLease) {
                if (DevToolsSourcePath_ResolvePri(payloadRoot, &xbfPath) &&
                    ReadBytes(xbfPath, 32 << 20, &xbf, &priLease)) verifiedPriHash = HashBytes(xbf);
            }
            file.payloadVerified = priLease && verifiedPriHash == priHash;
            file.payloadLease = priLease;
            const auto paths = entry.Find(L"payloadPaths");
            if (paths && paths->type != DevToolsJsonType::Null) {
                if (paths->type != DevToolsJsonType::Array || paths->arr.size() > 64) return;
                for (const auto& relative : paths->arr) {
                    std::shared_ptr<void> lease;
                    const bool verified = relative.type == DevToolsJsonType::String &&
                        DevToolsSourcePath_ResolveCompiled(payloadRoot, L"ms-appx:///" + relative.str, &xbfPath) &&
                        ReadBytes(xbfPath, 2 << 20, &xbf, &lease) && HashBytes(xbf) == entry.GetString(L"xbfHash");
                    file.payloadVerified = file.payloadVerified && verified;
                    if (verified) file.referencedLeases.push_back(std::move(lease));
                }
            }
        } else {
            file.payloadVerified = GetFileAttributesW((payloadRoot + L"\\resources.pri").c_str()) == INVALID_FILE_ATTRIBUTES &&
                DevToolsSourcePath_ResolveCompiled(payloadRoot,
                L"ms-appx:///" + resource.substr(0, resource.size() - 5) + L".xbf", &xbfPath) &&
                ReadBytes(xbfPath, 2 << 20, &xbf, &file.payloadLease) && HashBytes(xbf) == entry.GetString(L"xbfHash");
        }
        if (!file.payloadVerified) file.payloadLease.reset();
        if (!admitted.emplace(resource, std::move(file)).second) return;
    }
    g_coordinates = std::move(admitted);
    g_coordinateExclusions = std::move(exclusions);
    g_excludedSourcePaths = std::move(excludedPaths);
    g_coordinateError.clear();
}

DevToolsAuthoredState DevToolsAuthored_ReadElement(const std::wstring& fileUri, unsigned int line, std::wstring* out,
    unsigned int column, const std::wstring& type, const std::wstring& name, DevToolsAuthoredLocation* authoredLocation)
{
    if (out) out->clear();
    if (authoredLocation) *authoredLocation = {};
    if (fileUri.empty() || line == 0) return DevToolsAuthoredState::NoSourceInfo;
    if (!g_coordinateError.empty()) return DevToolsAuthoredState::UnverifiedBuild;
    if (g_coordinateExclusions.count(ResourceKey(fileUri))) return DevToolsAuthoredState::UnverifiedBuild;
    const auto mapping = g_coordinates.find(ResourceKey(fileUri));
    const CoordinateFile* coordinates = mapping == g_coordinates.end() ? nullptr : &mapping->second;
    if (coordinates && ((!coordinates->payloadVerified && !coordinates->likely) || coordinates->advisory)) return DevToolsAuthoredState::UnverifiedBuild;
    const std::wstring path = UriToDiskPath(coordinates ? L"ms-appx:///" + coordinates->source : fileUri);
    if (path.empty()) return DevToolsAuthoredState::NoFile;
    if (g_excludedSourcePaths.count(ResourceKey(path))) return DevToolsAuthoredState::UnverifiedBuild;
    CachedFile current;
    if (coordinates) current.ok = ReadLines(path, &current.lines, &current.hash);
    const CachedFile& f = coordinates ? current : GetFile(path);
    if (!f.ok) return DevToolsAuthoredState::NoFile;
    if (coordinates ? f.hash != coordinates->sourceHash : f.stale) return DevToolsAuthoredState::Stale;
    if (coordinates) {
        if (!column) return DevToolsAuthoredState::Unavailable;
        const DevToolsJson* selected = nullptr;
        for (const auto& element : coordinates->elements) {
            const auto startLine = element.GetInt(L"line", 0);
            const auto endLine = element.GetInt(L"endLine", 0);
            if (line >= startLine && line <= endLine) {
                if (selected) return DevToolsAuthoredState::Unavailable;
                selected = &element;
            }
        }
        if (!selected || type.empty() ||
            (selected->GetString(L"type") != type.substr(type.find_last_of(L'.') + 1) &&
                selected->GetString(L"runtimeClass") != type) ||
            selected->GetString(L"name") != name) return DevToolsAuthoredState::Unavailable;
        line = static_cast<unsigned int>(selected->GetInt(L"line", 0));
        column = static_cast<unsigned int>(selected->GetInt(L"column", 0));
        if (authoredLocation && coordinates->likely) {
            authoredLocation->parentLine = static_cast<unsigned int>(selected->GetInt(L"parentLine", 0));
            authoredLocation->parentType = selected->GetString(L"parentType");
            authoredLocation->parentName = selected->GetString(L"parentName");
            authoredLocation->evidence = coordinates->evidence;
        }
    }
    std::wstring span = ElementSpan(f.lines, line, column);
    if (span.empty()) return DevToolsAuthoredState::Unavailable;
    if (!type.empty()) {
        const size_t end = span.find_first_of(L" \t\r\n/>", 1);
        std::wstring tag = span.substr(1, end - 1);
        tag = tag.substr(tag.find_last_of(L':') + 1);
        const std::wstring shortType = type.substr(type.find_last_of(L'.') + 1);
        if (tag != shortType && !DeclaresRuntimeClass(span, type)) return DevToolsAuthoredState::Unavailable;
    }
    if (!name.empty()) {
        std::wstring authoredName;
        if ((!FindAttribute(span, L"x:Name", &authoredName) && !FindAttribute(span, L"Name", &authoredName)) ||
            authoredName != name) return DevToolsAuthoredState::Unavailable;
    }
    if (out) *out = std::move(span);
    if (authoredLocation) {
        authoredLocation->line = line; authoredLocation->column = column;
        authoredLocation->mapped = coordinates != nullptr;
        authoredLocation->sourceFile = coordinates ? coordinates->source : L"";
    }
    return coordinates && coordinates->likely ? DevToolsAuthoredState::Likely : DevToolsAuthoredState::Available;
}

bool DevToolsAuthored_FindAttribute(const std::wstring& element, const std::wstring& prop, std::wstring* out)
{
    if (!out) return false;
    out->clear();
    std::wstring value;
    if (!FindAttribute(element, prop, &value)) return false;
    *out = std::move(value);
    return true;
}

DevToolsAuthoredState DevToolsAuthored_Read(const std::wstring& fileUri, unsigned int line,
                                  const std::wstring& prop, std::wstring* out)
{
    if (out) out->clear();
    std::wstring element;
    const DevToolsAuthoredState state = DevToolsAuthored_ReadElement(fileUri, line, &element);
    if (state == DevToolsAuthoredState::Available && out) DevToolsAuthored_FindAttribute(element, prop, out);
    return state;
}
