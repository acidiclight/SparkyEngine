using Silk.NET.Core.Native;
using Silk.NET.GLFW;
using Silk.NET.Vulkan;
using Sparky.Graphics;

namespace Sparky.Windowing;

public class Window : IDisposable
{
    private int width;
    private int height;
    private string title;
    private Glfw glfw;
    private unsafe WindowHandle* glfwWindow;
    private GraphicsDevice graphicsDevice;

    public GraphicsDevice Graphicsdevice => this.graphicsDevice;

    public (int width, int height) FramebufferSize
    {
        get
        {
            unsafe
            {
                glfw.GetFramebufferSize(this.glfwWindow, out int w, out int h);
                return (w, h);
            }
        }
    }
    
    public bool WantsToClose
    {
        get
        {
            unsafe
            {
                return glfw.WindowShouldClose(this.glfwWindow);
            }
        }
    }

    public Window(int width, int height, string title)
    {
        Debug.Log("Creating GLFW window...");

        this.width = width;
        this.height = height;
        this.title = title;

        Debug.Log($"Window info: width={width} height={height} title={title}");
        
        this.glfw = Glfw.GetApi();
        this.glfw.Init();
        this.glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
        this.glfw.WindowHint(WindowHintBool.Resizable, false);
        
        Debug.Log("GLFW initialized successfully");

        unsafe
        {
            this.glfwWindow = this.glfw.CreateWindow(this.width, this.height, this.title, null, null);
        }

        Debug.Log("Window created successfully");

        unsafe
        {
            byte** extensions = glfw.GetRequiredInstanceExtensions(out uint extensionCount);
            this.graphicsDevice = new GraphicsDevice(this, extensions, extensionCount);
        }

    }

    public Window(int width, int height)
        : this(width, height, "Sparky")
    { }
    
    public Window()
        : this(1600, 900)
    { }

    public void PollEvents()
    {
        this.glfw.PollEvents();
    }
    
    public void Dispose()
    {
        this.graphicsDevice.Dispose();

        Debug.Log("Destroying the GLFW window...");
        
        unsafe
        {
            this.glfw.DestroyWindow(this.glfwWindow);
        }
        
        this.glfw.Terminate();
        Debug.Log("GLFW terminated!");
    }
    
    internal VkNonDispatchableHandle CreateWindowSurface(VkHandle vkHandle)
    {
        VkNonDispatchableHandle surface = new VkNonDispatchableHandle();

        unsafe
        {
            int result = glfw.CreateWindowSurface(vkHandle, this.glfwWindow, null, &surface);
            ThrowHelpers.ThrowVulkanException((Result)result);
        }

        return surface;
    }
}