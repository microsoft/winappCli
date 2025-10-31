

const csAddon = require('../csAddon/build/Release/csAddon.node');

// Call exported methods (note: C# method names are converted to camelCase in JavaScript)
console.log(csAddon.Addon.hello('World'));
// Output: Hello from C#, World!

console.log(csAddon.Addon.add(5, 3));
// Output: 8

console.log(csAddon.Addon.getCurrentTime());
// Output: 2025-10-22 14:30:45
