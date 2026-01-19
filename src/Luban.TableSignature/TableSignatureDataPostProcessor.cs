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
            // 获取签名表定义和签名计算结果（线程安全，使用Lazy<T>）
            var definitions = SignatureContext.Definitions;
            var signatures = SignatureContext.Signatures;
            
            if (definitions == null || definitions.TableDef == null)
            {
                s_logger.Warn("Signature definitions not ready, skip generating signature table");
                return;
            }
            
            if (signatures == null || signatures.Count == 0)
            {
                s_logger.Warn("No signatures calculated, skip generating signature table");
                return;
            }

            // 更新签名表的记录（替换代码后处理阶段添加的空记录）
            UpdateSignatureTableRecords(definitions, signatures);
            
            // 生成签名表数据文件
            GenerateSignatureDataFile(definitions, signatures, newOutputFileManifest);
        }
        catch (Exception ex)
        {
            s_logger.Error(ex, "Error in table signature post processing");
            throw;
        }
    }

    public override void PostProcess(OutputFileManifest oldOutputFileManifest, OutputFileManifest newOutputFileManifest, OutputFile outputFile)
    {
        // 检查是否是签名表的输出文件，如果是则过滤掉（因为我们会重新生成它）
        var definitions = SignatureContext.Definitions;
        if (definitions != null && definitions.TableDef != null)
        {
            // 获取签名表的输出文件名（不含扩展名）
            var signatureTableOutputFile = definitions.TableDef.OutputDataFile;
            if (!string.IsNullOrEmpty(signatureTableOutputFile))
            {
                // 检查当前文件是否是签名表的输出文件
                // outputFile.File 可能是完整路径或相对路径，格式通常是 {OutputDataFile}.{扩展名}
                // 例如：tbtablesignature.bin 或 path/to/tbtablesignature.json
                var outputFileNameWithoutExt = Path.GetFileNameWithoutExtension(outputFile.File);
                var signatureFileNameWithoutExt = Path.GetFileNameWithoutExtension(signatureTableOutputFile);
                
                // 如果文件名（不含扩展名）匹配，则过滤掉（不添加到 newOutputFileManifest）
                if (string.Equals(outputFileNameWithoutExt, signatureFileNameWithoutExt, StringComparison.OrdinalIgnoreCase))
                {
                    s_logger.Debug("Filtering out signature table output file: {} (will be regenerated with actual signature data)", outputFile.File);
                    return;
                }
            }
        }
        
        // 保留其他所有原始数据文件
        newOutputFileManifest.AddFile(outputFile);
        
        // 注意：签名计算在SignatureContext.Result中完成，这里不需要计算
        // 因为我们已经使用专用DefAssembly统一计算了所有表的签名
    }

    private void UpdateSignatureTableRecords(SignatureDefinitions definitions, Dictionary<string, string> signatures)
    {
        var ctx = GenerationContext.Current;
        if (ctx == null)
        {
            return;
        }
        
        // 创建签名表记录
        var records = CreateSignatureRecords(definitions, signatures);
        
        // 更新 GenerationContext 中的记录（替换代码后处理阶段添加的空记录）
        ctx.AddDataTable(definitions.TableDef, records, null);
        s_logger.Debug("Updated signature table records: {} (replaced empty records with actual signature data)", definitions.TableDef.FullName);
    }
    
    private void GenerateSignatureDataFile(
        SignatureDefinitions definitions,
        Dictionary<string, string> signatures,
        OutputFileManifest newOutputFileManifest)
    {
        var ctx = GenerationContext.Current;
        var dataTargetName = newOutputFileManifest.TargetName;
        var dataTarget = DataTargetManager.Ins.CreateDataTarget(dataTargetName);
        
        // 从 GenerationContext 获取签名表记录（已经更新为实际数据）
        var records = ctx.GetTableExportDataList(definitions.TableDef);
        
        // 生成数据文件
        var outputFile = dataTarget.ExportTable(definitions.TableDef, records);
        if (outputFile != null)
        {
            newOutputFileManifest.AddFile(outputFile);
            s_logger.Info("Generated signature data file: {} for target: {}", outputFile.File, dataTargetName);
        }
    }

    private List<Record> CreateSignatureRecords(SignatureDefinitions definitions, Dictionary<string, string> signatures)
    {
        var records = new List<Record>();
        var tableDef = definitions.TableDef;
        var tBean = tableDef.ValueTType;
        var dBean = tBean.DefBean;
        var enumDef = definitions.EnumDef;
        var tEnum = TEnum.Create(false, enumDef, null);
        var stringType = TString.Create(false, null);

        foreach (var enumItem in enumDef.Items)
        {
            // 从签名结果中查找对应的签名
            // enumItem.Name 是表名（如 "TableName"），需要找到对应的FullName
            var tableFullName = FindTableFullNameByEnumItemName(enumItem.Name);
            if (tableFullName != null && signatures.TryGetValue(tableFullName, out var signature))
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
}
