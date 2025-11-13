using System.Text;

namespace KoalaWiki.KoalaWarehouse.DocumentPending;

public partial class DocumentPendingService
{
    private static int TaskMaxSizePerUser = 3;

    static DocumentPendingService()
    {
        // 读取环境变量
        var maxSize = Environment.GetEnvironmentVariable("TASK_MAX_SIZE_PER_USER").GetTrimmedValueOrEmpty();
        if (!string.IsNullOrEmpty(maxSize) && int.TryParse(maxSize, out var maxSizeInt))
        {
            TaskMaxSizePerUser = maxSizeInt;
        }
    }

    /// <summary>
    /// 处理文档生成
    /// </summary>
    /// <param name="documents"></param>
    /// <param name="fileKernel"></param>
    /// <param name="catalogue"></param>
    /// <param name="gitRepository"></param>
    /// <param name="warehouse"></param>
    /// <param name="path"></param>
    /// <param name="dbContext"></param>
    /// <param name="classifyType">
    /// 分类类型
    /// </param>
    /// <exception cref="Exception"></exception>
    public static async Task HandlePendingDocumentsAsync(List<DocumentCatalog> documents, Kernel fileKernel,
        string catalogue,
        string gitRepository, Warehouse warehouse, string path, IKoalaWikiContext dbContext, ClassifyType? classifyType)
    {
        // 提供5个并发的信号量,很容易触发429错误
        var semaphore = new SemaphoreSlim(TaskMaxSizePerUser);

        // 等待中的任务列表
        var pendingDocuments = new ConcurrentBag<DocumentCatalog>(documents);
        var runningTasks = new List<Task<(DocumentCatalog catalog, DocumentFileItem fileItem, List<string> files)>>();

        // 开始处理文档，直到所有文档都处理完成
        while (pendingDocuments.Count > 0 || runningTasks.Count > 0)
        {
            // 尝试启动新任务，直到达到并发限制
            while (pendingDocuments.Count > 0 && runningTasks.Count < TaskMaxSizePerUser)
            {
                if (!pendingDocuments.TryTake(out var documentCatalog)) continue;

                var task = ProcessDocumentAsync(documentCatalog, fileKernel, catalogue, gitRepository,
                    warehouse.Branch, path, semaphore, classifyType);
                runningTasks.Add(task);

                // 这里使用了一个小的延迟来避免过于频繁的任务启动
                await Task.Delay(1000, CancellationToken.None);
            }

            // 如果没有正在运行的任务，退出循环
            if (runningTasks.Count == 0)
                break;

            // 等待任意一个任务完成
            var completedTask = await Task.WhenAny(runningTasks);
            runningTasks.Remove(completedTask);

            try
            {
                var (catalog, fileItem, files) = await completedTask.ConfigureAwait(false);

                if (fileItem == null || string.IsNullOrEmpty(fileItem.Content))
                {
                    // 构建失败
                    Log.Logger.Error("处理仓库；{path} ,处理标题：{name} 失败:文件内容为空", path, catalog.Name);
                    throw new Exception("处理失败，文件内容为空: " + catalog.Name);
                }

                // 更新文档状态
                await dbContext.DocumentCatalogs.Where(x => x.Id == catalog.Id)
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.IsCompleted, true));

                // 修复Mermaid语法错误
                RepairMermaid(fileItem);

                await dbContext.DocumentFileItems.AddAsync(fileItem);

                await dbContext.DocumentFileItemSources.AddRangeAsync(files.Select(x => new DocumentFileItemSource()
                {
                    Address = x,
                    DocumentFileItemId = fileItem.Id,
                    Name = Path.GetFileName(x),
                    CreatedAt = DateTime.Now,
                    Id = Guid.NewGuid().ToString("N"),
                }));

                await dbContext.SaveChangesAsync();

                Log.Logger.Information("处理仓库；{path}, 处理标题：{name} 完成并保存到数据库！", path, catalog.Name);
            }
            catch (Exception ex)
            {
                Log.Logger.Error("处理文档失败: {ex}", ex.ToString());
            }
        }
    }

    /// <summary>
    /// 新增：严格的消息内容验证方法
    /// </summary>
    private static (bool isValid, string errorMessage, int tokenLength) ValidateMessageContentStrict(
        ChatMessageContentItemCollection contents)
    {
        if (contents == null || contents.Count == 0)
            return (false, "消息内容集合为空", 0);
        
        var totalText = new StringBuilder();
        foreach (var content in contents)
        {
            var text = content.ToString();
            if (string.IsNullOrWhiteSpace(text))
                continue;
                
            totalText.Append(text);
        }
        
        var finalText = totalText.ToString().Trim();
        var tokenLength = finalText.Length; // 简化的token计算
        
        if (tokenLength == 0)
            return (false, "所有消息内容均为空或空白", 0);
            
        if (tokenLength > 1048576)
            return (false, $"消息内容过长: {tokenLength} > 1048576", tokenLength);
        
        return (true, string.Empty, tokenLength);
    }

    /// <summary>
    /// 新增：修复后的消息构建逻辑
    /// </summary>
    private static async Task<ChatHistory> BuildValidatedChatHistory(
        string catalogue, string gitRepository, string branch, 
        string catalogName, string prompt, ClassifyType? classifyType)
    {
        try
        {
            // 预验证输入参数
            ValidateInputParameters(catalogue, gitRepository, branch, catalogName, prompt);
            
            // 获取并验证prompt内容
            string promptContent = await GetDocumentPendingPrompt(classifyType, catalogue, 
                gitRepository, branch, catalogName, prompt);
                
            if (string.IsNullOrWhiteSpace(promptContent))
            {
                throw new InvalidOperationException("生成的prompt内容为空，请检查模板变量替换");
            }
            
            var history = new ChatHistory();
            history.AddSystemDocs();
            
            var contents = new ChatMessageContentItemCollection();
            
            // 添加主要prompt内容
            contents.Add(new TextContent(promptContent));
            
            // 添加系统提醒
            var systemReminder = $"""
                Generate comprehensive documentation using Docs tools only.
                Language: {Prompt.Language ?? "中文"}
                Use parallel File.Read operations for efficiency.
                Maximum 3 Docs.MultiEdit operations allowed.
                Never output document content directly in chat.
                """;
            
            contents.Add(new TextContent(systemReminder));
            
            // 严格验证
            var (isValid, errorMessage, tokenLength) = ValidateMessageContentStrict(contents);
            if (!isValid)
            {
                Log.Logger.Error("消息内容验证失败: {Error}, Token长度: {TokenLength}", errorMessage, tokenLength);
                throw new InvalidOperationException($"消息内容验证失败: {errorMessage}");
            }

            // 使用纯字符串消息以兼容部分代理/模型，避免422参数校验错误
            var messageText = new StringBuilder();
            foreach (var c in contents)
            {
                if (c is TextContent t && !string.IsNullOrWhiteSpace(t.Text))
                {
                    messageText.AppendLine(t.Text);
                }
            }
            history.AddUserMessage(messageText.ToString().Trim());
            
            Log.Logger.Information("消息构建成功，Token长度: {TokenLength}", tokenLength);
            return history;
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "构建ChatHistory时发生错误");
            throw;
        }
    }

    /// <summary>
    /// 新增：验证输入参数
    /// </summary>
    private static void ValidateInputParameters(
        string catalogue, string gitRepository, string branch, 
        string catalogName, string prompt)
    {
        var errors = new List<string>();
        
        if (string.IsNullOrWhiteSpace(catalogName))
            errors.Add("catalogName 不能为空");
        
        if (string.IsNullOrWhiteSpace(catalogue))
            errors.Add("catalogue 不能为空");
        
        if (string.IsNullOrWhiteSpace(gitRepository))
            errors.Add("gitRepository 不能为空");
        
        if (string.IsNullOrWhiteSpace(branch))
            errors.Add("branch 不能为空");
        
        if (string.IsNullOrWhiteSpace(prompt))
            errors.Add("prompt 不能为空");
        
        if (errors.Any())
        {
            var errorMsg = $"输入参数验证失败: {string.Join(", ", errors)}";
            Log.Logger.Error(errorMsg);
            throw new ArgumentException(errorMsg);
        }
        
        Log.Logger.Information("输入参数验证通过 - CatalogName: {CatalogName}, Repository: {Repository}", 
            catalogName, gitRepository);
    }

    /// <summary>
    /// 新增：API请求前的最终验证
    /// </summary>
    private static void ValidateChatHistoryBeforeApiCall(ChatHistory history)
    {
        if (history == null)
        {
            throw new InvalidOperationException("ChatHistory为null，无法发送API请求");
        }
        
        if (history.Count == 0)
        {
            throw new InvalidOperationException("ChatHistory为空，无法发送API请求");
        }
        
        var emptyMessages = new List<string>();
        
        foreach (var message in history)
        {
            if (message is null)
            {
                emptyMessages.Add("存在空的消息对象");
                continue;
            }
            
            // SK 1.66: ChatMessageContent exposes both string Content and Items collection.
            // Content may be empty when Items carries the actual payload.
            var contentStr = message.Content;
            var items = message.Items;
            
            var hasStringContent = !string.IsNullOrWhiteSpace(contentStr);
            var hasItems = items is not null && items.Count > 0;
            
            if (!hasStringContent && !hasItems)
            {
                emptyMessages.Add($"{message.Role} 消息内容为空（无字符串内容且Items为空）");
                continue;
            }
            
            // 如果存在字符串内容但为空白
            if (!hasItems && string.IsNullOrWhiteSpace(contentStr))
            {
                emptyMessages.Add($"{message.Role} 消息内容为空字符串");
            }
            // 如果存在Items，则检查是否包含有效文本
            else if (hasItems)
            {
                var hasText = false;
                foreach (var item in items)
                {
                    if (item is TextContent textContent && !string.IsNullOrWhiteSpace(textContent.Text))
                    {
                        hasText = true;
                        break;
                    }
                }
                
                if (!hasText && !hasStringContent)
                    emptyMessages.Add($"{message.Role} 消息内容集合中没有有效文本");
            }
        }
        
        if (emptyMessages.Any())
        {
            var errorMsg = $"ChatHistory包含无效消息: {string.Join("; ", emptyMessages)}";
            Log.Logger.Error(errorMsg);
            throw new InvalidOperationException(errorMsg);
        }
        
        Log.Logger.Information("ChatHistory验证通过，消息数量: {MessageCount}", history.Count);
    }

    /// <summary>
    /// 新增：获取模型特定设置
    /// </summary>
    private static OpenAIPromptExecutionSettings GetModelSpecificSettings(string model)
    {
        var baseSettings = new OpenAIPromptExecutionSettings()
        {
            MaxTokens = DocumentsHelper.GetMaxTokens(model),
            // 禁用工具调用，避免部分代理/模型返回422的参数校验错误
            ToolCallBehavior = null,
        };
        
        // Qwen模型特殊配置
        if (IsQwenModel(model))
        {
            baseSettings.Temperature = 0.3;
            baseSettings.TopP = 0.8;
            // 移除ChatResponseFormat设置，因为该属性可能不存在
            
            Log.Logger.Information("应用Qwen模型特殊配置: {Model}", model);
        }
        
        return baseSettings;
    }

    /// <summary>
    /// 新增：检查是否为Qwen模型
    /// </summary>
    private static bool IsQwenModel(string model)
    {
        return !string.IsNullOrEmpty(model) && 
               (model.Contains("qwen", StringComparison.OrdinalIgnoreCase) || 
                model.Contains("coder", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 处理单个文档的异步方法
    /// <returns>
    /// 返回列表
    /// </returns>
    /// </summary>
    public static async Task<(DocumentCatalog catalog, DocumentFileItem fileItem, List<string> files)>
        ProcessDocumentAsync(DocumentCatalog catalog, Kernel kernel, string catalogue, string gitRepository,
            string branch,
            string path,
            SemaphoreSlim? semaphore, ClassifyType? classifyType)
    {
        int retryCount = 0;
        const int retries = 5;
        var files = new List<string>();

        for (var retry = 0; retry < 3; retry++)
        {
            try
            {
                if (semaphore != null)
                    await semaphore.WaitAsync();

                Log.Logger.Information("处理仓库；{path} ,处理标题：{name}", path, catalog.Name);

                DocumentContext.DocumentStore = new DocumentStore();

                var docs = new DocsFunction();
                // 为每个文档处理创建独立的Kernel实例，避免状态管理冲突
                // 使用纯净kernel创建，不包含任何插件以避免工具调用问题
                var documentKernel = await KernelFactory.GetKernel(
                    OpenAIOptions.Endpoint,
                    OpenAIOptions.ChatApiKey,
                    path,
                    OpenAIOptions.ChatModel,
                    false,
                    files,
                    builder =>
                    {
                        // 注册文档写作工具，供模型调用 Docs.Write/Docs.Read/Docs.MultiEdit
                        builder.Plugins.AddFromObject(docs, "Docs");
                    });

                var chat = documentKernel.Services.GetService<IChatCompletionService>();

                // 使用新的消息构建方法
                var history = await BuildValidatedChatHistory(catalogue, gitRepository, branch, 
                    catalog.Name, catalog.Prompt, classifyType);

                // 使用模型特定设置
                var settings = GetModelSpecificSettings(OpenAIOptions.ChatModel);
                int count = 1;
                int inputTokenCount = 0;
                int outputTokenCount = 0;
                int maxRetries = 3;
                CancellationTokenSource token = null;
                var accumulatedContent = new StringBuilder();

                reset:

                try
                {
                    // 创建新的取消令牌（每次重试都重新创建）
                    token?.Dispose();
                    token = new CancellationTokenSource(TimeSpan.FromMinutes(30)); // 30分钟超时

                    Console.WriteLine($"开始处理文档 (尝试 {count}/{maxRetries + 1})，超时设置: 30分钟");

                    // API请求前的最终验证
                    ValidateChatHistoryBeforeApiCall(history);

                    try
                    {
                        var hasReceivedContent = false;
                        var lastActivityTime = DateTime.UtcNow;

                        await foreach (var item in chat.GetStreamingChatMessageContentsAsync(
                                           history,
                                           settings,
                                           documentKernel,
                                           token.Token).ConfigureAwait(false))
                        {
                            // 检查是否被取消
                            token.Token.ThrowIfCancellationRequested();

                            // 更新最后活动时间
                            lastActivityTime = DateTime.UtcNow;
                            hasReceivedContent = true;

                            switch (item.InnerContent)
                            {
                                case StreamingChatCompletionUpdate { Usage.InputTokenCount: > 0 } content:
                                    inputTokenCount += content.Usage.InputTokenCount;
                                    outputTokenCount += content.Usage.OutputTokenCount;
                                    Console.WriteLine($"[Token统计] 输入: {inputTokenCount}, 输出: {outputTokenCount}");
                                    break;

                                case StreamingChatCompletionUpdate tool when tool.ToolCallUpdates.Count > 0:
                                    
                                    break;

                                case StreamingChatCompletionUpdate value:
                                    var text = value.ContentUpdate.FirstOrDefault()?.Text;
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        Console.Write(text);
                                        accumulatedContent.Append(text);
                                    }
                                    break;

                                case ChatMessageContentItemCollection collection:
                                    // Handle ChatMessageContentItemCollection content
                                    foreach (var contentItem in collection)
                                    {
                                        if (contentItem is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                                        {
                                            Console.Write(textContent.Text);
                                            accumulatedContent.Append(textContent.Text);
                                        }
                                    }
                                    break;

                                default:
                                    // 记录未知的内容类型用于调试
                                    Console.WriteLine($"[DEBUG] 未处理的内容类型: {item.InnerContent?.GetType().Name}");
                                    break;
                            }
                        }

                        // 处理完成
                        Console.WriteLine($"\n文档处理完成! 最终Token统计 - 输入: {inputTokenCount}, 输出: {outputTokenCount}");

                        // 检查是否实际接收到了内容
                        if (!hasReceivedContent)
                        {
                            Console.WriteLine("警告: 没有接收到任何流式内容");
                        }
                    }
                    catch (OperationCanceledException) when (token.Token.IsCancellationRequested)
                    {
                        Console.WriteLine("操作被取消 (超时或手动取消)");

                        count++;
                        if (count <= maxRetries)
                        {
                            Console.WriteLine($"正在重试... ({count}/{maxRetries})");

                            // 指数退避延迟
                            var delayMs = Math.Min(1000 * (int)Math.Pow(2, count - 1), 10000); // 最大10秒
                            await Task.Delay(delayMs, CancellationToken.None);

                            goto reset;
                        }
                        else
                        {
                            Console.WriteLine("已达到最大重试次数，处理失败");
                            throw new TimeoutException($"文档处理在 {maxRetries} 次重试后仍然超时");
                        }
                    }
                    catch (HttpRequestException httpEx)
                    {
                        Console.WriteLine($"HTTP错误: {httpEx.Message}");
                        
                        // 检查是否是422错误
                        if (httpEx.Message.Contains("422") || httpEx.Message.Contains("UnprocessableEntity"))
                        {
                            Log.Logger.Error("422错误 - 消息内容验证失败: {Message}", httpEx.Message);
                            Console.WriteLine("422错误: 消息内容验证失败，这通常表示发送的消息为空或格式不正确");
                            
                            // 422错误不应该重试，因为这是内容问题
                            throw new InvalidOperationException("API请求因内容验证失败被拒绝，请检查消息内容", httpEx);
                        }

                        count++;
                        if (count <= maxRetries)
                        {
                            Console.WriteLine($"网络错误，正在重试... ({count}/{maxRetries})");

                            // 网络错误时增加延迟
                            await Task.Delay(3000 * count, CancellationToken.None);
                            goto reset;
                        }

                        Console.WriteLine("网络错误重试失败");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"处理流式响应时发生未知错误: {ex.Message}");
                        Console.WriteLine($"异常类型: {ex.GetType().Name}");
                        Console.WriteLine($"堆栈跟踪: {ex.StackTrace}");

                        // 对于未知错误，也可以尝试重试一次
                        count++;
                        if (count <= maxRetries)
                        {
                            Console.WriteLine($"未知错误，尝试重试... ({count}/{maxRetries})");
                            await Task.Delay(5000, CancellationToken.None); // 5秒延迟
                            goto reset;
                        }

                        throw; // 重新抛出异常
                    }
                }
                finally
                {
                    // 确保资源被正确释放
                    token?.Dispose();
                    Console.WriteLine("资源清理完成");
                }

                if (accumulatedContent.Length == 0 && count < 5)
                {
                    count++;
                    goto reset;
                }

                var fileItem = new DocumentFileItem()
                {
                    Content = accumulatedContent.ToString(),
                    DocumentCatalogId = catalog.Id,
                    Description = string.Empty,
                    Extra = new Dictionary<string, string>(),
                    Metadata = new Dictionary<string, string>(),
                    Source = [],
                    CommentCount = 0,
                    RequestToken = 0,
                    CreatedAt = DateTime.Now,
                    Id = Guid.NewGuid().ToString("N"),
                    ResponseToken = 0,
                    Size = 0,
                    Title = catalog.Name,
                };

                Log.Logger.Information("处理仓库；{path} ,处理标题：{name} 完成！", path, catalog.Name);

                semaphore?.Release();

                return (catalog, fileItem, files);
            }
            catch (Exception ex)
            {
                semaphore?.Release();
                Log.Logger.Error("处理仓库；{path} ,处理标题：{name} 失败:{ex}", path, catalog.Name, ex.ToString());

                retryCount++;
                if (retryCount >= retries)
                {
                    Console.WriteLine($"处理 {catalog.Name} 失败，已重试 {retryCount} 次，错误：{ex.Message}");
                    throw; // 重试耗尽后向上层抛出异常
                }
                else
                {
                    // 根据异常类型决定等待时间
                    int delayMs;
                    if (ex is InvalidOperationException && ex.Message.Contains("文档质量"))
                    {
                        // 质量问题重试间隔较短，因为主要是内容生成问题
                        delayMs = 5000 * retryCount;
                        Log.Logger.Information("文档质量问题重试 - 仓库: {path}, 标题: {name}, 第{retry}次重试, 等待{delay}ms",
                            path, catalog.Name, retryCount, delayMs);
                    }
                    else
                    {
                        // API限流等其他问题需要更长等待时间
                        delayMs = 10000 * retryCount;
                        Log.Logger.Information("API异常重试 - 仓库: {path}, 标题: {name}, 第{retry}次重试, 等待{delay}ms",
                            path, catalog.Name, retryCount, delayMs);
                    }

                    await Task.Delay(delayMs);
                }
            }
        }

        throw new Exception("处理失败，重试多次仍未成功: " + catalog.Name);
    }

    /// <summary>
    /// Mermaid可能存在语法错误，使用大模型进行修复
    /// </summary>
    /// <param name="fileItem"></param>
    public static void RepairMermaid(DocumentFileItem fileItem)
    {
        try
        {
            var regex = new Regex(@"```mermaid\s*([\s\S]*?)```", RegexOptions.Multiline);
            var matches = regex.Matches(fileItem.Content);

            foreach (Match match in matches)
            {
                var code = match.Groups[1].Value;

                // 只需要删除[]里面的(和)，它可能单独处理
                var codeWithoutBrackets =
                    Regex.Replace(code, @"\[[^\]]*\]",
                        m => m.Value.Replace("(", "").Replace(")", "").Replace("（", "").Replace("）", ""));
                // 然后替换原有内容
                fileItem.Content = fileItem.Content.Replace(match.Value, $"```mermaid\n{codeWithoutBrackets}```");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "修复mermaid语法失败");
        }
    }
}
