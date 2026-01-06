using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.JavaScript.NodeApi;

namespace winMlAddon;

[JSExport]
public class ChatClient
{
    private IChatClient? chatClient;
    private CancellationTokenSource? cts;
    private string _projectRoot;

    private ChatClient(string projectRoot)
    {
        _projectRoot = projectRoot;
    }

     [JSExport]
    public static async Task<ChatClient> CreateAsync(string projectRoot)
    {
        if (!Path.Exists(projectRoot))
        {
            throw new Exception("Project root is invalid.");
        }

        var client = new ChatClient(projectRoot);
        client.PreloadNativeDependencies();

        string modelPath = Path.Join(projectRoot, "models", "phi");
        await client.InitModel(modelPath);

        return client;
    }

    private async Task InitModel(string modelPath)
    {
        chatClient = await OnnxRuntimeGenAIChatClientFactory.CreateAsync(modelPath, new LlmPromptTemplate
        {
            System = "<|system|>\n{{CONTENT}}<|end|>\n",
            User = "<|user|>\n{{CONTENT}}<|end|>\n",
            Assistant = "<|assistant|>\n{{CONTENT}}<|end|>\n",
            Stop = [ "<|system|>", "<|user|>", "<|assistant|>", "<|end|>"]
        });
    }

    public async Task<string> GenerateText(string systemPrompt, string userPrompt)
    {
        if (chatClient == null)
        {
            throw new Exception("Chat client is not initialized.");
        }

        cts = new CancellationTokenSource();
        var generatedText = new StringBuilder();

        try
        {
            await foreach (var messagePart in chatClient.GetStreamingResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt)
                ],
                null,
                cts.Token))
            {
                generatedText.Append(messagePart);
            }

        }
        catch (Exception ex)
        {
            if (cts != null && !cts.Token.IsCancellationRequested)
            {
                throw ex;
            }
        }

        cts?.Dispose();
        cts = null;
        return generatedText.ToString();
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
            "onnxruntime-genai.dll",
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