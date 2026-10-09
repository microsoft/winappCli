// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#include "DevToolsQuery.h"
#include <cstdio>

int RunQueryTests()
{
    using namespace DevToolsQuery;
    int failures = 0, checks = 0;
    const auto check = [&](bool condition, const char* label) {
        ++checks;
        if (!condition) { ++failures; std::printf("  FAIL query: %s\n", label); }
    };
    Predicate predicate; std::wstring error;
    check(Parse(L"Text==", predicate, error) && predicate.literal.empty(), "empty string literal");
    check(Parse(L"AutomationProperties.Name*=Search", predicate, error), "attached property token");
    for (const auto* bad : {L"", L"Text", L"==x", L"Text=x", L"Text!x", L"Text**x", L".Text==x", L"Text..Value==x",
        L"FontSize>>20", L"FontSize<>20", L"FontSize><20", L"FontSize>==20", L"FontSize<<20", L"FontSize<*20", L"FontSize<=!20"})
        check(!Parse(bad, predicate, error), "invalid grammar refused");
    for (const auto* literal : {L">20", L"<20", L"=", L"!=", L"\"quoted\""}) {
        for (const auto* op : {L"==", L"!=", L"*="}) {
            check(Parse(L"Text" + std::wstring(op) + literal, predicate, error) && predicate.literal == literal,
                "string literal punctuation and quotes preserved");
        }
    }
    DevToolsReadProp row;
    row.name = L"Text"; row.type = L"Windows.Foundation.String"; row.value = L"Save";
    check(Compare(nullptr, {L"Text", L"!=", L"Save"}).verdict == Verdict::Unevaluated, "missing != never matches");
    check(Compare(&row, {L"Text", L"*=", L"av"}).verdict == Verdict::Match, "ordinal contains");
    check(Compare(&row, {L"Text", L"*=", L"AV"}).verdict == Verdict::NoMatch, "case sensitive contains");
    check(Compare(&row, {L"Text", L">", L"1"}).verdict == Verdict::Unevaluated, "no string ordering");
    row.value = L"";
    check(Compare(&row, {L"Text", L"==", L""}).verdict == Verdict::Match, "empty is comparable");
    for (const auto* state : {L"null", L"unset", L"unresolved", L"unreadable", L"unavailable"}) {
        row.valueState = state;
        check(Compare(&row, {L"Text", L"!=", L"Save"}).verdict == Verdict::Unevaluated, "unavailable state never matches");
    }
    row.valueState = L"binding"; row.value = L"Today"; row.binding = L"{Binding Heading}";
    check(Compare(&row, {L"Text", L"==", L"Today"}).verdict == Verdict::Match, "binding compares effective value");
    check(Compare(&row, {L"Text", L"==", L"{Binding Heading}"}).verdict == Verdict::NoMatch, "not the binding expression");
    row.valueState.clear(); row.name = L"FontSize"; row.type = L"Double"; row.value = L"24";
    check(Compare(&row, {L"FontSize", L">=", L"20"}).verdict == Verdict::Match, "numeric order");
    check(Compare(&row, {L"FontSize", L"*=", L"2"}).verdict == Verdict::Unevaluated, "no numeric contains");
    for (const auto* bad : {L"NaN", L"inf", L"1e400", L"0x12", L" 20", L"1,5", L"2x"})
        check(Compare(&row, {L"FontSize", L"==", bad}).verdict == Verdict::Unevaluated, "finite invariant decimal required");
    row.type = L"UInt64"; row.value = L"18446744073709551615";
    check(Compare(&row, {L"Count", L"==", L"18446744073709551615"}).verdict == Verdict::Match, "uint64 exact max");
    check(Compare(&row, {L"Count", L">", L"18446744073709551614"}).verdict == Verdict::Match, "uint64 no double rounding");
    check(Compare(&row, {L"Count", L"==", L"18446744073709551616"}).verdict == Verdict::Unevaluated, "uint64 overflow refused");
    row.type = L"Int64"; row.value = L"-9223372036854775808";
    check(Compare(&row, {L"Count", L"<", L"-9223372036854775807"}).verdict == Verdict::Match, "signed min exact");
    row.type = L"Boolean"; row.value = L"False";
    check(Compare(&row, {L"IsEnabled", L"==", L"false"}).verdict == Verdict::Match, "boolean equality");
    check(Compare(&row, {L"IsEnabled", L"==", L"off"}).verdict == Verdict::Unevaluated, "invalid boolean refused");
    row.type = L"Microsoft.UI.Xaml.Visibility"; row.value = L"Visible"; row.enumValues = {L"Visible", L"Collapsed"};
    check(Compare(&row, {L"Visibility", L"==", L"Visible"}).verdict == Verdict::Match, "runtime enum");
    check(Compare(&row, {L"Visibility", L"==", L"Hidden"}).verdict == Verdict::Unevaluated, "unknown enum member refused");
    row.enumValues.clear(); row.type = L"Microsoft.UI.Xaml.Controls.StackPanel"; row.value = L"Save";
    check(Compare(&row, {L"Content", L"*=", L"Save"}).verdict == Verdict::Unevaluated, "object display text not scalar");
    row.name = L"Text"; row.type = L"String"; row.value = L"Today";
    check(Match({row}, {{L"Text", L"==", L"Today"}, {L"Text", L"*=", L"day"}}).verdict == Verdict::Match, "AND both true");
    check(Match({row}, {{L"Text", L"==", L"Today"}, {L"Text", L"==", L"Upcoming"}}).verdict == Verdict::NoMatch, "AND false");
    check(Match({row}, {{L"Text", L"==", L"Upcoming"}, {L"Missing", L"==", L"x"}}).verdict == Verdict::NoMatch, "false excludes despite missing predicate");
    check(Match({row, row}, {{L"Text", L"==", L"Today"}}).verdict == Verdict::Unevaluated, "duplicate property metadata refused");
    DevToolsReadProp id;
    id.name = L"AutomationProperties.AutomationId"; id.type = L"String"; id.value = L"Other";
    for (bool reversed : {false, true}) {
        std::vector<Predicate> predicates = {{id.name, L"==", L"SpacesHeading"}, {L"Text", L"==", L"Spaces"}};
        if (reversed) std::reverse(predicates.begin(), predicates.end());
        for (const auto* state : {L"null", L"unset", L"unresolved", L"unreadable", L"unavailable"}) {
            row.valueState = state;
            id.value = L"Other";
            check(Match({id, row}, predicates).verdict == Verdict::NoMatch, "ID mismatch excludes unknown Text in either order");
            id.value = L"SpacesHeading";
            check(Match({id, row}, predicates).verdict == Verdict::Unevaluated, "matching ID cannot exclude unknown Text");
            id.valueState = L"unavailable";
            check(Match({id, row}, predicates).verdict == Verdict::Unevaluated, "both unknown remain unknown");
            id.valueState.clear();
        }
        id.value = L"Other";
        check(Match({id}, predicates).verdict == Verdict::NoMatch, "ID mismatch excludes missing Text");
        check(Match({id, row, row}, predicates).reason == L"ambiguous-property", "duplicate Text blocks despite false ID");
        check(Match({id, id, row}, predicates).reason == L"ambiguous-property", "duplicate ID blocks despite unknown Text");
        row.valueState.clear(); row.value = L"Different";
        check(Match({id, id, row}, predicates).reason == L"ambiguous-property", "duplicate ID blocks despite false Text");
        id.value = L"SpacesHeading"; row.value = L"Spaces";
        check(Match({id, row}, predicates).verdict == Verdict::Match, "both true match in either order");
    }
    std::printf("Query predicates: %d checks, %d failures\n", checks, failures);
    return failures;
}
