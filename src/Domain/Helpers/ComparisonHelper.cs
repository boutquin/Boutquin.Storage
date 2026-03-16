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
namespace Boutquin.Storage.Domain.Helpers;

/// <summary>
/// Provides null-safe comparison utilities for <see cref="IComparable{T}"/> types.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a static helper for null-safe comparison?</b> <see cref="IComparable{T}.CompareTo"/>
/// doesn't specify behavior for null arguments. This helper provides a consistent null-ordering
/// convention: null is less than any non-null value, and two nulls are equal. This prevents
/// <see cref="NullReferenceException"/> in sorted collections and ensures deterministic ordering.
/// </para>
/// </remarks>
public static class ComparisonHelper
{
    /// <summary>
    /// Compares two values of the same type with null-safe semantics.
    /// Null is considered less than any non-null value; two nulls are equal.
    /// </summary>
    /// <typeparam name="T">The comparable type.</typeparam>
    /// <param name="obj">The first value to compare.</param>
    /// <param name="other">The second value to compare.</param>
    /// <returns>A value indicating the relative order of the values.</returns>
    public static int SafeCompareTo<T>(T? obj, T? other) where T : IComparable<T>
    {
        if (obj == null)
        {
            return other == null ? 0 : -1;
        }

        return other == null ? 1 : obj.CompareTo(other);
    }
}
