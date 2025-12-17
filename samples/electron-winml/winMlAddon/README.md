# winMlAddon - C# Native Addon

This is a C# native addon for Node.js/Electron, created using node-api-dotnet.

## Building the Addon

To build the C# addon, run:

```bash
npm run build-winMlAddon
```

This will compile the C# code and output the assembly to `build/Release/winMlAddon.dll`.

## Using the Addon in JavaScript

After building, you can use the addon in your JavaScript code:

```javascript
// Load the C# module
const winMlAddon = require('./winMlAddon/dist/winMlAddon.node');

// Call exported methods (note: C# method names are converted to camelCase in JavaScript)
console.log(winMlAddon.Addon.hello('World'));
// Output: Hello from C#, World!

console.log(winMlAddon.Addon.add(5, 3));
// Output: 8

console.log(winMlAddon.Addon.getCurrentTime());
// Output: (current time)
```

**Note:** 
- The module is loaded via `dotnet.require()` from the `node-api-dotnet` package.
- C# method names (PascalCase) are automatically converted to camelCase in JavaScript.
- Make sure `node-api-dotnet` is installed (it should be added automatically when you created this addon).

## Development

The addon source code is in `winMlAddon/addon.cs`. You can modify this file to add your own C# functionality.

### Adding New Methods

To add new methods that are callable from JavaScript:

1. Add the `[JSExport]` attribute to the method
2. Make sure the method is `public static`
3. Rebuild the addon with `npm run build-winMlAddon`

Example:

```csharp
[JSExport]
public static string MyNewMethod(string input)
{
    return $"Processed: {input}";
}
```

## Debugging

To debug the C# addon in Visual Studio or VS Code:

1. Open the `.csproj` file in Visual Studio
2. Set breakpoints in your C# code
3. Attach the debugger to the Node.js/Electron process

## Type Definitions

The build process automatically generates TypeScript type definitions (`.d.ts` file) in the output directory. You can reference these types in your TypeScript code for full IntelliSense support.

## Dependencies

This addon uses:
- [node-api-dotnet](https://github.com/microsoft/node-api-dotnet) - .NET interop for Node.js
- .NET 8.0 SDK (required for building)

## Learn More

- [node-api-dotnet documentation](https://microsoft.github.io/node-api-dotnet/)
- [C# Node.js addon module guide](https://microsoft.github.io/node-api-dotnet/scenarios/js-dotnet-module.html)
- [Node-API (N-API) documentation](https://nodejs.org/api/n-api.html)
