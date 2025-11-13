using System.Diagnostics;
using KoalaWiki.Domains;
using KoalaWiki.Domains.DocumentFile;
using KoalaWiki.KoalaWarehouse.DocumentPending;
using KoalaWiki.Options;
using Microsoft.EntityFrameworkCore;

namespace KoalaWiki.KoalaWarehouse.Pipeline.Steps;

public class DocumentContentGenerationStep(ILogger<DocumentContentGenerationStep> logger)
    : DocumentProcessingStepBase<DocumentProcessingContext, DocumentProcessingContext>(logger)
{
    public override string StepName => "生成目录结构中的文档";

    public override async Task<DocumentProcessingContext> ExecuteAsync(DocumentProcessingContext context,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity(StepName);
        SetActivityTags(activity, context);

        Logger.LogInformation("开始执行 {StepName} 步骤", StepName);

        try
        {
            // 从步骤结果获取文档目录，如果没有则从数据库查询
            var documentCatalogs = context.GetStepResult<List<DocumentCatalog>>("生成目录结构");
            if (documentCatalogs == null)
            {
                // 从数据库查询文档目录
                documentCatalogs = await context.DbContext.DocumentCatalogs
                    .Where(x => x.WarehouseId == context.Warehouse.Id)
                    .ToListAsync();
            }

            if (documentCatalogs == null || !documentCatalogs.Any())
            {
                Logger.LogWarning("没有文档目录需要生成内容");
                return context;
            }

            // 确保有文件内核实例
            if (context.FileKernelInstance == null)
            {
                context.FileKernelInstance = await KernelFactory.GetKernel(
                    OpenAIOptions.Endpoint,
                    OpenAIOptions.ChatApiKey, 
                    context.Document.GitPath, 
                    OpenAIOptions.ChatModel, 
                    false);
            }

            // 从步骤结果获取分类，如果没有则使用仓库的分类
            var classification = context.GetStepResult<ClassifyType?>("读取或生成项目类别") ?? context.Warehouse.Classify;

            await DocumentPendingService.HandlePendingDocumentsAsync(
                documentCatalogs, 
                (Microsoft.SemanticKernel.Kernel)context.FileKernelInstance!, 
                context.Catalogue ?? string.Empty,
                context.GitRepository?.ToString() ?? string.Empty,
                context.Warehouse, 
                context.Document.GitPath, 
                context.DbContext, 
                classification);

            activity?.SetTag("documents.processed", documentCatalogs.Count);
            context.SetStepResult(StepName, documentCatalogs.Count);
            
            Logger.LogInformation("完成 {StepName} 步骤，处理文档数量: {Count}", 
                StepName, documentCatalogs.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "执行 {StepName} 步骤时发生错误", StepName);
            activity?.SetTag("error", ex.Message);
            throw;
        }

        return context;
    }

    protected override void SetActivityTags(Activity? activity, DocumentProcessingContext input)
    {
        activity?.SetTag("warehouse.id", input.Warehouse.Id);
        // 从步骤结果获取文档目录数量，如果没有则为0
        var documentCatalogs = input.GetStepResult<List<DocumentCatalog>>("生成目录结构");
        activity?.SetTag("documents.count", documentCatalogs?.Count ?? 0);
    }
}
