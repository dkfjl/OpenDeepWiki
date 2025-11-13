using System.Diagnostics;
using KoalaWiki.Domains;
using KoalaWiki.Domains.DocumentFile;
using KoalaWiki.Entities;
using KoalaWiki.KoalaWarehouse.GenerateThinkCatalogue;

namespace KoalaWiki.KoalaWarehouse.Pipeline.Steps;

public sealed class DocumentStructureGenerationStep(ILogger<DocumentStructureGenerationStep> logger)
    : DocumentProcessingStepBase<DocumentProcessingContext, DocumentProcessingContext>(logger)
{
    public override string StepName => "生成目录结构";

    public override async Task<DocumentProcessingContext> ExecuteAsync(DocumentProcessingContext context,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity(StepName);
        SetActivityTags(activity, context);

        Logger.LogInformation("开始执行 {StepName} 步骤", StepName);

        try
        {
            // 从步骤结果获取分类，如果没有则使用仓库的分类
            var classification = context.GetStepResult<ClassifyType?>("读取或生成项目类别") ?? context.Warehouse.Classify;
            
            var result = await GenerateThinkCatalogueService.GenerateCatalogue(
                context.Document.GitPath,
                context.Catalogue ?? string.Empty,
                context.Warehouse,
                classification).ConfigureAwait(false);

            var documentCatalogs = new List<DocumentCatalog>();

            // 递归处理目录层次结构
            DocumentsHelper.ProcessCatalogueItems(
                result.items,
                null,
                null,
                context.Warehouse,
                context.Document,
                documentCatalogs);

            // 设置目录项属性
            documentCatalogs.ForEach(x =>
            {
                x.IsCompleted = false;
                if (string.IsNullOrWhiteSpace(x.Prompt))
                {
                    x.Prompt = " ";
                }
            });

            // 删除遗留数据
            await context.DbContext.DocumentCatalogs.Where(x => x.WarehouseId == context.Warehouse.Id)
                .ExecuteDeleteAsync();

            // 将解析的目录结构保存到数据库
            await context.DbContext.DocumentCatalogs.AddRangeAsync(documentCatalogs);
            await context.DbContext.SaveChangesAsync();

            activity?.SetTag("documents.count", documentCatalogs.Count);
            context.SetStepResult(StepName, documentCatalogs);

            Logger.LogInformation("完成 {StepName} 步骤，生成文档数量: {Count}",
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
    }
}
