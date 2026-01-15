using Luban.CodeTarget;
using Luban.Datas;
using Luban.DataTarget;
using Luban.Defs;
using Luban.PostProcess;
using Luban.Types;
using NLog;

namespace Luban.TableSignature;

[PostProcess("signatureData", TargetFileType.DataExport)]
public class TableSignatureDataPostProcessor : PostProcessBase
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    public override void PostProcess(OutputFileManifest oldOutputFileManifest, OutputFileManifest newOutputFileManifest)
    {
        var ctx = GenerationContext.Current;
        if (ctx == null)
        {
            s_logger.Warn("GenerationContext.Current is null, skip table signature post processing");
            return;
        }

        // 先处理所有文件，计算签名
        base.PostProcess(oldOutputFileManifest, newOutputFileManifest);

        try
        {
            // 获取签名计算结果（线程安全，使用Lazy<T>）
            var result = SignatureContext.Result;
            
            if (result == null || result.Signatures == null || result.Signatures.Count == 0)
            {
                s_logger.Warn("No signatures calculated, skip generating signature table");
                return;
            }

            // 生成签名表数据文件
            GenerateSignatureDataFile(result, newOutputFileManifest);
            
            // 生成签名表代码文件（直接写入代码目录）
            GenerateSignatureCodeFiles(result);
        }
        catch (Exception ex)
        {
            s_logger.Error(ex, "Error in table signature post processing");
            throw;
        }
    }

    public override void PostProcess(OutputFileManifest oldOutputFileManifest, OutputFileManifest newOutputFileManifest, OutputFile outputFile)
    {
        // 保留所有原始数据文件
        newOutputFileManifest.AddFile(outputFile);
        
        // 注意：签名计算在SignatureContext.Result中完成，这里不需要计算
        // 因为我们已经使用专用DefAssembly统一计算了所有表的签名
    }

    private void GenerateSignatureDataFile(
        SignatureCalculationResult result, 
        OutputFileManifest newOutputFileManifest)
    {
        var ctx = GenerationContext.Current;
        var dataTargetName = newOutputFileManifest.TargetName;
        var dataTarget = DataTargetManager.Ins.CreateDataTarget(dataTargetName);
        
        // 创建签名表记录
        var records = CreateSignatureRecords(result);
        
        // 生成数据文件
        var outputFile = dataTarget.ExportTable(result.TableDef, records);
        if (outputFile != null)
        {
            newOutputFileManifest.AddFile(outputFile);
            s_logger.Info("Generated signature data file: {} for target: {}", outputFile.File, dataTargetName);
        }
    }

    private List<Record> CreateSignatureRecords(SignatureCalculationResult result)
    {
        var records = new List<Record>();
        var tableDef = result.TableDef;
        var tBean = tableDef.ValueTType;
        var dBean = tBean.DefBean;
        var enumDef = result.EnumDef;
        var tEnum = TEnum.Create(false, enumDef, null);
        var stringType = TString.Create(false, null);

        foreach (var enumItem in enumDef.Items)
        {
            // 从签名结果中查找对应的签名
            // enumItem.Name 是表名（如 "TableName"），需要找到对应的FullName
            var tableFullName = FindTableFullNameByEnumItemName(enumItem.Name);
            if (tableFullName != null && result.Signatures.TryGetValue(tableFullName, out var signature))
            {
                var fields = new List<DType>();
                var enumData = new DEnum(tEnum, enumItem.Name);
                var signatureData = DString.ValueOf(stringType, signature);

                foreach (var defField in dBean.Fields)
                {
                    DType field = defField.Name switch
                    {
                        "table_name" => enumData,
                        "signature" => signatureData,
                        _ => null
                    };
                    fields.Add(field);
                }

                var bean = new DBean(tBean, dBean, fields);
                records.Add(new Record(bean, $"post_process_{enumItem.Name}", new List<string>()));
            }
            else
            {
                s_logger.Warn("Signature for enum item '{enumItem.Name}' (table: {tableFullName}) not found", 
                    enumItem.Name, tableFullName ?? "unknown");
            }
        }

        return records;
    }

    private string FindTableFullNameByEnumItemName(string enumItemName)
    {
        var ctx = GenerationContext.Current;
        // enumItemName 是表名（如 "TableName"），需要找到对应的FullName
        // 枚举项名称是通过 FullName.Replace('.', '_') 生成的
        var tableFullName = enumItemName.Replace('_', '.');
        
        // 尝试直接匹配
        foreach (var table in ctx.ExportTables)
        {
            var name = table.FullName.Replace('.', '_');
            if (name == enumItemName)
            {
                return table.FullName;
            }
        }
        
        return null;
    }

    private void GenerateSignatureCodeFiles(SignatureCalculationResult result)
    {
        var ctx = GenerationContext.Current;
        
        // 获取所有代码target（需要从配置或全局状态获取）
        var codeTargetNames = GetCodeTargetNames();
        
        foreach (string codeTargetName in codeTargetNames)
        {
            try
            {
                var codeTarget = CodeTargetManager.Ins.CreateCodeTarget(codeTargetName);
                var codeOutputDir = EnvManager.Current.GetOption(
                    codeTargetName, BuiltinOptionNames.OutputCodeDir, true);
                
                GenerationContext.CurrentCodeTarget = codeTarget;
                
                // 生成枚举代码
                var enumWriter = new CodeWriter();
                codeTarget.GenerateEnum(ctx, result.EnumDef, enumWriter);
                var enumPath = Path.Combine(codeOutputDir, codeTarget.GetPathFromFullName(result.EnumDef.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(enumPath));
                File.WriteAllText(enumPath, enumWriter.ToResult(codeTarget.FileHeader), codeTarget.FileEncoding);
                s_logger.Info("Generated enum code file: {}", enumPath);
                
                // 生成Bean代码
                var beanWriter = new CodeWriter();
                codeTarget.GenerateBean(ctx, result.BeanDef, beanWriter);
                var beanPath = Path.Combine(codeOutputDir, codeTarget.GetPathFromFullName(result.BeanDef.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(beanPath));
                File.WriteAllText(beanPath, beanWriter.ToResult(codeTarget.FileHeader), codeTarget.FileEncoding);
                s_logger.Info("Generated bean code file: {}", beanPath);
                
                // 生成Table代码
                var tableWriter = new CodeWriter();
                codeTarget.GenerateTable(ctx, result.TableDef, tableWriter);
                var tablePath = Path.Combine(codeOutputDir, codeTarget.GetPathFromFullName(result.TableDef.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(tablePath));
                File.WriteAllText(tablePath, tableWriter.ToResult(codeTarget.FileHeader), codeTarget.FileEncoding);
                s_logger.Info("Generated table code file: {}", tablePath);
                
                // 重新生成TableManager（包含签名表）
                var managerWriter = new CodeWriter();
                var allExportTables = new List<DefTable>(ctx.ExportTables);
                codeTarget.GenerateTables(ctx, allExportTables, managerWriter);
                var managerPath = Path.Combine(codeOutputDir, codeTarget.GetPathFromFullName(ctx.Target.Manager));
                Directory.CreateDirectory(Path.GetDirectoryName(managerPath));
                File.WriteAllText(managerPath, managerWriter.ToResult(codeTarget.FileHeader), codeTarget.FileEncoding);
                s_logger.Info("Regenerated TableManager: {}", managerPath);
            }
            catch (Exception ex)
            {
                s_logger.Error(ex, "Error generating code files for code target: {}", codeTargetName);
                // 继续处理其他code target
            }
        }
    }

    private List<string> GetCodeTargetNames()
    {
        var codeTargetNames = new List<string>();
        
        // 尝试从配置选项获取code targets列表
        var codeTargetsStr = EnvManager.Current.GetOptionOrDefault(
            "", "signatureCodeTargets", true, "");
        
        if (!string.IsNullOrEmpty(codeTargetsStr))
        {
            // 从配置中读取，逗号分隔
            codeTargetNames.AddRange(codeTargetsStr.Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s)));
        }
        
        // 如果配置为空，尝试使用当前CodeTarget（如果有）
        if (codeTargetNames.Count == 0 && GenerationContext.CurrentCodeTarget != null)
        {
            codeTargetNames.Add(GenerationContext.CurrentCodeTarget.Name);
            s_logger.Info("Using current code target: {}", GenerationContext.CurrentCodeTarget.Name);
        }
        
        if (codeTargetNames.Count == 0)
        {
            s_logger.Warn("No code target found, cannot generate signature code files. " +
                "Please specify code targets via option 'signatureCodeTargets' (comma-separated)");
        }
        
        return codeTargetNames;
    }
}
