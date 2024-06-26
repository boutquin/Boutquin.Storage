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
global using System.Collections;
global using System.Security.Cryptography;
global using System.Text;

global using Boutquin.Domain.Helpers;
global using Boutquin.Storage.Domain.Enums;
global using Boutquin.Storage.Domain.Exceptions;
global using Boutquin.Storage.Domain.Interfaces;
global using Boutquin.Storage.Domain.ValueObjects;
global using Boutquin.Storage.Infrastructure.Algorithms;
global using Boutquin.Storage.Infrastructure.AppendOnlyFileStorage;
global using Boutquin.Storage.Infrastructure.KeyValueStore;

global using System.Buffers.Binary;
global using System.Collections.Concurrent;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;

global using SerializationException = Boutquin.Storage.Domain.Exceptions.SerializationException;