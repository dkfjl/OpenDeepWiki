using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.Configuration;

namespace KoalaWiki.Prompts;

public class PromptContext
{
    private static readonly string PromptPath = Path.Combine("./", "Prompts");
    private static IConfiguration? configuration;

    /// <summary>
    /// 初始化配置
    /// </summary>
    public static void Initialize(IConfiguration config)
    {
        configuration = config;
    }

    /// <summary>
    /// 仓库提示词
    /// </summary>
    private static string WarehousePrompt => Path.Combine(PromptPath, "Warehouse");

    private static string ChatPrompt => Path.Combine(PromptPath, "Chat");

    private static string Mem0Prompt => Path.Combine(PromptPath, "Mem0");

    public static async Task<string> Chat(
        string name,
        KernelArguments args, string model)
    {
        var fileName = name + ".md";
        var originalFileName = fileName;

        if (model != null && !string.IsNullOrEmpty(model))
        {
            // 优先级1: 完整模型名匹配（保持现有逻辑）
            var fullModelFileName = $"{model}_{fileName}";
            var fullModelPath = Path.Combine(ChatPrompt, fullModelFileName);
            
            if (File.Exists(fullModelPath))
            {
                fileName = fullModelFileName;
            }
            else
            {
                // 优先级2: 基于ENDPOINT的智能检测（新增）
                var endpointPrefix = GetModelPrefixByEndpoint(model);
                if (!string.IsNullOrEmpty(endpointPrefix))
                {
                    var endpointFileName = $"{endpointPrefix}_{fileName}";
                    var endpointPath = Path.Combine(ChatPrompt, endpointFileName);
                    
                    if (File.Exists(endpointPath))
                    {
                        fileName = endpointFileName;
                    }
                    else
                    {
                        // 优先级3: 基于模型名的前缀匹配（新增）
                        var modelPrefix = GetModelPrefixByName(model);
                        if (!string.IsNullOrEmpty(modelPrefix))
                        {
                            var prefixFileName = $"{modelPrefix}_{fileName}";
                            var prefixPath = Path.Combine(ChatPrompt, prefixFileName);
                            
                            if (File.Exists(prefixPath))
                            {
                                fileName = prefixFileName;
                            }
                            else
                            {
                                // 回退到默认文件名
                                fileName = originalFileName;
                                Log.Logger.Warning(
                                    "Chat prompt not found with model: {Model}, falling back to default name: {FileName}", model,
                                    fileName);
                            }
                        }
                        else
                        {
                            // 回退到默认文件名
                            fileName = originalFileName;
                            Log.Logger.Warning(
                                "Chat prompt not found with model: {Model}, falling back to default name: {FileName}", model,
                                fileName);
                        }
                    }
                }
                else
                {
                    // 保持原有逻辑：尝试完整模型名，失败则回退
                    fileName = fullModelFileName;
                    if (!File.Exists(Path.Combine(ChatPrompt, fileName)))
                    {
                        fileName = originalFileName;
                        Log.Logger.Warning(
                            "Chat prompt not found with model: {Model}, falling back to default name: {FileName}", model,
                            fileName);
                    }
                }
            }
        }

        var finalPath = Path.Combine(ChatPrompt, fileName);
        if (!File.Exists(finalPath))
        {
            throw new FileNotFoundException($"Chat prompt not found name:{fileName}");
        }

        var values = await File.ReadAllTextAsync(finalPath);

        return args.Aggregate(values,
            (current, value) =>
                current.Replace("{{$" + value.Key + "}}", value.Value?.ToString(),
                    StringComparison.CurrentCultureIgnoreCase)) + Prompt.Language;
    }

    public static async Task<string> Mem0(string name,
        KernelArguments args, string model)
    {
        var fileName = name + ".md";
        var originalFileName = fileName;

        if (model != null && !string.IsNullOrEmpty(model))
        {
            // 优先级1: 完整模型名匹配（保持现有逻辑）
            var fullModelFileName = $"{model}_{fileName}";
            var fullModelPath = Path.Combine(Mem0Prompt, fullModelFileName);
            
            if (File.Exists(fullModelPath))
            {
                fileName = fullModelFileName;
            }
            else
            {
                // 优先级2: 基于ENDPOINT的智能检测（新增）
                var endpointPrefix = GetModelPrefixByEndpoint(model);
                if (!string.IsNullOrEmpty(endpointPrefix))
                {
                    var endpointFileName = $"{endpointPrefix}_{fileName}";
                    var endpointPath = Path.Combine(Mem0Prompt, endpointFileName);
                    
                    if (File.Exists(endpointPath))
                    {
                        fileName = endpointFileName;
                    }
                    else
                    {
                        // 优先级3: 基于模型名的前缀匹配（新增）
                        var modelPrefix = GetModelPrefixByName(model);
                        if (!string.IsNullOrEmpty(modelPrefix))
                        {
                            var prefixFileName = $"{modelPrefix}_{fileName}";
                            var prefixPath = Path.Combine(Mem0Prompt, prefixFileName);
                            
                            if (File.Exists(prefixPath))
                            {
                                fileName = prefixFileName;
                            }
                            else
                            {
                                // 回退到默认文件名
                                fileName = originalFileName;
                                Log.Logger.Warning(
                                    "Mem0 prompt not found with model: {Model}, falling back to default name: {FileName}", model,
                                    fileName);
                            }
                        }
                        else
                        {
                            // 回退到默认文件名
                            fileName = originalFileName;
                            Log.Logger.Warning(
                                "Mem0 prompt not found with model: {Model}, falling back to default name: {FileName}", model,
                                fileName);
                        }
                    }
                }
                else
                {
                    // 保持原有逻辑：尝试完整模型名，失败则回退
                    fileName = fullModelFileName;
                    if (!File.Exists(Path.Combine(Mem0Prompt, fileName)))
                    {
                        fileName = originalFileName;
                        Log.Logger.Warning(
                            "Mem0 prompt not found with model: {Model}, falling back to default name: {FileName}", model,
                            fileName);
                    }
                }
            }
        }

        var finalPath = Path.Combine(Mem0Prompt, fileName);
        if (!File.Exists(finalPath))
        {
            throw new FileNotFoundException($"Mem0 prompt not found name:{fileName}");
        }

        var values = await File.ReadAllTextAsync(finalPath);

        return args.Aggregate(values,
            (current, value) =>
                current.Replace("{{$" + value.Key + "}}", value.Value?.ToString(),
                    StringComparison.CurrentCultureIgnoreCase)) + Prompt.Language;
    }

    // 创建一个默认的索引
    public static async Task<string> Warehouse(
        string name,
        KernelArguments args, string model)
    {
        var fileName = name + ".md";
        var originalFileName = fileName;

        if (model != null && !string.IsNullOrEmpty(model))
        {
            // 优先级1: 完整模型名匹配（保持现有逻辑）
            var fullModelFileName = $"{model}_{fileName}";
            var fullModelPath = Path.Combine(WarehousePrompt, fullModelFileName);
            
            if (File.Exists(fullModelPath))
            {
                fileName = fullModelFileName;
            }
            else
            {
                // 优先级2: 基于ENDPOINT的智能检测（新增）
                var endpointPrefix = GetModelPrefixByEndpoint(model);
                if (!string.IsNullOrEmpty(endpointPrefix))
                {
                    var endpointFileName = $"{endpointPrefix}_{fileName}";
                    var endpointPath = Path.Combine(WarehousePrompt, endpointFileName);
                    
                    if (File.Exists(endpointPath))
                    {
                        fileName = endpointFileName;
                    }
                    else
                    {
                        // 优先级3: 基于模型名的前缀匹配（新增）
                        var modelPrefix = GetModelPrefixByName(model);
                        if (!string.IsNullOrEmpty(modelPrefix))
                        {
                            var prefixFileName = $"{modelPrefix}_{fileName}";
                            var prefixPath = Path.Combine(WarehousePrompt, prefixFileName);
                            
                            if (File.Exists(prefixPath))
                            {
                                fileName = prefixFileName;
                            }
                            else
                            {
                                // 回退到默认文件名
                                fileName = originalFileName;
                                Log.Logger.Warning(
                                    "Warehouse prompt not found with model: {Model}, falling back to default name: {FileName}", model,
                                    fileName);
                            }
                        }
                        else
                        {
                            // 回退到默认文件名
                            fileName = originalFileName;
                            Log.Logger.Warning(
                                "Warehouse prompt not found with model: {Model}, falling back to default name: {FileName}", model,
                                fileName);
                        }
                    }
                }
                else
                {
                    // 保持原有逻辑：尝试完整模型名，失败则回退
                    fileName = fullModelFileName;
                    if (!File.Exists(Path.Combine(WarehousePrompt, fileName)))
                    {
                        fileName = originalFileName;
                        Log.Logger.Warning(
                            "Warehouse prompt not found with model: {Model}, falling back to default name: {FileName}", model,
                            fileName);
                    }
                }
            }
        }

        var finalPath = Path.Combine(WarehousePrompt, fileName);
        if (!File.Exists(finalPath))
        {
            throw new FileNotFoundException($"Warehouse prompt not found name:{fileName}");
        }

        var values = await File.ReadAllTextAsync(finalPath);

        return args.Aggregate(values,
            (current, value) =>
                current.Replace("{{$" + value.Key + "}}", value.Value?.ToString(),
                    StringComparison.CurrentCultureIgnoreCase)) + Prompt.Language;
    }

    /// <summary>
    /// 基于ENDPOINT获取模型前缀（新增逻辑）
    /// </summary>
    private static string GetModelPrefixByEndpoint(string model)
    {
        // 获取ENDPOINT配置
        var endpoint = configuration?.GetValue<string>("ENDPOINT") ?? 
                       configuration?.GetValue<string>("Endpoint") ?? 
                       string.Empty;
        
        if (string.IsNullOrEmpty(endpoint)) return string.Empty;
        
        // 阿里云dashscope统一使用qwen
        if (endpoint.Contains("dashscope.aliyuncs.com", StringComparison.CurrentCultureIgnoreCase))
        {
            return "qwen";
        }
        
        // 可以添加其他ENDPOINT映射
        if (endpoint.Contains("api.openai.com", StringComparison.CurrentCultureIgnoreCase))
        {
            return "gpt";
        }
        
        if (endpoint.Contains("api.anthropic.com", StringComparison.CurrentCultureIgnoreCase))
        {
            return "claude";
        }
        
        return string.Empty;
    }

    /// <summary>
    /// 基于模型名获取前缀（新增逻辑）
    /// </summary>
    private static string GetModelPrefixByName(string model)
    {
        if (string.IsNullOrEmpty(model)) return string.Empty;
        
        // 检测qwen系列
        if (model.Contains("qwen", StringComparison.CurrentCultureIgnoreCase))
        {
            return "qwen";
        }
        
        // 检测其他模型系列
        if (model.Contains("gpt", StringComparison.CurrentCultureIgnoreCase))
        {
            return "gpt";
        }
        
        if (model.Contains("claude", StringComparison.CurrentCultureIgnoreCase))
        {
            return "claude";
        }
        
        return string.Empty;
    }
}
