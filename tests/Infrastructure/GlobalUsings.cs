// Global using directives
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;

global using Moq;

global using Xunit;
global using Xunit.Abstractions;

global using Boutquin.Domain.Helpers;

global using Boutquin.Storage.Domain.Enums;
global using Boutquin.Storage.Domain.Exceptions;
global using Boutquin.Storage.Domain.Helpers;
global using Boutquin.Storage.Domain.Interfaces;
global using Boutquin.Storage.Domain.ValueObjects;
global using Boutquin.Storage.Infrastructure.AppendOnlyFileStorage;
global using Boutquin.Storage.Infrastructure.DataStructures;
global using Boutquin.Storage.Infrastructure.Indexing;
global using Boutquin.Storage.Infrastructure.KeyValueStore;
global using Boutquin.Storage.Infrastructure.LogSegmentFileStorage;
global using Boutquin.Storage.Infrastructure.Serialization;
global using Boutquin.Storage.Infrastructure.StorageWithBloomFilter;

global using FluentAssertions;

global using System.Runtime.Serialization;

global using SerializationException = Boutquin.Storage.Domain.Exceptions.SerializationException;
