using KoalaWiki.Domains;
using KoalaWiki.Options;
using KoalaWiki.Prompts;

namespace KoalaWiki.KoalaWarehouse.DocumentPending;

public partial class DocumentPendingService
{
    public static async Task<string> GetDocumentPendingPrompt(ClassifyType? classifyType, string codeFiles,
        string gitRepository, string branch, string title, string prompt)
    {
        string projectType = GetProjectTypeDescription(classifyType);

        return await PromptContext.Warehouse(nameof(PromptConstant.Warehouse.GenerateDocs),
            new KernelArguments()
            {
                ["code_files"] = codeFiles,
                ["prompt"] = prompt,
                ["git_repository"] = gitRepository.Replace(".git", ""),
                ["branch"] = branch,
                ["title"] = title,
                ["projectType"] = projectType
            }, OpenAIOptions.ChatModel);
    }

    private static string GetProjectTypeDescription(ClassifyType? classifyType)
    {
        if (classifyType == ClassifyType.Applications)
        {
            return "Generate comprehensive enterprise application documentation covering architecture, implementation, and operational aspects.";
        }

        if (classifyType == ClassifyType.Frameworks)
        {
            return "Generate complete framework documentation including adoption guide, API reference, and implementation examples.";
        }

        if (classifyType == ClassifyType.Libraries)
        {
            return "Generate thorough library documentation with integration guide, API reference, and best practices.";
        }

        if (classifyType == ClassifyType.DevelopmentTools)
        {
            return "Generate complete development tool documentation covering setup, features, and integration workflows.";
        }

        if (classifyType == ClassifyType.CLITools)
        {
            return "Generate comprehensive CLI tool documentation with command reference, usage examples, and automation guides.";
        }

        if (classifyType == ClassifyType.DevOpsConfiguration)
        {
            return "Generate complete DevOps infrastructure documentation covering architecture, deployment, and operations.";
        }

        if (classifyType == ClassifyType.Documentation)
        {
            return "Generate documentation project documentation covering structure, contribution guidelines, and maintenance procedures.";
        }

        return "Generate comprehensive project documentation covering overview, implementation, and development guidance.";
    }
}
