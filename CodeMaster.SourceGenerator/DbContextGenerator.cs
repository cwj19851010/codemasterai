using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeMaster.SourceGenerator
{
    [Generator]
    public class DbContextGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context)
        {
            // 不需要 SyntaxReceiver，直接扫描编译中的所有类型
        }

        public void Execute(GeneratorExecutionContext context)
        {
            var compilation = context.Compilation;
            var entityClasses = new List<EntityInfo>();
            var allEntityClasses = new List<EntityInfo>(); // 去重前，用于判断类名歧义
            var tableNames = new HashSet<string>(); // 用于检测重复的表名

            // 扫描所有引用的程序集中的类型
            foreach (var reference in compilation.References)
            {
                var assemblySymbol = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
                if (assemblySymbol != null)
                {
                    // 只扫描 CodeMaster.Domain 程序集
                    if (assemblySymbol.Name == "CodeMaster.Domain")
                    {
                        var types = GetAllTypes(assemblySymbol.GlobalNamespace);
                        foreach (var type in types)
                        {
                            if (type.TypeKind == TypeKind.Class &&
                                !type.IsAbstract &&
                                !type.IsStatic &&
                                ImplementsIEntity(type))
                            {
                                var entityInfo = AnalyzeEntity(type);
                                allEntityClasses.Add(entityInfo);

                                // 检查表名是否重复
                                if (!tableNames.Contains(entityInfo.TableName))
                                {
                                    tableNames.Add(entityInfo.TableName);
                                    entityClasses.Add(entityInfo);
                                }
                            }
                        }
                    }
                }
            }

            if (entityClasses.Count == 0)
                return;

            // 类名歧义必须按“扫描到的全部实体”判断，不能看去重后的列表：
            // 两个模块各有一个 Material 时，它们往往连表名都相同，会被按表名去重成一个，
            // 于是输出列表看起来毫无冲突，但两个命名空间都被 using 引入，
            // 未限定的 DbSet<Material> 照样报 CS0104。
            var ambiguousClassNames = FindAmbiguousClassNames(allEntityClasses);

            // 分部类的落点必须与项目里真正使用的 DbContext 一致。类名与命名空间都不能写死：
            // 模板会按项目名替换（CodeMaster → 项目名），写死会让生成结果落到另一个类型上 ——
            // 既报 CS0759（实现没有对应的声明），又让 DbSet 压根没挂进真正使用的上下文。
            var dbContextType = FindDbContextType(compilation);
            var dbContextNamespace = dbContextType != null
                ? dbContextType.ContainingNamespace.ToDisplayString()
                : "CodeMaster.Migrator.Persistence.EfCore";
            var dbContextClassName = dbContextType != null ? dbContextType.Name : "CodeMasterDbContext";
            var configurationNamespace = dbContextNamespace + ".Configurations";

            // 生成 DbContext partial class
            var dbContextSource = GenerateDbContextPartial(
                entityClasses, ambiguousClassNames, dbContextNamespace, dbContextClassName, configurationNamespace);
            context.AddSource("CodeMasterDbContext.g.cs", SourceText.From(dbContextSource, Encoding.UTF8));

            // 生成 EntityConfiguration 类
            foreach (var entity in entityClasses)
            {
                var configSource = GenerateEntityConfiguration(entity, ambiguousClassNames, configurationNamespace);
                // hint 名必须唯一：两个模块各有一个同名实体时，只用简单类名会撞名，
                // AddSource 遇到重复 hint 会直接抛异常，整个生成器失效。
                context.AddSource(
                    $"{entity.Namespace}.{entity.ClassName}Configuration.g.cs",
                    SourceText.From(configSource, Encoding.UTF8));
            }
        }

        private IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol namespaceSymbol)
        {
            foreach (var type in namespaceSymbol.GetTypeMembers())
            {
                yield return type;
            }

            foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
            {
                foreach (var type in GetAllTypes(childNamespace))
                {
                    yield return type;
                }
            }
        }

        private bool ImplementsIEntity(INamedTypeSymbol classSymbol)
        {
            // 检查是否实现了 IEntity 接口
            foreach (var iface in classSymbol.AllInterfaces)
            {
                if (iface.Name == "IEntity" && iface.ContainingNamespace.ToDisplayString() == "CodeMaster.Core.Entities")
                {
                    return true;
                }
            }
            return false;
        }

        private bool InheritsFromEntityBase(INamedTypeSymbol classSymbol)
        {
            var baseType = classSymbol.BaseType;
            while (baseType != null)
            {
                var baseTypeName = baseType.Name;
                if (baseTypeName == "EntityBase" || baseTypeName == "TenantEntityBase")
                    return true;
                baseType = baseType.BaseType;
            }
            return false;
        }

        private EntityInfo AnalyzeEntity(INamedTypeSymbol typeSymbol)
        {
            var entityInfo = new EntityInfo
            {
                ClassName = typeSymbol.Name,
                Namespace = typeSymbol.ContainingNamespace.ToDisplayString(),
                InheritsEntityBase = InheritsFromEntityBase(typeSymbol),
                IsView = typeSymbol.AllInterfaces.Any(i => i.Name == "IViewEntity")
            };

            // 1. 检查 SugarTable 特性获取表名
            var sugarTableAttr = typeSymbol.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "SugarTableAttribute" || a.AttributeClass?.Name == "SugarTable");

            if (sugarTableAttr != null && sugarTableAttr.ConstructorArguments.Length > 0)
            {
                // 有 SugarTable 特性，使用其 TableName
                entityInfo.TableName = sugarTableAttr.ConstructorArguments[0].Value?.ToString() ?? ToSnakeCase(typeSymbol.Name);
            }
            else
            {
                // 没有 SugarTable 特性，使用默认规则（类名转 snake_case 并复数化）
                var tableName = ToSnakeCase(typeSymbol.Name);
                entityInfo.TableName = tableName.EndsWith("s")
                    ? tableName
                    : tableName + "s";
            }

            // 2. 分析所有属性
            var allMembers = new List<ISymbol>();
            var currentType = typeSymbol;
            while (currentType != null)
            {
                allMembers.AddRange(currentType.GetMembers());
                currentType = currentType.BaseType;
            }

            var properties = allMembers.OfType<IPropertySymbol>();

            foreach (var property in properties)
            {
                bool shouldIgnore = false;
                string columnName = null;
                bool isPrimaryKey = false;

                // 检查属性的所有特性
                foreach (var attribute in property.GetAttributes())
                {
                    var attrName = attribute.AttributeClass?.Name;

                    // 检查 SugarColumn 特性
                    if (attrName == "SugarColumnAttribute" || attrName == "SugarColumn")
                    {
                        foreach (var namedArg in attribute.NamedArguments)
                        {
                            if (namedArg.Key == "IsIgnore" && namedArg.Value.Value is bool isIgnore && isIgnore)
                            {
                                shouldIgnore = true;
                            }
                            else if (namedArg.Key == "ColumnName" && namedArg.Value.Value is string colName)
                            {
                                columnName = colName;
                            }
                            else if (namedArg.Key == "IsPrimaryKey" && namedArg.Value.Value is bool isPk && isPk)
                            {
                                isPrimaryKey = true;
                            }
                        }
                    }

                    // 检查 Navigate 特性（SqlSugar 导航属性）
                    if (attrName == "NavigateAttribute" || attrName == "Navigate")
                    {
                        shouldIgnore = true;
                    }
                }

                if (shouldIgnore)
                {
                    entityInfo.IgnoredProperties.Add(property.Name);
                }
                else
                {
                    // 如果没有指定列名，使用默认规则
                    if (string.IsNullOrEmpty(columnName))
                    {
                        columnName = ToSnakeCase(property.Name);
                    }

                    entityInfo.PropertyColumns[property.Name] = columnName;

                    if (isPrimaryKey)
                    {
                        entityInfo.PrimaryKeys.Add(property.Name);
                    }
                }
            }

            // 3. 如果没有找到主键，判断是否继承 EntityBase
            if (entityInfo.PrimaryKeys.Count == 0)
            {
                if (entityInfo.InheritsEntityBase)
                {
                    // 继承 EntityBase，默认主键是 Id
                    entityInfo.PrimaryKeys.Add("Id");
                }
                // 否则是无主键表
            }

            return entityInfo;
        }

        private string ToSnakeCase(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // 处理连续大写字母的情况（如 "HTTPServer" -> "http_server"）
            var result = Regex.Replace(input, "([A-Z]+)([A-Z][a-z])", "$1_$2");
            // 处理普通驼峰命名（如 "userId" -> "user_id"）
            result = Regex.Replace(result, "([a-z0-9])([A-Z])", "$1_$2");
            return result.ToLower();
        }

        /// <summary>
        /// 在编译中找出真正使用的 DbContext 类型（继承 Microsoft.EntityFrameworkCore.DbContext 的类）。
        /// 找不到时返回 null，调用方回退到历史默认值，保证框架自身不受影响。
        /// </summary>
        private static INamedTypeSymbol FindDbContextType(Compilation compilation)
        {
            return FindDbContextIn(compilation.Assembly.GlobalNamespace);
        }

        private static INamedTypeSymbol FindDbContextIn(INamespaceSymbol namespaceSymbol)
        {
            foreach (var type in namespaceSymbol.GetTypeMembers())
            {
                if (type.TypeKind == TypeKind.Class && IsDbContextType(type))
                    return type;
            }

            foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
            {
                var found = FindDbContextIn(childNamespace);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static bool IsDbContextType(INamedTypeSymbol type)
        {
            var baseType = type.BaseType;
            while (baseType != null)
            {
                if (baseType.ToDisplayString() == "Microsoft.EntityFrameworkCore.DbContext")
                    return true;
                baseType = baseType.BaseType;
            }

            return false;
        }

        /// <summary>
        /// 找出简单类名重复的实体。
        /// 两个模块各有一个 Material 是合法建模，但 DbSet 属性名与配置类名会因此冲突，
        /// 而 DbSet 用简单类名引用时会产生 CS0104（引用不明确）导致整个生成工程编译不过。
        /// 先把这类实体标出来，后面按需限定命名空间。
        /// </summary>
        private static HashSet<string> FindAmbiguousClassNames(List<EntityInfo> entities)
        {
            var counts = new Dictionary<string, int>(System.StringComparer.Ordinal);
            foreach (var entity in entities)
            {
                int current;
                counts.TryGetValue(entity.ClassName, out current);
                counts[entity.ClassName] = current + 1;
            }

            var ambiguous = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var pair in counts)
            {
                if (pair.Value > 1)
                    ambiguous.Add(pair.Key);
            }

            return ambiguous;
        }

        /// <summary>
        /// 歧义实体的去重前缀取命名空间末段（模块名）；不歧义时为空，保持原有可读输出。
        /// </summary>
        private static string DisambiguatingPrefix(EntityInfo entity, HashSet<string> ambiguousClassNames)
        {
            if (!ambiguousClassNames.Contains(entity.ClassName))
                return string.Empty;

            var ns = entity.Namespace ?? string.Empty;
            var separator = ns.LastIndexOf('.');
            return separator >= 0 ? ns.Substring(separator + 1) : ns;
        }

        private static string QualifiedTypeName(EntityInfo entity, HashSet<string> ambiguousClassNames)
        {
            return ambiguousClassNames.Contains(entity.ClassName)
                ? entity.Namespace + "." + entity.ClassName
                : entity.ClassName;
        }

        private static string DbSetPropertyName(EntityInfo entity, HashSet<string> ambiguousClassNames)
        {
            return DisambiguatingPrefix(entity, ambiguousClassNames) + entity.ClassName + "s";
        }

        private static string ConfigurationClassName(EntityInfo entity, HashSet<string> ambiguousClassNames)
        {
            return DisambiguatingPrefix(entity, ambiguousClassNames) + entity.ClassName + "Configuration";
        }

        private string GenerateDbContextPartial(
            List<EntityInfo> entities,
            HashSet<string> ambiguousClassNames,
            string dbContextNamespace,
            string dbContextClassName,
            string configurationNamespace)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("using Microsoft.EntityFrameworkCore;");
            sb.AppendLine($"using {configurationNamespace};");

            // 添加所有实体的命名空间
            var namespaces = entities.Select(e => e.Namespace).Distinct().OrderBy(n => n);
            foreach (var ns in namespaces)
            {
                sb.AppendLine($"using {ns};");
            }

            sb.AppendLine();
            sb.AppendLine($"namespace {dbContextNamespace}");
            sb.AppendLine("{");
            sb.AppendLine($"    public partial class {dbContextClassName}");
            sb.AppendLine("    {");

            // 生成 DbSet 属性
            foreach (var entity in entities.OrderBy(e => e.ClassName))
            {
                sb.AppendLine($"        public DbSet<{QualifiedTypeName(entity, ambiguousClassNames)}> {DbSetPropertyName(entity, ambiguousClassNames)} {{ get; set; }}");
            }

            sb.AppendLine();
            sb.AppendLine("        partial void OnModelCreatingPartial(ModelBuilder modelBuilder)");
            sb.AppendLine("        {");

            // 应用所有配置
            foreach (var entity in entities.OrderBy(e => e.ClassName))
            {
                sb.AppendLine($"            modelBuilder.ApplyConfiguration(new {ConfigurationClassName(entity, ambiguousClassNames)}());");
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private string GenerateEntityConfiguration(
            EntityInfo entity,
            HashSet<string> ambiguousClassNames,
            string configurationNamespace)
        {
            var typeName = QualifiedTypeName(entity, ambiguousClassNames);
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("using Microsoft.EntityFrameworkCore;");
            sb.AppendLine("using Microsoft.EntityFrameworkCore.Metadata.Builders;");
            sb.AppendLine($"using {entity.Namespace};");
            sb.AppendLine();
            sb.AppendLine($"namespace {configurationNamespace}");
            sb.AppendLine("{");
            sb.AppendLine($"    public class {ConfigurationClassName(entity, ambiguousClassNames)} : IEntityTypeConfiguration<{typeName}>");
            sb.AppendLine("    {");
            sb.AppendLine($"        public void Configure(EntityTypeBuilder<{typeName}> builder)");
            sb.AppendLine("        {");

            // Database views must not become CREATE TABLE operations in EF migrations.
            sb.AppendLine($"            // 配置数据源");
            if (entity.IsView)
                sb.AppendLine($"            builder.ToView(\"{entity.TableName}\");");
            else
                sb.AppendLine($"            builder.ToTable(\"{entity.TableName}\");");
            sb.AppendLine();

            // 配置主键
            if (entity.PrimaryKeys.Count > 0)
            {
                sb.AppendLine("            // 配置主键");
                if (entity.PrimaryKeys.Count == 1)
                {
                    var pkName = entity.PrimaryKeys[0];
                    sb.AppendLine($"            builder.HasKey(e => e.{pkName});");

                    // IEntity<long> uses application-generated snowflake IDs.
                    if (pkName == "Id")
                    {
                        sb.AppendLine($"            builder.Property(e => e.{pkName}).ValueGeneratedNever();");
                    }
                }
                else
                {
                    // 复合主键
                    var pkProperties = string.Join(", ", entity.PrimaryKeys.Select(pk => $"e.{pk}"));
                    sb.AppendLine($"            builder.HasKey(e => new {{ {pkProperties} }});");
                }
                sb.AppendLine();
            }
            else
            {
                // 无主键表
                sb.AppendLine("            // 无主键表");
                sb.AppendLine("            builder.HasNoKey();");
                sb.AppendLine();
            }

            // 配置列名
            if (entity.PropertyColumns.Count > 0)
            {
                sb.AppendLine("            // 配置列名");
                foreach (var kvp in entity.PropertyColumns.OrderBy(k => k.Key))
                {
                    sb.AppendLine($"            builder.Property(e => e.{kvp.Key}).HasColumnName(\"{kvp.Value}\");");
                }
                sb.AppendLine();
            }

            // 添加 Ignore 配置
            if (entity.IgnoredProperties.Count > 0)
            {
                sb.AppendLine("            // 忽略 SqlSugar 标记为 IsIgnore 的属性和导航属性");
                foreach (var propertyName in entity.IgnoredProperties.OrderBy(p => p))
                {
                    sb.AppendLine($"            builder.Ignore(e => e.{propertyName});");
                }
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private class EntityInfo
        {
            public string ClassName { get; set; }
            public string Namespace { get; set; }
            public string TableName { get; set; }
            public bool InheritsEntityBase { get; set; }
            public bool IsView { get; set; }
            public List<string> PrimaryKeys { get; set; } = new List<string>();
            public List<string> IgnoredProperties { get; set; } = new List<string>();
            public Dictionary<string, string> PropertyColumns { get; set; } = new Dictionary<string, string>();
        }
    }
}
