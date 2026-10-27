// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include <windows.h>
#include <xmllite.h>
#include <bcrypt.h>

#pragma comment(lib, "xmllite.lib")
#pragma comment(lib, "bcrypt.lib")

#include <algorithm>
#include <atomic>
#include <cstring>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <vector>

#include "DevToolsAuthored.h"
#include "DevToolsCSharpArguments.h"
#include "DevToolsCSharpSource.h"
#include "DevToolsReactorNames.h"
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

using DeclarationIndex = std::shared_ptr<const DevToolsCSharpArguments::Declarations>;

// The C# declaration indexes (ProjectDeclarations) and the per-file declarations they are merged from. Background
// builds hold a reference, so what they write to outlives the engine's own.
struct DeclarationCache
{
    struct Project { ULONGLONG built = 0; bool building = false; DeclarationIndex index; };
    struct File { FILETIME written{}; unsigned long long size = 0; DeclarationIndex declarations; };
    std::mutex lock;
    std::map<std::wstring, Project> projects;   // by .csproj, or by file for a file in no project
    std::map<std::wstring, File> files;          // by path
};
const std::shared_ptr<DeclarationCache> g_declarations = std::make_shared<DeclarationCache>();
std::atomic<unsigned long long> g_csharpFilesParsed{0};

// Not the app's own source, the same folders the CLI's comment resolver skips: build output, the XAML compiler's
// copies of each page, restored packages and tool folders.
bool IsSkippedSourceFolder(const wchar_t* name)
{
    for (const auto skipped : { L"bin", L"obj", L"Generated Files", L"packages", L"node_modules", L".git", L".vs" })
        if (_wcsicmp(name, skipped) == 0) return true;
    return false;
}

SRWLOCK g_indexLock = SRWLOCK_INIT;
std::wstring g_indexRoot;
std::vector<std::wstring> g_indexedXaml;   // root-relative, '/'-separated, lowercase

// The .xaml files under the source root, listed once per root. Reparse points are not followed, so every entry stays
// inside the root.
void IndexXaml_nolock(const std::wstring& root)
{
    g_indexRoot = root;
    g_indexedXaml.clear();
    std::vector<std::wstring> pending{ L"" };
    size_t visited = 0;
    while (!pending.empty() && visited < 200000) {
        const std::wstring relative = pending.back();
        pending.pop_back();
        WIN32_FIND_DATAW data{};
        HANDLE find = FindFirstFileExW((root + L"\\" + relative + L"*").c_str(), FindExInfoBasic, &data,
            FindExSearchNameMatch, nullptr, FIND_FIRST_EX_LARGE_FETCH);
        if (find == INVALID_HANDLE_VALUE) continue;
        do {
            ++visited;
            const std::wstring name = data.cFileName;
            if (name == L"." || name == L".." || (data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) continue;
            if (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
                if (!IsSkippedSourceFolder(name.c_str())) pending.push_back(relative + name + L"\\");
            } else if (name.size() > 5 && _wcsicmp(name.c_str() + name.size() - 5, L".xaml") == 0) {
                std::wstring entry = relative + name;
                for (auto& c : entry) c = c == L'\\' ? L'/' : static_cast<wchar_t>(towlower(c));
                g_indexedXaml.push_back(std::move(entry));
            }
        } while (FindNextFileW(find, &data));
        FindClose(find);
    }
}

// The one .xaml file under the root whose path ends with `relative`, as the CLI resolves a comment's file; empty when
// none or several do.
std::wstring FindXamlUnderRoot(const std::wstring& root, const std::wstring& relative)
{
    std::wstring key = relative;
    for (auto& c : key) c = static_cast<wchar_t>(towlower(c));
    if (key.size() <= 5 || key.compare(key.size() - 5, 5, L".xaml") != 0 || key.find(L"..") != std::wstring::npos) return L"";
    const std::wstring suffix = L"/" + key;
    std::wstring found;
    AcquireSRWLockExclusive(&g_indexLock);
    if (g_indexRoot != root) IndexXaml_nolock(root);
    int matches = 0;
    for (const auto& entry : g_indexedXaml) {
        if (entry == key || (entry.size() > suffix.size() && entry.compare(entry.size() - suffix.size(), suffix.size(), suffix) == 0)) {
            if (++matches > 1) break;
            found = entry;
        }
    }
    ReleaseSRWLockExclusive(&g_indexLock);
    if (matches != 1) return L"";
    std::wstring path;
    return DevToolsSourcePath_ResolveExisting(root, L"ms-appx:///" + found, &path) ? path : L"";
}

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
    return DevToolsSourcePath_ResolveExisting(g_sourceRoot, kPrefix + rel, &path) ? path : FindXamlUnderRoot(g_sourceRoot, rel);
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
std::wstring ElementSpan(const std::vector<std::wstring>& lines, unsigned int line, unsigned int column,
                         size_t* endLine = nullptr, size_t* endCol = nullptr)
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
        size_t shift = begin;   // piece[c] is l[c + shift], less the two indent spaces added below
        bool indented = false;
        if (i != startLine && quote == 0) {
            size_t lead = 0;
            while (lead < piece.size() && (piece[lead] == L' ' || piece[lead] == L'\t')) ++lead;
            piece = std::wstring(2, L' ') + piece.substr(lead);
            shift = lead; indented = true;
        }
        for (size_t c = 0; c < piece.size(); ++c) {
            const wchar_t ch = piece[c];
            if (quote != 0) { if (ch == quote) quote = 0; continue; }
            if (ch == L'"' || ch == L'\'') { quote = ch; continue; }
            if (ch == L'>') {
                if (endLine) *endLine = i;
                if (endCol) *endCol = c + 1 + shift - (indented ? 2 : 0);
                span += piece.substr(0, c + 1);
                return span;
            }
        }
        span += piece;
        if (i + 1 < lines.size()) span += L'\n';
    }
    return L""; // unterminated markup is not an available declaration
}

// The direct children of an element whose opening tag ends just before lines[line][col]: the names set as
// <Type.Property> children, and whether anything else (text or an element) is its content. False when the body
// cannot be followed to its closing tag.
bool ScanElementBody(const std::vector<std::wstring>& lines, size_t line, size_t col,
                     std::vector<std::wstring>* properties, bool* content)
{
    std::wstring body;
    for (size_t i = line; i < lines.size() && i < line + 4000; ++i) {
        body += i == line ? lines[i].substr((std::min)(col, lines[i].size())) : lines[i];
        body += L'\n';
    }
    int depth = 0;
    for (size_t c = 0; c < body.size(); ++c) {
        const wchar_t ch = body[c];
        if (ch != L'<') {
            if (depth == 0 && ch != L' ' && ch != L'\t' && ch != L'\r' && ch != L'\n') *content = true;
            continue;
        }
        const wchar_t* skip = body.compare(c, 4, L"<!--") == 0 ? L"-->" :
                              body.compare(c, 9, L"<![CDATA[") == 0 ? L"]]>" :
                              body.compare(c, 2, L"<?") == 0 ? L"?>" : nullptr;
        if (skip) {
            if (depth == 0 && body[c + 1] == L'!' && body[c + 2] == L'[') *content = true;
            const size_t end = body.find(skip, c + 2);
            if (end == std::wstring::npos) return false;
            c = end + wcslen(skip) - 1;
            continue;
        }
        const bool closing = c + 1 < body.size() && body[c + 1] == L'/';
        if (closing && depth == 0) return true;
        const size_t nameStart = c + (closing ? 2 : 1);
        size_t nameEnd = nameStart;
        while (nameEnd < body.size() && !wcschr(L" \t\r\n/>", body[nameEnd])) ++nameEnd;
        std::wstring tag = body.substr(nameStart, nameEnd - nameStart);
        tag = tag.substr(tag.find_last_of(L':') + 1);
        if (!closing && depth == 0) {
            const size_t dot = tag.find_last_of(L'.');
            if (dot == std::wstring::npos) *content = true;
            else { properties->push_back(tag.substr(dot + 1)); properties->push_back(tag); }
        }
        wchar_t quote = 0, previous = 0;
        size_t end = nameEnd;
        for (; end < body.size(); ++end) {
            const wchar_t tch = body[end];
            if (quote) { if (tch == quote) quote = 0; continue; }
            if (tch == L'"' || tch == L'\'') { quote = tch; continue; }
            if (tch == L'>') break;
            if (!wcschr(L" \t\r\n", tch)) previous = tch;
        }
        if (end >= body.size()) return false;
        if (closing) --depth;
        else if (previous != L'/') ++depth;
        c = end;
    }
    return false;
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
    AcquireSRWLockExclusive(&g_indexLock);
    g_indexRoot.clear();
    g_indexedXaml.clear();
    ReleaseSRWLockExclusive(&g_indexLock);
    // Parsed files stay cached by path, size and write time; only the project indexes are built again.
    std::lock_guard<std::mutex> guard(g_declarations->lock);
    g_declarations->projects.clear();
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

namespace {

bool IsDirectory(const std::wstring& path)
{
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) && !(attributes & FILE_ATTRIBUTE_REPARSE_POINT);
}

std::wstring ParentFolder(const std::wstring& path)
{
    const size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring() : path.substr(0, slash);
}

// The only .csproj in the nearest folder above `file` that has one; empty when there is none or several.
std::wstring ProjectFileFor(const std::wstring& file)
{
    for (std::wstring folder = ParentFolder(file); folder.size() > 2; folder = ParentFolder(folder)) {
        WIN32_FIND_DATAW data{};
        HANDLE find = FindFirstFileW((folder + L"\\*.csproj").c_str(), &data);
        if (find == INVALID_HANDLE_VALUE) continue;
        std::wstring project;
        int count = 0;
        do { if (!(data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) { ++count; project = folder + L"\\" + data.cFileName; } }
        while (FindNextFileW(find, &data));
        FindClose(find);
        return count == 1 ? project : std::wstring();
    }
    return {};
}

std::wstring AttributeValue(const std::wstring& tag, const wchar_t* name)
{
    const std::wstring key = std::wstring(name) + L"=\"";
    const size_t at = tag.find(key);
    if (at == std::wstring::npos || (at > 0 && !iswspace(tag[at - 1]))) return {};
    const size_t end = tag.find(L'"', at + key.size());
    return end == std::wstring::npos ? std::wstring() : tag.substr(at + key.size(), end - at - key.size());
}

// The project's folder, then each referenced project's folder (one level; analyzers excluded).
std::vector<std::wstring> ProjectFolders(const std::wstring& project)
{
    std::vector<std::wstring> folders{ParentFolder(project)};
    std::vector<std::wstring> lines;
    std::wstring hash;
    if (!ReadLines(project, &lines, &hash)) return folders;
    std::wstring text;
    for (const auto& line : lines) { text += line; text += L'\n'; }
    for (size_t at = text.find(L"<ProjectReference"); at != std::wstring::npos; at = text.find(L"<ProjectReference", at + 1)) {
        const size_t end = text.find(L'>', at);
        if (end == std::wstring::npos) break;
        const std::wstring tag = text.substr(at, end - at);
        const std::wstring include = AttributeValue(tag, L"Include");
        if (include.empty() || include.find(L"$(") != std::wstring::npos ||
            _wcsicmp(AttributeValue(tag, L"OutputItemType").c_str(), L"Analyzer") == 0 ||
            _wcsicmp(AttributeValue(tag, L"ReferenceOutputAssembly").c_str(), L"false") == 0) continue;
        std::wstring combined = include.size() > 1 && include[1] == L':' ? include : folders.front() + L"\\" + include;
        std::replace(combined.begin(), combined.end(), L'/', L'\\');
        wchar_t full[MAX_PATH * 2]{};
        if (!GetFullPathNameW(combined.c_str(), MAX_PATH * 2, full, nullptr)) continue;
        const DWORD attributes = GetFileAttributesW(full);
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        const std::wstring referenced = ParentFolder(full);
        bool known = false;
        for (const auto& folder : folders)
            known = known || _wcsicmp(folder.c_str(), referenced.c_str()) == 0 ||
                (referenced.size() > folder.size() && _wcsnicmp(referenced.c_str(), folder.c_str(), folder.size()) == 0 &&
                 referenced[folder.size()] == L'\\');
        if (!known) folders.push_back(referenced);
    }
    return folders;
}

struct SourceFile
{
    std::wstring path;
    FILETIME written{};
    unsigned long long size = 0;
};

void CollectSources(const std::wstring& folder, std::vector<SourceFile>* files, size_t limit, int depth = 0)
{
    if (files->size() >= limit || depth > 32) return;
    WIN32_FIND_DATAW data{};
    HANDLE find = FindFirstFileW((folder + L"\\*").c_str(), &data);
    if (find == INVALID_HANDLE_VALUE) return;
    do {
        const std::wstring name = data.cFileName;
        if (name == L"." || name == L".." || (data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) continue;
        if (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            static const wchar_t* const kSkipped[] = {L"bin", L"obj", L".git", L".vs", L"node_modules", L"packages", L"AppPackages"};
            bool skipped = false;
            for (const wchar_t* s : kSkipped) skipped = skipped || _wcsicmp(name.c_str(), s) == 0;
            if (!skipped) CollectSources(folder + L"\\" + name, files, limit, depth + 1);
        } else if (name.size() > 3 && _wcsicmp(name.c_str() + name.size() - 3, L".cs") == 0) {
            files->push_back({folder + L"\\" + name, data.ftLastWriteTime,
                (static_cast<unsigned long long>(data.nFileSizeHigh) << 32) | data.nFileSizeLow});
        }
    } while (files->size() < limit && FindNextFileW(find, &data));
    FindClose(find);
}

// Reads the declarations of the project's files (the file alone when it is in no project), parsing only the files
// whose size or write time changed since they were last parsed. Takes no lock but the cache's, and that only briefly.
DeclarationIndex BuildDeclarations(DeclarationCache& cache, const std::wstring& project, const std::wstring& file)
{
    std::vector<SourceFile> files;
    if (project.empty()) {
        WIN32_FILE_ATTRIBUTE_DATA attributes{};
        if (GetFileAttributesExW(file.c_str(), GetFileExInfoStandard, &attributes))
            files.push_back({file, attributes.ftLastWriteTime,
                (static_cast<unsigned long long>(attributes.nFileSizeHigh) << 32) | attributes.nFileSizeLow});
    } else {
        for (const auto& folder : ProjectFolders(project)) if (IsDirectory(folder)) CollectSources(folder, &files, 4096);
    }
    auto index = std::make_shared<DevToolsCSharpArguments::Declarations>();
    for (const auto& source : files) {
        DeclarationIndex declarations;
        {
            std::lock_guard<std::mutex> guard(cache.lock);
            const auto known = cache.files.find(source.path);
            if (known != cache.files.end() && known->second.size == source.size &&
                CompareFileTime(&known->second.written, &source.written) == 0)
                declarations = known->second.declarations;
        }
        if (!declarations) {
            auto parsed = std::make_shared<DevToolsCSharpArguments::Declarations>();
            std::vector<std::wstring> lines;
            std::wstring hash;
            if (ReadLines(source.path, &lines, &hash)) parsed->Add(lines);
            g_csharpFilesParsed.fetch_add(1, std::memory_order_relaxed);
            declarations = parsed;
            std::lock_guard<std::mutex> guard(cache.lock);
            if (cache.files.size() >= 65536) cache.files.clear();
            cache.files[source.path] = DeclarationCache::File{source.written, source.size, declarations};
        }
        index->Merge(*declarations);
    }
    return index;
}

struct DeclarationJob
{
    std::shared_ptr<DeclarationCache> cache;
    std::wstring key, project, file;
    HMODULE module = nullptr;
};

void CALLBACK BuildDeclarationsInBackground(PTP_CALLBACK_INSTANCE instance, void* context)
{
    std::unique_ptr<DeclarationJob> job(static_cast<DeclarationJob*>(context));
    // The engine stays loaded until this returns.
    if (job->module) FreeLibraryWhenCallbackReturns(instance, job->module);
    DeclarationIndex index;
    try { index = BuildDeclarations(*job->cache, job->project, job->file); } catch (...) {}
    try {
        std::lock_guard<std::mutex> guard(job->cache->lock);
        auto& entry = job->cache->projects[job->key];
        entry.building = false;
        if (index) { entry.index = std::move(index); entry.built = GetTickCount64(); }
    } catch (...) {}
}

// Under cache->lock: starts a background build of `key`'s index unless one is running.
void ScheduleDeclarations_locked(const std::shared_ptr<DeclarationCache>& cache, DeclarationCache::Project& entry,
    const std::wstring& key, const std::wstring& project, const std::wstring& file)
{
    if (entry.building) return;
    auto job = std::make_unique<DeclarationJob>(DeclarationJob{cache, key, project, file, nullptr});
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(&BuildDeclarationsInBackground), &job->module);
    const HMODULE module = job->module;
    if (TrySubmitThreadpoolCallback(&BuildDeclarationsInBackground, job.get(), nullptr)) {
        job.release();
        entry.building = true;
    } else if (module) {
        FreeLibrary(module);
    }
}

// The declarations of the project a C# file belongs to and of the projects it references (the file alone when it is
// in no project), rebuilt in the background once they are a few seconds old so edits are seen. Null when there is no
// index yet and `mode` may not build one here.
DeclarationIndex ProjectDeclarations(const std::wstring& file, DevToolsCSharpIndex mode)
{
    const std::shared_ptr<DeclarationCache> cache = g_declarations;
    const std::wstring project = ProjectFileFor(file);
    const std::wstring key = project.empty() ? file : project;
    DeclarationIndex index;
    {
        std::lock_guard<std::mutex> guard(cache->lock);
        auto known = cache->projects.find(key);
        if (known == cache->projects.end()) {
            if (cache->projects.size() >= 16) cache->projects.clear();
            known = cache->projects.emplace(key, DeclarationCache::Project{}).first;
        }
        index = known->second.index;
        const bool stale = !index || GetTickCount64() - known->second.built >= 5000;
        // A Build caller with no index builds one itself below instead.
        if (stale && (index || mode == DevToolsCSharpIndex::Cached))
            ScheduleDeclarations_locked(cache, known->second, key, project, file);
    }
    if (index || mode == DevToolsCSharpIndex::Cached) return index;
    index = BuildDeclarations(*cache, project, file);
    std::lock_guard<std::mutex> guard(cache->lock);
    auto& entry = cache->projects[key];
    entry.index = index;
    entry.built = GetTickCount64();
    return index;
}

} // namespace

bool DevToolsAuthored_ReadCSharp(const std::wstring& path, unsigned int line, unsigned int column, std::wstring* out,
                                 DevToolsCSharpIndex index, const std::wstring& kind, std::wstring* site, size_t* lineCount)
{
    if (!out) return false;
    out->clear();
    if (site) site->clear();
    if (lineCount) *lineCount = 0;
    std::vector<std::wstring> lines;
    std::wstring hash;
    if (path.empty() || !ReadLines(path, &lines, &hash)) return false;
    if (lineCount) *lineCount = lines.size();
    if (site) {
        AcquireSRWLockShared(&g_lock);
        const unsigned long long built = g_buildTimeUnix;
        ReleaseSRWLockShared(&g_lock);
        const unsigned long long written = built ? LastWriteUnix(path) : 0;
        // 2s slack for filesystem timestamp granularity, as for XAML.
        *site = DevToolsReactorNames::SiteState(lines, line, column, kind, written != 0 && written > built + 2);
    }
    // A string passed to a password parameter is redacted even when the statement never says "password".
    if (const auto declarations = ProjectDeclarations(path, index)) {
        *out = DevToolsCSharpSource::Statement(DevToolsCSharpArguments::RedactLines(lines, *declarations), line, column);
        DevToolsCSharpSource::Redact(*out);
    } else {
        // Which parameters take a password is not known until the index is built, so no literal is shown until then.
        *out = DevToolsCSharpSource::Statement(lines, line, column);
        DevToolsCSharpSource::RedactLiterals(*out, [](size_t) { return true; });
    }
    return !out->empty();
}

unsigned long long DevToolsAuthored_CSharpFilesParsed()
{
    return g_csharpFilesParsed.load(std::memory_order_relaxed);
}

std::wstring DevToolsAuthored_CSharpName(const std::wstring& path, unsigned int line, unsigned int column,
    const std::wstring& kind)
{
    if (path.empty() || line == 0) return L"";
    // Every element of a describe asks, so each file is read once until it changes.
    struct Cached { FILETIME written{}; std::vector<std::wstring> lines; };
    static std::mutex lock;
    static std::map<std::wstring, Cached> files;
    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes)) return L"";
    std::lock_guard<std::mutex> guard(lock);
    auto known = files.find(path);
    if (known == files.end() || CompareFileTime(&known->second.written, &attributes.ftLastWriteTime) != 0) {
        Cached cached;
        std::wstring hash;
        if (!ReadLines(path, &cached.lines, &hash)) return L"";
        cached.written = attributes.ftLastWriteTime;
        if (files.size() >= 256) files.clear();
        known = files.insert_or_assign(path, std::move(cached)).first;
    }
    const auto& lines = known->second.lines;
    if (line > lines.size()) return L"";
    return DevToolsReactorNames::FromSource(lines[line - 1], column, line >= 2 ? lines[line - 2] : std::wstring(), kind);
}

bool DevToolsAuthored_CSharpHookNames(const std::wstring& path, unsigned int line, unsigned int column,
    const std::vector<std::wstring>& kinds, std::vector<std::wstring>* names)
{
    std::vector<std::wstring> lines;
    std::wstring hash;
    if (!names || path.empty() || !ReadLines(path, &lines, &hash)) return false;
    return DevToolsCSharpSource::HookNames(lines, line, column, kinds, names);
}
std::wstring DevToolsAuthored_FileHash(const std::wstring& path, const std::wstring& algorithm)
{
    const BCRYPT_ALG_HANDLE alg = algorithm == L"SHA256" ? BCRYPT_SHA256_ALG_HANDLE
                                : algorithm == L"SHA1" ? BCRYPT_SHA1_ALG_HANDLE : nullptr;
    std::vector<char> bytes;
    if (!alg || path.empty() || !ReadBytes(path, 64u * 1024 * 1024, &bytes)) return L"";
    unsigned char hash[32]{};
    const ULONG size = algorithm == L"SHA256" ? 32 : 20;
    if (BCryptHash(alg, nullptr, 0, reinterpret_cast<PUCHAR>(bytes.data()), static_cast<ULONG>(bytes.size()), hash, size) < 0)
        return L"";
    std::wstring result;
    constexpr wchar_t hex[] = L"0123456789abcdef";
    for (ULONG i = 0; i < size; i++) { result += hex[hash[i] >> 4]; result += hex[hash[i] & 15]; }
    return result;
}

namespace {

bool IsControlTemplateName(const std::wstring& s, size_t at)
{
    static constexpr wchar_t kName[] = L"ControlTemplate";
    constexpr size_t length = sizeof(kName) / sizeof(kName[0]) - 1;
    if (s.compare(at, length, kName) != 0) return false;
    return at + length == s.size() || iswspace(s[at + length]) || s[at + length] == L'>' || s[at + length] == L'/';
}

// Counts <ControlTemplate> nesting before line/column (1-based; column 0 means the line start), skipping comments.
bool InsideControlTemplate(const std::vector<std::wstring>& lines, unsigned int line, unsigned int column)
{
    int depth = 0;
    bool comment = false, opening = false;
    wchar_t quote = 0;
    for (size_t l = 0; l < lines.size() && l < line; ++l) {
        const std::wstring& s = lines[l];
        const size_t end = l + 1 == line ? std::min<size_t>(column ? column - 1 : 0, s.size()) : s.size();
        for (size_t c = 0; c < end; ++c) {
            if (comment) {
                if (s.compare(c, 3, L"-->") == 0) { comment = false; c += 2; }
            } else if (opening) {
                if (quote) { if (s[c] == quote) quote = 0; }
                else if (s[c] == L'"' || s[c] == L'\'') quote = s[c];
                else if (s[c] == L'>') { opening = false; if (c > 0 && s[c - 1] == L'/') --depth; }
            } else if (s[c] == L'<') {
                if (s.compare(c, 4, L"<!--") == 0) { comment = true; c += 3; }
                else if (IsControlTemplateName(s, c + 1)) { ++depth; opening = true; }
                else if (c + 1 < s.size() && s[c + 1] == L'/' && IsControlTemplateName(s, c + 2)) --depth;
            }
        }
    }
    return depth > 0;
}

}

bool DevToolsAuthored_IsControlTemplatePart(const std::wstring& fileUri, unsigned int line, unsigned int column)
{
    if (line == 0) return false;
    const std::wstring path = UriToDiskPath(fileUri);
    if (path.empty()) return false;
    const CachedFile& file = GetFile(path);
    return file.ok && !file.stale && InsideControlTemplate(file.lines, line, column);
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

// Whether `type` gives itself `name`: an x:Class root in this build's XAML that carries that x:Name.
static bool SelfNamedType(const std::wstring& type, const std::wstring& name)
{
    if (type.empty() || name.empty()) return false;
    for (const auto& file : g_coordinates)
        for (const auto& element : file.second.elements)
            if (element.GetString(L"runtimeClass") == type && element.GetString(L"name") == name) return true;
    return false;
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
        // A declaration without a name does not contradict a runtime name that the type gives itself: a UserControl
        // with x:Name on its own x:Class root reports that name at every place it is used.
        const std::wstring declaredName = selected ? selected->GetString(L"name") : std::wstring();
        if (!selected || type.empty() ||
            (selected->GetString(L"type") != type.substr(type.find_last_of(L'.') + 1) &&
                selected->GetString(L"runtimeClass") != type) ||
            (declaredName != name && !(declaredName.empty() && SelfNamedType(type, name))))
            return DevToolsAuthoredState::Unavailable;
        line = static_cast<unsigned int>(selected->GetInt(L"line", 0));
        column = static_cast<unsigned int>(selected->GetInt(L"column", 0));
        if (authoredLocation) authoredLocation->endLine = static_cast<unsigned int>(selected->GetInt(L"endLine", 0));
        if (authoredLocation && coordinates->likely) {
            authoredLocation->parentLine = static_cast<unsigned int>(selected->GetInt(L"parentLine", 0));
            authoredLocation->parentType = selected->GetString(L"parentType");
            authoredLocation->parentName = selected->GetString(L"parentName");
            authoredLocation->evidence = coordinates->evidence;
        }
    }
    size_t spanEndLine = 0, spanEndCol = 0;
    std::wstring span = ElementSpan(f.lines, line, column, &spanEndLine, &spanEndCol);
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
        const bool declared = FindAttribute(span, L"x:Name", &authoredName) || FindAttribute(span, L"Name", &authoredName);
        if (declared ? authoredName != name : !SelfNamedType(type, name)) return DevToolsAuthoredState::Unavailable;
    }
    const bool selfClosing = span.size() >= 2 && span.compare(span.size() - 2, 2, L"/>") == 0;
    if (out) *out = std::move(span);
    if (authoredLocation) {
        authoredLocation->line = line; authoredLocation->column = column;
        authoredLocation->mapped = coordinates != nullptr;
        authoredLocation->sourceFile = coordinates ? coordinates->source : L"";
        authoredLocation->bodyRead = selfClosing ||
            ScanElementBody(f.lines, spanEndLine, spanEndCol, &authoredLocation->bodyProperties,
                            &authoredLocation->bodyContent);
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

namespace {
// A source file's hash, recomputed only when its size or write time changes: a tree of thousands of elements asks
// about the same few files.
struct SourceStamp { unsigned long long write = 0, size = 0; std::wstring hash; };
std::map<std::wstring, SourceStamp> g_sourceStamps;
SRWLOCK g_stampLock = SRWLOCK_INIT;

std::wstring CurrentSourceHash(const std::wstring& path)
{
    WIN32_FILE_ATTRIBUTE_DATA data{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data)) return L"";
    const unsigned long long write = (static_cast<unsigned long long>(data.ftLastWriteTime.dwHighDateTime) << 32) |
        data.ftLastWriteTime.dwLowDateTime;
    const unsigned long long size = (static_cast<unsigned long long>(data.nFileSizeHigh) << 32) | data.nFileSizeLow;
    AcquireSRWLockShared(&g_stampLock);
    const auto known = g_sourceStamps.find(path);
    if (known != g_sourceStamps.end() && known->second.write == write && known->second.size == size) {
        std::wstring hash = known->second.hash;
        ReleaseSRWLockShared(&g_stampLock);
        return hash;
    }
    ReleaseSRWLockShared(&g_stampLock);
    std::vector<char> bytes;
    if (!ReadBytes(path, 4 << 20, &bytes)) return L"";
    SourceStamp stamp{ write, size, HashBytes(bytes) };
    AcquireSRWLockExclusive(&g_stampLock);
    g_sourceStamps[path] = stamp;
    ReleaseSRWLockExclusive(&g_stampLock);
    return stamp.hash;
}
}

bool DevToolsAuthored_Locate(const std::wstring& fileUri, unsigned int line, unsigned int column,
    const std::wstring& type, const std::wstring& name, DevToolsAuthoredLocation* location)
{
    if (location) *location = {};
    if (!location || fileUri.empty() || !line || !column || type.empty() || !g_coordinateError.empty()) return false;
    const auto key = ResourceKey(fileUri);
    if (g_coordinateExclusions.count(key)) return false;
    const auto mapping = g_coordinates.find(key);
    if (mapping == g_coordinates.end()) return false;
    const CoordinateFile& coordinates = mapping->second;
    if (!coordinates.payloadVerified || coordinates.likely || coordinates.advisory) return false;
    const std::wstring path = UriToDiskPath(L"ms-appx:///" + coordinates.source);
    if (path.empty() || g_excludedSourcePaths.count(ResourceKey(path)) ||
        CurrentSourceHash(path) != coordinates.sourceHash) return false;
    const DevToolsJson* selected = nullptr;
    for (const auto& element : coordinates.elements) {
        if (line < static_cast<unsigned int>(element.GetInt(L"line", 0)) ||
            line > static_cast<unsigned int>(element.GetInt(L"endLine", 0))) continue;
        if (selected) return false;
        selected = &element;
    }
    const std::wstring declaredName = selected ? selected->GetString(L"name") : std::wstring();
    if (!selected ||
        (selected->GetString(L"type") != type.substr(type.find_last_of(L'.') + 1) &&
            selected->GetString(L"runtimeClass") != type) ||
        (declaredName != name && !(declaredName.empty() && SelfNamedType(type, name))))
        return false;
    location->line = static_cast<unsigned int>(selected->GetInt(L"line", 0));
    location->endLine = static_cast<unsigned int>(selected->GetInt(L"endLine", 0));
    location->column = static_cast<unsigned int>(selected->GetInt(L"column", 0));
    location->mapped = true;
    location->sourceFile = coordinates.source;
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
