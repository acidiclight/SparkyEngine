using System.Reflection;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using Sparky.Graphics;
using Sparky.Windowing;

namespace Sparky;

public static class Application
{
    private static Window mainWindow;

    public static string EngineName { get; private set; }
    public static Version EngineVersion { get; private set; }
    public static string ProductName { get; private set; } = "Sparky Application";
    
    private static void RetrieveAppInfo()
    {
        // Retrieve the Sparky assembly to get Sparky's engine information.
        Assembly sparky = typeof(Application).Assembly;

        // Retrieve product name
        object[] allAttributes = sparky.GetCustomAttributes(false);
        AssemblyProductAttribute product = allAttributes.OfType<AssemblyProductAttribute>().First();
        
        // And retrieve the version
        Version? engineVersion = sparky.GetName().Version;
        if (engineVersion == null)
            engineVersion = Version.Parse("0.0.0.0");

        EngineName = product.Product ?? "Sparky";
        EngineVersion = engineVersion;

        Debug.Log($"{EngineName} version {EngineVersion.ToString()}");
    }
    
    public static void Run()
    {
        Debug.Init();
        
        RetrieveAppInfo();
        
        mainWindow = new Window();

        GraphicsDevice gfxDevice = mainWindow.Graphicsdevice;

        Debug.Log("Starting the run loop...");
        
        while (!mainWindow.WantsToClose)
        {
            mainWindow.PollEvents();
            gfxDevice.Present();
        }

        Debug.Log("Application Main Window closed - tearing the engine down.");

        mainWindow.Dispose();
    }
}

public static class ThrowHelpers
{
    public static void ThrowNoGpuDetected()
    {
        Debug.LogError("No graphics card found! About to fucking crash and burn <3");
        throw new SparkyException("No compatible GPU was found.");
    }
    
    public static void ThrowVulkanException(Result vkResult)
    {
        Debug.Log($"Vulkan said: {vkResult}");
        if (vkResult == Result.Success)
            return;

        throw new SparkyException("An error occurred during a Vulkan operation. Vulkan said: " + vkResult);
    }
}