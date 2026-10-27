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

global using System.Collections.Concurrent;
global using System.Globalization;
global using System.Numerics;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.Json;
global using Boutquin.Storage.Distributed.Interfaces.Consensus;
global using Boutquin.Storage.Distributed.Interfaces.DistributedSystems;
global using Boutquin.Storage.Distributed.Interfaces.Merkle;
global using Boutquin.Storage.Distributed.Interfaces.Partitioning;
global using Boutquin.Storage.Distributed.Interfaces.Replication;
global using Boutquin.Storage.Distributed.Interfaces.Sharding;
global using Boutquin.Storage.Distributed.Interfaces.Sync;
global using Boutquin.Storage.Distributed.Interfaces.Transactions;
global using Boutquin.Storage.Distributed.Replication.Exceptions;
global using Boutquin.Storage.Distributed.Sharding;
global using Boutquin.Storage.Distributed.Sync;
global using Boutquin.Storage.Domain.Enums;
global using Boutquin.Storage.Domain.Interfaces;
global using Boutquin.Storage.Domain.ValueObjects;
global using Boutquin.Storage.Infrastructure.Algorithms;
