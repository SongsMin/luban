using System.Collections.Concurrent;
using Luban.Defs;
using Luban.RawDefs;

namespace Luban.TableSignature;

public class SignatureCalculationResult
{
    public Dictionary<string, string> Signatures { get; set; }
    public DefEnum EnumDef { get; set; }
    public DefBean BeanDef { get; set; }
    public DefTable TableDef { get; set; }
}

public static class SignatureContext
{
    private static readonly Lazy<SignatureCalculationResult> _signatureResult = new(
        () => CalculateSignatures(),
        LazyThreadSafetyMode.ExecutionAndPublication
    );
    
    public static SignatureCalculationResult Result => _signatureResult.Value;
    
    private static SignatureCalculationResult CalculateSignatures()
    {
        return SignatureCalculatorHelper.CalculateUnifiedSignatures();
    }
}
