using Luban.Defs;

namespace Luban.TableSignature;

public class SignatureDefinitions
{
    public DefEnum EnumDef { get; init; }
    public DefBean BeanDef { get; init; }
    public DefTable TableDef { get; init; }
}

public static class SignatureContext
{
    // 定义（不依赖数据加载，可以在代码后处理中使用）
    private static readonly Lazy<SignatureDefinitions> LazyDefinitions = new(
        SignatureCalculatorHelper.CreateSignatureDefinitions,
        LazyThreadSafetyMode.ExecutionAndPublication
    );
    
    // 签名（依赖数据加载，只在数据后处理中计算）
    private static readonly Lazy<Dictionary<string, string>> LazySignatures = new(
        SignatureCalculatorHelper.CalculateUnifiedSignatures,
        LazyThreadSafetyMode.ExecutionAndPublication
    );
    
    /// <summary>
    /// 获取签名表定义（不依赖数据加载）
    /// </summary>
    public static SignatureDefinitions Definitions => LazyDefinitions.Value;
    
    /// <summary>
    /// 获取签名计算结果（依赖数据加载）
    /// </summary>
    public static Dictionary<string, string> Signatures => LazySignatures.Value;
}
