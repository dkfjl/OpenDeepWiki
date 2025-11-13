using KoalaWiki.Core.Extensions;
using KoalaWiki.Prompts;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Newtonsoft.Json;
using OpenAI.Chat;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace KoalaWiki.KoalaWarehouse.GenerateThinkCatalogue;

public static partial class GenerateThinkCatalogueService
{
    private const int MaxRetries = 8; // 增加重试次数
    private const int BaseDelayMs = 1000;
    private const double MaxDelayMs = 30000; // 最大延迟30秒
    private const double JitterRange = 0.3; // 抖动范围30%

    // 错误分类
    private enum ErrorType
    {
        NetworkError, // 网络相关错误
        JsonParseError, // JSON解析错误
        ApiRateLimit, // API限流
        ModelError, // 模型响应错误
        UnknownError // 未知错误
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
                var result =
                    await ExecuteSingleAttempt(path, catalogue, classify, retryCount).ConfigureAwait(false);

                if (result != null)
                {
                    Log.Logger.Information("成功处理仓库：{path}，处理标题：{name}，尝试次数：{retryCount}",
                        path, warehouse.Name, retryCount + 1);
                    return result;
                }

                // result为null也算失败，继续重试
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

                // 根据错误类型决定是否继续重试
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

                // 如果连续失败过多，尝试重置某些状态
                if (consecutiveFailures >= 3)
                {
                    Log.Logger.Information("连续失败 {consecutiveFailures} 次，尝试重置状态", consecutiveFailures);
                    // 可以在这里添加一些重置逻辑，比如清理缓存等
                    await Task.Delay(2000); // 额外等待
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
        // 根据尝试次数调整提示词策略
        var enhancedPrompt = await GenerateThinkCataloguePromptAsync(classify, catalogue);

        var history = new ChatHistory();

        history.AddSystemEnhance();

        // 优化提示词：简化系统提醒，减少长度
        var simplifiedSystemReminder = $"""
                 <system-reminder>
                 **PARALLEL READ OPERATIONS**: Batch file reads for efficiency
                 **EDITING LIMITS**: Max 3 operations (catalog.MultiEdit only)
                 **EXECUTION STEPS**: 1) agent-think 2) file.Read 3) catalog.Write
                 **TOOLS**: Use catalog.Read/MultiEdit exclusively, never print JSON
                 </system-reminder>
                 """;

        // 使用单个字符串而不是 ChatMessageContentItemCollection 来避免 content 被序列化为数组
        var combinedContent = $"{enhancedPrompt}\n{simplifiedSystemReminder}\n{Prompt.Language}";
        
        // 添加调试日志：记录提示词长度
        Log.Logger.Information("提示词长度：{length} 字符，尝试次数：{attemptNumber}", combinedContent.Length, attemptNumber + 1);
        
        history.AddUserMessage(combinedContent);

        var catalogueTool = new CatalogueFunction();
        var analysisModel =await KernelFactory.GetKernel(OpenAIOptions.Endpoint,
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

        // 根据尝试次数调整设置
        var settings = new OpenAIPromptExecutionSettings()
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
            MaxTokens = DocumentsHelper.GetMaxTokens(OpenAIOptions.AnalysisModel)
        };

        int retry = 1;
        var inputTokenCount = 0;
        var outputTokenCount = 0;

        retry:
        // 添加超时控制
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));

        try
        {
            // 流式获取响应 - 添加取消令牌和异常处理
            await foreach (var item in chat.GetStreamingChatMessageContentsAsync(
                               history,
                               settings,
                               analysisModel,
                               cts.Token).ConfigureAwait(false))
            {
                // 定期检查取消
                cts.Token.ThrowIfCancellationRequested();

                switch (item.InnerContent)
                {
                    case StreamingChatCompletionUpdate { Usage.InputTokenCount: > 0 } content:
                        inputTokenCount += content.Usage.InputTokenCount;
                        outputTokenCount += content.Usage.OutputTokenCount;
                        break;

                    case StreamingChatCompletionUpdate tool when tool.ToolCallUpdates.Count > 0:
                        Log.Logger.Debug("检测到工具调用更新，工具数量：{count}", tool.ToolCallUpdates.Count);
                        break;

                    case StreamingChatCompletionUpdate value:
                        var text = value.ContentUpdate.FirstOrDefault()?.Text;
                        if (!string.IsNullOrEmpty(text))
                        {
                            Console.Write(text);
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

                // 正确地重置超时令牌
                cts.Dispose();
                cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)); // 重新赋值给cts
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
            cts?.Dispose(); // 确保资源被释放
        }

        // 添加调试日志：记录工具状态
        Log.Logger.Information("流式处理完成，输入Token：{inputTokens}，输出Token：{outputTokens}，工具内容长度：{toolContentLength}", 
            inputTokenCount, outputTokenCount, catalogueTool.Content?.Length ?? 0);

        // 增强错误处理：检查工具内容并提供更好的诊断
        if (string.IsNullOrWhiteSpace(catalogueTool.Content))
        {
            Log.Logger.Warning("工具内容为空，尝试次数：{attemptNumber}，将进行重试或质量增强", attemptNumber + 1);
            
            // 质量增强逻辑
            if (!DocumentOptions.RefineAndEnhanceQuality || attemptNumber >= 3) // 前几次尝试才进行质量增强
            {
                Log.Logger.Information("跳过质量增强，直接返回空结果");
                return null; // 改进：直接返回null而不是尝试解析空内容
            }

            Log.Logger.Information("开始质量增强流程");
            await RefineResponse(history, chat, settings, analysisModel);
            
            // 质量增强后再次检查
            if (string.IsNullOrWhiteSpace(catalogueTool.Content))
            {
                Log.Logger.Warning("质量增强后仍然为空，返回null");
                return null;
            }
            
            return ExtractAndParseJson(catalogueTool.Content);
        }
        else
        {
            Log.Logger.Information("成功获取工具内容，长度：{length}", catalogueTool.Content.Length);
            return ExtractAndParseJson(catalogueTool.Content);
        }
    }

    private static async Task RefineResponse(ChatHistory history, IChatCompletionService chat,
        OpenAIPromptExecutionSettings settings, Kernel kernel)
    {
        try
        {
            // 简化细化提示词，减少长度
            const string refinementPrompt = """
                 Refine the stored JSON using catalog tools:
                 - Use catalog.Read to inspect current JSON
                 - Apply catalog.MultiEdit for improvements (max 3 operations)
                 - Focus on: structure, completeness, and accuracy
                 - Never print JSON in chat, use tools only
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
                    extractedJson.Items?.Count ?? 0);
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
        // 总是允许至少重试几次
        if (retryCount < 3) return true;

        // 根据错误类型决定是否继续重试
        return errorType switch
        {
            ErrorType.NetworkError => retryCount < MaxRetries,
            ErrorType.ApiRateLimit => retryCount < MaxRetries && consecutiveFailures < 5,
            ErrorType.JsonParseError => retryCount < 6, // JSON错误多重试几次
            ErrorType.ModelError => retryCount < 4,
            ErrorType.UnknownError => retryCount < MaxRetries,
            _ => throw new ArgumentOutOfRangeException(nameof(errorType), errorType, null)
        };
    }

    private static int CalculateDelay(int retryCount, int consecutiveFailures)
    {
        // 指数退避 + 抖动 + 连续失败惩罚
        var exponentialDelay = BaseDelayMs * Math.Pow(2, retryCount);
        var consecutiveFailurePenalty = consecutiveFailures * 1000;
        var jitter = Random.Shared.NextDouble() * JitterRange * exponentialDelay;

        var totalDelay = exponentialDelay + consecutiveFailurePenalty + jitter;

        return (int)Math.Min(totalDelay, MaxDelayMs);
    }
}
