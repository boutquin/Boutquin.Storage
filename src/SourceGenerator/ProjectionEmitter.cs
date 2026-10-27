// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License").
//  You may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
namespace Boutquin.Storage.SourceGenerator;

// Generator emission for IProjectable<TSelf>.ProjectTo on [StorageSerializable] records.
// Emits a switch-per-field body that
// clips unprojected fields to default(TField). The memtable path in LsmTimeSeriesStorageEngine
// runtime-casts to IProjectable<TRecord> and calls ProjectTo per record so memtable-resident
// rows expose the same projection shape as columnar-segment rows (DDIA 2e Ch. 4 p. 141).
//
// Shape uniformity is the whole point: without this emission, memtable records for
// [StorageSerializable] types would flow through unprojected while columnar-segment records
// would be clipped, silently breaking the caller's shape assumption.
internal static class ProjectionEmitter
{
    private const string ColumnProjection =
        "global::Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnProjection";

    private const string ColumnNotFoundException =
        "global::Boutquin.Storage.Domain.Exceptions.ColumnNotFoundException";

    // Segment-format version used in ColumnNotFoundException messages. Matches the tag-0
    // schema-format version the tagged-wire emitter writes; columnar segments surface their
    // own footer version at read time, but in-memory ProjectTo has no footer to consult, so
    // 1u is the stable choice under the greenfield premise (no pre-2.0 wire anywhere).
    private const uint SchemaFormatVersion = 1u;

    /// <summary>
    /// Emit <c>public TSelf ProjectTo(ColumnProjection projection)</c> for a record whose
    /// property list is carried on <paramref name="type"/>. Mutates via <c>with</c> expression
    /// so record-struct <c>init</c>-only properties stay honoured.
    /// </summary>
    public static void AppendProjectTo(StringBuilder sb, TypeToGenerate type, string indent)
    {
        // Stable list of declared field names the method advertises. Built from the property
        // list so renames flow automatically; unsupported properties are excluded so BSSG001
        // cases don't leak into the name list.
        var declared = type.Properties
            .Where(p => p.Kind != TypeKind.Unsupported)
            .Select(p => p.Name)
            .ToList();

        var typeName = IdentifierEscaper.Escape(type.Name);

        sb.AppendLine($"{indent}public {typeName} ProjectTo({ColumnProjection} projection)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    if (projection.FieldNames.Count == 0) return this;");
        sb.AppendLine();

        // Declared-name array for ColumnNotFoundException. Skip emission when there are zero
        // declared fields — the switch's default-branch throw carries the array by reference,
        // and an empty array is harmless.
        sb.Append($"{indent}    var __declared = new string[] {{ ");
        for (var i = 0; i < declared.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append('"').Append(declared[i]).Append('"');
        }
        sb.AppendLine(" };");

        // Seed __result with an all-default instance, then `with` expressions layer the projected
        // field assignments on top. For a record STRUCT, default(TSelf) is the zero-initialised
        // value. For a record CLASS, default(TSelf) is null — `null with { ... }` would throw a
        // NullReferenceException at the first projected-field assignment. A positional record class
        // has no public parameterless constructor, so `new TSelf()` will not compile either;
        // instead clone the receiver (`this with { }` is non-null even for a record class) and
        // clip every declared field back to default in the same initialiser, yielding an all-
        // default instance without naming any constructor.
        if (type.IsRecordStruct)
        {
            sb.AppendLine($"{indent}    var __result = default({typeName});");
        }
        else
        {
            sb.Append($"{indent}    var __result = this with {{ ");
            for (var i = 0; i < declared.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                // Identifier position — escape the member name in the `with` initialiser.
                sb.Append(IdentifierEscaper.Escape(declared[i])).Append(" = default!");
            }
            sb.AppendLine(" };");
        }
        sb.AppendLine($"{indent}    foreach (var __name in projection.FieldNames)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        switch (__name)");
        sb.AppendLine($"{indent}        {{");

        foreach (var prop in type.Properties)
        {
            if (prop.Kind == TypeKind.Unsupported)
            {
                continue;
            }

            // The case label is a STRING literal (matches the runtime field name) → bare name.
            // The `with` member assignment and `this.` access are IDENTIFIER positions → escaped.
            var member = IdentifierEscaper.Escape(prop.Name);
            sb.AppendLine($"{indent}            case \"{prop.Name}\":");
            sb.AppendLine($"{indent}                __result = __result with {{ {member} = this.{member} }};");
            sb.AppendLine($"{indent}                break;");
        }

        sb.AppendLine($"{indent}            default:");
        sb.AppendLine($"{indent}                if (!projection.AllowMissing)");
        sb.AppendLine($"{indent}                {{");
        sb.AppendLine($"{indent}                    throw new {ColumnNotFoundException}(__name, {SchemaFormatVersion}u, __declared);");
        sb.AppendLine($"{indent}                }}");
        sb.AppendLine($"{indent}                break;");
        sb.AppendLine($"{indent}        }}");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    return __result;");
        sb.AppendLine($"{indent}}}");
    }
}
