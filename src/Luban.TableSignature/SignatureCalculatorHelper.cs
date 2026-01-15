using System.Collections.Concurrent;
using System.Reflection;
using Luban.DataTarget;
using Luban.Defs;
using Luban.RawDefs;
using Luban.Utils;
using NLog;

namespace Luban.TableSignature;

internal static class SignatureCalculatorHelper
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    
    public static SignatureCalculationResult CalculateUnifiedSignatures()
    {
        var ctx = GenerationContext.Current;
        if (ctx == null)
        {
            throw new Exception("GenerationContext.Current is null");
        }
        
        var signatureDataTargetName = EnvManager.Current.GetOptionOrDefault(
            "", "signatureDataTarget", true, "bin");
        var signatureTargetName = EnvManager.Current.GetOptionOrDefault(
            "", "signatureTarget", true, "all");
        var signatureGroupsStr = EnvManager.Current.GetOptionOrDefault(
            "", "signatureGroups", true, "");
        var signatureGroups = string.IsNullOrEmpty(signatureGroupsStr) 
            ? null 
            : signatureGroupsStr.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
        
        s_logger.Info("Calculating unified signatures using target: {}, dataTarget: {}, groups: {}", 
            signatureTargetName, signatureDataTargetName, 
            signatureGroups != null ? string.Join(",", signatureGroups) : "default");
        
        // 获取签名专用的target
        var signatureTarget = ctx.Assembly.GetTarget(signatureTargetName);
        if (signatureTarget == null)
        {
            throw new Exception($"signature target '{signatureTargetName}' not found");
        }
        
        // 创建包装的DefAssembly，使用指定的target和groups
        var wrappedAssembly = CreateWrappedDefAssembly(ctx.Assembly, signatureTarget, signatureGroups);
        
        Dictionary<string, string> signatures;
        DefEnum enumDef;
        DefBean beanDef;
        DefTable tableDef;
        
        try
        {
            // 创建专用的GenerationContext
            var signatureCtx = CreateSignatureGenerationContext(wrappedAssembly.Assembly, ctx);
            
            // 统一签名计算
            signatures = new Dictionary<string, string>();
            var signatureDataTarget = DataTargetManager.Ins.CreateDataTarget(signatureDataTargetName);
            
            // 使用 signatureCtx.Tables 确保包含所有表
            // 字段过滤会自动使用 wrappedAssembly.Assembly.Target.Groups
            foreach (var table in signatureCtx.Tables)
            {
                var records = signatureCtx.GetTableExportDataList(table);
                var outputFile = signatureDataTarget.ExportTable(table, records);
                signatures[table.FullName] = SignatureCalculator.CalculateSignature(outputFile);
                s_logger.Debug("Calculated signature for table: {} = {}", table.FullName, signatures[table.FullName]);
            }
        }
        finally
        {
            // 恢复原始Target
            wrappedAssembly.Restore();
        }
        
        // 创建签名表定义（添加到原始DefAssembly）
        (enumDef, beanDef, tableDef) = CreateSignatureDefinitions(ctx.Assembly, ctx);
        
        return new SignatureCalculationResult
        {
            Signatures = signatures,
            EnumDef = enumDef,
            BeanDef = beanDef,
            TableDef = tableDef
        };
    }
    
    private static WrappedDefAssembly CreateWrappedDefAssembly(
        DefAssembly originalAssembly, 
        RawTarget signatureTarget, 
        List<string> signatureGroups)
    {
        // 如果指定了signatureGroups，创建新的RawTarget
        RawTarget targetForSignature = signatureTarget;
        if (signatureGroups != null && signatureGroups.Count > 0)
        {
            targetForSignature = new RawTarget
            {
                Name = signatureTarget.Name,
                Manager = signatureTarget.Manager,
                TopModule = signatureTarget.TopModule,
                Groups = signatureGroups
            };
        }
        
        return new WrappedDefAssembly(originalAssembly, targetForSignature);
    }
    
    private static GenerationContext CreateSignatureGenerationContext(
        DefAssembly assembly, 
        GenerationContext originalCtx)
    {
        var signatureCtx = new GenerationContext();
        var builder = new GenerationContextBuilder
        {
            Assembly = assembly,
            IncludeTags = originalCtx.IncludeTags,
            ExcludeTags = originalCtx.ExcludeTags,
            TimeZone = originalCtx.TimeZone.Id, // TimeZone是TimeZoneInfo，需要转换为Id
        };
        signatureCtx.Init(builder);
        
        // 复制已加载的数据（从原始context）
        var recordsField = typeof(GenerationContext).GetField(
            "_recordsByTables", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        var originalRecords = (ConcurrentDictionary<string, TableDataInfo>)recordsField.GetValue(originalCtx);
        
        foreach (var kvp in originalRecords)
        {
            signatureCtx.AddDataTable(kvp.Value.Table, kvp.Value.MainRecords, kvp.Value.PatchRecords);
        }
        
        return signatureCtx;
    }
    
    private static (DefEnum, DefBean, DefTable) CreateSignatureDefinitions(
        DefAssembly originalAssembly, 
        GenerationContext ctx)
    {
        var enumName = EnvManager.Current.GetOptionOrDefault(
            "", "tableEnum", true, "TableNames");
        var signatureName = EnvManager.Current.GetOptionOrDefault(
            "", "signatureName", true, "TableSignature");
        
        // 创建枚举定义
        var rawEnum = new RawEnum
        {
            Namespace = TypeUtil.GetNamespace(enumName),
            Name = TypeUtil.GetName(enumName),
            Comment = "表名枚举",
            IsUniqueItemId = true,
            Groups = new List<string>(),
            Items = new List<EnumItem>(),
            Tags = new Dictionary<string, string>(),
        };
        
        foreach (var defTable in ctx.ExportTables)
        {
            var name = defTable.FullName.Replace('.', '_');
            rawEnum.Items.Add(new EnumItem
            {
                Name = name,
                Comment = defTable.Comment,
                Tags = defTable.Tags,
                Value = string.Empty,
                Alias = string.Empty
            });
        }
        
        var enumDef = new DefEnum(rawEnum) { Assembly = originalAssembly };
        originalAssembly.AddType(enumDef);
        enumDef.PreCompile();
        enumDef.Compile();
        enumDef.PostCompile();
        
        // 创建Bean定义
        var rawBean = new RawBean
        {
            Namespace = TypeUtil.GetNamespace(signatureName),
            Name = TypeUtil.GetName(signatureName),
            Parent = string.Empty,
            Comment = "表数据签名",
            Tags = new Dictionary<string, string>(),
            Alias = string.Empty,
            Groups = new List<string>(),
            Fields = new List<RawField>
            {
                new RawField
                {
                    Name = "table_name",
                    Alias = string.Empty,
                    Type = enumDef.FullName,  // 使用枚举的FullName
                    Comment = "表名枚举",
                    Tags = new Dictionary<string, string>(),
                    Variants = new List<string>(),
                    Groups = new List<string>()
                },
                new RawField
                {
                    Name = "signature",
                    Alias = string.Empty,
                    Type = "string",
                    Comment = "签名",
                    Tags = new Dictionary<string, string>(),
                    Variants = new List<string>(),
                    Groups = new List<string>()
                }
            },
            TypeMappers = new List<TypeMapper>()
        };
        
        var beanDef = new DefBean(rawBean) { Assembly = originalAssembly };
        originalAssembly.AddType(beanDef);
        beanDef.PreCompile();
        beanDef.Compile();
        beanDef.PostCompile();
        
        // 创建Table定义
        var pascalName = string.Concat(signatureName.Replace('.', '_')
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
        var tableName = $"Tb{pascalName}";
        
        var rawTable = new RawTable
        {
            Namespace = string.Empty,
            Name = tableName,
            Index = "table_name",
            ValueType = beanDef.FullName,
            ReadSchemaFromFile = false,
            Mode = TableMode.MAP,
            Comment = "表数据签名表",
            Tags = new Dictionary<string, string>(),
            Groups = new List<string>(),
            InputFiles = new List<string>(),
            OutputFile = tableName.ToLower()
        };
        
        var tableDef = new DefTable(rawTable) { Assembly = originalAssembly };
        originalAssembly.AddType(tableDef);
        originalAssembly.AddCfgTable(tableDef);
        tableDef.PreCompile();
        tableDef.Compile();
        tableDef.PostCompile();
        
        // 添加到 ExportTables
        var exportTablesField = typeof(DefAssembly).GetField(
            "_exportTables", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        var exportTables = (List<DefTable>)exportTablesField.GetValue(originalAssembly);
        if (!exportTables.Contains(tableDef))
        {
            exportTables.Add(tableDef);
            tableDef.IsExported = true;
        }
        
        return (enumDef, beanDef, tableDef);
    }
}

// 包装的DefAssembly，通过反射临时替换Target字段
internal class WrappedDefAssembly
{
    private readonly DefAssembly _original;
    private readonly RawTarget _target;
    private readonly FieldInfo _targetField;
    private readonly RawTarget _originalTarget;
    
    public WrappedDefAssembly(DefAssembly original, RawTarget target)
    {
        _original = original;
        _target = target;
        
        // 通过反射获取Target字段并临时替换
        // Target是只读属性，编译器会生成一个私有字段
        // 尝试查找类型为RawTarget的只读字段
        var fields = typeof(DefAssembly).GetFields(
            BindingFlags.NonPublic | BindingFlags.Instance);
        _targetField = fields.FirstOrDefault(f => 
            f.FieldType == typeof(RawTarget) && 
            f.IsInitOnly); // 只读字段
        
        if (_targetField == null)
        {
            // 如果找不到，尝试查找所有RawTarget类型的字段
            _targetField = fields.FirstOrDefault(f => f.FieldType == typeof(RawTarget));
        }
        
        if (_targetField == null)
        {
            throw new Exception("Cannot find Target field in DefAssembly. " +
                "Available fields: " + string.Join(", ", fields.Select(f => f.Name)));
        }
        
        _originalTarget = (RawTarget)_targetField.GetValue(_original);
        _targetField.SetValue(_original, _target);
    }
    
    public DefAssembly Assembly => _original;
    
    public void Restore()
    {
        // 恢复原始Target
        _targetField.SetValue(_original, _originalTarget);
    }
}

