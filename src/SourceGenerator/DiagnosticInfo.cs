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

// Serializable diagnostic record for pipeline caching. Never stores a
// Microsoft.CodeAnalysis.Diagnostic or Location directly — those are not equatable
// across compilations and defeat incremental caching.
internal sealed record DiagnosticInfo(
    string Id,
    string Title,
    string Message,
    DiagnosticSeverity Severity,
    LocationInfo? Location)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, Location? location, params object[] messageArgs)
    {
        LocationInfo? locationInfo = location is not null ? LocationInfo.From(location) : null;
        var message = messageArgs.Length > 0
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, descriptor.MessageFormat.ToString(), messageArgs)
            : descriptor.MessageFormat.ToString();
        // Capture the descriptor's human-readable Title (a plain string, keeping the record
        // equatable for incremental caching). Without it, ToDiagnostic reconstructed the
        // DiagnosticDescriptor with the Id in the title slot, so IDEs showed e.g. "BSSG001"
        // as the diagnostic title instead of "Unsupported property type".
        return new DiagnosticInfo(
            descriptor.Id,
            descriptor.Title.ToString(System.Globalization.CultureInfo.InvariantCulture),
            message,
            descriptor.DefaultSeverity,
            locationInfo);
    }

    public Diagnostic ToDiagnostic()
    {
        var descriptor = new DiagnosticDescriptor(
            Id,
            Title,
            Message,
            "Boutquin.Storage.SourceGenerator",
            Severity,
            isEnabledByDefault: true);
        var location = Location?.ToLocation() ?? Microsoft.CodeAnalysis.Location.None;
        return Diagnostic.Create(descriptor, location);
    }
}
