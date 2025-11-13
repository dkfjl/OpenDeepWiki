using System.Text.RegularExpressions;

namespace KoalaWiki.KoalaWarehouse;

/// <summary>
/// 针对Qwen模型的项目分类数据优化器
/// </summary>
public static class QwenClassificationDataOptimizer
{
    /// <summary>
    /// 为Qwen模型优化README内容
    /// </summary>
    /// <param name="readme">原始README内容</param>
    /// <param name="maxLength">最大长度限制</param>
    /// <returns>优化后的README内容</returns>
    public static string OptimizeReadmeForQwen(string readme, int maxLength = 3000)
    {
        if (string.IsNullOrEmpty(readme))
            return string.Empty;

        // 如果内容已经很短，直接返回
        if (readme.Length <= maxLength)
            return readme;

        var optimized = new List<string>();
        
        // 优先保留的关键信息
        var sections = ExtractKeySections(readme);
        
        // 按优先级添加内容
        foreach (var section in sections)
        {
            if (optimized.Sum(s => s.Length) + section.Length > maxLength)
            {
                // 如果添加这个section会超限，尝试截断
                var remainingLength = maxLength - optimized.Sum(s => s.Length);
                if (remainingLength > 200) // 至少保留200字符
                {
                    optimized.Add(section.Length > remainingLength ? 
                        section.Substring(0, remainingLength - 3) + "..." : section);
                }
                break;
            }
            optimized.Add(section);
        }

        var result = string.Join("\n\n", optimized);
        
        // 记录优化信息
        Console.WriteLine($"README优化: 原长度 {readme.Length} -> 优化后 {result.Length}");
        
        return result;
    }

    /// <summary>
    /// 为Qwen模型优化目录结构内容
    /// </summary>
    /// <param name="catalogue">原始目录结构内容</param>
    /// <param name="maxLength">最大长度限制</param>
    /// <returns>优化后的目录结构内容</returns>
    public static string OptimizeCatalogueForQwen(string catalogue, int maxLength = 1500)
    {
        if (string.IsNullOrEmpty(catalogue))
            return string.Empty;

        if (catalogue.Length <= maxLength)
            return catalogue;

        // 提取关键目录信息
        var keyStructure = ExtractKeyDirectoryStructure(catalogue, maxLength);
        
        Console.WriteLine($"目录结构优化: 原长度 {catalogue.Length} -> 优化后 {keyStructure.Length}");
        
        return keyStructure;
    }

    /// <summary>
    /// 提取README中的关键部分
    /// </summary>
    private static List<string> ExtractKeySections(string readme)
    {
        var sections = new List<string>();
        var lines = readme.Split('\n');
        
        var currentSection = new List<string>();
        var inCodeBlock = false;
        var sectionPriority = 0;
        
        foreach (var line in lines)
        {
            // 检查代码块
            if (line.Trim().StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                if (!inCodeBlock && currentSection.Any())
                {
                    // 代码块结束，添加当前section
                    AddSectionByPriority(sections, currentSection, ref sectionPriority);
                    currentSection.Clear();
                }
                continue;
            }
            
            if (inCodeBlock)
            {
                // 在代码块中，只保留简短的代码示例
                if (currentSection.Sum(s => s.Length) < 500)
                {
                    currentSection.Add(line);
                }
                continue;
            }
            
            // 检查标题
            if (line.StartsWith("#"))
            {
                // 保存之前的section
                if (currentSection.Any())
                {
                    AddSectionByPriority(sections, currentSection, ref sectionPriority);
                    currentSection.Clear();
                }
                
                // 确定新section的优先级
                sectionPriority = GetSectionPriority(line);
                currentSection.Add(line);
            }
            else
            {
                currentSection.Add(line);
            }
        }
        
        // 添加最后一个section
        if (currentSection.Any())
        {
            AddSectionByPriority(sections, currentSection, ref sectionPriority);
        }
        
        return sections;
    }

    /// <summary>
    /// 根据优先级添加section
    /// </summary>
    private static void AddSectionByPriority(List<string> sections, List<string> currentSection, ref int priority)
    {
        var section = string.Join("\n", currentSection);
        
        // 高优先级section直接添加
        if (priority <= 2)
        {
            sections.Insert(0, section);
        }
        else if (priority <= 4)
        {
            // 中等优先级插入到合适位置
            var insertIndex = sections.Count(s => s.StartsWith("#"));
            sections.Insert(insertIndex, section);
        }
        else
        {
            // 低优先级添加到末尾
            sections.Add(section);
        }
    }

    /// <summary>
    /// 获取section优先级
    /// </summary>
    private static int GetSectionPriority(string header)
    {
        var headerLower = header.ToLower();
        
        // 高优先级 (1-2)
        if (headerLower.Contains("description") || headerLower.Contains("描述") ||
            headerLower.Contains("overview") || headerLower.Contains("概述") ||
            headerLower.Contains("简介"))
            return 1;
            
        if (headerLower.Contains("feature") || headerLower.Contains("特性") ||
            headerLower.Contains("功能") || headerLower.Contains("what") ||
            headerLower.Contains("什么"))
            return 2;
        
        // 中等优先级 (3-4)
        if (headerLower.Contains("install") || headerLower.Contains("安装") ||
            headerLower.Contains("quick start") || headerLower.Contains("快速开始") ||
            headerLower.Contains("usage") || headerLower.Contains("使用"))
            return 3;
            
        if (headerLower.Contains("tech") || headerLower.Contains("技术") ||
            headerLower.Contains("stack") || headerLower.Contains("架构") ||
            headerLower.Contains("architecture"))
            return 4;
        
        // 低优先级 (5+)
        return 5;
    }

    /// <summary>
    /// 提取关键目录结构
    /// </summary>
    private static string ExtractKeyDirectoryStructure(string catalogue, int maxLength)
    {
        var lines = catalogue.Split('\n');
        var keyLines = new List<string>();
        var currentLength = 0;
        
        // 优先保留根目录和重要子目录
        foreach (var line in lines)
        {
            if (currentLength + line.Length > maxLength)
                break;
                
            // 保留重要的目录和文件
            if (IsImportantDirectoryOrFile(line))
            {
                keyLines.Add(line);
                currentLength += line.Length + 1; // +1 for newline
            }
        }
        
        return string.Join("\n", keyLines);
    }

    /// <summary>
    /// 判断是否为重要的目录或文件
    /// </summary>
    private static bool IsImportantDirectoryOrFile(string line)
    {
        var trimmed = line.Trim();
        
        // 保留根目录结构
        if (trimmed.Length <= 2) // 根级别
            return true;
            
        // 重要的目录
        var importantDirs = new[] 
        {
            "src", "app", "lib", "bin", "tools", "docs", "examples", 
            "test", "tests", "config", "scripts", "build", "dist"
        };
        
        // 重要的文件
        var importantFiles = new[]
        {
            "package.json", "requirements.txt", "pom.xml", "csproj", "sln",
            "dockerfile", "docker-compose", "makefile", "readme", "license",
            "gitignore", "yml", "yaml", "json", "config", "ini"
        };
        
        var lineLower = trimmed.ToLower();
        
        // 检查是否包含重要目录
        if (importantDirs.Any(dir => lineLower.Contains(dir)))
            return true;
            
        // 检查是否包含重要文件
        if (importantFiles.Any(file => lineLower.Contains(file)))
            return true;
            
        // 保留前几层的目录结构
        var indentLevel = line.Length - line.TrimStart().Length;
        return indentLevel <= 4; // 保留前两层目录
    }

    /// <summary>
    /// 检查数据是否足够进行分类
    /// </summary>
    public static bool IsDataSufficient(string readme, string catalogue)
    {
        var readmeLength = readme?.Length ?? 0;
        var catalogueLength = catalogue?.Length ?? 0;
        
        // 至少需要一些基本信息
        return readmeLength > 100 || catalogueLength > 50;
    }
}
