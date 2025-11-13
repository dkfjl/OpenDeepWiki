using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;

namespace KoalaWiki.Prompts;

public class PromptContext
{
    private static readonly string PromptPath = Path.Combine("./", "Prompts");

    /// <summary>
    /// GenerateDocs模板的必需变量列表
    /// </summary>
    private static readonly HashSet<string> GenerateDocsRequiredVariables = new()
    {
        "prompt", "code_files", "title", "git_repository", "branch", "projectType"
    };

    /// <summary>
    /// 仓库提示词
    /// </summary>
    private static string WarehousePrompt => Path.Combine(PromptPath, "Warehouse");

    private static string ChatPrompt => Path.Combine(PromptPath, "Chat");

    private static string Mem0Prompt => Path.Combine(PromptPath, "Mem0");

    /// <summary>
    /// 新增：增强的模板变量替换方法
    /// </summary>
    private static string ReplaceTemplateVariablesWithValidation(
        string template, KernelArguments args, string fileName)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            Log.Logger.Error("模板文件内容为空: {FileName}", fileName);
            throw new InvalidOperationException($"模板文件内容为空: {fileName}");
        }
        
        // 对于GenerateDocs模板，验证必需变量
        if (fileName.Contains("GenerateDocs"))
        {
            ValidateRequiredVariables(args, fileName);
        }
        
        var result = template;
        var replacedVariables = new List<string>();
        var missingVariables = new List<string>();
        var emptyVariables = new List<string>();
        
        // 查找所有模板变量
        var regex = new Regex(@"\{\{\$(\w+)\}\}");
        var matches = regex.Matches(template);
        
        foreach (Match match in matches)
        {
            var variableName = match.Groups[1].Value;
            var placeholder = match.Value;
            
            if (args.TryGetValue(variableName, out var value))
            {
                var stringValue = value?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(stringValue))
                {
                    emptyVariables.Add(variableName);
                    Log.Logger.Warning("模板变量值为空: {VariableName} 在文件: {FileName}", variableName, fileName);
                }
                result = result.Replace(placeholder, stringValue, StringComparison.OrdinalIgnoreCase);
                replacedVariables.Add(variableName);
            }
            else
            {
                missingVariables.Add(variableName);
                Log.Logger.Warning("未找到模板变量: {VariableName} 在文件: {FileName}", variableName, fileName);
            }
        }
        
        // 记录替换结果
        Log.Logger.Information("模板变量替换完成 - 文件: {FileName}, 成功: {ReplacedCount}, 缺失: {MissingCount}, 空值: {EmptyCount}", 
            fileName, replacedVariables.Count, missingVariables.Count, emptyVariables.Count);
        
        if (missingVariables.Any())
        {
            Log.Logger.Warning("缺失的模板变量: {MissingVariables}", string.Join(", ", missingVariables));
        }
        
        if (emptyVariables.Any())
        {
            Log.Logger.Warning("值为空的模板变量: {EmptyVariables}", string.Join(", ", emptyVariables));
        }
        
        // 验证最终结果不为空
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidOperationException($"模板变量替换后内容为空: {fileName}");
        }
        
        return result;
    }

    /// <summary>
    /// 验证必需的模板变量
    /// </summary>
    private static void ValidateRequiredVariables(KernelArguments args, string fileName)
    {
        var missingRequired = new List<string>();
        var emptyRequired = new List<string>();
        
        foreach (var requiredVar in GenerateDocsRequiredVariables)
        {
            if (!args.ContainsKey(requiredVar))
            {
                missingRequired.Add(requiredVar);
            }
            else if (string.IsNullOrWhiteSpace(args[requiredVar]?.ToString()))
            {
                emptyRequired.Add(requiredVar);
            }
        }
        
        if (missingRequired.Any())
        {
            var errorMsg = $"GenerateDocs模板缺少必需变量: {string.Join(", ", missingRequired)}";
            Log.Logger.Error(errorMsg);
            throw new InvalidOperationException(errorMsg);
        }
        
        if (emptyRequired.Any())
        {
            var errorMsg = $"GenerateDocs模板必需变量值为空: {string.Join(", ", emptyRequired)}";
            Log.Logger.Error(errorMsg);
            throw new InvalidOperationException(errorMsg);
        }
        
        Log.Logger.Information("GenerateDocs模板必需变量验证通过");
    }

    public static async Task<string> Chat(
        string name,
        KernelArguments args, string model)
    {
        var fileName = name + ".md";

        // 将model插入fileName到.md前面，如果文件不存在则删除model
        if (model != null && !string.IsNullOrEmpty(model))
        {
            fileName = $"{model}_{fileName}";

            if (!File.Exists(Path.Combine(ChatPrompt, fileName)))
            {
                // 如果文件不存在，则删除model
                fileName = name + ".md";
                Log.Logger.Warning(
                    "Chat prompt not found with model: {Model}, falling back to default name: {FileName}", model,
                    fileName);
            }
        }

        if (!File.Exists(Path.Combine(ChatPrompt, fileName)))
        {
            throw new FileNotFoundException($"Chat prompt not found name:{fileName}");
        }

        var values = await File.ReadAllTextAsync(Path.Combine(ChatPrompt, fileName));

        return args.Aggregate(values,
            (current, value) =>
                current.Replace("{{$" + value.Key + "}}", value.Value?.ToString(),
                    StringComparison.CurrentCultureIgnoreCase)) + Prompt.Language;
    }

    public static async Task<string> Mem0(string name,
        KernelArguments args, string model)
    {
        var fileName = name + ".md";

        // 将model插入fileName到.md前面，如果文件不存在则删除model
        if (model != null && !string.IsNullOrEmpty(model))
        {
            fileName = $"{model}_{fileName}";

            if (!File.Exists(Path.Combine(Mem0Prompt, fileName)))
            {
                // 如果文件不存在，则删除model
                fileName = name + ".md";
                Log.Logger.Warning(
                    "Chat prompt not found with model: {Model}, falling back to default name: {FileName}", model,
                    fileName);
            }
        }

        if (!File.Exists(Path.Combine(Mem0Prompt, fileName)))
        {
            throw new FileNotFoundException($"Chat prompt not found name:{fileName}");
        }

        var values = await File.ReadAllTextAsync(Path.Combine(Mem0Prompt, fileName));

        return args.Aggregate(values,
            (current, value) =>
                current.Replace("{{$" + value.Key + "}}", value.Value?.ToString(),
                    StringComparison.CurrentCultureIgnoreCase)) + Prompt.Language;
    }

    /// <summary>
    /// 修复后的Warehouse方法 - 使用增强的模板变量替换
    /// </summary>
    public static async Task<string> Warehouse(string name, KernelArguments args, string model)
    {
        var fileName = name + ".md";
        
        // 模型特定文件处理
        if (!string.IsNullOrEmpty(model))
        {
            var modelSpecificFileName = $"{model}_{fileName}";
            if (File.Exists(Path.Combine(WarehousePrompt, modelSpecificFileName)))
            {
                fileName = modelSpecificFileName;
            }
            else
            {
                Log.Logger.Warning("模型特定文件不存在: {ModelFileName}, 使用默认文件: {FileName}", 
                    modelSpecificFileName, fileName);
            }
        }
        
        var filePath = Path.Combine(WarehousePrompt, fileName);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Warehouse prompt文件不存在: {fileName}");
        }
        
        try
        {
            var template = await File.ReadAllTextAsync(filePath);
            var processedContent = ReplaceTemplateVariablesWithValidation(template, args, fileName);
            
            // 验证处理结果
            if (string.IsNullOrWhiteSpace(processedContent))
            {
                throw new InvalidOperationException($"模板处理后内容为空: {fileName}");
            }
            
            // 添加语言设置
            var language = Prompt.Language ?? "中文";
            return processedContent + language;
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "处理Warehouse模板时发生错误: {FileName}", fileName);
            throw;
        }
    }
}
