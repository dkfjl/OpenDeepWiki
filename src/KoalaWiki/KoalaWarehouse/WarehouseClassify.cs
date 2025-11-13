using System.ClientModel.Primitives;
using System.Text.RegularExpressions;
using KoalaWiki.Domains;
using KoalaWiki.Dto;
using KoalaWiki.Prompts;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace KoalaWiki.KoalaWarehouse;

public class WarehouseClassify
{
    /// <summary>
    /// 根据仓库信息分析得出仓库分类
    /// </summary>
    public static async Task<ClassifyType?> ClassifyAsync(Kernel kernel, string catalog, string readme)
    {
        try
        {
            // 记录原始数据大小
            var originalReadmeLength = readme?.Length ?? 0;
            var originalCatalogLength = catalog?.Length ?? 0;
            Console.WriteLine($"开始分类处理 - README长度: {originalReadmeLength}, 目录长度: {originalCatalogLength}");

            // 根据模型类型优化数据
            var optimizedReadme = readme;
            var optimizedCatalog = catalog;

            // 检查是否为Qwen模型
            if (IsQwenModel())
            {
                Console.WriteLine("检测到Qwen模型，应用数据优化策略");
                optimizedReadme = QwenClassificationDataOptimizer.OptimizeReadmeForQwen(readme ?? string.Empty);
                optimizedCatalog = QwenClassificationDataOptimizer.OptimizeCatalogueForQwen(catalog ?? string.Empty);
                
                // 检查优化后的数据是否足够
                if (!QwenClassificationDataOptimizer.IsDataSufficient(optimizedReadme, optimizedCatalog))
                {
                    Console.WriteLine("警告: 优化后数据可能不足，但继续处理");
                }
            }

            var prompt = await PromptContext.Warehouse(nameof(PromptConstant.Warehouse.RepositoryClassification),
                new KernelArguments(new OpenAIPromptExecutionSettings()
                {
                    Temperature = 0.1,
                    MaxTokens = DocumentsHelper.GetMaxTokens(OpenAIOptions.ChatModel)
                })
                {
                    ["category"] = optimizedCatalog,
                    ["readme"] = optimizedReadme
                }, OpenAIOptions.ChatModel);

            Console.WriteLine($"发送到AI的数据大小 - README: {optimizedReadme?.Length ?? 0}, 目录: {optimizedCatalog?.Length ?? 0}");

            var result = string.Empty;
            var isDeep = false;
            var processingStartTime = DateTime.Now;

            await foreach (var i in kernel.InvokePromptStreamingAsync(prompt))
            {
                try
                {
                    var jsonContent = JsonSerializer.Deserialize<OpenAIResponse>(ModelReaderWriter.Write(i.InnerContent));

                    if (!(jsonContent?.choices.Length > 0)) continue;
                    
                    if (string.IsNullOrEmpty(jsonContent.choices[0].message?.reasoning_content) &&
                        string.IsNullOrEmpty(jsonContent.choices[0].delta?.reasoning_content))
                    {
                        if (isDeep)
                        {
                            result += "</think>";
                            isDeep = false;
                        }

                        result += i.ToString();
                        continue;
                    }

                    if (isDeep == false)
                    {
                        result += "</think>";
                        isDeep = true;
                    }

                    // 提取分类结果
                    result += jsonContent.choices[0].message?.reasoning_content ??
                              jsonContent.choices[0].delta?.reasoning_content;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"处理流式响应时发生错误: {ex.Message}");
                    // 继续处理下一个响应
                    continue;
                }
            }

            var processingTime = DateTime.Now - processingStartTime;
            Console.WriteLine($"AI响应处理完成，耗时: {processingTime.TotalSeconds:F2}秒，响应长度: {result.Length}");

            // 提取分类结果正则表达式<classify>(.*?)</classify>
            var regex = new Regex(@"<classify>(.*?)</classify>", RegexOptions.Singleline);

            var match = regex.Match(result);
            if (match.Success)
            {
                // 提取到的内容
                var extractedContent = match.Groups[1].Value.Replace("classifyName:", "").Trim();

                Console.WriteLine($"AI返回的分类结果: {extractedContent}");

                // 将提取的内容转换为枚举类型
                if (Enum.TryParse<ClassifyType>(extractedContent, true, out var classifyType))
                {
                    Console.WriteLine($"分类成功: {classifyType}");
                    return classifyType;
                }
                else
                {
                    Console.WriteLine($"无法解析分类结果: {extractedContent}");
                    return null;
                }
            }
            else
            {
                Console.WriteLine("未找到有效的分类标签，AI响应内容:");
                Console.WriteLine(result.Length > 500 ? result.Substring(0, 500) + "..." : result);
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"分类过程中发生异常: {ex.Message}");
            Console.WriteLine($"异常详情: {ex}");
            return null;
        }
    }

    /// <summary>
    /// 检查是否为Qwen模型
    /// </summary>
    private static bool IsQwenModel()
    {
        var model = OpenAIOptions.ChatModel?.ToLower() ?? string.Empty;
        return model.Contains("qwen") || model.Contains("coder");
    }
}
