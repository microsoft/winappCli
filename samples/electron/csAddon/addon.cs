using System;
using Microsoft.JavaScript.NodeApi;

namespace csAddon
{
    /// <summary>
    /// Sample C# addon for Node.js using node-api-dotnet.
    /// This class demonstrates how to export C# methods to JavaScript.
    /// </summary>
    [JSExport]
    public class Addon
    {
        /// <summary>
        /// A simple hello function that returns a greeting message.
        /// </summary>
        /// <param name="name">The name to greet</param>
        /// <returns>A greeting message</returns>
        [JSExport]
        public static string Hello(string name)
        {
            return $"Hello from C#, {name}!";
        }

        /// <summary>
        /// A sample function that adds two numbers.
        /// </summary>
        /// <param name="a">First number</param>
        /// <param name="b">Second number</param>
        /// <returns>The sum of a and b</returns>
        [JSExport]
        public static int Add(int a, int b)
        {
            return a + b;
        }

        /// <summary>
        /// A sample function that returns the current date and time.
        /// </summary>
        /// <returns>Current date and time as a string</returns>
        [JSExport]
        public static string GetCurrentTime()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        [JSExport]
        public static void ShowAppWindow()
        {
            var controller = Microsoft.UI.Dispatching.DispatcherQueueController.CreateOnDedicatedThread();
            var dispatcher = controller.DispatcherQueue;
            dispatcher.TryEnqueue(() =>
            {
                var w = Microsoft.UI.Windowing.AppWindow.Create();
                w.Title = "Hello, World!";
                w.Show();
            });
            dispatcher.RunEventLoop();
        }

        /// <summary>
        /// Gets the version of the Windows App Runtime currently in use.
        /// </summary>
        /// <returns>The Windows App Runtime version as a string</returns>
        [JSExport]
        public static string GetWindowsAppRuntimeVersion()
        {
            return Microsoft.Windows.ApplicationModel.WindowsAppRuntime.RuntimeInfo.AsString;
        }

        /// <summary>
        /// Initializes the Windows App Runtime in an unpackaged app.  This is required for
        /// using the Windows App Runtime APIs in an unpackaged app.
        /// Requires this DLL: Microsoft.WindowsAppRuntime.Bootstrap.dll
        /// Example JavaScript usage:
        ///    const csAddon = require('../csAddon/build/Release/csAddon.node');
        ///    csAddon.Addon.initializeWindowsAppRuntimeInUnpackagedApp(2, 0, 'experimental1');
        /// </summary>
        [JSExport]
        public static void InitializeWindowsAppRuntimeInUnpackagedApp(
            int majorVersion,
            int minorVersion,
            string versionTag)
        {
            Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.Initialize(
                ((uint)majorVersion) << 16 | (uint)minorVersion,
                versionTag);
        }
    }
}
