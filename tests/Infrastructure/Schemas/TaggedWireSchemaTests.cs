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
using Boutquin.Storage.Infrastructure.Schemas;

namespace Boutquin.Storage.Infrastructure.Tests.Schemas;

/// <summary>
/// Tests for <see cref="TaggedWireSchema"/> — the tagged-wire-backed <c>ISchema</c> implementation.
/// </summary>
public sealed class TaggedWireSchemaTests
{
    [Fact]
    public void Fields_ProjectsTaggedFields_PreservingOrderNameOptionalityAndMappingType()
    {
        var schema = new TaggedWireSchema(
        [
            new TaggedWireField(1, "Name", "string", IsOptional: false),
            new TaggedWireField(2, "Age", "int", IsOptional: true),
        ]);

        schema.Fields.Should().Equal(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Age", SchemaFieldType.Int32, true));
    }
}
