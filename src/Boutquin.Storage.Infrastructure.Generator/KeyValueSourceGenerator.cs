namespace Boutquin.Storage.Infrastructure.Generator;

[Generator]
public class KeyValueSourceGenerator : ISourceGenerator
{
    public void Execute(GeneratorExecutionContext context)
    {
        var compilation = context.Compilation;
        var typesToGenerate = new List<INamedTypeSymbol>();

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();
            var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

            foreach (var typeDeclaration in typeDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(typeDeclaration) as INamedTypeSymbol;

                if (symbol.GetAttributes().Any(attr =>
                        attr.AttributeClass.Name == nameof(KeyAttribute) ||
                        attr.AttributeClass.Name == nameof(SerializableAttribute)))
                {
                    typesToGenerate.Add(symbol);
                }
            }
        }

        foreach (var typeSymbol in typesToGenerate)
        {
            var source = GenerateMethods(typeSymbol);
            context.AddSource($"{typeSymbol.Name}_Generated.cs", SourceText.From(source, Encoding.UTF8));
        }
    }

    private string GenerateMethods(INamedTypeSymbol typeSymbol)
    {
        var typeName = typeSymbol.Name;
        var properties = typeSymbol.GetMembers().OfType<IPropertySymbol>();

        var serializeMethod = new StringBuilder();
        var deserializeMethod = new StringBuilder();
        var compareToMethod = new StringBuilder();

        serializeMethod.AppendLine("public void Serialize(BinaryWriter writer)");
        serializeMethod.AppendLine("{");
        foreach (var property in properties)
        {
            serializeMethod.AppendLine($"    writer.Write({property.Name});");
        }
        serializeMethod.AppendLine("}");

        deserializeMethod.AppendLine($"public static {typeName} Deserialize(BinaryReader reader)");
        deserializeMethod.AppendLine("{");
        deserializeMethod.AppendLine($"    return new {typeName}(");
        deserializeMethod.AppendLine(string.Join(", ", properties.Select(p => $"({p.Type})reader.Read{GetReadMethodSuffix(p.Type)}()")));
        deserializeMethod.AppendLine("    );");
        deserializeMethod.AppendLine("}");

        if (typeSymbol.GetAttributes().Any(attr => attr.AttributeClass.Name == nameof(KeyAttribute)))
        {
            compareToMethod.AppendLine($"public int CompareTo({typeName} other)");
            compareToMethod.AppendLine("{");
            compareToMethod.AppendLine($"    return ComparisonHelper.GenerateCompareTo(this.Value, other.Value);");
            compareToMethod.AppendLine("}");
        }

        var source = new StringBuilder();
        source.AppendLine($"namespace {typeSymbol.ContainingNamespace}");
        source.AppendLine("{");
        source.AppendLine($"    public readonly record struct {typeName} : ISerializable<{typeName}>{(typeSymbol.GetAttributes().Any(attr => attr.AttributeClass.Name == nameof(KeyAttribute)) ? $", IComparable<{typeName}>" : "")}");
        source.AppendLine("    {");
        source.AppendLine(string.Join("\n", properties.Select(p => $"        public {p.Type} {p.Name} {{ get; }}")));
        source.AppendLine();
        source.AppendLine($"        public {typeName}({string.Join(", ", properties.Select(p => $"{p.Type} {p.Name.ToLower()}"))})");
        source.AppendLine("        {");
        foreach (var property in properties)
        {
            source.AppendLine($"            {property.Name} = {property.Name.ToLower()};");
        }
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine(serializeMethod.ToString());
        source.AppendLine();
        source.AppendLine(deserializeMethod.ToString());

        if (compareToMethod.Length > 0)
        {
            source.AppendLine();
            source.AppendLine(compareToMethod.ToString());
        }

        source.AppendLine("    }");
        source.AppendLine("}");

        return source.ToString();
    }

    private string GetReadMethodSuffix(ITypeSymbol typeSymbol)
    {
        switch (typeSymbol.ToString())
        {
            case "System.Int32":
                return "Int32";
            case "System.Int64":
                return "Int64";
            case "System.String":
                return "String";
            default:
                return "String"; // Defaulting to string for simplicity. Adjust as needed.
        }
    }

    public void Initialize(GeneratorInitializationContext context)
    {
        // No initialization required for this generator
    }
}