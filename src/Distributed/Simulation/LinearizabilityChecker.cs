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
namespace Boutquin.Storage.Distributed.Simulation;

/// <summary>Distinguishes a proof/counterexample for a finite history from exhausted or incomplete evidence.</summary>
public enum LinearizabilityResult
{
    /// <summary>A legal sequential order respects all real-time precedence edges.</summary>
    Valid,
    /// <summary>Every permitted sequential order violates the supplied model.</summary>
    Invalid,
    /// <summary>A pending operation, history-size bound, or search budget prevents a conclusion.</summary>
    Inconclusive
}

/// <summary>Bounded exhaustive search for a sequential explanation of completed concurrent operations.</summary>
/// <remarks>Wing/Gong-style backtracking respects response-before-invocation edges. Worst-case time is
/// factorial and space is O(n). No heuristic rejection or production-system guarantee is implied.
/// The model must be pure and return a fresh or immutable successor state for every branch.</remarks>
public static class LinearizabilityChecker
{
    /// <summary>Checks a finite history against a caller-supplied sequential state machine.</summary>
    /// <typeparam name="TInput">Command type.</typeparam>
    /// <typeparam name="TOutput">Response type.</typeparam>
    /// <typeparam name="TState">Immutable model state.</typeparam>
    /// <param name="history">Well-formed invocation/response pairs with unique IDs and event ordinals.</param>
    /// <param name="initialState">State before the history begins.</param>
    /// <param name="transition">Pure model: state, command, response to legality and successor state.</param>
    /// <param name="maxSearchSteps">Positive bound on attempted transitions.</param>
    /// <returns>Valid, Invalid, or Inconclusive; pending operations never silently disappear.</returns>
    /// <exception cref="ArgumentNullException">History or transition is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Search budget is nonpositive.</exception>
    /// <exception cref="ArgumentException">History IDs, ordinals, or response intervals are malformed.</exception>
    public static LinearizabilityResult Check<TInput, TOutput, TState>(
        IReadOnlyList<HistoryOperation<TInput, TOutput>> history,
        TState initialState,
        Func<TState, TInput, TOutput, (bool Allowed, TState State)> transition,
        int maxSearchSteps = 100000)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSearchSteps);
        var ids = new HashSet<long>();
        var ordinals = new HashSet<long>();
        foreach (var operation in history)
        {
            if (operation is null || !ids.Add(operation.Id) || operation.Start < 0 ||
                !ordinals.Add(operation.Start) ||
                (operation.Completed && !operation.End.HasValue) ||
                (operation.End is { } end && (end <= operation.Start || !ordinals.Add(end))))
            {
                throw new ArgumentException("Malformed operation history.", nameof(history));
            }
        }
        if (history.Any(operation => !operation.End.HasValue))
        {
            return LinearizabilityResult.Inconclusive;
        }
        var operations = history.Where(operation => operation.Completed).ToArray();
        // A depth bound prevents unbounded recursion, independently of transition budget.
        if (operations.Length > 256)
        {
            return LinearizabilityResult.Inconclusive;
        }
        var used = new bool[operations.Length];
        var steps = 0;
        var exhausted = false;
        var valid = Search(initialState, 0);
        return valid ? LinearizabilityResult.Valid :
            exhausted ? LinearizabilityResult.Inconclusive : LinearizabilityResult.Invalid;

        bool Search(TState state, int depth)
        {
            if (depth == operations.Length)
            {
                return true;
            }
            for (var i = 0; i < operations.Length; i++)
            {
                if (used[i] || HasPredecessor(i))
                {
                    continue;
                }
                if (steps++ >= maxSearchSteps)
                {
                    exhausted = true;
                    return false;
                }
                var (allowed, next) = transition(state, operations[i].Input, operations[i].Output!);
                if (!allowed)
                {
                    continue;
                }
                used[i] = true;
                if (Search(next, depth + 1))
                {
                    return true;
                }
                used[i] = false;
                if (exhausted)
                {
                    return false;
                }
            }
            return false;
        }

        bool HasPredecessor(int candidate)
        {
            for (var j = 0; j < operations.Length; j++)
            {
                if (!used[j] && operations[j].End < operations[candidate].Start)
                {
                    return true;
                }
            }
            return false;
        }
    }
}

