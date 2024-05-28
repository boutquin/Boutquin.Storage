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
/// Specifies that a class or struct should be used as a key in the storage engine.
/// This attribute is used to mark classes or structs that will be utilized as keys 
/// for key-value pairs in the storage engine.
/// </summary>
/// <remarks>
/// The KeyAttribute should be applied to classes or structs that implement the IComparable&lt;T&gt; interface.
/// It ensures that the type can be used effectively as a key within the storage engine, which requires 
/// keys to be comparable.
/// </remarks>
/// <example>
/// The following example shows how to use the KeyAttribute:
/// <code>
/// using Boutquin.Storage.Domain.Attributes;
/// using Boutquin.Storage.Samples;
///
/// namespace Boutquin.Storage.Samples
/// {
///     /// &lt;summary&gt;
///     /// A record struct that represents a key with a single long value.
///     /// &lt;/summary&gt;
///     [Key]
///     public partial record struct Key(long Value) : IComparable&lt;Key&gt;
///     {
///         public int CompareTo(Key other)
///         {
///             return Value.CompareTo(other.Value);
///         }
///     }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class KeyAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KeyAttribute"/> class.
    /// </summary>
    /// <remarks>
    /// This constructor initializes the attribute and sets it to the target class or struct.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the attribute is applied to a type that does not implement the IComparable&lt;T&gt; interface.
    /// </exception>
    public KeyAttribute()
    {
        // Implementation logic can be added here, if needed.
        // For example, you can enforce that the attribute can only be applied to types that implement IComparable<T>.
    }
}