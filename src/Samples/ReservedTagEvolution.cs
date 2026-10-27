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
namespace Boutquin.Storage.Samples;

// Schema-evolution walk-through for [StorageSerializable] records.
//
// V1 shipped three fields:
//
//     [StorageSerializable]
//     public partial record struct SampleV1(
//         [property: Tag(1)] string Name,
//         [property: Tag(2)] int Count,
//         [property: Tag(3)] string Status);
//
// V2 removes `Status`, reserves tag 3, and adds `Description` at tag 4.
//
// Why tag 3 is reserved:
//   Tag 3 previously carried `Status`. Re-using it for a different property
//   would let a V1 writer and V2 reader silently corrupt data — the V2 reader
//   would decode a `Status` string into the new property's slot. `[ReservedTag(3)]`
//   tells the generator to raise BSSG013 if anyone tries to reassign tag 3 in a
//   future revision.
//
// Forward compatibility (V1 reader, V2 data):
//   The V1 reader scans tags and recognises 1, 2, 3. It encounters tag 4
//   (Description), doesn't know it, and skips the length-prefixed payload
//   via VarintHelpers.SkipUnknownField. The V1 reader therefore reads a V2
//   record as a V1 record with `Status = ""` (tag 3 absent, default value).
//
// Backward compatibility (V2 reader, V1 data):
//   The V2 reader scans tags and recognises 1, 2, 4. It encounters tag 3
//   (Status), doesn't know it (tag 3 is reserved, not wired to a property),
//   and skips it. `Description` populates with the empty-string default
//   because V1 data carried no tag 4.
//
// Both directions are lossy for the removed / added property, but neither
// direction corrupts the surviving fields — the tagged-wire unknown-skip
// discipline is what makes schema evolution safe across the break.
[StorageSerializable]
[ReservedTag(3)]
public partial record struct SampleV2(
    [property: Tag(1)] string Name,
    [property: Tag(2)] int Count,
    [property: Tag(4)] string Description);
