using System.Diagnostics;
using System.Net;
using KoalaWiki.MCP.ModelContextProtocol;
using KoalaWiki.plugins;
using KoalaWiki.Tools;

#pragma warning disable SKEXP0070

#pragma warning disable SKEXP0010

namespace KoalaWiki;

/// <summary>
/// 提供一个静态方法来创建和配置一个内核实例，用于各种基于ai的操作。
/// KernelFactory类负责设置必要的服务、插件和配置
/// 内核需要的，包括聊天完成服务，日志记录和文件处理功能。
/// 它支持多个AI模型提供者，并允许可选的代码分析功能。
/// </summary>
public static class KernelFactory
{
    public static async Task<Kernel> GetKernel(string chatEndpoint,
        string apiKey,
        string gitPath,
        string model, bool isCodeAnalysis = true,
        List<string>? files = null, Action<IKernelBuilder>? kernelBuilderAction = null)
    {
        return await GetKernel(chatEndpoint, apiKey, gitPath, model, isCodeAnalysis, files, kernelBuilderAction, includePlugins: true);
    }

    /// <summary>
    /// 创建一个纯净的内核实例，不包含任何插件，用于文档生成等不需要工具调用的场景
    /// </summary>
    public static async Task<Kernel> GetCleanKernel(string chatEndpoint,
        string apiKey,
        string gitPath,
        string model, List<string>? files = null, Action<IKernelBuilder>? kernelBuilderAction = null)
    {
        return await GetKernel(chatEndpoint, apiKey, gitPath, model, false, files, kernelBuilderAction, includePlugins: false);
    }

    private static async Task<Kernel> GetKernel(string chatEndpoint,
        string apiKey,
        string gitPath,
        string model, bool isCodeAnalysis = true,
        List<string>? files = null, Action<IKernelBuilder>? kernelBuilderAction = null, bool includePlugins = true)
    {
        using var activity = Activity.Current?.Source.StartActivity();
        activity?.SetTag("model", model);
        activity?.SetTag("provider", OpenAIOptions.ModelProvider);
        activity?.SetTag("code_analysis_enabled", isCodeAnalysis);
        activity?.SetTag("git_path", gitPath);

        var kernelBuilder = Kernel.CreateBuilder();

        kernelBuilder.Services.AddSerilog(Log.Logger);

        kernelBuilder.Services.AddSingleton<IPromptRenderFilter, LanguagePromptFilter>();

        // 创建优化的HTTP客户端
        var httpClient = new HttpClient(new KoalaHttpClientHandler()
        {
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Brotli |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.None
        })
        {
            Timeout = TimeSpan.FromSeconds(300), // 增加超时时间到5分钟
        };

        if (OpenAIOptions.ModelProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            kernelBuilder.AddOpenAIChatCompletion(model, new Uri(chatEndpoint), apiKey, httpClient: httpClient);
            
            // 针对Qwen模型的特殊配置
            if (IsQwenModel(model))
            {
                activity?.SetTag("qwen_optimization", "enabled");
                Log.Logger.Information("检测到Qwen模型，应用优化配置");
            }
        }
        else if (OpenAIOptions.ModelProvider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase))
        {
            kernelBuilder.AddAzureOpenAIChatCompletion(model, chatEndpoint, apiKey, httpClient: httpClient);
        }
        else
        {
            activity?.SetStatus(ActivityStatusCode.Error, "不支持的模型提供者");
            throw new Exception("暂不支持：" + OpenAIOptions.ModelProvider + "，请使用OpenAI、AzureOpenAI");
        }

        if (isCodeAnalysis)
        {
            kernelBuilder.Plugins.AddFromPromptDirectory(Path.Combine(AppContext.BaseDirectory, "plugins",
                "CodeAnalysis"));
            activity?.SetTag("plugins.code_analysis", "loaded");
        }

        // 只有在需要插件时才添加
        if (includePlugins)
        {
            // 添加文件函数
            var fileFunction = new FileTool(gitPath, files);
            kernelBuilder.Plugins.AddFromObject(fileFunction, "file");
            activity?.SetTag("plugins.file_function", "loaded");

            kernelBuilder.Plugins.AddFromType<AgentTool>("agent");
            activity?.SetTag("plugins.agent_tool", "loaded");

            if (DocumentOptions.McpStreamable.Count > 0)
            {
                foreach (var mcpStreamable in DocumentOptions.McpStreamable)
                {
                    try
                    {
                        await kernelBuilder.Plugins.AddMcpFunctionsFromSseServerAsync(mcpStreamable.Value,
                            mcpStreamable.Key);
                    }
                    catch (Exception e)
                    {
                        Log.Logger.Error(e, "从MCP服务器加载工具失败: {ServerUrl}", mcpStreamable.Value);
                        activity?.SetStatus(ActivityStatusCode.Error, "从MCP服务器加载工具失败");
                    }
                }
            }
        }

        kernelBuilderAction?.Invoke(kernelBuilder);

        var kernel = kernelBuilder.Build();

        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.SetTag("kernel.created", true);

        return kernel;
    }

    /// <summary>
    /// 检查是否为Qwen模型
    /// </summary>
    private static bool IsQwenModel(string model)
    {
        return !string.IsNullOrEmpty(model) && 
               (model.Contains("qwen", StringComparison.OrdinalIgnoreCase) || 
                model.Contains("coder", StringComparison.OrdinalIgnoreCase));
    }
}
