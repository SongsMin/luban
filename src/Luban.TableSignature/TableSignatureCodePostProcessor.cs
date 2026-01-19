using System.Collections.Concurrent;
using System.Reflection;
using Luban.CodeTarget;
using Luban.Defs;
using Luban.PostProcess;
using NLog;

namespace Luban.TableSignature;

[PostProcess("signatureCode", TargetFileType.Code)]
public class TableSignatureCodePostProcessor : PostProcessBase
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    public override void PostProcess(OutputFileManifest oldOutputFileManifest, OutputFileManifest newOutputFileManifest)
    {
        var ctx = GenerationContext.Current;
        if (ctx == null)
        {
            s_logger.Warn("GenerationContext.Current is null, skip table signature code post processing");
            return;
        }

        // 先处理所有文件
        base.PostProcess(oldOutputFileManifest, newOutputFileManifest);

        try
        {
            // 获取签名表定义（不依赖数据加载，线程安全，使用Lazy<T>）
            var definitions = SignatureContext.Definitions;
            
            if (definitions == null || definitions.EnumDef == null || definitions.BeanDef == null || definitions.TableDef == null)
            {
                s_logger.Warn("Signature definitions not ready, skip generating signature code files");
                return;
            }

            // 为签名表添加空记录，避免 LoadDatas 阶段报错
            AddEmptyRecordsForSignatureTable(definitions.TableDef);
            
            // 生成签名表代码文件
            GenerateSignatureCodeFiles(definitions, newOutputFileManifest);
        }
        catch (Exception ex)
        {
            s_logger.Error(ex, "Error in table signature code post processing");
            throw;
        }
    }
    
    private void AddEmptyRecordsForSignatureTable(DefTable tableDef)
    {
        var ctx = GenerationContext.Current;
        if (ctx == null)
        {
            return;
        }
        
        // 为签名表添加空记录，避免 LoadDatas 阶段报错
        // 使用反射访问 _recordsByTables 字段，并使用 TryAdd 原子操作
        // 如果已存在数据（可能被 LoadDatas 或数据后处理先添加了），则跳过，避免覆盖实际数据
        var emptyRecords = new List<Record>();
        var tableDataInfo = new TableDataInfo(tableDef, emptyRecords, null);
        
        // 使用反射访问 _recordsByTables 字段
        var recordsByTablesField = typeof(GenerationContext).GetField(
            "_recordsByTables", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        
        if (recordsByTablesField != null)
        {
            var recordsByTables = (ConcurrentDictionary<string, TableDataInfo>)recordsByTablesField.GetValue(ctx);
            if (recordsByTables != null)
            {
                // 使用 TryAdd 原子操作：如果 key 不存在则添加，如果已存在则返回 false（不覆盖）
                if (recordsByTables.TryAdd(tableDef.FullName, tableDataInfo))
                {
                    s_logger.Debug("Added empty records for signature table: {} (will be populated in data post-processing)", tableDef.FullName);
                }
                else
                {
                    s_logger.Debug("Signature table {} already has data, skipping empty record addition (atomic check)", tableDef.FullName);
                }
                return;
            }
        }
        
        // 如果反射失败，回退到使用 AddDataTable（虽然可能有竞态条件，但至少不会崩溃）
        ctx.AddDataTable(tableDef, emptyRecords, null);
        s_logger.Debug("Added empty records for signature table: {} (using AddDataTable fallback)", tableDef.FullName);
    }

    public override void PostProcess(OutputFileManifest oldOutputFileManifest, OutputFileManifest newOutputFileManifest, OutputFile outputFile)
    {
        // 过滤掉原始的 TableManager，因为我们会重新生成它（包含签名表）
        var ctx = GenerationContext.Current;
        var codeTarget = GenerationContext.CurrentCodeTarget;
        
        if (ctx != null && codeTarget != null)
        {
            string managerFileName = codeTarget.GetPathFromFullName(ctx.Target.Manager);
            if (outputFile.File == managerFileName)
            {
                s_logger.Debug("Filtering out original TableManager file: {} (will be regenerated with signature table)", outputFile.File);
                return; // 不添加原始的 TableManager
            }
        }
        
        // 保留其他所有原始代码文件
        newOutputFileManifest.AddFile(outputFile);
    }

    private void GenerateSignatureCodeFiles(SignatureDefinitions definitions, OutputFileManifest newOutputFileManifest)
    {
        var ctx = GenerationContext.Current;
        var codeTarget = GenerationContext.CurrentCodeTarget;
        
        if (codeTarget == null)
        {
            s_logger.Warn("CurrentCodeTarget is null, cannot generate signature code files");
            return;
        }

        try
        {
            // 生成枚举代码
            var enumWriter = new CodeWriter();
            codeTarget.GenerateEnum(ctx, definitions.EnumDef, enumWriter);
            var enumFile = CreateOutputFile(definitions.EnumDef.FullName, enumWriter, codeTarget);
            if (enumFile != null)
            {
                newOutputFileManifest.AddFile(enumFile);
                s_logger.Info("Generated enum code file: {}", enumFile.File);
            }

            // 生成Bean代码
            var beanWriter = new CodeWriter();
            codeTarget.GenerateBean(ctx, definitions.BeanDef, beanWriter);
            var beanFile = CreateOutputFile(definitions.BeanDef.FullName, beanWriter, codeTarget);
            if (beanFile != null)
            {
                newOutputFileManifest.AddFile(beanFile);
                s_logger.Info("Generated bean code file: {}", beanFile.File);
            }

            // 生成Table代码
            var tableWriter = new CodeWriter();
            codeTarget.GenerateTable(ctx, definitions.TableDef, tableWriter);
            var tableFile = CreateOutputFile(definitions.TableDef.FullName, tableWriter, codeTarget);
            if (tableFile != null)
            {
                newOutputFileManifest.AddFile(tableFile);
                s_logger.Info("Generated table code file: {}", tableFile.File);
            }

            // 重新生成TableManager（包含签名表）
            var managerWriter = new CodeWriter();
            var allExportTables = new List<DefTable>(ctx.ExportTables);
            if (!allExportTables.Contains(definitions.TableDef))
            {
                allExportTables.Add(definitions.TableDef);
            }
            codeTarget.GenerateTables(ctx, allExportTables, managerWriter);
            var managerFile = CreateOutputFile(ctx.Target.Manager, managerWriter, codeTarget);
            if (managerFile != null)
            {
                newOutputFileManifest.AddFile(managerFile);
                s_logger.Info("Regenerated TableManager: {} (includes signature table)", managerFile.File);
            }
        }
        catch (Exception ex)
        {
            s_logger.Error(ex, "Error generating signature code files");
            throw;
        }
    }

    private OutputFile CreateOutputFile(string fullName, CodeWriter writer, ICodeTarget codeTarget)
    {
        try
        {
            string fileName = codeTarget.GetPathFromFullName(fullName);
            string content = writer.ToResult(codeTarget.FileHeader);
            return new OutputFile
            {
                File = fileName,
                Content = content,
                Encoding = codeTarget.FileEncoding
            };
        }
        catch (Exception ex)
        {
            s_logger.Error(ex, "Error creating output file for {}. FullName: {}", fullName, fullName);
            return null;
        }
    }
}

