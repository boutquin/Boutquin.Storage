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

// Custom record struct to hold location data without retaining a Roslyn
// Microsoft.CodeAnalysis.Location reference.  Roslyn's ISymbol, Location, and
// related types implement only reference equality — two syntactically identical
// compilation passes produce distinct instances that compare unequal.  A pipeline
// model type holding one would therefore never produce a Cached step during
// incremental generation, silently forcing full regeneration on every keystroke.
// By extracting only the three plain-value fields here (FilePath, Span,
// LinePositionSpan), the incremental caching layer can compare instances by value
// and skip re-emission when nothing has actually changed.
internal readonly record struct LocationInfo(
    string FilePath,
    TextSpan Span,
    LinePositionSpan LineSpan)
{
    public static LocationInfo From(Location location)
    {
        var mapped = location.GetMappedLineSpan();
        return new LocationInfo(
            mapped.Path,
            location.SourceSpan,
            mapped.Span);
    }

    public Location ToLocation() =>
        Location.Create(FilePath, Span, LineSpan);
}
