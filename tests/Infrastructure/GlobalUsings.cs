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

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;
global using Boutquin.Domain.Helpers;
global using Boutquin.Storage.Domain.Enums;
global using Boutquin.Storage.Domain.Exceptions;
global using Boutquin.Storage.Domain.Helpers;
global using Boutquin.Storage.Domain.Interfaces;
global using Boutquin.Storage.Domain.ValueObjects;
global using Boutquin.Storage.Infrastructure.Algorithms;
global using Boutquin.Storage.Infrastructure.AppendOnlyFileStorage;
global using Boutquin.Storage.Infrastructure.DataStructures;
global using Boutquin.Storage.Infrastructure.DistributedSystems;
global using Boutquin.Storage.Infrastructure.Indexing;
global using Boutquin.Storage.Infrastructure.KeyValueStore;
global using Boutquin.Storage.Infrastructure.LogSegmentFileStorage;
global using Boutquin.Storage.Infrastructure.LsmTree;
global using Boutquin.Storage.Infrastructure.Serialization;
global using Boutquin.Storage.Infrastructure.SortedStringTable;
global using Boutquin.Storage.Infrastructure.StorageWithBloomFilter;
global using Boutquin.Storage.Infrastructure.WriteAheadLog;
global using FluentAssertions;
global using Moq;
global using Xunit;
global using Xunit.Abstractions;
global using SerializationException = Boutquin.Storage.Domain.Exceptions.SerializationException;
