# winsdk

Windows SDK utilities for Node.js and Electron applications.

This package provides tools for working with Windows SDK features in Node.js and Electron applications, including native addon generation, C# addon support, and MSIX packaging utilities.

## Installation

```bash
npm install winsdk
```

## Features

- **Native Addon Generation**: Quickly scaffold C++ native addons for Electron
- **C# Addon Generation**: Create .NET addons using node-api-dotnet
- **MSIX Packaging**: Utilities for creating and signing MSIX packages
- **Build Tools Integration**: Easy access to Windows Build Tools
- **Electron Debug Identity**: Add MSIX identity to Electron for debugging

## Commands

### Node.js Commands

#### `node create-addon`

Generate native C++ or C# addon files for an Electron project.

```bash
npx winsdk node create-addon --name myAddon --template=cs
```

**Options:**
- `--name <name>`: Addon name (default: `nativeWindowsAddon`)
- `--template=cs`: Create a C# addon.  If omitted, a C++ addon is created.
- `--verbose`: Enable verbose output (default: true)
- `--help`: Show help

**What it does:**
1. Creates a new addon directory with template files (`.cc` and `binding.gyp`)
2. Installs required npm packages (`nan`, `node-addon-api`, `node-gyp`)
3. Adds build script to `package.json`

**Usage in JavaScript:**
```javascript
const myAddon = require('./myAddon/build/Release/myAddon.node');
```

**Requirements for C# addon:**
- .NET 8.0 SDK or later must be installed

**How a C# addon is different than native:**
1. Creates a new C# addon directory with `.csproj` and `addon.cs` files
2. Installs `node-api-dotnet` package
3. Adds build and clean scripts to `package.json`.  The build script will publish your C# project
as NAOT, so it won't have a dependency on the .net runtime.
4. Updates `.gitignore` with C# build artifacts

**C# Code Example:**
```csharp
using Microsoft.JavaScript.NodeApi;

namespace MyCsAddon
{
    [JSExport]
    public class Addon
    {
        [JSExport]
        public static string Hello(string name)
        {
            return $"Hello from C#, {name}!";
        }

        [JSExport]
        public static int Add(int a, int b)
        {
            return a + b;
        }
    }
}
```

---

#### `node add-electron-debug-identity`

Add MSIX identity to Electron debug process for testing Windows Runtime APIs.

```bash
npx winsdk node add-electron-debug-identity
```

**Options:**
- `--verbose`: Enable verbose output (default: true)
- `--help`: Show help

**What it does:**
1. Creates a backup of `node_modules/electron/dist/electron.exe`
2. Generates a sparse MSIX manifest and assets
3. Adds MSIX identity to the Electron executable
4. Registers the sparse package

---

### Native CLI Commands

The package also includes a native CLI that provides additional commands for MSIX packaging, signing, and manifest generation. Run `npx winsdk --help` to see all available commands.

## API Reference

### Addon Utilities

```javascript
const { generateAddonFiles } = require('winsdk/addon-utils');

const result = await generateAddonFiles({
  name: 'myAddon',
  projectRoot: process.cwd(),
  verbose: true
});
```

### C# Addon Utilities

```javascript
const { generateCsAddonFiles } = require('winsdk/cs-addon-utils');

const result = await generateCsAddonFiles({
  name: 'MyCsAddon',
  projectRoot: process.cwd(),
  verbose: true
});
```

### MSIX Utilities

```javascript
const { addElectronDebugIdentity } = require('winsdk/msix-utils');

await addElectronDebugIdentity({
  verbose: true
});
```

### Build Tools Utilities

```javascript
const { execSyncWithBuildTools } = require('winsdk/buildtools-utils');

// Execute a command with Build Tools in PATH
execSyncWithBuildTools('signtool.exe sign /fd SHA256 myapp.exe', {
  stdio: 'inherit'
});
```

## Examples

### Creating a Native C++ Addon

```bash
cd my-electron-app
npx winsdk node create-addon --name windowsFeatures
npm run build-windowsFeatures
```

### Creating a C# Addon

```bash
cd my-electron-app
npx winsdk node create-cs-addon --name WindowsApis
npm run build-WindowsApis
```

Then in your Electron app:

```javascript
const windowsApis = require('./build/Release/WindowsApis.node');
console.log(windowsApis.Hello('Electron'));
```

### Adding Debug Identity to Electron

```bash
cd my-electron-app
npx winsdk node add-electron-debug-identity
npm start  # Electron now has MSIX identity
```

## Development

For development and testing:

```bash
# Install dependencies
npm install

# Link globally for testing
npm link

# Test CLI
winsdk node create-cs-addon --name TestAddon
```

## License

See [LICENSE](./LICENSE) file.

## Contributing

Contributions are welcome! Please see the main repository for contribution guidelines.

## Related Projects

- [node-api-dotnet](https://github.com/microsoft/node-api-dotnet) - .NET integration for Node.js
- [Electron](https://www.electronjs.org/) - Build cross-platform desktop apps

## Support

For issues and questions, please file an issue in the main repository.
