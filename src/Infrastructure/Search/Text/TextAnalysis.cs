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

namespace Boutquin.Storage.Infrastructure.Search.Text;

/// <summary>Splits text at Unicode whitespace boundaries.</summary>
public sealed class WhitespaceTokenizer : ITokenizer
{
    /// <inheritdoc />
    public IReadOnlyList<string> Tokenize(ReadOnlyMemory<char> text) =>
        text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Produces overlapping character n-grams.</summary>
public sealed class NGramTokenizer : ITokenizer
{
    private readonly int _size;

    /// <summary>Initializes a tokenizer with the requested gram size.</summary>
    public NGramTokenizer(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        _size = size;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Tokenize(ReadOnlyMemory<char> text)
    {
        if (text.Length < _size)
        {
            return [];
        }

        var result = new string[text.Length - _size + 1];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = text.Slice(index, _size).ToString();
        }

        return result;
    }
}

/// <summary>Normalizes tokens with invariant lower casing.</summary>
public sealed class LowercaseNormalizer : INormalizer
{
    /// <inheritdoc />
    public string Normalize(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.ToLowerInvariant();
    }
}

/// <summary>Discards configured stop words after invariant case folding.</summary>
public sealed class StopWordNormalizer : INormalizer
{
    private readonly HashSet<string> _stopWords;

    /// <summary>Initializes a stop-word filter.</summary>
    public StopWordNormalizer(IEnumerable<string> stopWords)
    {
        ArgumentNullException.ThrowIfNull(stopWords);
        _stopWords = new HashSet<string>(stopWords, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public string? Normalize(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return _stopWords.Contains(token) ? null : token;
    }
}

/// <summary>Composes a tokenizer with an ordered normalization pipeline.</summary>
public sealed class TextAnalyzer : ITextAnalyzer
{
    private readonly ITokenizer _tokenizer;
    private readonly IReadOnlyList<INormalizer> _normalizers;

    /// <summary>Initializes a text analyzer.</summary>
    public TextAnalyzer(ITokenizer tokenizer, params INormalizer[] normalizers)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(normalizers);
        _tokenizer = tokenizer;
        _normalizers = normalizers;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Analyze(ReadOnlyMemory<char> text)
    {
        var result = new List<string>();
        foreach (var rawToken in _tokenizer.Tokenize(text))
        {
            string? token = rawToken;
            foreach (var normalizer in _normalizers)
            {
                token = normalizer.Normalize(token);
                if (token is null)
                {
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                result.Add(token);
            }
        }

        return result;
    }
}

/// <summary>Scores terms with smoothed term-frequency/inverse-document-frequency.</summary>
public sealed class TfIdfScorer : ITextScorer
{
    /// <inheritdoc />
    public double Score(int termFrequency, int documentFrequency, int documentCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(termFrequency);
        ArgumentOutOfRangeException.ThrowIfNegative(documentFrequency);
        ArgumentOutOfRangeException.ThrowIfNegative(documentCount);
        return termFrequency == 0 ? 0 : termFrequency * (Math.Log((documentCount + 1D) / (documentFrequency + 1D)) + 1D);
    }
}
