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
namespace Boutquin.Storage.BenchMark;

/// <summary>
/// Compares serialization and deserialization throughput between BinaryEntrySerializer and CsvEntrySerializer.
/// </summary>
/// <remarks>
/// This class is not sealed because BenchmarkDotNet requires benchmark classes to be inheritable.
/// </remarks>
[MemoryDiagnoser]
public class EntrySerializerBenchmark
{
    /// <summary>
    /// Number of entries to serialize/deserialize.
    /// </summary>
    [Params(100, 1000)]
    public int ItemCount;

    private BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>> _binarySerializer = null!;
    private CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>> _csvSerializer = null!;
    private List<SerializableWrapper<int>> _keys = null!;
    private List<SerializableWrapper<string>> _values = null!;

    // Pre-serialized data for read benchmarks.
    private byte[] _binaryData = null!;

    // CSV entries are serialized individually because CsvEntrySerializer.ReadEntry creates a new
    // StreamReader per call with a 1024-byte internal buffer. When reading sequentially from a
    // single stream, the reader buffers ahead and on dispose the unconsumed bytes are lost,
    // corrupting subsequent reads. Serializing per-entry avoids this while still measuring
    // CSV deserialization throughput accurately.
    private byte[][] _csvEntries = null!;

    /// <summary>
    /// Initializes serializers and pre-generates test data.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _binarySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        _csvSerializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        _keys = new List<SerializableWrapper<int>>(ItemCount);
        _values = new List<SerializableWrapper<string>>(ItemCount);

        for (var i = 0; i < ItemCount; i++)
        {
            _keys.Add(new SerializableWrapper<int>(i));
            _values.Add(new SerializableWrapper<string>($"Value {i}"));
        }

        // Pre-serialize for read benchmarks.
        using (var binaryStream = new MemoryStream())
        {
            for (var i = 0; i < ItemCount; i++)
            {
                _binarySerializer.WriteEntry(binaryStream, _keys[i], _values[i]);
            }

            _binaryData = binaryStream.ToArray();
        }

        _csvEntries = new byte[ItemCount][];
        for (var i = 0; i < ItemCount; i++)
        {
            using var csvStream = new MemoryStream();
            _csvSerializer.WriteEntry(csvStream, _keys[i], _values[i]);
            _csvEntries[i] = csvStream.ToArray();
        }
    }

    /// <summary>
    /// Benchmarks binary serialization write throughput.
    /// </summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Write")]
    public void BinaryWrite()
    {
        using var stream = new MemoryStream();
        for (var i = 0; i < ItemCount; i++)
        {
            _binarySerializer.WriteEntry(stream, _keys[i], _values[i]);
        }
    }

    /// <summary>
    /// Benchmarks CSV serialization write throughput.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Write")]
    public void CsvWrite()
    {
        using var stream = new MemoryStream();
        for (var i = 0; i < ItemCount; i++)
        {
            _csvSerializer.WriteEntry(stream, _keys[i], _values[i]);
        }
    }

    /// <summary>
    /// Benchmarks binary deserialization read throughput.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Read")]
    public void BinaryRead()
    {
        using var stream = new MemoryStream(_binaryData);
        while (stream.Position < stream.Length)
        {
            _binarySerializer.ReadEntry(stream);
        }
    }

    /// <summary>
    /// Benchmarks CSV deserialization read throughput.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Read")]
    public void CsvRead()
    {
        for (var i = 0; i < ItemCount; i++)
        {
            using var stream = new MemoryStream(_csvEntries[i]);
            _csvSerializer.ReadEntry(stream);
        }
    }
}
