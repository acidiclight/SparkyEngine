using System.Diagnostics;
using System.Reflection;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Sparky.Windowing;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Sparky.Graphics;

public class GraphicsDevice : IDisposable
{
    private Window window;
    private Vk vk;
    private uint extensionCount;
    private unsafe byte** extensions;
    private Instance instance;
    private PhysicalDevice physicalDevice;
    private Device logicalDevice;
    private Queue graphicsQueue;
    private Queue presentQueue;
    private PhysicalDeviceProperties deviceProperties;
    private SurfaceKHR surface;
    private SwapchainKHR swapchain;
    private KhrSurface khrSurfaceExtension;
    private KhrSwapchain khrSwapchainExtension;
    private Image[] swapchainImages;
    private Format swapchainFormat;
    private Extent2D swapchainExtent;
    private ImageView[] imageViews;
    private Shader coreVertShader;
    private Shader coreFragShader;
    private RenderPass renderPass;
    private PipelineLayout pipelineLayout;
    private Pipeline graphicsPipeline;
    private Framebuffer[] framebuffers;
    private CommandPool commandPool;
    private CommandBuffer commandBuffer;
    private Semaphore imageAcquiredSemaphore;
    private Semaphore renderFinishedSemaphore;
    private Fence inFlightFence;

    private readonly string[] requiredExtensionNames =
    {
        KhrSwapchain.ExtensionName
    };

    public string DeviceName { get; private set; }

    internal VkHandle Handle => instance.ToHandle();
    
    public unsafe GraphicsDevice(Window window, byte** extensions, uint extensionCount)
    {
        Debug.Log("Creating Sparky Vulkan device...");

        this.window = window;
        this.extensions = extensions;
        this.extensionCount = extensionCount;
        this.vk = Vk.GetApi();
        this.khrSurfaceExtension = new KhrSurface(this.vk.Context);
        this.khrSwapchainExtension = new KhrSwapchain(this.vk.Context);

        this.CreateInstance();
        this.CreateWindowSurface();
        this.FindPhysicalDevice();
        this.CreateLogicalDevice();
        this.CreateSwapchain();
        this.CreateImageViews();
        this.CreateGraphicsPipeline();
        this.CreateFramebuffers();
        this.CreateCommandPool();
        this.CreateCommandBuffer();
        this.CreateSyncObjects();
    }

    public void Dispose()
    {
        Debug.Log("Sending a pipe bomb to Vulkan's doorstep...");
        
        unsafe
        {
            vk.DestroyFence(logicalDevice, inFlightFence, null);
            vk.DestroySemaphore(logicalDevice, imageAcquiredSemaphore, null);
            vk.DestroySemaphore(logicalDevice, renderFinishedSemaphore, null);
            
            vk.DestroyCommandPool(logicalDevice, commandPool, null);

            for (var i = 0; i < framebuffers.Length; i++)
            {
                vk.DestroyFramebuffer(logicalDevice, framebuffers[i], null);
            }
            
            vk.DestroyPipeline(logicalDevice, graphicsPipeline, null);
            vk.DestroyRenderPass(logicalDevice, renderPass, null);
            vk.DestroyPipelineLayout(logicalDevice, pipelineLayout, null);
            
            for (var i = 0; i < imageViews.Length; i++)
            {
                vk.DestroyImageView(logicalDevice, imageViews[i], null);
                imageViews[i] = default;
            }
            
            khrSwapchainExtension.DestroySwapchain(logicalDevice, swapchain, null);
            khrSurfaceExtension.DestroySurface(this.instance, this.surface, null);
            vk.DestroyDevice(logicalDevice, null);
            vk.DestroyInstance(instance, null);
        }

        Debug.Log("Vulkan has been burned at the steak.");
    }

    private unsafe void CreateSyncObjects()
    {
        SemaphoreCreateInfo createInfo = new();
        createInfo.SType = StructureType.SemaphoreCreateInfo;

        FenceCreateInfo fenceCreateInfo = new();
        fenceCreateInfo.SType = StructureType.FenceCreateInfo;
        fenceCreateInfo.Flags = FenceCreateFlags.SignaledBit;
        
        Result result = vk.CreateSemaphore(logicalDevice, createInfo, null, out imageAcquiredSemaphore);
        ThrowHelpers.ThrowVulkanException(result);
        result = vk.CreateSemaphore(logicalDevice, createInfo, null, out renderFinishedSemaphore);
        ThrowHelpers.ThrowVulkanException(result);
        result = vk.CreateFence(logicalDevice, fenceCreateInfo, null, out inFlightFence);
        ThrowHelpers.ThrowVulkanException(result);

    }

    private unsafe void CreateCommandBuffer()
    {
        CommandBufferAllocateInfo allocInfo = new();
        allocInfo.SType = StructureType.CommandBufferAllocateInfo;
        allocInfo.CommandPool = commandPool;
        allocInfo.CommandBufferCount = 1;
        allocInfo.Level = CommandBufferLevel.Primary;

        Debug.Log("Allocating command buffer...");
        Result result = vk.AllocateCommandBuffers(logicalDevice, in allocInfo, out commandBuffer);
        ThrowHelpers.ThrowVulkanException(result);
    }
    
    private unsafe void CreateCommandPool()
    {
        QueueFamilyIndices indices = FindQueueFamilyIndices(this.physicalDevice);

        CommandPoolCreateInfo createInfo = new();
        createInfo.SType = StructureType.CommandPoolCreateInfo;
        createInfo.Flags = CommandPoolCreateFlags.ResetCommandBufferBit;
        createInfo.QueueFamilyIndex = indices.GraphicsFamily.GetValueOrDefault();

        Debug.Log("Creating command pool.");
        Result result = vk.CreateCommandPool(logicalDevice, createInfo, null, out commandPool);
        ThrowHelpers.ThrowVulkanException(result);
    }
    
    private void CompileShaders()
    {
        Assembly thisAssembly = this.GetType().Assembly;

        using Stream? vertShaderStream = thisAssembly.GetManifestResourceStream("Sparky.Shaders.sparky.vert");
        using Stream? fragShaderStream = thisAssembly.GetManifestResourceStream("Sparky.Shaders.sparky.frag");

        if (vertShaderStream == null)
            throw new SparkyException("Cannot find internal vertex shader");

        if (fragShaderStream == null)
            throw new SparkyException("Cannot find internal fragment shader");

        Shader vertShader = Shader.FromStream(vertShaderStream, ShaderType.VertexShader);
        Shader fragShader = Shader.FromStream(fragShaderStream, ShaderType.FragmentShader);

        vertShader.Compile();
        fragShader.Compile();

        this.coreVertShader = vertShader;
        this.coreFragShader = fragShader;
    }

    private unsafe void RecordCommandBuffer(CommandBuffer buffer, int swapchainImageIndex)
    {
        CommandBufferBeginInfo beginInfo = new();
        beginInfo.SType = StructureType.CommandBufferBeginInfo;

        Result beginResult = vk.BeginCommandBuffer(buffer, beginInfo);
        ThrowHelpers.ThrowVulkanException(beginResult);

        RenderPassBeginInfo renderPassBegin = new();
        renderPassBegin.SType = StructureType.RenderPassBeginInfo;
        renderPassBegin.RenderPass = renderPass;
        renderPassBegin.Framebuffer = framebuffers[swapchainImageIndex];
        renderPassBegin.RenderArea.Offset = new Offset2D(0, 0);
        renderPassBegin.RenderArea.Extent = swapchainExtent;
        renderPassBegin.ClearValueCount = 1;

        ClearValue clearValue = new(new ClearColorValue(0, 0, 0, 1));
        renderPassBegin.PClearValues = &clearValue;
        
        Viewport viewport = new();
        viewport.X = 0;
        viewport.Y = 0;
        viewport.Width = swapchainExtent.Width;
        viewport.Height = swapchainExtent.Height;
        viewport.MinDepth = 0;
        viewport.MaxDepth = 1;

        Rect2D scissor;
        scissor.Offset = new Offset2D(0, 0);
        scissor.Extent = swapchainExtent;
        
        vk.CmdBeginRenderPass(buffer, &renderPassBegin, SubpassContents.Inline);
        vk.CmdBindPipeline(buffer, PipelineBindPoint.Graphics, graphicsPipeline);
        vk.CmdSetViewport(buffer, 0, 1, &viewport);
        vk.CmdSetScissor(buffer, 0, 1, &scissor);
        vk.CmdDraw(buffer, 3, 1, 0,0);
        vk.CmdEndRenderPass(buffer);

        Result result = vk.EndCommandBuffer(buffer);
        ThrowHelpers.ThrowVulkanException(result);
        
        
    }
    
    private unsafe void CreateRenderPass()
    {
        SubpassDependency dependency = new();
        dependency.SrcSubpass = Vk.SubpassExternal;
        dependency.DstSubpass = 0;
        dependency.SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit;
        dependency.SrcAccessMask = 0;
        
        AttachmentDescription colorAttachment = new();
        colorAttachment.Format = swapchainFormat;
        colorAttachment.Samples = SampleCountFlags.Count1Bit;
        colorAttachment.LoadOp = AttachmentLoadOp.Clear;
        colorAttachment.StoreOp = AttachmentStoreOp.Store;
        colorAttachment.StencilLoadOp = AttachmentLoadOp.DontCare;
        colorAttachment.StencilStoreOp = AttachmentStoreOp.DontCare;
        colorAttachment.InitialLayout = ImageLayout.Undefined;
        colorAttachment.FinalLayout = ImageLayout.PresentSrcKhr;

        AttachmentReference attachmentRef = new();
        attachmentRef.Attachment = 0;
        attachmentRef.Layout = ImageLayout.ColorAttachmentOptimal;

        SubpassDescription subpass = new();
        subpass.PipelineBindPoint = PipelineBindPoint.Graphics;
        subpass.ColorAttachmentCount = 1;
        subpass.PColorAttachments = &attachmentRef;

        RenderPassCreateInfo renderPassCreateInfo = new();
        renderPassCreateInfo.DependencyCount = 1;
        renderPassCreateInfo.PDependencies = &dependency;
        renderPassCreateInfo.SType = StructureType.RenderPassCreateInfo;
        renderPassCreateInfo.AttachmentCount = 1;
        renderPassCreateInfo.PAttachments = &colorAttachment;
        renderPassCreateInfo.SubpassCount = 1;
        renderPassCreateInfo.PSubpasses = &subpass;

        Debug.Log("Creating render pass...");
        Result renderPassResult = vk.CreateRenderPass(logicalDevice, renderPassCreateInfo, null, out renderPass);
        ThrowHelpers.ThrowVulkanException(renderPassResult);
    }
    
    private unsafe void CreateGraphicsPipeline()
    {
        CreateRenderPass();
        CompileShaders();

        ShaderModule vertShader = CreateShaderModule(coreVertShader);
        ShaderModule fragShader = CreateShaderModule(coreFragShader);

        PipelineShaderStageCreateInfo vertCreateInfo = new();
        PipelineShaderStageCreateInfo fragCreateInfo = new();
        
        vertCreateInfo.SType = StructureType.PipelineShaderStageCreateInfo;
        fragCreateInfo.SType = StructureType.PipelineShaderStageCreateInfo;

        vertCreateInfo.Module = vertShader;
        fragCreateInfo.Module = fragShader;

        vertCreateInfo.Stage = ShaderStageFlags.VertexBit;
        fragCreateInfo.Stage = ShaderStageFlags.FragmentBit;

        vertCreateInfo.PName = (byte*) SilkMarshal.StringToPtr("main");
        fragCreateInfo.PName = (byte*) SilkMarshal.StringToPtr("main");

        
        PipelineShaderStageCreateInfo* programmableStages = stackalloc PipelineShaderStageCreateInfo[2];
        programmableStages[0] = vertCreateInfo;
        programmableStages[1] = fragCreateInfo;
        
        DynamicState* dynamicStatesPtr = stackalloc DynamicState[2];
        dynamicStatesPtr[0] = DynamicState.Viewport;
        dynamicStatesPtr[1] = DynamicState.Scissor;

        PipelineDynamicStateCreateInfo dynamicState = new();
        dynamicState.SType = StructureType.PipelineDynamicStateCreateInfo;
        dynamicState.DynamicStateCount = 2;
        dynamicState.PDynamicStates = dynamicStatesPtr;

        PipelineVertexInputStateCreateInfo vertexInputState = new();
        vertexInputState.SType = StructureType.PipelineVertexInputStateCreateInfo;
        vertexInputState.VertexBindingDescriptionCount = 0;
        vertexInputState.PVertexBindingDescriptions = null;
        vertexInputState.VertexAttributeDescriptionCount = 0;
        vertexInputState.PVertexAttributeDescriptions = null;

        PipelineInputAssemblyStateCreateInfo inputAssemblerState = new();
        inputAssemblerState.SType = StructureType.PipelineInputAssemblyStateCreateInfo;
        inputAssemblerState.Topology = PrimitiveTopology.TriangleList;
        inputAssemblerState.PrimitiveRestartEnable = false;

        PipelineViewportStateCreateInfo viewportState = new();
        viewportState.SType = StructureType.PipelineViewportStateCreateInfo;
        viewportState.ViewportCount = 1;
        viewportState.ScissorCount = 1;

        PipelineRasterizationStateCreateInfo rasterizerState = new();
        rasterizerState.SType = StructureType.PipelineRasterizationStateCreateInfo;
        rasterizerState.DepthClampEnable = false;
        rasterizerState.RasterizerDiscardEnable = false;
        rasterizerState.PolygonMode = PolygonMode.Fill;
        rasterizerState.LineWidth = 1.0f;
        rasterizerState.DepthBiasEnable = false;
        
        PipelineMultisampleStateCreateInfo multisampling = new();
        multisampling.SType = StructureType.PipelineMultisampleStateCreateInfo;
        multisampling.SampleShadingEnable = false;
        multisampling.RasterizationSamples = SampleCountFlags.Count1Bit;
        multisampling.MinSampleShading = 1.0f;
        multisampling.PSampleMask = null;
        multisampling.AlphaToCoverageEnable = false;
        multisampling.AlphaToOneEnable = false;
        
        PipelineColorBlendAttachmentState colorBlendAttachment = new();
        colorBlendAttachment.ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                              ColorComponentFlags.BBit | ColorComponentFlags.ABit;
        colorBlendAttachment.BlendEnable = false;
        colorBlendAttachment.SrcColorBlendFactor = BlendFactor.One;
        colorBlendAttachment.DstColorBlendFactor = BlendFactor.Zero;
        colorBlendAttachment.ColorBlendOp = BlendOp.Add;
        colorBlendAttachment.SrcAlphaBlendFactor = BlendFactor.One;
        colorBlendAttachment.DstAlphaBlendFactor = BlendFactor.Zero;
        colorBlendAttachment.AlphaBlendOp = BlendOp.Add;
        
        PipelineColorBlendStateCreateInfo colorBlending = new();
        colorBlending.SType = StructureType.PipelineColorBlendStateCreateInfo;
        colorBlending.LogicOpEnable = false;
        colorBlending.LogicOp = LogicOp.Copy;
        colorBlending.AttachmentCount = 1;
        colorBlending.PAttachments = &colorBlendAttachment;
        colorBlending.BlendConstants[0] = 0.0f;
        colorBlending.BlendConstants[1] = 0.0f;
        colorBlending.BlendConstants[2] = 0.0f;
        colorBlending.BlendConstants[3] = 0.0f;

        PipelineLayoutCreateInfo pipelineLayoutCreateInfo = new();
        pipelineLayoutCreateInfo.SType = StructureType.PipelineLayoutCreateInfo;

        Result pipelineLayoutResult =
            vk.CreatePipelineLayout(logicalDevice, pipelineLayoutCreateInfo, null, out pipelineLayout);
        ThrowHelpers.ThrowVulkanException(pipelineLayoutResult);

        GraphicsPipelineCreateInfo pipelineCreateInfo = new();
        pipelineCreateInfo.SType = StructureType.GraphicsPipelineCreateInfo;
        pipelineCreateInfo.StageCount = 2;
        pipelineCreateInfo.PStages = programmableStages;
        pipelineCreateInfo.PVertexInputState = &vertexInputState;
        pipelineCreateInfo.PInputAssemblyState = &inputAssemblerState;
        pipelineCreateInfo.PDynamicState = &dynamicState;
        pipelineCreateInfo.PMultisampleState = &multisampling;
        pipelineCreateInfo.PColorBlendState = &colorBlending;
        pipelineCreateInfo.PRasterizationState = &rasterizerState;
        pipelineCreateInfo.PViewportState = &viewportState;
        pipelineCreateInfo.Layout = pipelineLayout;
        pipelineCreateInfo.RenderPass = renderPass;
        pipelineCreateInfo.Subpass = 0;

        Debug.Log("Creating graphics pipeline");

        fixed (Pipeline* pipelinePtr = &this.graphicsPipeline)
        {
            Result pipelineResult =
                vk.CreateGraphicsPipelines(logicalDevice, default, 1u, &pipelineCreateInfo, null,
                    pipelinePtr);
            ThrowHelpers.ThrowVulkanException(pipelineResult);
        }

        vk.DestroyShaderModule(logicalDevice, vertShader, null);
        vk.DestroyShaderModule(logicalDevice, fragShader, null);
    }

    public void Present()
    {
        vk.WaitForFences(logicalDevice, 1, inFlightFence, true, ulong.MaxValue);
        vk.ResetFences(logicalDevice, 1, inFlightFence);

        uint imageIndex = 0;
        khrSwapchainExtension.AcquireNextImage(logicalDevice, swapchain, ulong.MaxValue, imageAcquiredSemaphore,
            default,
            ref imageIndex);

        vk.ResetCommandBuffer(commandBuffer, 0);
        RecordCommandBuffer(commandBuffer, (int)imageIndex);

        SubmitInfo submitInfo = new();
        submitInfo.SType = StructureType.SubmitInfo;

        unsafe
        {
            fixed (Semaphore* signalSemaphores = &this.renderFinishedSemaphore)
            fixed (CommandBuffer* commandBuffers = &this.commandBuffer)
            fixed (SwapchainKHR* swapchains = &this.swapchain)
            fixed (Semaphore* waitSemaphores = &this.imageAcquiredSemaphore)
            {
                PipelineStageFlags* waitStages = stackalloc PipelineStageFlags[1];
                waitStages[0] = PipelineStageFlags.ColorAttachmentOutputBit;
                
                submitInfo.PWaitSemaphores = waitSemaphores;
                submitInfo.WaitSemaphoreCount = 1;
                submitInfo.CommandBufferCount = 1;
                submitInfo.PCommandBuffers = commandBuffers;
                submitInfo.SignalSemaphoreCount = 1;
                submitInfo.PSignalSemaphores = signalSemaphores;

                Result result = vk.QueueSubmit(graphicsQueue, 1, submitInfo, inFlightFence);
                ThrowHelpers.ThrowVulkanException(result);

                PresentInfoKHR presentInfo = new();
                presentInfo.SType = StructureType.PresentInfoKhr;
                presentInfo.WaitSemaphoreCount = 1;
                presentInfo.PWaitSemaphores = signalSemaphores;
                presentInfo.SwapchainCount = 1;
                presentInfo.PSwapchains = swapchains;
                presentInfo.PImageIndices = &imageIndex;
                result = khrSwapchainExtension.QueuePresent(graphicsQueue, presentInfo);
                ThrowHelpers.ThrowVulkanException(result);
            }
        }
    }
    
    private unsafe void CreateFramebuffers()
    {
        framebuffers = new Framebuffer[imageViews.Length];

        fixed(Framebuffer* framebufferPtr = framebuffers)
        fixed (ImageView* viewPtr = imageViews)
        {
            for (var i = 0; i < framebuffers.Length; i++)
            {
                ImageView* imageView = viewPtr + i;

                FramebufferCreateInfo createInfo = new();
                createInfo.SType = StructureType.FramebufferCreateInfo;
                createInfo.RenderPass = renderPass;
                createInfo.PAttachments = imageView;
                createInfo.AttachmentCount = 1;
                createInfo.Width = swapchainExtent.Width;
                createInfo.Height = swapchainExtent.Height;
                createInfo.Layers = 1;

                Debug.Log("Creating swapcvhain framebuffer " + i);
                Result result = vk.CreateFramebuffer(logicalDevice, createInfo, null, framebufferPtr + i);
                ThrowHelpers.ThrowVulkanException(result);
            }
        }
    }
    
    private void CreateInstance()
    {
        ApplicationInfo vkApplicationInfo = new();
        vkApplicationInfo.SType = StructureType.ApplicationInfo;

        // Set up the AppInfo struct.
        unsafe
        {
            vkApplicationInfo.PEngineName = (byte*) SilkMarshal.StringToPtr(Application.EngineName);
            vkApplicationInfo.EngineVersion = Vk.MakeVersion((uint) Application.EngineVersion.Major, (uint) Application.EngineVersion.Minor, (uint) Application.EngineVersion.Build);
            vkApplicationInfo.PApplicationName = (byte*)SilkMarshal.StringToPtr(Application.ProductName);
            vkApplicationInfo.ApplicationVersion = Vk.MakeVersion(1, 0, 0);
            vkApplicationInfo.ApiVersion = Vk.Version10;
        }

        InstanceCreateInfo createInfo = new();
        createInfo.SType = StructureType.InstanceCreateInfo;
        
        unsafe
        {
            createInfo.PApplicationInfo = &vkApplicationInfo;
            createInfo.EnabledExtensionCount = extensionCount;
            createInfo.PpEnabledExtensionNames = extensions;
            createInfo.EnabledLayerCount = 0;
        }


        Debug.Log("Attempting to create Vulkan API instance...");
        
        Result result;
        unsafe
        {
            result = vk.CreateInstance(in createInfo, null, out instance);
        }

        ThrowHelpers.ThrowVulkanException(result);

        Debug.Log("Vulkan is online.");
    }

    private void CreateWindowSurface()
    {
        VkNonDispatchableHandle surfaceHandle = this.window.CreateWindowSurface(this.Handle);
        this.surface = new SurfaceKHR(surfaceHandle.Handle);
    }
    
    private void FindPhysicalDevice()
    {
        Debug.Log("Attempting to find a suitable Graphics Processing Unit... we like to be very verbose with these debug messages :)");
        
        uint deviceCount = 0;
        unsafe
        {
            vk.EnumeratePhysicalDevices(instance, ref deviceCount, null);
        }

        Debug.Log($"Found {deviceCount} graphics cards in the system");

        if (deviceCount == 0)
            ThrowHelpers.ThrowNoGpuDetected();

        Span<PhysicalDevice> devices = stackalloc PhysicalDevice[(int) deviceCount];

        unsafe
        {
            vk.EnumeratePhysicalDevices(instance, &deviceCount, devices);
        }

        foreach (PhysicalDevice device in devices)
        {
            if (IsPhysicalDeviceSuitable(device))
            {
                this.physicalDevice = device;
                break;
            }
        }

        if (this.physicalDevice.Handle == default)
            ThrowHelpers.ThrowNoGpuDetected();

        Debug.Log("Found a physical device worthy of being sparked");

        vk.GetPhysicalDeviceProperties(this.physicalDevice, out deviceProperties);

        Debug.Log("Device properties:");

        unsafe
        {
            fixed (byte* deviceName = deviceProperties.DeviceName)
            {
                this.DeviceName = SilkMarshal.PtrToString((nint)deviceName) ?? "Graphics Device";
            }
        }

        Debug.Log($"  Device name: {DeviceName}");
    }

    private bool IsPhysicalDeviceSuitable(PhysicalDevice device)
    {
        QueueFamilyIndices indices = FindQueueFamilyIndices(device);

        bool supportsRequiredExtensions = CheckDeviceExtensionSupport(device);

        bool isSwapChainAdequate = false;
        if (supportsRequiredExtensions)
        {
            SwapChainSupportDetails supportDetails = QuerySwapChainSupportDetails(device);
            isSwapChainAdequate = supportDetails.Formats.Any()
                                  && supportDetails.PresentModes.Any();
        }
        
        return indices.IsValid && supportsRequiredExtensions && isSwapChainAdequate;
    }

    private bool CheckDeviceExtensionSupport(PhysicalDevice device)
    {
        uint extensionCount = 0;
        List<string> names = new List<string>();
        
        unsafe
        {
            vk.EnumerateDeviceExtensionProperties(device, (byte*) null, &extensionCount, null);
            ExtensionProperties* extensionProperties = stackalloc ExtensionProperties[(int) extensionCount];
            vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &extensionCount, extensionProperties);

            for (var i = 0; i < extensionCount; i++)
            {
                byte* rawName = extensionProperties[i].ExtensionName;
                string? name = SilkMarshal.PtrToString((nint)rawName);
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }

        return requiredExtensionNames.All(x => names.Contains(x));
    }

    private QueueFamilyIndices FindQueueFamilyIndices(PhysicalDevice physicalDevice)
    {
        QueueFamilyIndices indices = new QueueFamilyIndices();

        uint queueFamilyCount = 0;

        unsafe
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref queueFamilyCount, null);
        }

        Span<QueueFamilyProperties> properties = stackalloc QueueFamilyProperties[(int)queueFamilyCount];
        
        unsafe 
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, properties);
        }

        uint i = 0;
        foreach (QueueFamilyProperties family in properties)
        {
            if ((family.QueueFlags & QueueFlags.GraphicsBit) != 0)
                indices.GraphicsFamily = i;

            khrSurfaceExtension.GetPhysicalDeviceSurfaceSupport(physicalDevice, i, surface, out Bool32 surfaceSupport);
            if (surfaceSupport)
                indices.PresentFamily = i;
            
            if (indices.IsValid)
                break;
            
            i++;
        }

        return indices;
    }

    private void CreateLogicalDevice()
    {
        Debug.Log("Querying device features...");
        QueueFamilyIndices indices = this.FindQueueFamilyIndices(this.physicalDevice);

        float queuePriority = 1.0f;
        List<DeviceQueueCreateInfo> queueCreateRequests = new List<DeviceQueueCreateInfo>();

        foreach (uint? queueFamilyIndex in indices.Indices.Distinct())
        {
            if (!queueFamilyIndex.HasValue)
                continue;
            
            DeviceQueueCreateInfo queueCreateInfo = new();
            queueCreateInfo.SType = StructureType.DeviceQueueCreateInfo;
            queueCreateInfo.QueueFamilyIndex = queueFamilyIndex.Value;
            queueCreateInfo.QueueCount = 1;
            
            unsafe
            {
                queueCreateInfo.PQueuePriorities = &queuePriority;
            }
            
            queueCreateRequests.Add(queueCreateInfo);
        }

        PhysicalDeviceFeatures features = new();

        DeviceCreateInfo deviceCreateInfo = new();
        deviceCreateInfo.SType = StructureType.DeviceCreateInfo;

        unsafe
        {
            DeviceQueueCreateInfo* createInfoBuffer = stackalloc DeviceQueueCreateInfo[queueCreateRequests.Count];
            for (var i = 0; i < queueCreateRequests.Count; i++)
            {
                createInfoBuffer[i] = queueCreateRequests[i];
            }
            
            deviceCreateInfo.QueueCreateInfoCount = 1;
            deviceCreateInfo.PQueueCreateInfos = createInfoBuffer;
            deviceCreateInfo.PEnabledFeatures = &features;

            deviceCreateInfo.EnabledExtensionCount = (uint)requiredExtensionNames.Length;
            byte** enabledExtensions = stackalloc byte*[requiredExtensionNames.Length];

            for (var i = 0; i < requiredExtensionNames.Length; i++)
            {
                byte* rawName = (byte*) SilkMarshal.StringToPtr(requiredExtensionNames[i]);
                enabledExtensions[i] = rawName;
            }

            deviceCreateInfo.PpEnabledExtensionNames = enabledExtensions;
        }

        Debug.Log("Creating Vulkan logical device for " + DeviceName);
        
        unsafe
        {
            Result result = vk.CreateDevice(this.physicalDevice, in deviceCreateInfo, null, out this.logicalDevice);
            ThrowHelpers.ThrowVulkanException(result);
        }

        Debug.Log("Getting graphics queue handle...");
        vk.GetDeviceQueue(this.logicalDevice, indices.GraphicsFamily.GetValueOrDefault(), 0, out graphicsQueue);
        vk.GetDeviceQueue(this.logicalDevice, indices.PresentFamily.GetValueOrDefault(), 0, out presentQueue);

        Debug.Log("Right... that's that. GPU's up.");
    }

    private void CreateSwapchain()
    {
        Debug.Log("Creating Vulkan swapchain...");
        SwapChainSupportDetails supportDetails = QuerySwapChainSupportDetails(this.physicalDevice);

        (int width, int height) = this.window.FramebufferSize;

        PresentModeKHR presentMode = supportDetails.BestPresentMode;
        SurfaceFormatKHR surfaceFormat = supportDetails.BestSurfaceFormat;
        Extent2D framebufferSize = supportDetails.GetBestFramebufferSize(width, height);

        uint imageCount = supportDetails.Capabilities.MinImageCount + 1;
        if (supportDetails.Capabilities.MaxImageCount != 0)
        {
            imageCount = Math.Min(supportDetails.Capabilities.MaxImageCount, imageCount);
        }

        SwapchainCreateInfoKHR swapchainCreateInfo = new();
        swapchainCreateInfo.SType = StructureType.SwapchainCreateInfoKhr;
        swapchainCreateInfo.Surface = this.surface;
        swapchainCreateInfo.MinImageCount = imageCount;
        swapchainCreateInfo.ImageFormat = surfaceFormat.Format;
        swapchainCreateInfo.ImageColorSpace = surfaceFormat.ColorSpace;
        swapchainCreateInfo.ImageExtent = framebufferSize;
        swapchainCreateInfo.ImageArrayLayers = 1;
        swapchainCreateInfo.ImageUsage = ImageUsageFlags.ColorAttachmentBit;

        QueueFamilyIndices indices = FindQueueFamilyIndices(this.physicalDevice);

        unsafe
        {
            if (indices.GraphicsFamily != indices.PresentFamily)
            {
                swapchainCreateInfo.ImageSharingMode = SharingMode.Concurrent;
                swapchainCreateInfo.QueueFamilyIndexCount = 2;
                uint* queues = stackalloc uint[2]
                {
                    indices.GraphicsFamily.GetValueOrDefault(),
                    indices.PresentFamily.GetValueOrDefault()
                };
                swapchainCreateInfo.PQueueFamilyIndices = queues;
            }
            else
            {
                swapchainCreateInfo.ImageSharingMode = SharingMode.Exclusive;
                swapchainCreateInfo.QueueFamilyIndexCount = 0;
                swapchainCreateInfo.PQueueFamilyIndices = null;
            }
        }

        swapchainCreateInfo.PreTransform = supportDetails.Capabilities.CurrentTransform;
        swapchainCreateInfo.CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr;
        swapchainCreateInfo.PresentMode = presentMode;
        swapchainCreateInfo.Clipped = true;
        swapchainCreateInfo.OldSwapchain = default;

        unsafe
        {
            Result result =
                khrSwapchainExtension.CreateSwapchain(this.logicalDevice, swapchainCreateInfo, null, out swapchain);
            ThrowHelpers.ThrowVulkanException(result);
        }

        uint swapchainImageCount = 0;

        unsafe
        {
            khrSwapchainExtension.GetSwapchainImages(logicalDevice, swapchain, &swapchainImageCount, null);
            this.swapchainImages = new Image[(int)swapchainImageCount];

            fixed (Image* ptr = this.swapchainImages)
            {
                this.khrSwapchainExtension.GetSwapchainImages(this.logicalDevice, this.swapchain, &swapchainImageCount,
                    ptr);
            }
        }

        this.swapchainFormat = swapchainCreateInfo.ImageFormat;
        this.swapchainExtent = swapchainCreateInfo.ImageExtent;
    }

    public void CreateImageViews()
    {
        this.imageViews = new ImageView[swapchainImages.Length];

        for (var i = 0; i < this.swapchainImages.Length; i++)
        {
            ImageViewCreateInfo createInfo = new();
            createInfo.SType = StructureType.ImageViewCreateInfo;
            createInfo.Image = swapchainImages[i];
            createInfo.ViewType = ImageViewType.Type2D;
            createInfo.Format = swapchainFormat;

            createInfo.Components.R = ComponentSwizzle.Identity;
            createInfo.Components.G = ComponentSwizzle.Identity;
            createInfo.Components.B = ComponentSwizzle.Identity;
            createInfo.Components.A = ComponentSwizzle.Identity;
            
            createInfo.SubresourceRange.AspectMask = ImageAspectFlags.ColorBit;
            createInfo.SubresourceRange.BaseMipLevel = 0;
            createInfo.SubresourceRange.LevelCount = 1;
            createInfo.SubresourceRange.BaseArrayLayer = 0;
            createInfo.SubresourceRange.LayerCount = 1;

            unsafe
            {
                Debug.Log($"Creating swapchain image view {i}");
                Result result = vk.CreateImageView(logicalDevice, createInfo, null, out ImageView view);
                ThrowHelpers.ThrowVulkanException(result);

                this.imageViews[i] = view;
            }
        }
    }
    
    private SwapChainSupportDetails QuerySwapChainSupportDetails(PhysicalDevice device)
    {
        SwapChainSupportDetails details = new();

        unsafe
        {
            khrSurfaceExtension.GetPhysicalDeviceSurfaceCapabilities(device, surface, &details.Capabilities);

            uint formatCount = 0;
            uint presentModeCount = 0;

            khrSurfaceExtension.GetPhysicalDeviceSurfaceFormats(device, surface, &formatCount, null);
            khrSurfaceExtension.GetPhysicalDeviceSurfacePresentModes(device, surface, &presentModeCount, null);

            SurfaceFormatKHR* formats = stackalloc SurfaceFormatKHR[(int) formatCount];
            PresentModeKHR* modes = stackalloc PresentModeKHR[(int)presentModeCount];
            
            khrSurfaceExtension.GetPhysicalDeviceSurfaceFormats(device, surface, &formatCount, formats);
            khrSurfaceExtension.GetPhysicalDeviceSurfacePresentModes(device, surface, &presentModeCount, modes);

            UnsafeHelpers.CopyToList(formats, formatCount, details.Formats);
            UnsafeHelpers.CopyToList(modes, presentModeCount, details.PresentModes);
        }

        return details;
    }

    private struct QueueFamilyIndices
    {
        public uint? GraphicsFamily;
        public uint? PresentFamily;

        public IEnumerable<uint?> Indices
        {
            get
            {
                yield return GraphicsFamily;
                yield return PresentFamily;
            }
        }
        
        public bool IsValid
            => GraphicsFamily != null
            && PresentFamily != null;
    }

    private ShaderModule CreateShaderModule(Shader shader)
    {
        Debug.Log("Creating Vulkan shader module...");
        ShaderModule result;
        ShaderModuleCreateInfo createInfo = new();

        createInfo.SType = StructureType.ShaderModuleCreateInfo;

        unsafe
        {
            createInfo.CodeSize = (uint) shader.Length;
            fixed (byte* code = shader.Bytecode)
            {
                createInfo.PCode = (uint*)code;

                Result vkResult = vk.CreateShaderModule(this.logicalDevice, createInfo, null, out result);
                ThrowHelpers.ThrowVulkanException(vkResult);
            }
        }

        return result;
    }

    private struct SwapChainSupportDetails
    {
        public SurfaceCapabilitiesKHR Capabilities = default;
        public List<SurfaceFormatKHR> Formats = new List<SurfaceFormatKHR>();
        public List<PresentModeKHR> PresentModes = new List<PresentModeKHR>();

        public SwapChainSupportDetails()
        {
            
        }
        
        public SurfaceFormatKHR BestSurfaceFormat
            => Formats.First();

        public PresentModeKHR BestPresentMode
        {
            get
            {
                if (this.PresentModes.Contains(PresentModeKHR.MailboxKhr))
                    return PresentModeKHR.MailboxKhr;

                return PresentModeKHR.FifoKhr;
            }
        }

        public Extent2D GetBestFramebufferSize(int width, int height)
        {
            if (Capabilities.CurrentExtent.Width != uint.MaxValue)
                return Capabilities.CurrentExtent;
            
            Extent2D min = Capabilities.MinImageExtent;
            Extent2D max = Capabilities.MaxImageExtent;

            Extent2D best = new Extent2D(
                (uint)Math.Max(min.Width, Math.Min(max.Width, width)),
                (uint)Math.Max(min.Width, Math.Min(max.Height, height))
            );

            return best;
        }
    }
}