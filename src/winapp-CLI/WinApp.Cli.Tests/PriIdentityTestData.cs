// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

internal static class PriIdentityTestData
{
    // Reduced from the successful WinUI fidelity fixture's MakePri /dt detailed dump.
    // Includes its exact qualifiers, decisions, localized/library strings, qualified paths,
    // and compiled App.xbf bytes. Only omitted resources and their scope/item counts differ.
    internal const string Original = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <PriInfo>
          <PriHeader>
            <WindowsEnvironment name="WinCore" version="1.2" checksum="1912541329"/>
            <AutoMerge>false</AutoMerge>
            <IsDeploymentMergeable>true</IsDeploymentMergeable>
            <TargetOS version="10.0.0"/>
            <ReverseMap>false</ReverseMap>
          </PriHeader>
          <QualifierInfo>
            <Qualifiers>
              <Qualifier name="Language" value="EN-US" priority="700" scoreAsDefault="1.0" index="1"/>
              <Qualifier name="Language" value="FR-FR" priority="700" scoreAsDefault="0.0" index="2"/>
              <Qualifier name="Scale" value="200" priority="200" scoreAsDefault="1.0" index="3"/>
              <Qualifier name="Scale" value="100" priority="200" scoreAsDefault="0.5" index="4"/>
              <Qualifier name="Contrast" value="BLACK" priority="400" scoreAsDefault="0.0" index="5"/>
              <Qualifier name="TargetSize" value="24" priority="300" scoreAsDefault="0.5" index="6"/>
              <Qualifier name="AlternateForm" value="UNPLATED" priority="100" scoreAsDefault="0.0" index="7"/>
              <Qualifier name="TargetSize" value="48" priority="300" scoreAsDefault="0.5" index="8"/>
              <Qualifier name="AlternateForm" value="LIGHTUNPLATED" priority="100" scoreAsDefault="0.0" index="9"/>
            </Qualifiers>
            <QualifierSets>
              <QualifierSet index="1">
                <Qualifier name="Language" value="EN-US" priority="700" scoreAsDefault="1.0" index="1"/>
              </QualifierSet>
              <QualifierSet index="2">
                <Qualifier name="Language" value="FR-FR" priority="700" scoreAsDefault="0.0" index="2"/>
              </QualifierSet>
              <QualifierSet index="3">
                <Qualifier name="Scale" value="200" priority="200" scoreAsDefault="1.0" index="3"/>
              </QualifierSet>
              <QualifierSet index="4">
                <Qualifier name="Scale" value="100" priority="200" scoreAsDefault="0.5" index="4"/>
              </QualifierSet>
              <QualifierSet index="5">
                <Qualifier name="Contrast" value="BLACK" priority="400" scoreAsDefault="0.0" index="5"/>
                <Qualifier name="Scale" value="200" priority="200" scoreAsDefault="1.0" index="3"/>
              </QualifierSet>
              <QualifierSet index="6">
                <Qualifier name="TargetSize" value="24" priority="300" scoreAsDefault="0.5" index="6"/>
                <Qualifier name="AlternateForm" value="UNPLATED" priority="100" scoreAsDefault="0.0" index="7"/>
              </QualifierSet>
              <QualifierSet index="7">
                <Qualifier name="TargetSize" value="48" priority="300" scoreAsDefault="0.5" index="8"/>
                <Qualifier name="AlternateForm" value="LIGHTUNPLATED" priority="100" scoreAsDefault="0.0" index="9"/>
              </QualifierSet>
            </QualifierSets>
            <Decisions>
              <Decision index="2">
                <QualifierSet index="2">
                  <Qualifier name="Language" value="FR-FR" priority="700" scoreAsDefault="0.0" index="2"/>
                </QualifierSet>
                <QualifierSet index="1">
                  <Qualifier name="Language" value="EN-US" priority="700" scoreAsDefault="1.0" index="1"/>
                </QualifierSet>
              </Decision>
              <Decision index="3">
                <QualifierSet index="1">
                  <Qualifier name="Language" value="EN-US" priority="700" scoreAsDefault="1.0" index="1"/>
                </QualifierSet>
              </Decision>
              <Decision index="5">
                <QualifierSet index="5">
                  <Qualifier name="Contrast" value="BLACK" priority="400" scoreAsDefault="0.0" index="5"/>
                  <Qualifier name="Scale" value="200" priority="200" scoreAsDefault="1.0" index="3"/>
                </QualifierSet>
                <QualifierSet index="3">
                  <Qualifier name="Scale" value="200" priority="200" scoreAsDefault="1.0" index="3"/>
                </QualifierSet>
                <QualifierSet index="4">
                  <Qualifier name="Scale" value="100" priority="200" scoreAsDefault="0.5" index="4"/>
                </QualifierSet>
              </Decision>
              <Decision index="6">
                <QualifierSet index="7">
                  <Qualifier name="TargetSize" value="48" priority="300" scoreAsDefault="0.5" index="8"/>
                  <Qualifier name="AlternateForm" value="LIGHTUNPLATED" priority="100" scoreAsDefault="0.0" index="9"/>
                </QualifierSet>
                <QualifierSet index="6">
                  <Qualifier name="TargetSize" value="24" priority="300" scoreAsDefault="0.5" index="6"/>
                  <Qualifier name="AlternateForm" value="UNPLATED" priority="100" scoreAsDefault="0.0" index="7"/>
                </QualifierSet>
                <QualifierSet index="3">
                  <Qualifier name="Scale" value="200" priority="200" scoreAsDefault="1.0" index="3"/>
                </QualifierSet>
              </Decision>
            </Decisions>
          </QualifierInfo>
          <ResourceMap name="PriFidelityB97C" primary="true" uniqueName="ms-appx://PriFidelityB97C/" version="1.0">
            <VersionInfo version="1.0" checksum="-620439538" numScopes="7" numItems="7"/>
            <ResourceMapSubtree index="3" name="FidelityLibrary">
              <ResourceMapSubtree index="4" name="Resources">
                <NamedResource name="LibraryMessage" index="3" uri="ms-resource://PriFidelityB97C/FidelityLibrary/Resources/LibraryMessage">
                  <Decision index="2">
                    <QualifierSet index="2"><Qualifier name="Language" value="FR-FR" priority="700" scoreAsDefault="0.0" index="2"/></QualifierSet>
                    <QualifierSet index="1"><Qualifier name="Language" value="EN-US" priority="700" scoreAsDefault="1.0" index="1"/></QualifierSet>
                  </Decision>
                  <Candidate type="String">
                    <QualifierSet index="2"><Qualifier name="Language" value="FR-FR" priority="700" scoreAsDefault="0.0" index="2"/></QualifierSet>
                    <Value>Ressource bibliothèque français</Value>
                  </Candidate>
                  <Candidate type="String">
                    <QualifierSet index="1"><Qualifier name="Language" value="EN-US" priority="700" scoreAsDefault="1.0" index="1"/></QualifierSet>
                    <Value>Library resource English</Value>
                  </Candidate>
                </NamedResource>
              </ResourceMapSubtree>
            </ResourceMapSubtree>
            <ResourceMapSubtree index="1" name="Files">
              <ResourceMapSubtree index="8" name="Assets">
                <NamedResource name="Probe.png" index="11" uri="ms-resource://PriFidelityB97C/Files/Assets/Probe.png">
                  <Decision index="5">
                    <QualifierSet index="5">
                      <Qualifier name="Contrast" value="BLACK" priority="400" scoreAsDefault="0.0" index="5"/>
                      <Qualifier name="Scale" value="200" priority="200" scoreAsDefault="1.0" index="3"/>
                    </QualifierSet>
                    <QualifierSet index="3"><Qualifier name="Scale" value="200" priority="200" scoreAsDefault="1.0" index="3"/></QualifierSet>
                    <QualifierSet index="4"><Qualifier name="Scale" value="100" priority="200" scoreAsDefault="0.5" index="4"/></QualifierSet>
                  </Decision>
                  <Candidate type="Path">
                    <QualifierSet index="5">
                      <Qualifier name="Contrast" value="BLACK" priority="400" scoreAsDefault="0.0" index="5"/>
                      <Qualifier name="Scale" value="200" priority="200" scoreAsDefault="1.0" index="3"/>
                    </QualifierSet>
                    <Value>Assets\Probe.scale-200_contrast-black.png</Value>
                  </Candidate>
                  <Candidate type="Path">
                    <QualifierSet index="3"><Qualifier name="Scale" value="200" priority="200" scoreAsDefault="1.0" index="3"/></QualifierSet>
                    <Value>Assets\Probe.scale-200.png</Value>
                  </Candidate>
                  <Candidate type="Path">
                    <QualifierSet index="4"><Qualifier name="Scale" value="100" priority="200" scoreAsDefault="0.5" index="4"/></QualifierSet>
                    <Value>Assets\Probe.scale-100.png</Value>
                  </Candidate>
                </NamedResource>
                <NamedResource name="Square44x44Logo.png" index="14" uri="ms-resource://PriFidelityB97C/Files/Assets/Square44x44Logo.png">
                  <Decision index="6">
                    <QualifierSet index="7">
                      <Qualifier name="TargetSize" value="48" priority="300" scoreAsDefault="0.5" index="8"/>
                      <Qualifier name="AlternateForm" value="LIGHTUNPLATED" priority="100" scoreAsDefault="0.0" index="9"/>
                    </QualifierSet>
                    <QualifierSet index="6">
                      <Qualifier name="TargetSize" value="24" priority="300" scoreAsDefault="0.5" index="6"/>
                      <Qualifier name="AlternateForm" value="UNPLATED" priority="100" scoreAsDefault="0.0" index="7"/>
                    </QualifierSet>
                    <QualifierSet index="3"><Qualifier name="Scale" value="200" priority="200" scoreAsDefault="1.0" index="3"/></QualifierSet>
                  </Decision>
                  <Candidate type="Path">
                    <QualifierSet index="7">
                      <Qualifier name="TargetSize" value="48" priority="300" scoreAsDefault="0.5" index="8"/>
                      <Qualifier name="AlternateForm" value="LIGHTUNPLATED" priority="100" scoreAsDefault="0.0" index="9"/>
                    </QualifierSet>
                    <Value>Assets\Square44x44Logo.targetsize-48_altform-lightunplated.png</Value>
                  </Candidate>
                  <Candidate type="Path">
                    <QualifierSet index="6">
                      <Qualifier name="TargetSize" value="24" priority="300" scoreAsDefault="0.5" index="6"/>
                      <Qualifier name="AlternateForm" value="UNPLATED" priority="100" scoreAsDefault="0.0" index="7"/>
                    </QualifierSet>
                    <Value>Assets\Square44x44Logo.targetsize-24_altform-unplated.png</Value>
                  </Candidate>
                  <Candidate type="Path">
                    <QualifierSet index="3"><Qualifier name="Scale" value="200" priority="200" scoreAsDefault="1.0" index="3"/></QualifierSet>
                    <Value>Assets\Square44x44Logo.scale-200.png</Value>
                  </Candidate>
                </NamedResource>
              </ResourceMapSubtree>
              <ResourceMapSubtree index="2" name="FidelityLibrary">
                <NamedResource name="LibraryPanel.xbf" index="2" uri="ms-resource://PriFidelityB97C/Files/FidelityLibrary/LibraryPanel.xbf">
                  <Decision index="1"><QualifierSet index="0"/></Decision>
                  <Candidate type="Path"><QualifierSet index="0"/><Value>FidelityLibrary\LibraryPanel.xbf</Value></Candidate>
                </NamedResource>
              </ResourceMapSubtree>
              <NamedResource name="App.xbf" index="1" uri="ms-resource://PriFidelityB97C/Files/App.xbf">
                <Decision index="1"><QualifierSet index="0"/></Decision>
                <Candidate type="EmbeddedData">
                  <QualifierSet index="0"/>
                  <Base64Value>WEJGAIICAACOAAAAAgAAAAEAAAB4AAAAAAAAAE4CAAAAAAAAUgIAAAAAAABWAgAAAAAAAGYCAAAAAAAAagIAAAAAAABBMDExNTYxNUYyQkVDOTUyQzY1REU5QUZBMjhDMkVEMURGMzA3NUM2NEVEOTYwMDBENzg3NURGREQwM0QzN0U0BgAAADkAAABoAHQAdABwADoALwAvAHMAYwBoAGUAbQBhAHMALgBtAGkAYwByAG8AcwBvAGYAdAAuAGMAbwBtAC8AdwBpAG4AZgB4AC8AMgAwADAANgAvAHgAYQBtAGwALwBwAHIAZQBzAGUAbgB0AGEAdABpAG8AbgAAACwAAABoAHQAdABwADoALwAvAHMAYwBoAGUAbQBhAHMALgBtAGkAYwByAG8AcwBvAGYAdAAuAGMAbwBtAC8AdwBpAG4AZgB4AC8AMgAwADAANgAvAHgAYQBtAGwAAAAVAAAAdQBzAGkAbgBnADoAUAByAGkARgBpAGQAZQBsAGkAdAB5AEIAOQA3AEMAAAAgAAAAdQBzAGkAbgBnADoATQBpAGMAcgBvAHMAbwBmAHQALgBVAEkALgBYAGEAbQBsAC4AQwBvAG4AdAByAG8AbABzAAAAFQAAAFgAYQBtAGwAQwBvAG4AdAByAG8AbABzAFIAZQBzAG8AdQByAGMAZQBzAAAAKAAAAGgAdAB0AHAAOgAvAC8AcwBjAGgAZQBtAGEAcwAuAG0AaQBjAHIAbwBzAG8AZgB0AC4AYwBvAG0ALwBjAGwAaQBlAG4AdAAvADIAMAAwADcAAAAAAAAAAAAAAAEAAAACAAAABAAAAAQAAAAAAAAABQAAAAAAAAABAAAAAgAAAAMAAAAFAAAAAQAAAAAAAABnAAAAEgAAAAAAAAMBAAEAAAB4AAMCAAUAAABsAG8AYwBhAGwACxMAAABQAHIAaQBGAGkAZABlAGwAaQB0AHkAQgA5ADcAQwAuAEEAcABwABcfgBRzgROvghIDAAAAAAAXAAAhCAIhB0mAIQcKCgkCABEFACsGAAMECgMEPA8CIAEEEwQEIQ==</Base64Value>
                </Candidate>
              </NamedResource>
            </ResourceMapSubtree>
            <ResourceMapSubtree index="6" name="Resources">
              <NamedResource name="AppDisplayName" index="6" uri="ms-resource://PriFidelityB97C/Resources/AppDisplayName">
                <Decision index="2">
                  <QualifierSet index="2"><Qualifier name="Language" value="FR-FR" priority="700" scoreAsDefault="0.0" index="2"/></QualifierSet>
                  <QualifierSet index="1"><Qualifier name="Language" value="EN-US" priority="700" scoreAsDefault="1.0" index="1"/></QualifierSet>
                </Decision>
                <Candidate type="String">
                  <QualifierSet index="2"><Qualifier name="Language" value="FR-FR" priority="700" scoreAsDefault="0.0" index="2"/></QualifierSet>
                  <Value>Fidélité PRI français</Value>
                </Candidate>
                <Candidate type="String">
                  <QualifierSet index="1"><Qualifier name="Language" value="EN-US" priority="700" scoreAsDefault="1.0" index="1"/></QualifierSet>
                  <Value>PRI fidelity English</Value>
                </Candidate>
              </NamedResource>
              <NamedResource name="FallbackOnly" index="8" uri="ms-resource://PriFidelityB97C/Resources/FallbackOnly">
                <Decision index="3"><QualifierSet index="1"><Qualifier name="Language" value="EN-US" priority="700" scoreAsDefault="1.0" index="1"/></QualifierSet></Decision>
                <Candidate type="String">
                  <QualifierSet index="1"><Qualifier name="Language" value="EN-US" priority="700" scoreAsDefault="1.0" index="1"/></QualifierSet>
                  <Value>English fallback only — café ∑</Value>
                </Candidate>
              </NamedResource>
            </ResourceMapSubtree>
          </ResourceMap>
        </PriInfo>
        """;
}
