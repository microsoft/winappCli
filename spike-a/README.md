# Spike A: XAML hot reload as a side-car next to `dotnet watch` (throwaway)

Not for merging. Code for review of the option-A spike (N1 in the hot reload design).

- `src/winapp-devtools/sidecar/WinApp.XamlSidecar/`: the side-car (XAML compiler service, app image, obj writes, pipe to the agent, `[watch]` lines).
- `src/winapp-devtools/agent/WinApp.DevTools.Managed/HotReload/`: the in-app agent (MetadataUpdateHandler, .xbf, page re-create, UiState).
- `src/winapp-CLI/WinApp.Cli/Commands/RunCommand.XamlSidecar.cs` + `RunCommand.Watch.cs`: start the side-car, capture compiler arguments, relaunch `dotnet watch` (E10).
- `src/winapp-devtools/native/…/DevToolsTap.*`: hand out WinUI's IXamlHotReloadService.
- `spike-a/`: extra journeys (E1, E10, rude restart, packaged) and the scripts used to run them against hr-latest's journey runner.
