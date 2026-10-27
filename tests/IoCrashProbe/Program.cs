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

using System.Globalization;
using System.Text.Json;
using Boutquin.Storage.Domain.Interfaces;
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.AppendOnlyFileStorage;
using Boutquin.Storage.Infrastructure.Indexing;
using Boutquin.Storage.Infrastructure.KeyValueStore;
using Boutquin.Storage.Infrastructure.LogSegmentFileStorage;
using Boutquin.Storage.Infrastructure.LsmTree;
using Boutquin.Storage.Infrastructure.Serialization;
using Boutquin.Storage.Infrastructure.ValueLog;
using Boutquin.Storage.Infrastructure.WriteAheadLog;
using Key = Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>;
using Text = Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>;

if (args.Length == 1 && args[0] == "inventory")
{
    var ownedNamespaces = new[] { ".LsmTree", ".SortedStringTable", ".ValueLog", ".WriteAheadLog", ".AppendOnlyFileStorage", ".LogSegmentFileStorage" };
    var types = typeof(LsmStorageEngine<Key, Text>).Assembly.GetExportedTypes()
        .Where(type => ownedNamespaces.Any(suffix => type.Namespace?.EndsWith(suffix, StringComparison.Ordinal) == true)
            || type == typeof(StorageFile))
        .OrderBy(type => type.FullName, StringComparer.Ordinal)
        .Select(type => new
        {
            type = type.FullName,
            members = type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(member => member.MemberType is System.Reflection.MemberTypes.Method or System.Reflection.MemberTypes.Constructor
                    or System.Reflection.MemberTypes.Property or System.Reflection.MemberTypes.Field)
                .Select(member => member.ToString()).OrderBy(name => name, StringComparer.Ordinal).ToArray()
        }).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(types, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

// Process-level durability probe. The writer reports only completed operations and then waits
// without disposing anything; the controller kills it and a separate process validates recovery.
var mode = args[0];
var family = args[1];
var folder = args[2];
Directory.CreateDirectory(folder);
var write = mode == "write";
var expected = new SortedDictionary<int, string>();
for (var key = 1; key <= 24; key++)
{
    expected[key] = Value(key, 0);
}

for (var key = 1; key <= 12; key++)
{
    expected[key] = Value(key, 1);
}

if (family.StartsWith("lsm", StringComparison.Ordinal))
{
    var parts = family.Split('-');
    var strategy = int.Parse(parts[1], CultureInfo.InvariantCulture);
    var separated = parts[2] == "separated";
    using var wal = new WriteAheadLog<Key, Text>(Path.Combine(folder, "wal"),
        new WriteAheadLogOptions { SegmentMaxBytes = 1024 * 1024 });
    var engine = await LsmStorageEngine<Key, Text>.OpenAsync(new LsmStorageEngineOptions<Key, Text>(
        MemTableCapacity: 8, SegmentFolder: Path.Combine(folder, "segments"), SegmentPrefix: "crash",
        SegmentFormat: new SstSegmentFormat<Key, Text>(), WriteAheadLog: wal,
        CompactionStrategy: strategy switch
        {
            0 => new FullCompactionStrategy<Key>(4),
            1 => new SizeTieredCompactionStrategy<Key>(minSegments: 4),
            _ => new LeveledCompactionStrategy<Key>(level0Threshold: 4)
        },
        ValueLog: separated ? new ValueLogOptions(ValueLogThresholdBytes: 64, SegmentMaxBytes: 480) : null));
    if (write)
    {
        for (var key = 1; key <= 24; key++)
        {
            await engine.SetAsync(new Key(key), new Text(Value(key, 0)));
        }

        await engine.FlushAsync();
        for (var key = 1; key <= 12; key++)
        {
            await engine.SetAsync(new Key(key), new Text(Value(key, 1)));
        }

        for (var key = 20; key <= 24; key++) { await engine.RemoveAsync(new Key(key)); expected.Remove(key); }
        var boundary = args.Length > 3 ? args[3] : "wal";
        if (boundary == "manifest")
        {
            await engine.FlushAsync();
            // Stop inside the production commit path after CURRENT is durable, before old
            // inputs are retired. The controller kills the process at this exact boundary.
            typeof(LsmStorageEngine<Key, Text>).GetProperty("CompactionManifestCommittedHook",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(engine, (Action)(() => { Console.WriteLine("ACK"); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); }));
            await engine.CompactAsync();
        }
        // Leave acknowledged updates/deletes in WAL and memtable across SIGKILL.
        await AcknowledgeAndWait();
    }
    else
    {
        for (var key = 20; key <= 24; key++)
        {
            expected.Remove(key);
        }

        await Check(engine);
        await engine.CompactAsync();
        if (separated)
        {
            await engine.CompactValueLogAsync();
        }

        await Check(engine);
        await engine.DisposeAsync();
    }
}
else if (family == "wal")
{
    using var wal = new WriteAheadLog<Key, Text>(folder, new WriteAheadLogOptions { SegmentMaxBytes = 1024 * 1024 });
    if (write)
    {
        ulong sequence = 0;
        for (var key = 1; key <= 24; key++)
        {
            await wal.AppendAsync(new Key(key), new Text(Value(key, 0)), ++sequence);
        }

        for (var key = 1; key <= 12; key++)
        {
            await wal.AppendAsync(new Key(key), new Text(Value(key, 1)), ++sequence);
        }

        await AcknowledgeAndWait();
    }
    else
    {
        var actual = new SortedDictionary<int, string>();
        foreach (var entry in await wal.RecoverAsync())
        {
            actual[entry.Key.Value] = entry.InlineValue!.Value;
        }

        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException("WAL recovery differs from acknowledged state.");
        }
    }
}
else if (family == "value-log")
{
    await using var manifest = new ValueLogManifest(Path.Combine(folder, "values.manifest"));
    await using var writer = await ValueLogWriter.OpenAsync(folder, manifest,
        new ValueLogOptions(SegmentMaxBytes: 480));
    if (write)
    {
        var pointers = new List<ValueLogPointer>();
        for (var key = 1; key <= 24; key++)
        {
            pointers.Add(await writer.AppendAsync(System.Text.Encoding.UTF8.GetBytes(Value(key, 0)), (ulong)key));
        }
        // The controller carries pointer identities outside the killed process.
        Console.WriteLine(JsonSerializer.Serialize(pointers.Select(pointer => Convert.ToHexString(pointer.ToArray())).ToArray()));
        await AcknowledgeAndWait();
    }
    else
    {
        var pointers = JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[3]))!
            .Select(hex => ValueLogPointer.ReadFrom(Convert.FromHexString(hex))).ToArray();
        using var reader = new ValueLogReader(folder, ValueLogOptions.Default);
        for (var i = 0; i < pointers.Length; i++)
        {
            if (System.Text.Encoding.UTF8.GetString(reader.Read(pointers[i])) != Value(i + 1, 0))
            {
                throw new InvalidDataException("Value-log pointer lost across process death.");
            }
        }
    }
}
else
{
    IStorageEngine<Key, Text> engine = family switch
    {
        "append" => new AppendOnlyFileStorageEngine<Key, Text>(new StorageFile(folder, "data.log"), new BinaryEntrySerializer<Key, Text>()),
        "indexed" => new AppendOnlyFileStorageEngineWithIndex<Key, Text>(new StorageFile(folder, "data.log"), new BinaryEntrySerializer<Key, Text>(), new InMemoryFileIndex<Key>()),
        "segmented" => new LogSegmentedStorageEngine<Key, Text>(new BinaryEntrySerializer<Key, Text>(), folder, "seg", 1024,
            (directory, name, serializer, limit) => new LogSegmentFile<Key, Text>(
                new AppendOnlyFileStorageEngineWithIndex<Key, Text>(new StorageFile(directory, name), serializer, new InMemoryFileIndex<Key>()), limit)),
        _ => throw new ArgumentException("Unknown family.")
    };
    if (write)
    {
        for (var key = 1; key <= 24; key++)
        {
            await engine.SetAsync(new Key(key), new Text(Value(key, 0)));
        }

        for (var key = 1; key <= 12; key++)
        {
            await engine.SetAsync(new Key(key), new Text(Value(key, 1)));
        }

        await AcknowledgeAndWait();
    }
    else if (family == "segmented")
    {
        // The type documents an in-memory segment catalog. Exercise that intentional
        // difference explicitly: orphan files survive but a fresh instance is empty.
        for (var key = 1; key <= 24; key++)
        {
            if ((await engine.TryGetValueAsync(new Key(key))).Found)
            {
                throw new InvalidDataException("Segmented restart unexpectedly adopted an orphan.");
            }
        }

        if (Directory.GetFiles(folder, "*.log").Length < 2)
        {
            throw new InvalidDataException("Segmented boundary probe did not produce rotated files.");
        }

        Console.WriteLine("EXPECTED DIFFERENCE segmented catalog is not recoverable across restart");
    }
    else
    {
        await Check(engine);
    }
}
if (!write)
{
    Console.WriteLine($"PASS {family} acknowledged state recovered in fresh process");
}

static string Value(int key, int generation) => $"{key:D3}:{generation}:" + new string((char)('a' + key % 26), 140);

static async Task AcknowledgeAndWait()
{
    Console.WriteLine("ACK");
    Console.Out.Flush();
    await Task.Delay(Timeout.InfiniteTimeSpan);
}

async Task Check(IStorageEngine<Key, Text> engine)
{
    for (var key = 1; key <= 24; key++)
    {
        var (value, found) = await engine.TryGetValueAsync(new Key(key));
        if (found != expected.ContainsKey(key) || found && value.Value != expected[key])
        {
            throw new InvalidDataException($"Recovered key {key} differs from acknowledged state.");
        }
    }
}
