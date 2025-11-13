using System.Text.RegularExpressions;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI.Chat;
using Serilog;

namespace KoalaWiki.KoalaWarehouse.GenerateThinkCatalogue;

/// <summary>
/// 处理Qwen模型的特殊工具调用格式
/// Qwen模型使用 <function> 标签格式而不是标准的 tool_calls 格式
/// </summary>
public static class QwenToolCallHandler
{
    private static readonly Regex FunctionRegex = new Regex(
        @"<function=(?<functionName>[^>]+)>\s*<parameter=(?<parameterName>[^>]+)>(?<parameterContent>.*?)</parameter>\s*</function>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>
    /// 检查文本是否包含Qwen格式的工具调用
    /// </summary>
    public static bool ContainsQwenToolCall(string content)
    {
        return !string.IsNullOrEmpty(content) && content.Contains("<function=");
    }

    /// <summary>
    /// 从Qwen格式的响应中提取工具调用并执行
    /// </summary>
    public static async Task<bool> ProcessQwenToolCallAsync(
        string content, 
        CatalogueFunction catalogueTool,
        IChatCompletionService chat,
        ChatHistory history,
        OpenAIPromptExecutionSettings settings,
        Kernel kernel)
    {
        if (string.IsNullOrEmpty(content) || !ContainsQwenToolCall(content))
        {
            return false;
        }

        var matches = FunctionRegex.Matches(content);
        var toolExecuted = false;

        foreach (Match match in matches)
        {
            var functionName = match.Groups["functionName"].Value.Trim();
            var parameterName = match.Groups["parameterName"].Value.Trim();
            var parameterContent = match.Groups["parameterContent"].Value.Trim();

            Log.Logger.Information("检测到Qwen工具调用：{functionName}，参数：{parameterName} = {content}", 
                functionName, parameterName, parameterContent);

            // 根据函数名执行对应的工具
            switch (functionName)
            {
                case "catalog_Write":
                    if (parameterName.Equals("json", StringComparison.OrdinalIgnoreCase))
                    {
                        catalogueTool.Write(parameterContent);
                        toolExecuted = true;
                        Log.Logger.Information("成功执行 catalog_Write 工具，内容长度：{length}", parameterContent.Length);
                    }
                    break;

                case "catalog_Read":
                    catalogueTool.Read();
                    toolExecuted = true;
                    Log.Logger.Information("成功执行 catalog_Read 工具");
                    break;

                case "catalog_MultiEdit":
                    try
                    {
                        var edits = System.Text.Json.JsonSerializer.Deserialize<MultiEditInput[]>(parameterContent);
                        if (edits != null)
                        {
                            catalogueTool.MultiEdit(edits);
                            toolExecuted = true;
                            Log.Logger.Information("成功执行 catalog_MultiEdit 工具，编辑数量：{count}", edits.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Logger.Error(ex, "解析 catalog_MultiEdit 参数失败");
                    }
                    break;

                default:
                    Log.Logger.Warning("未知的工具函数：{functionName}", functionName);
                    break;
            }
        }

        return toolExecuted;
    }

    /// <summary>
    /// 从流式响应中收集所有内容并处理工具调用
    /// </summary>
    public static async Task<bool> ProcessStreamingContentAsync(
        string accumulatedContent,
        CatalogueFunction catalogueTool,
        IChatCompletionService chat,
        ChatHistory history,
        OpenAIPromptExecutionSettings settings,
        Kernel kernel)
    {
        return await ProcessQwenToolCallAsync(accumulatedContent, catalogueTool, chat, history, settings, kernel);
    }
}
