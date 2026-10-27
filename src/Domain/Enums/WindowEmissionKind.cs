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
namespace Boutquin.Storage.Domain.Enums;

/// <summary>Classifies how a window result changes a downstream materialized view.</summary>
public enum WindowEmissionKind
{
    /// <summary>Publishes a window for the first time after the watermark closes it.</summary>
    Final,

    /// <summary>Creates or replaces a result after an included late event.</summary>
    Upsert,

    /// <summary>Removes a previously published window that a late session bridge superseded.</summary>
    Retraction,
}
