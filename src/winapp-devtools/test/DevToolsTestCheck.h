// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// The native tests' one way to assert: prints "ok" or "FAIL" for each check and counts the failures, which the file's
// Run*Tests returns. Every test file that includes this has its own count.

#pragma once

#include <cstdio>
#include <string>

namespace {

int g_testFailures = 0;

[[maybe_unused]] void Check(bool cond, const char* what)
{
    if (!cond) { ++g_testFailures; std::printf("  FAIL  %s\n", what); }
    else       {                   std::printf("  ok    %s\n", what); }
}

[[maybe_unused]] void CheckEq(long long got, long long want, const char* what)
{
    if (got != want) { ++g_testFailures; std::printf("  FAIL  %s (want %lld, got %lld)\n", what, want, got); }
    else             {                   std::printf("  ok    %s (%lld)\n", what, got); }
}

[[maybe_unused]] void CheckEqD(double got, double want, const char* what)
{
    const double d = got - want;
    if (d > 0.001 || d < -0.001) { ++g_testFailures; std::printf("  FAIL  %s (want %.2f, got %.2f)\n", what, want, got); }
    else                         {                   std::printf("  ok    %s (%.2f)\n", what, got); }
}

[[maybe_unused]] void CheckEqW(const std::wstring& got, const std::wstring& want, const char* what)
{
    if (got != want) { ++g_testFailures; std::printf("  FAIL  %s (want \"%ls\", got \"%ls\")\n", what, want.c_str(), got.c_str()); }
    else             {                   std::printf("  ok    %s (\"%ls\")\n", what, got.c_str()); }
}

} // namespace
