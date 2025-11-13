using KoalaWiki.Core.Extensions;
using KoalaWiki.Prompts;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Newtonsoft.Json;
using OpenAI.Chat;
using System.Text;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace KoalaWiki.KoalaWarehouse.GenerateThinkCatalogue;

public static partial class GenerateThinkCatalogueService
{
    private const int MaxRetries = 8;
    private const int BaseDelayMs = 1000;
    private const double MaxDelayMs = 30000;
    private const double JitterRange = 0.3;

    private enum ErrorType
    {
        NetworkError,
        JsonParseError,
        ApiRateLimit,
        ModelError,
        UnknownError
    }

    public static async Task<DocumentResultCatalogue?> GenerateCatalogue(string path,
        string catalogue, Warehouse warehouse, ClassifyType? classify)
    {
        var retryCount = 0;
        Exception? lastException = null;
        var consecutiveFailures = 0;

        Log.Logger.Information("开始处理仓库：{path}，处理标题：{name}", path, warehouse.Name);

        while (retryCount < MaxRetries)
        {
            try
            {
                var result = await ExecuteSingleAttempt(path, catalogue, classify, retryCount).ConfigureAwait(false);

                if (result != null)
                {
                    Log.Logger.Information("成功处理仓库：{path}，处理标题：{name}，尝试次数：{retryCount}",
                        path, warehouse.Name, retryCount + 1);
                    return result;
                }

                Log.Logger.Warning("处理仓库返回空结果：{path}，处理标题：{name}，尝试次数：{retryCount}",
                    path, warehouse.Name, retryCount + 1);
                consecutiveFailures++;
            }
            catch (Exception ex)
            {
                lastException = ex;
                consecutiveFailures++;
                var errorType = ClassifyError(ex);

                Log.Logger.Warning("处理仓库失败：{path}，处理标题：{name}，尝试次数：{retryCount}，错误类型：{errorType}，错误：{error}",
                    path, warehouse.Name, retryCount + 1, errorType, ex.Message);

                if (!ShouldRetry(errorType, retryCount, consecutiveFailures))
                {
                    Log.Logger.Error("错误类型 {errorType} 不适合重试或达到最大重试次数，停止重试", errorType);
                    break;
                }
            }

            retryCount++;

            if (retryCount < MaxRetries)
            {
                var delay = CalculateDelay(retryCount, consecutiveFailures);
                Log.Logger.Information("等待 {delay}ms 后进行第 {nextAttempt} 次尝试", delay, retryCount + 1);
                await Task.Delay(delay);

                if (consecutiveFailures >= 3)
                {
                    Log.Logger.Information("连续失败 {consecutiveFailures} 次，尝试重置状态", consecutiveFailures);
                    await Task.Delay(2000);
                }
            }
        }

        Log.Logger.Error("处理仓库最终失败：{path}，处理标题：{name}，总尝试次数：{totalAttempts}，最后错误：{error}",
            path, warehouse.Name, retryCount, lastException?.Message ?? "未知错误");

        return null;
    }

    private static async Task<DocumentResultCatalogue?> ExecuteSingleAttempt(
        string path, string catalogue, ClassifyType? classify, int attemptNumber)
    {
        // 应用数据优化（针对Qwen模型）
        var optimizedCatalogue = catalogue;
        if (IsQwenModel())
        {
            Log.Logger.Information("检测到Qwen模型，应用数据优化");
            optimizedCatalogue = QwenClassificationDataOptimizer.OptimizeCatalogueForQwen(catalogue);
        }

        var enhancedPrompt = await GenerateThinkCataloguePromptAsync(classify, optimizedCatalogue);

        var history = new ChatHistory();
        history.AddSystemEnhance();

        // 极简工具调用指令 - 专门针对Qwen模型优化
        var qwenToolInstruction = """
            **重要：你必须使用 catalog.Write 工具输出JSON**
            - 不要在聊天中直接输出JSON
            - 必须调用 catalog.Write 工具
            - 这是强制要求，不是可选项
            """;

        var combinedContent = $"{enhancedPrompt}\n{qwenToolInstruction}\n{Prompt.Language}";
        
        Log.Logger.Information("提示词长度：{length} 字符，尝试次数：{attemptNumber}", combinedContent.Length, attemptNumber + 1);
        
        history.AddUserMessage(combinedContent);

        var catalogueTool = new CatalogueFunction();
        var analysisModel = await KernelFactory.GetKernel(OpenAIOptions.Endpoint,
            OpenAIOptions.ChatApiKey, path, OpenAIOptions.AnalysisModel, false, null,
            builder =>
            {
                builder.Plugins.AddFromObject(catalogueTool, "catalog");
            });

        var chat = analysisModel.Services.GetService<IChatCompletionService>();
        if (chat == null)
        {
            throw new InvalidOperationException("无法获取聊天完成服务");
        }

        var settings = new OpenAIPromptExecutionSettings()
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
            MaxTokens = DocumentsHelper.GetMaxTokens(OpenAIOptions.AnalysisModel)
        };

        int retry = 1;
        var inputTokenCount = 0;
        var outputTokenCount = 0;

        retry:
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));

        try
        {
            await foreach (var item in chat.GetStreamingChatMessageContentsAsync(
                               history,
                               settings,
                               analysisModel,
                               cts.Token).ConfigureAwait(false))
            {
                cts.Token.ThrowIfCancellationRequested();

                switch (item.InnerContent)
                {
                    case StreamingChatCompletionUpdate { Usage.InputTokenCount: > 0 } content:
                        inputTokenCount += content.Usage.InputTokenCount;
                        outputTokenCount += content.Usage.OutputTokenCount;
                        break;

                    case StreamingChatCompletionUpdate tool when tool.ToolCallUpdates.Count > 0:
                        Log.Logger.Information("检测到工具调用更新，工具数量：{count}", tool.ToolCallUpdates.Count);
                        foreach (var toolCall in tool.ToolCallUpdates)
                        {
                            Log.Logger.Information("工具调用详情：{toolId} - {toolName} - 参数：{arguments}", 
                                toolCall.ToolCallId, toolCall.FunctionName, 
                                toolCall.FunctionArgumentsUpdate != null ? 
                                    Encoding.UTF8.GetString(toolCall.FunctionArgumentsUpdate) : "null");
                        }
                        break;

                    case StreamingChatCompletionUpdate value:
                        var text = value.ContentUpdate.FirstOrDefault()?.Text;
                        if (!string.IsNullOrEmpty(text))
                        {
                            Console.Write(text);
                            if (text.Length > 50)
                            {
                                Log.Logger.Debug("AI响应片段：{text}...", text.Substring(0, 50));
                            }
                            else
                            {
                                Log.Logger.Debug("AI响应片段：{text}", text);
                            }
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            retry++;
            if (retry <= 3)
            {
                Console.WriteLine($"超时，正在重试 ({retry}/3)...");
                await Task.Delay(2000, CancellationToken.None);
                cts.Dispose();
                cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                goto retry;
            }

            throw new TimeoutException("流式处理超时");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"流式处理错误: {ex.Message}");
            throw;
        }
        finally
        {
            cts?.Dispose();
        }

        Log.Logger.Information("流式处理完成，输入Token：{inputTokens}，输出Token：{outputTokens}，工具内容长度：{toolContentLength}", 
            inputTokenCount, outputTokenCount, catalogueTool.Content?.Length ?? 0);

        if (string.IsNullOrWhiteSpace(catalogueTool.Content))
        {
            Log.Logger.Warning("工具内容为空，尝试次数：{attemptNumber}，将进行强制工具调用", attemptNumber + 1);
            
            var forceResult = await ForceToolCall(history, chat, settings, analysisModel, attemptNumber);
            if (forceResult != null)
            {
                Log.Logger.Information("强制工具调用成功");
                return ExtractAndParseJson(forceResult);
            }
            
            if (DocumentOptions.RefineAndEnhanceQuality && attemptNumber < 3)
            {
                Log.Logger.Information("开始质量增强流程");
                await RefineResponse(history, chat, settings, analysisModel);
                
                if (!string.IsNullOrWhiteSpace(catalogueTool.Content))
                {
                    Log.Logger.Information("质量增强成功，获取到内容");
                    return ExtractAndParseJson(catalogueTool.Content);
                }
            }
            
            Log.Logger.Warning("所有尝试均失败，返回null");
            return null;
        }
        else
        {
            Log.Logger.Information("成功获取工具内容，长度：{length}", catalogueTool.Content.Length);
            return ExtractAndParseJson(catalogueTool.Content);
        }
    }

    private static async Task<string?> ForceToolCall(ChatHistory history, IChatCompletionService chat,
        OpenAIPromptExecutionSettings settings, Kernel kernel, int attemptNumber)
    {
        try
        {
            // 极简强制工具调用指令
            const string forceToolPrompt = """
                你必须立即使用 catalog.Write 工具输出JSON！
                
                要求：
                1. 立即调用 catalog.Write 工具
                2. 不要在聊天中输出JSON
                3. 输出基本的文档目录结构
                
                示例JSON：
                {
                  "items": [
                    {
                      "title": "getting-started",
                      "name": "Getting Started",
                      "prompt": "Help users understand the project",
                      "children": [
                        {
                          "title": "overview",
                          "name": "Overview", 
                          "prompt": "Project overview and purpose",
                          "children": []
                        }
                      ]
                    }
                  ]
                }
                
                立即调用 catalog.Write 工具！
                """;

            history.AddUserMessage(forceToolPrompt);
            Log.Logger.Information("发送强制工具调用请求，尝试次数：{attemptNumber}", attemptNumber + 1);

            var forceCatalogueTool = new CatalogueFunction();
            var forceKernel = await KernelFactory.GetKernel(OpenAIOptions.Endpoint,
                OpenAIOptions.ChatApiKey, "", OpenAIOptions.AnalysisModel, false, null,
                builder =>
                {
                    builder.Plugins.AddFromObject(forceCatalogueTool, "catalog");
                });

            await foreach (var _ in chat.GetStreamingChatMessageContentsAsync(history, settings, forceKernel))
            {
                // 处理强制工具调用的响应
            }
            
            Log.Logger.Information("强制工具调用流程完成，工具内容长度：{length}", 
                forceCatalogueTool.Content?.Length ?? 0);

            return forceCatalogueTool.Content;
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "强制工具调用过程中发生错误");
            return null;
        }
    }

    private static async Task RefineResponse(ChatHistory history, IChatCompletionService chat,
        OpenAIPromptExecutionSettings settings, Kernel kernel)
    {
        try
        {
            const string refinementPrompt = """
                使用 catalog 工具优化存储的JSON：
                - 使用 catalog.Read 检查当前JSON
                - 应用 catalog.MultiEdit 进行改进（最多3次操作）
                - 专注于：结构、完整性和准确性
                - 不要在聊天中输出JSON，只使用工具
                """;

            history.AddUserMessage(refinementPrompt);
            Log.Logger.Information("发送质量增强请求");

            await foreach (var _ in chat.GetStreamingChatMessageContentsAsync(history, settings, kernel))
            {
                // 简化处理，只记录关键信息
            }
            
            Log.Logger.Information("质量增强流程完成");
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "质量增强过程中发生错误");
        }
    }

    private static DocumentResultCatalogue? ExtractAndParseJson(string responseText)
    {
        try
        {
            Log.Logger.Debug("尝试解析JSON，长度：{length}", responseText?.Length ?? 0);
            
            if (string.IsNullOrWhiteSpace(responseText))
            {
                Log.Logger.Warning("尝试解析空的JSON响应");
                return null;
            }

            var extractedJson = JsonConvert.DeserializeObject<DocumentResultCatalogue>(responseText);
            
            if (extractedJson != null)
            {
                Log.Logger.Information("JSON解析成功，项目数量：{count}", 
                    extractedJson.items?.Count ?? 0);
            }
            else
            {
                Log.Logger.Warning("JSON解析返回null");
            }
            
            return extractedJson;
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "JSON解析失败");
            return null;
        }
    }

    private static ErrorType ClassifyError(Exception ex)
    {
        return ex switch
        {
            HttpRequestException => ErrorType.NetworkError,
            TaskCanceledException => ErrorType.NetworkError,
            JsonException => ErrorType.JsonParseError,
            InvalidOperationException when ex.Message.Contains("rate") => ErrorType.ApiRateLimit,
            InvalidOperationException when ex.Message.Contains("quota") => ErrorType.ApiRateLimit,
            _ when ex.Message.Contains("model") => ErrorType.ModelError,
            _ => ErrorType.UnknownError
        };
    }

    private static bool ShouldRetry(ErrorType errorType, int retryCount, int consecutiveFailures)
    {
        if (retryCount < 3) return true;

        return errorType switch
        {
            ErrorType.NetworkError => retryCount < MaxRetries,
            ErrorType.ApiRateLimit => retryCount < MaxRetries && consecutiveFailures < 5,
            ErrorType.JsonParseError => retryCount < 6,
            ErrorType.ModelError => retryCount < 4,
            ErrorType.UnknownError => retryCount < MaxRetries,
            _ => throw new ArgumentOutOfRangeException(nameof(errorType), errorType, null)
        };
    }

    private static int CalculateDelay(int retryCount, int consecutiveFailures)
    {
        var exponentialDelay = BaseDelayMs * Math.Pow(2, retryCount);
        var consecutiveFailurePenalty = consecutiveFailures * 1000;
        var jitter = Random.Shared.NextDouble() * JitterRange * exponentialDelay;

        var totalDelay = exponentialDelay + consecutiveFailurePenalty + jitter;

        return (int)Math.Min(totalDelay, MaxDelayMs);
    }

    private static bool IsQwenModel()
    {
        var model = OpenAIOptions.AnalysisModel?.ToLower() ?? string.Empty;
        return model.Contains("qwen") || model.Contains("coder");
    }
}
