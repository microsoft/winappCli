using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.JavaScript.NodeApi;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace winMlAddon;

/// <summary>
/// Sample C# addon for Node.js using node-api-dotnet.
/// This class demonstrates how to export C# methods to JavaScript.
/// </summary>
[JSExport]
public class Addon
{
    private InferenceSession? _inferenceSession;
    private string _projectRoot;
    private Addon(string projectRoot)
    {
        _projectRoot = projectRoot;
    }

    [JSExport]
    public static async Task<Addon> CreateAsync(string projectRoot)
    {
        if (!Path.Exists(projectRoot))
        {
            throw new Exception("Project root is invalid.");
        }

        var addon = new Addon(projectRoot);
        addon.PreloadNativeDependencies();

        string modelPath = Path.Join(projectRoot, "models", @"squeezenet1.1-7.onnx");
        await addon.InitModel(modelPath, ExecutionProviderDevicePolicy.DEFAULT, null, false, null);

        return addon;
    }

    [JSExport]
    public async Task<Prediction[]> ClassifyImage(string imagePath)
    {
        // InitializeWindowsAppRuntimeInUnpackagedApp(2, 0, "experimental3");

        if (_inferenceSession == null)
        {
            throw new Exception("Model is not loaded.");
        }

        if (!Path.Exists(imagePath))
        {
            throw new Exception("Image path is invalid.");
        }

         // Grab model metadata
        var inputName = _inferenceSession.InputNames[0];
        var inputMetadata = _inferenceSession.InputMetadata[inputName];
        var dimensions = inputMetadata.Dimensions;

        // Set batch size to 1
        int batchSize = 1;
        dimensions[0] = batchSize;

        int inputWidth = dimensions[2];
        int inputHeight = dimensions[3];

        var predictions = await Task.Run(() =>
        {
            Bitmap image = new(imagePath);

            // Resize image
            var resizedImage = BitmapFunctions.ResizeBitmap(image, inputWidth, inputHeight);
            image.Dispose();
            image = resizedImage;

            // Preprocess image
            Tensor<float> input = new DenseTensor<float>(dimensions);
            input = BitmapFunctions.PreprocessBitmapWithStdDev(image, input);
            image.Dispose();

            // Setup inputs
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, input)
            };

            // Run inference
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _inferenceSession!.Run(inputs);

            // Postprocess to get softmax vector
            IEnumerable<float> output = results[0].AsEnumerable<float>();
            return ImageNet.GetSoftmax(output);
        });


        // Placeholder for image classification logic using _inferenceSession
        // In a real implementation, you would load the image, preprocess it,
        // run inference, and return the classification result.

        return predictions;
    }

    public static void InitializeWindowsAppRuntimeInUnpackagedApp(
        int majorVersion,
        int minorVersion,
        string versionTag)
    {
        Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.Initialize(
            ((uint)majorVersion) << 16 | (uint)minorVersion,
            versionTag);
    }

    private Task InitModel(string modelPath, ExecutionProviderDevicePolicy? policy, string? epName, bool compileModel, string? deviceType)
    {
        return Task.Run(async () =>
        {
            if (_inferenceSession != null)
            {
                return;
            }

            var catalog = Microsoft.Windows.AI.MachineLearning.ExecutionProviderCatalog.GetDefault();

            try
            {
                var registeredProviders = await catalog.EnsureAndRegisterCertifiedAsync();
            }
            catch (Exception ex)
            {
                throw new Exception($"WARNING: Failed to install packages: {ex.Message}");
            }

            SessionOptions sessionOptions = new();
            sessionOptions.RegisterOrtExtensions();

            if (policy != null)
            {
                sessionOptions.SetEpSelectionPolicy(policy.Value);
            }
            else if (epName != null)
            {
                sessionOptions.AppendExecutionProviderFromEpName(epName, deviceType);

                if (compileModel)
                {
                    modelPath = sessionOptions.GetCompiledModel(modelPath, epName) ?? modelPath;
                }
            }

            _inferenceSession = new InferenceSession(modelPath, sessionOptions);
        });
    }

    public void PreloadNativeDependencies()
    {

        // 1. Get the folder where THIS .NET assembly lives (your bin folder)
        string assemblyDir = Path.Join(_projectRoot, "winMlAddon", "dist");

        Console.WriteLine($"Initializing  {assemblyDir}");

        // 2. Define the critical DLLs to preload
        string[] dllsToLoad = new[] 
        { 
            "ortextensions.dll",    // extensions for ONNX Runtime
            // "Microsoft.WindowsAppRuntime.Bootstrap.dll" // if using unpackaged
        };

        foreach (var dllName in dllsToLoad)
        {
            string fullPath = Path.Combine(assemblyDir, dllName);
            Console.WriteLine($"Attempting to preload: {fullPath}");

            if (File.Exists(fullPath))
            {
                // Load it into the process address space
                IntPtr handle = NativeLibrary.Load(fullPath);
                if (handle != IntPtr.Zero)
                {
                    Console.WriteLine($"[Success] Pre-loaded: {dllName}");
                }
                else
                {
                    Console.WriteLine($"[Error] Found but failed to load: {dllName} (Architecture mismatch?)");
                }
            }
            else
            {
                // If it's not in the bin folder, it might be in 'runtimes/win-x64/native'
                // You might need to adjust the path search here
                Console.WriteLine($"[Warning] Could not find file to preload: {fullPath}");
            }
        }
    }
}
