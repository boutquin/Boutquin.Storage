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
// Global using directives
global using BenchmarkDotNet.Attributes;
global using BenchmarkDotNet.Columns;
global using BenchmarkDotNet.Configs;
global using BenchmarkDotNet.Environments;
global using BenchmarkDotNet.Exporters;
global using BenchmarkDotNet.Jobs;
global using BenchmarkDotNet.Loggers;
global using BenchmarkDotNet.Order;
global using BenchmarkDotNet.Reports;
global using BenchmarkDotNet.Running;
global using BenchmarkDotNet.Validators;

global using Boutquin.Storage.Domain.Helpers;
global using Boutquin.Storage.Domain.Interfaces;
global using Boutquin.Storage.Infrastructure;
global using Boutquin.Storage.Infrastructure.AppendOnlyFileStorage;
global using Boutquin.Storage.Infrastructure.Indexing;
global using Boutquin.Storage.Infrastructure.KeyValueStore;
global using Boutquin.Storage.Infrastructure.Serialization;

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Threading.Tasks;