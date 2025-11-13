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
                    // 构建失败 - 标记目录为删除状态，避免孤立记录
                    Log.Logger.Error("处理仓库；{path} ,处理标题：{name} 失败:文件内容为空，将标记为删除", path, catalog.Name);
                    
                    // 标记为删除而不是抛出异常，避免影响其他文档处理
                    await dbContext.DocumentCatalogs.Where(x => x.Id == catalog.Id)
                        .ExecuteUpdateAsync(x => x.SetProperty(y => y.IsDeleted, true));
                    
                    await dbContext.SaveChangesAsync();
                    continue; // 跳过这个文档，继续处理下一个
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
                
                // 发生异常时也标记为删除，避免孤立记录
                try
                {
                    // 尝试从失败的任务中获取文档ID
                    string documentId = null;
                    try
                    {
                        var result = await completedTask.ConfigureAwait(false);
                        documentId = result.catalog.Id;
                    }
                    catch
                    {
                        // 如果无法从completedTask获取，跳过数据库操作
                        Log.Logger.Warning("无法从失败任务中获取文档ID，跳过删除标记操作");
                        return;
                    }

                    await dbContext.DocumentCatalogs.Where(x => x.Id == documentId)
                        .ExecuteUpdateAsync(x => x.SetProperty(y => y.IsDeleted, true));
                    await dbContext.SaveChangesAsync();
                }
                catch (Exception dbEx)
                {
                    Log.Logger.Error("标记失败文档为删除状态时发生数据库错误: {ex}", dbEx.ToString());
                }
            }
        }
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

        while (true) // 使用无限循环，内部通过 return 或 throw 退出
        {
            try
            {
                if (semaphore != null)
                    await semaphore.WaitAsync();

                Log.Logger.Information("处理仓库；{path} ,处理标题：{name}", path, catalog.Name);

                DocumentContext.DocumentStore = new DocumentStore();

                var docs = new DocsFunction();
                // 为每个文档处理创建独立的Kernel实例，避免状态管理冲突
                var documentKernel = await KernelFactory.GetKernel(
                    OpenAIOptions.Endpoint,
                    OpenAIOptions.ChatApiKey,
                    path,
                    OpenAIOptions.ChatModel,
                    false, // 文档生成不需要代码分析功能
                    files, (builder => { builder.Plugins.AddFromObject(docs, "Docs"); })
                );

                var chat = documentKernel.Services.GetService<IChatCompletionService>();

                string prompt = await
                    GetDocumentPendingPrompt(classifyType, catalogue, gitRepository, branch, catalog.Name,
                        catalog.Prompt);

                var history = new ChatHistory();

                history.AddSystemDocs();

                var contents = new ChatMessageContentItemCollection
                {
                    new TextContent(prompt),
                    new TextContent(
                        $"""
                          ```xml
                          <system-reminder>
                          For maximum efficiency, whenever you need to perform multiple independent operations, invoke all relevant tools simultaneously rather than sequentially.
                          Note: The repository's directory structure has been provided in <code_files>. Please utilize the provided structure directly for file navigation and reading operations, rather than relying on glob patterns or filesystem traversal methods.
                          Below is an example of the directory structure of the warehouse, where /D represents a directory and /F represents a file:
                          server/D
                            src/D
                              Main/F
                          web/D
                            components/D
                              Header.tsx/F
                          
                          {Prompt.Language}
                          
                          ## Docs Tool Usage Guidelines
                          
                          **MANDATORY TOOL USAGE**
                          - ABSOLUTE REQUIREMENT: ALL document generation, creation, editing, and output MUST use Docs tools exclusively
                          - STRICTLY PROHIBITED: Direct output of document content in chat responses
                          - CRITICAL: Never display document content directly in conversation - always use Docs.Write, Docs.MultiEdit, or Docs.Create
                          - ENFORCEMENT: Any document-related task must result in actual Docs tool invocation, not chat-based content delivery
                          
                          **PARALLEL READ OPERATIONS**
                          - MANDATORY: Always perform PARALLEL File.Read calls — batch multiple files in a SINGLE message for maximum efficiency
                          - CRITICAL: Read MULTIPLE files simultaneously in one operation
                          - PROHIBITED: Sequential one-by-one file reads (inefficient and wastes context capacity)
                          
                          **EDITING OPERATION LIMITS**
                          - HARD LIMIT: Maximum of 3 editing operations total (Docs.MultiEdit only)
                          - PRIORITY: Maximize each Docs.MultiEdit operation by bundling ALL related changes across multiple files
                          - STRATEGIC PLANNING: Consolidate all modifications into minimal MultiEdit operations to stay within the limit
                          - Use Docs.Write **only once** for initial creation or full rebuild (counts as initial structure creation, not part of the 3 edits)
                          - Always verify content before further changes using Docs.Read (Reads do NOT count toward limit)
                          
                          **CRITICAL MULTIEDIT BEST PRACTICES**
                          - MAXIMIZE EFFICIENCY: Each MultiEdit should target multiple distinct sections across files
                          - AVOID CONFLICTS: Never edit overlapping or identical content regions within the same MultiEdit operation
                          - UNIQUE TARGETS: Ensure each edit instruction addresses a completely different section or file
                          - BATCH STRATEGY: Group all necessary changes by proximity and relevance, but maintain clear separation between edit targets
                          
                          **DOCUMENT OUTPUT ENFORCEMENT**
                          - ZERO TOLERANCE: Never output complete documents or substantial document content directly in chat
                          - TOOL-FIRST APPROACH: Every document creation request must immediately trigger Docs tool usage
                          - NO EXCEPTIONS: Even for "previews," "examples," or "demonstrations" - use Docs tools to create actual files
                          - VERIFICATION METHOD: After tool usage, only provide brief status updates and file location information
                          
                          **RECOMMENDED EDITING SEQUENCE**
                          1. Initial creation → Docs.Write (one-time full structure creation)
                          2. Bulk refinements → Docs.MultiEdit with maximum parallel changes (counts toward 3-operation limit)
                          3. Validation → Use Docs.Read after each MultiEdit to verify success before next operation
                          4. Final adjustments → Remaining MultiEdit operations for any missed changes
                          
                          **COMPLIANCE VERIFICATION**
                          - SELF-CHECK: Before responding, verify that no substantial document content appears in your response
                          - TOOL CONFIRMATION: Ensure every document-related request results in actual Docs tool invocation
                          - STATUS REPORTING: Provide only brief summaries of what was created/modified, never the full content
                          </system-reminder>
                          """)
                };

                contents.AddDocsGenerateSystemReminder();
                history.AddUserMessage(contents);

                var settings = new OpenAIPromptExecutionSettings()
                {
                    ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                    MaxTokens = DocumentsHelper.GetMaxTokens(OpenAIOptions.ChatModel),
                };
                int count = 1;
                int inputTokenCount = 0;
                int outputTokenCount = 0;
                int maxRetries = 3;
                CancellationTokenSource token = null;

                reset:

                try
                {
                    // 创建新的取消令牌（每次重试都重新创建）
                    token?.Dispose();
                    token = new CancellationTokenSource(TimeSpan.FromMinutes(30)); // 30分钟超时

                    Console.WriteLine($"开始处理文档 (尝试 {count}/{maxRetries + 1})，超时设置: 30分钟");

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
                        Console.WriteLine($"网络错误: {httpEx.Message}");

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

                if (string.IsNullOrEmpty(docs.Content) && count < 5)
                {
                    count++;
                    goto reset;
                }


                if (DocumentOptions.RefineAndEnhanceQuality)
                {
                    try
                    {
                        var refineContents = new ChatMessageContentItemCollection
                        {
                            new TextContent(
                                """
                                Please refine and enhance the previous documentation content while maintaining its structure and approach. Focus on:

                                **Enhancement Areas:**
                                - Deepen existing architectural explanations with more technical detail
                                - Expand code analysis with additional insights from the repository
                                - Strengthen existing Mermaid diagrams with more comprehensive representations
                                - Improve clarity and readability of existing explanations
                                - Add more specific code references and examples where appropriate
                                - Enhance existing sections with additional technical depth

                                **Quality Standards:**
                                - Maintain the 90-10 description-to-code ratio established in the original
                                - Ensure all additions are evidence-based from the actual code files
                                - Preserve the Microsoft documentation style approach
                                - Enhance conceptual understanding through improved explanations
                                - Strengthen the progressive learning structure

                                **Refinement Protocol (tools only):**
                                1) Use Docs.Read to review the current document thoroughly.
                                2) Plan improvements that preserve structure and voice.
                                3) Apply multiple small, precise Docs.MultiEdit operations to improve clarity, add missing details, and strengthen diagrams/citations.
                                4) After each edit, re-run Docs.Read to verify changes and continue iterating (at least 2–3 passes).
                                5) Avoid full overwrites; prefer targeted edits that enhance existing content.

                                Build upon the solid foundation that exists to create even more comprehensive and valuable documentation.
                                """),
                            new TextContent(
                                """
                                <system-reminder>
                                CRITICAL: You are now in DOCUMENT REFINEMENT PHASE. Your task is to ENHANCE and IMPROVE the EXISTING documentation content that was just generated, NOT to create completely new content.

                                MANDATORY REQUIREMENTS:
                                1. PRESERVE the original document structure and organization
                                2. ENHANCE existing explanations with more depth and clarity
                                3. IMPROVE technical accuracy and completeness based on actual code analysis
                                4. EXPAND existing sections with more detailed architectural analysis
                                5. REFINE language for better readability while maintaining technical precision
                                6. STRENGTHEN existing Mermaid diagrams or add complementary ones
                                7. ENSURE all enhancements are based on the code files analyzed in the original generation

                                FORBIDDEN ACTIONS:
                                - Do NOT restructure or reorganize the document completely
                                - Do NOT remove existing sections or content
                                - Do NOT add content not based on the analyzed code files
                                - Do NOT change the fundamental approach or style established in the original

                                Your goal is to take the good foundation that exists and make it BETTER, MORE DETAILED, and MORE COMPREHENSIVE while preserving its core structure and insights.
                                </system-reminder>
                                """),
                            new TextContent(Prompt.Language)
                        };
                        history.AddUserMessage(refineContents);

                        int reset1 = 1;
                        reset1:

                        await chat.GetChatMessageContentAsync(history, settings, documentKernel);

                        if (string.IsNullOrEmpty(docs.Content) && reset1 < 3)
                        {
                            reset1++;
                            goto reset1;
                        }

                        // 检查精炼后的内容是否有效
                        if (!string.IsNullOrWhiteSpace(docs.Content))
                        {
                            Log.Logger.Information("文档精炼成功，文档：{name}", catalog.Name);
                        }
                        else
                        {
                            Log.Logger.Warning("文档精炼后内容为空，使用原始内容，文档：{name}", catalog.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Logger.Error("文档精炼失败，使用原始内容，文档：{name}，错误：{error}", catalog.Name, ex.Message);
                    }
                }


                var fileItem = new DocumentFileItem()
                {
                    Content = docs.Content,
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
