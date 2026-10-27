// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//
using Boutquin.Storage.Domain.Interfaces.Search;

namespace Boutquin.Storage.Infrastructure.Search.Vector;

/// <summary>Computes cosine distance with explicit zero-vector semantics.</summary>
public sealed class CosineDistance : IVectorDistanceMetric, IWideVectorDistanceMetric
{
    /// <inheritdoc />
    public float Distance(ReadOnlySpan<float> left, ReadOnlySpan<float> right) => (float)WideDistance(left, right);

    double IWideVectorDistanceMetric.Distance(ReadOnlySpan<float> left, ReadOnlySpan<float> right) => WideDistance(left, right);

    private static double WideDistance(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        VectorValidation.ValidatePair(left, right);
        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (var index = 0; index < left.Length; index++)
        {
            // Widen before multiplying: a float product overflows for components above ~1.8e19.
            double leftValue = left[index];
            double rightValue = right[index];
            dot += leftValue * rightValue;
            leftNorm += leftValue * leftValue;
            rightNorm += rightValue * rightValue;
        }

        if (leftNorm == 0 && rightNorm == 0)
        {
            return 0;
        }

        if (leftNorm == 0 || rightNorm == 0)
        {
            return 1;
        }

        var similarity = Math.Clamp(dot / Math.Sqrt(leftNorm * rightNorm), -1D, 1D);
        return 1D - similarity;
    }
}

/// <summary>Computes Euclidean distance.</summary>
public sealed class L2Distance : IVectorDistanceMetric, IWideVectorDistanceMetric
{
    /// <inheritdoc />
    public float Distance(ReadOnlySpan<float> left, ReadOnlySpan<float> right) => (float)WideDistance(left, right);

    double IWideVectorDistanceMetric.Distance(ReadOnlySpan<float> left, ReadOnlySpan<float> right) => WideDistance(left, right);

    private static double WideDistance(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        VectorValidation.ValidatePair(left, right);
        double squared = 0;
        for (var index = 0; index < left.Length; index++)
        {
            var difference = (double)left[index] - right[index];
            squared += difference * difference;
        }

        return Math.Sqrt(squared);
    }
}

/// <summary>Returns the negative dot product so smaller values remain better.</summary>
public sealed class DotProductDistance : IVectorDistanceMetric, IWideVectorDistanceMetric
{
    /// <inheritdoc />
    public float Distance(ReadOnlySpan<float> left, ReadOnlySpan<float> right) => (float)WideDistance(left, right);

    double IWideVectorDistanceMetric.Distance(ReadOnlySpan<float> left, ReadOnlySpan<float> right) => WideDistance(left, right);

    private static double WideDistance(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        VectorValidation.ValidatePair(left, right);
        double dot = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += (double)left[index] * right[index];
        }

        return -dot;
    }
}

/// <summary>
/// A built-in metric's distance before it is narrowed to <see cref="float"/>. A distance between
/// finite float vectors can exceed float range (for example L2 between components near +/-3e38, or a
/// dot product of components near 1.4e19); narrowing collapses distinct distances to the same
/// infinity, so the indexes rank on this value and narrow only the distance they report.
/// </summary>
internal interface IWideVectorDistanceMetric
{
    double Distance(ReadOnlySpan<float> left, ReadOnlySpan<float> right);
}

internal static class VectorRanking
{
    /// <summary>Returns the ranking distance: the unnarrowed value when the metric provides one.</summary>
    internal static double Distance(IVectorDistanceMetric metric, ReadOnlySpan<float> left, ReadOnlySpan<float> right) =>
        metric is IWideVectorDistanceMetric wide ? wide.Distance(left, right) : metric.Distance(left, right);
}

internal static class VectorValidation
{
    internal static float[] Copy(ReadOnlySpan<float> vector, int dimension, string parameterName)
    {
        if (vector.Length != dimension)
        {
            throw new ArgumentException($"Vector dimension must be {dimension}.", parameterName);
        }

        ValidateFinite(vector, parameterName);
        return vector.ToArray();
    }

    internal static void ValidatePair(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length == 0 || left.Length != right.Length)
        {
            throw new ArgumentException("Vectors must be non-empty and have equal dimensions.");
        }

        ValidateFinite(left, nameof(left));
        ValidateFinite(right, nameof(right));
    }

    private static void ValidateFinite(ReadOnlySpan<float> vector, string parameterName)
    {
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentException("Vectors must contain only finite values.", parameterName);
            }
        }
    }
}
