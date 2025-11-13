using KoalaWiki.Domains;

namespace KoalaWiki.KoalaWarehouse.Pipeline;

/// <summary>
/// 文档处理上下文
/// </summary>
public class DocumentProcessingContext
{
    /// <summary>
    /// 文档信息
    /// </summary>
    public Document Document { get; set; } = new();

    /// <summary>
    /// 仓库信息
    /// </summary>
    public Warehouse Warehouse { get; set; } = new();

    /// <summary>
    /// Git 仓库信息
    /// </summary>
    public object? GitRepository { get; set; }

    /// <summary>
    /// 数据库上下文
    /// </summary>
    public IKoalaWikiContext? DbContext { get; set; }

    /// <summary>
    /// 内核实例
    /// </summary>
    public object? KernelInstance { get; set; }

    /// <summary>
    /// 文件内核实例
    /// </summary>
    public object? FileKernelInstance { get; set; }

    /// <summary>
    /// 目录结构
    /// </summary>
    public string? Catalogue { get; set; }

    /// <summary>
    /// 步骤结果
    /// </summary>
    private readonly Dictionary<string, object> _stepResults = new();

    /// <summary>
    /// 元数据
    /// </summary>
    private readonly Dictionary<string, object> _metadata = new();

    /// <summary>
    /// 设置步骤结果
    /// </summary>
    /// <param name="stepName">步骤名称</param>
    /// <param name="result">结果</param>
    public void SetStepResult(string stepName, object result)
    {
        _stepResults[stepName] = result;
    }

    /// <summary>
    /// 获取步骤结果
    /// </summary>
    /// <param name="stepName">步骤名称</param>
    /// <returns>结果</returns>
    public T? GetStepResult<T>(string stepName)
    {
        if (_stepResults.TryGetValue(stepName, out var result) && result is T typedResult)
        {
            return typedResult;
        }
        return default;
    }

    /// <summary>
    /// 设置元数据
    /// </summary>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    public void SetMetadata(string key, object value)
    {
        _metadata[key] = value;
    }

    /// <summary>
    /// 获取元数据
    /// </summary>
    /// <param name="key">键</param>
    /// <returns>值</returns>
    public T? GetMetadata<T>(string key)
    {
        if (_metadata.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }
        return default;
    }

    /// <summary>
    /// 获取所有步骤结果
    /// </summary>
    /// <returns>步骤结果字典</returns>
    public IReadOnlyDictionary<string, object> GetAllStepResults()
    {
        return _stepResults;
    }

    /// <summary>
    /// 获取所有元数据
    /// </summary>
    /// <returns>元数据字典</returns>
    public IReadOnlyDictionary<string, object> GetAllMetadata()
    {
        return _metadata;
    }
}
