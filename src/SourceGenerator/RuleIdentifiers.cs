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

internal static class RuleIdentifiers
{
    public const string UnsupportedPropertyType = "BSSG001";
    public const string TypeMustBePartial = "BSSG002";
    public const string TypeShouldBeRecordStruct = "BSSG003";
    public const string DuplicateAssemblyDefaults = "BSSG004";
    public const string MutuallyExclusiveAttributes = "BSSG005";
    public const string InterfaceAlreadyImplemented = "BSSG006";
}
