// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
namespace Boutquin.Storage.Domain.Attributes;

/// <summary>
/// Specifies that a class or struct is serializable within the storage engine.
/// This attribute is used to mark classes or structs that will be serialized 
/// and deserialized when stored and retrieved from the storage engine.
/// </summary>
/// <remarks>
/// The SerializableAttribute should be applied to classes or structs that need 
/// to be persisted in a storage medium. It ensures that the marked types can 
/// be serialized and deserialized appropriately by the storage engine.
/// </remarks>
/// <example>
/// The following example shows how to use the SerializableAttribute:
/// <code>
/// using Boutquin.Storage.Domain.Attributes;
///
/// namespace Boutquin.Storage.Samples
/// {
///     /// &lt;summary&gt;
///     /// A record struct that represents an attraction with a name.
///     /// &lt;/summary&gt;
///     [Serializable]
///     public partial record struct Attraction(string Name);
///
///     /// &lt;summary&gt;
///     /// A record struct that represents a city with a name and a collection of attractions.
///     /// &lt;/summary&gt;
///     [Serializable]
///     public partial record struct City(string Name, IEnumerable&lt;Attraction&gt; Attractions);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class SerializableAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SerializableAttribute"/> class.
    /// </summary>
    /// <remarks>
    /// This constructor initializes the attribute and sets it to the target class or struct.
    /// </remarks>
    public SerializableAttribute()
    {
        // Implementation logic can be added here, if needed.
        // For example, you can enforce additional constraints or logging.
    }
}