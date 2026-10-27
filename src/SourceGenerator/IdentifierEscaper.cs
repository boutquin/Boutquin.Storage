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

// Centralised identifier escaping for every emitter. A user may declare a property (or type)
// whose name is a verbatim C# keyword — `@class`, `@event`, `@int`. The semantic model strips the
// leading `@`, so ISymbol.Name returns the bare keyword ("class"). Interpolating that bare name
// into an identifier position in the generated source (`this.class`, `_class`, `new T(...)`, a
// `with` member assignment) produces uncompilable C#. Re-escaping with a leading `@` restores the
// verbatim identifier the user wrote.
//
// Apply this ONLY at identifier positions. String-literal positions (e.g. a `case "class":` label
// matching a runtime field name, or a ColumnNotFoundException message argument) carry the bare
// name and must NOT be escaped — the `@` is purely a lexer escape and is not part of the
// identifier's value.
internal static class IdentifierEscaper
{
    // Returns the identifier prefixed with `@` when it collides with a C# keyword (reserved or
    // contextual); otherwise returns it unchanged. SyntaxFacts is the canonical keyword oracle, so
    // this stays correct as the language adds keywords. Already-escaped names (defensive: a leading
    // `@`) and the empty string pass through untouched.
    public static string Escape(string identifier)
    {
        if (string.IsNullOrEmpty(identifier) || identifier[0] == '@')
        {
            return identifier;
        }

        var kind = SyntaxFacts.GetKeywordKind(identifier);
        if (kind != SyntaxKind.None && SyntaxFacts.IsReservedKeyword(kind))
        {
            return "@" + identifier;
        }

        var contextual = SyntaxFacts.GetContextualKeywordKind(identifier);
        if (contextual != SyntaxKind.None && SyntaxFacts.IsContextualKeyword(contextual))
        {
            return "@" + identifier;
        }

        return identifier;
    }
}
