import { definePluginEntry } from "openclaw/plugin-sdk/plugin-entry";

// win-dev-skills is a content plugin: it ships WinUI 3 skills (and agents) only,
// with no runtime tools, providers, or channels. OpenClaw native plugins require
// a code entry point, so this registers nothing and lets the manifest's `skills`
// field drive skill discovery.
export default definePluginEntry({
  id: "winui",
  name: "WinUI",
  description:
    "Agents and skills for WinUI 3 app development. Create new WinUI 3 desktop apps, convert from other frameworks to WinUI 3, or add features to existing WinUI 3 applications.",
  register() {},
});
