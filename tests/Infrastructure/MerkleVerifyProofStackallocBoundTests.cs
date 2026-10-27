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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// C10 — MERKLE-VERIFYPROOF-STACKALLOC-001. <see cref="MerkleTree.VerifyProof"/> is a public
/// static API whose <c>proof</c> sibling hashes are caller-controlled. The internal
/// <c>ComputeCombinedHash</c> did an UNBOUNDED <c>stackalloc byte[1 + left.Length + right.Length]</c>
/// over those hashes, so an oversized sibling hash overflowed the stack and crashed the process
/// uncatchably. The fix must bound the on-stack buffer (small-size stackalloc, heap/ArrayPool
/// above a threshold) so a large sibling hash completes instead of blowing the stack.
///
/// <para>The probe is run on a thread with a deliberately small max stack: an unbounded stackalloc
/// of a megabyte-scale sibling hash overflows that stack and the worker never sets its completion
/// flag (RED). The bounded version routes the large buffer to the heap, so the worker completes and
/// sets the flag (GREEN).</para>
/// </summary>
public sealed class MerkleVerifyProofStackallocBoundTests
{
    [Fact]
    public void VerifyProof_OversizedSiblingHash_DoesNotBlowTheStack()
    {
        // A sibling hash far larger than any small stackalloc threshold but cheap to allocate
        // on the heap. ~1 MB comfortably exceeds the 128 KB worker stack below if stackalloc'd.
        var oversizedSibling = new byte[1 << 20];
        var leafHash = MerkleTree.HashLeaf("leaf"u8.ToArray());
        var rootHash = new byte[32];

        var completed = false;
        Exception? workerFault = null;

        // Small bounded stack: an unbounded stackalloc of the 1 MB sibling overflows it and the
        // worker dies before setting 'completed'. A bounded buffer keeps the stack frame tiny.
        var worker = new Thread(
            () =>
            {
                try
                {
                    // currentIndex even → ComputeCombinedHash(computedHash, oversizedSibling):
                    // the sibling becomes the 'right' operand whose length drives the stackalloc.
                    _ = MerkleTree.VerifyProof(leafHash, [oversizedSibling], rootHash, leafIndex: 0);
                    completed = true;
                }
                catch (Exception ex)
                {
                    // A catchable exception still means the stack survived — the defect is the
                    // UNCATCHABLE StackOverflow that aborts the process, which never reaches here.
                    workerFault = ex;
                    completed = true;
                }
            },
            maxStackSize: 128 * 1024);

        worker.Start();
        var finished = worker.Join(TimeSpan.FromSeconds(30));

        finished.Should().BeTrue("the verification thread must terminate, not hang");
        completed.Should().BeTrue(
            "VerifyProof must not blow the stack on a large caller-supplied sibling hash — the " +
            "internal combined-hash buffer must be bounded (heap above a small stackalloc threshold)");
        workerFault.Should().BeNull("VerifyProof on a well-formed (if oversized) proof must not throw");
    }

    [Fact]
    public void VerifyProof_SmallSiblingHash_StillVerifies()
    {
        // Regression guard: the bounded path must preserve the small-input fast path correctness.
        var blocks = new List<byte[]>
        {
            "a"u8.ToArray(),
            "b"u8.ToArray(),
            "c"u8.ToArray(),
            "d"u8.ToArray(),
        };

        var tree = new MerkleTree();
        tree.Build(blocks);
        var root = tree.RootHash;
        var proof = tree.GetProof(0);
        var leafHash = MerkleTree.HashLeaf(blocks[0]);

        MerkleTree.VerifyProof(leafHash, proof, root, leafIndex: 0)
            .Should().BeTrue("a valid proof of an unmodified leaf must verify");
    }
}
