<?xml version="1.0" encoding="utf-8"?>
<xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">

  <!--
    Removes the generated interop entries from a compiler-produced XML documentation file.

    Both packages run the CsWin32 source generator, which emits several hundred documented P/Invoke
    wrappers under Windows.Win32. They are already internal - and must stay internal, because both
    assemblies generate them and sharing them makes Windows.Win32.PInvoke ambiguous - but the C#
    compiler writes a documentation entry for every member carrying a doc comment regardless of
    accessibility, and CsWin32 offers no option to suppress them. Left alone they are roughly 2,000 of
    2,400 entries, which nearly doubles the .nupkg and documents nothing a consumer can reference.
  -->

  <xsl:output method="xml" indent="yes" encoding="utf-8" />

  <xsl:template match="@*|node()">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()" />
    </xsl:copy>
  </xsl:template>

  <!-- Names are "T:Namespace.Type" or "M:Namespace.Type.Method(...)"; match on the part after the kind. -->
  <xsl:template match="member[starts-with(substring-after(@name, ':'), 'Windows.Win32.')]" />

</xsl:stylesheet>
