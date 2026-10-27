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
namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

/// <summary>
/// Little-endian byte encoders for columnar zone-map bounds and bitmap-index keys.
/// </summary>
/// <remarks>
/// The decode side (<see cref="ColumnarPredicatePushdown.DecodeBound"/>) reads these footer-extension
/// bytes with <see cref="BinaryPrimitives"/> <c>Read*LittleEndian</c>, so the encode side MUST be
/// explicitly little-endian too. <c>BitConverter.GetBytes</c> and its siblings are
/// <em>host-endian</em>: they coincide with little-endian only on little-endian hardware. On a
/// big-endian writer they would byte-reverse every persisted min/max bound and bitmap key, so the
/// little-endian reader (and the byte-reversed big-endian reader alike) would decode them wrong and
/// silently corrupt every range/equality row-group skip. Routing the encode side through these
/// helpers makes the on-disk representation host-independent, matching the repository's
/// little-endian on-disk convention. On little-endian hosts they produce byte-identical output to the
/// <see cref="BitConverter"/> calls they replace, so existing segments and fixtures are unchanged.
/// (Bit-reinterpretation helpers such as <see cref="BitConverter.DoubleToInt64Bits(double)"/> are
/// register-level and endianness-independent, so they are intentionally left in place.)
/// </remarks>
internal static class LittleEndianColumnBytes
{
    public static byte[] From(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] From(long value)
    {
        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] From(double value)
    {
        var bytes = new byte[sizeof(double)];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] From(float value)
    {
        var bytes = new byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return bytes;
    }
}
