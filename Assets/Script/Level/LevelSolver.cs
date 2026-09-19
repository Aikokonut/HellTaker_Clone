using System.Collections.Generic;

public sealed class LevelSolveResult
{
    public bool Success;
    public bool TimedOut;
    public bool StateLimitReached;
    public int MinimumMoves = -1;
    public int SolutionCountAtMinimum = -1;
    public int NearPlus1Count = -1;
    public int NearPlus2Count = -1;
    public int StoredOptimalSolutionCount;
    public bool OptimalSolutionsCapped;
    public readonly List<LevelDir> OptimalPath = new List<LevelDir>();
    public readonly List<List<LevelDir>> OptimalSolutions = new List<List<LevelDir>>();
    public string Message;

    public List<List<LevelDir>> StoredSolutions
    {
        get { return OptimalSolutions; }
    }
}

public static class LevelSolver
{
    private const int DefaultMaxStates = 2000000;
    private const int DefaultMaxDepthPad = 64;
    public const int DefaultMaxStoredSolutions = 32;

    private struct SearchNode
    {
        public LevelSimState State;
        public int Parent;
        public LevelDir Dir;
    }

    public static LevelSolveResult Solve(LevelData data)
    {
        return Solve(data, DefaultMaxStates, true, DefaultMaxStoredSolutions);
    }

    public static LevelSolveResult Solve(LevelData data, int maxStates, bool computeNear)
    {
        return Solve(data, maxStates, computeNear, DefaultMaxStoredSolutions);
    }

    public static LevelSolveResult Solve(LevelData data, int maxStates, bool computeNear, int maxStoredSolutions)
    {
        LevelSolveResult result = new LevelSolveResult();
        result.NearPlus1Count = -1;
        result.NearPlus2Count = -1;
        if (maxStoredSolutions < 1)
        {
            maxStoredSolutions = 1;
        }
        if (maxStates < 1)
        {
            maxStates = DefaultMaxStates;
        }
        if (data == null)
        {
            result.Message = "LevelData is null.";
            return result;
        }
        if (data.Width * data.Height > LevelLogic.MaxCells)
        {
            result.Message = "Grid too large for solver.";
            return result;
        }

        LevelValidationResult validation = LevelValidator.Validate(data);
        if (!validation.IsValid)
        {
            result.Message = "Level invalid; solve skipped.";
            return result;
        }

        LevelLogic logic = new LevelLogic(data);
        LevelSimState initial = logic.CreateInitialState(data);
        if (initial.PlayerIndex < 0 || logic.GoalIndex < 0)
        {
            result.Message = "Missing start or goal.";
            return result;
        }

        int moveLimit = data.MoveLimit;
        if (moveLimit <= 0)
        {
            moveLimit = data.Width * data.Height + DefaultMaxDepthPad;
        }

        int minMoves = -1;
        int expansions = 0;
        List<LevelDir> firstPath = new List<LevelDir>(32);
        bool found = FindMinMovesBfs(
            logic,
            initial,
            moveLimit,
            maxStates,
            result,
            ref expansions,
            ref minMoves,
            firstPath);

        if (!found || minMoves < 0)
        {
            result.MinimumMoves = -1;
            result.StoredOptimalSolutionCount = 0;
            result.SolutionCountAtMinimum = 0;
            result.Success = false;
            if (string.IsNullOrEmpty(result.Message))
            {
                result.Message = result.StateLimitReached
                    ? "No solution found before state limit."
                    : "No solution within MoveLimit.";
            }
            result.TimedOut = result.StateLimitReached;
            return result;
        }

        result.MinimumMoves = minMoves;
        result.OptimalSolutions.Clear();
        result.OptimalPath.Clear();

        bool savedLimit = result.StateLimitReached;
        result.StateLimitReached = false;
        int enumExpansions = 0;
        LevelDir[] dirs = { LevelDir.Up, LevelDir.Right, LevelDir.Down, LevelDir.Left };
        long startKey = logic.PackStateKey(ref initial);

        int bypassSlots = maxStoredSolutions;
        int doorSlots = 0;
        if (logic.DoorCount > 0 && maxStoredSolutions >= 2)
        {
            doorSlots = maxStoredSolutions / 2;
            if (doorSlots < 1)
            {
                doorSlots = 1;
            }
            bypassSlots = maxStoredSolutions - doorSlots;
            if (bypassSlots < 1)
            {
                bypassSlots = 1;
                doorSlots = maxStoredSolutions - 1;
            }
        }

        List<List<LevelDir>> bypassPaths = new List<List<LevelDir>>(bypassSlots);
        List<List<LevelDir>> doorPaths = new List<List<LevelDir>>(doorSlots > 0 ? doorSlots : 1);
        FillWinsByDoorClass(
            logic, initial, startKey, dirs, minMoves, moveLimit, maxStates,
            bypassSlots, false, bypassPaths, ref enumExpansions, result);
        if (doorSlots > 0)
        {
            FillWinsByDoorClass(
                logic, initial, startKey, dirs, minMoves, moveLimit, maxStates,
                doorSlots, true, doorPaths, ref enumExpansions, result);
        }

        if (bypassPaths.Count == 0 && firstPath.Count > 0)
        {
            bypassPaths.Add(CopyNewPath(firstPath));
        }

        int storedAtMin = 0;
        for (int i = 0; i < bypassPaths.Count; i++)
        {
            result.OptimalSolutions.Add(bypassPaths[i]);
            if (PathMoveCost(logic, initial, bypassPaths[i]) == minMoves)
            {
                storedAtMin++;
            }
        }
        for (int i = 0; i < doorPaths.Count; i++)
        {
            if (!ContainsSequence(result.OptimalSolutions, doorPaths[i]))
            {
                result.OptimalSolutions.Add(doorPaths[i]);
            }
        }

        result.SolutionCountAtMinimum = storedAtMin > 0 ? storedAtMin : (bypassPaths.Count > 0 ? 1 : 0);
        result.NearPlus1Count = 0;
        result.NearPlus2Count = 0;

        if (result.OptimalSolutions.Count >= maxStoredSolutions)
        {
            result.OptimalSolutionsCapped = true;
        }

        if (result.StateLimitReached && result.OptimalSolutions.Count > 0)
        {
            result.OptimalSolutionsCapped = true;
            result.StateLimitReached = savedLimit;
        }
        else if (!result.StateLimitReached)
        {
            result.StateLimitReached = savedLimit;
        }

        result.StoredOptimalSolutionCount = result.OptimalSolutions.Count;
        if (result.OptimalSolutions.Count > 0)
        {
            CopyPath(result.OptimalSolutions[0], result.OptimalPath);
        }

        result.Success = result.OptimalSolutions.Count > 0;
        if (!result.Success)
        {
            result.Message = result.StateLimitReached
                ? "No solution found before state limit."
                : "No solution within MoveLimit.";
            result.TimedOut = result.StateLimitReached;
        }
        else if (result.OptimalSolutionsCapped)
        {
            result.Message = "Solved (stored bypass + door paths, capped).";
        }
        else
        {
            result.Message = "Solved.";
        }

        return result;
    }

    private static void FillWinsByDoorClass(
        LevelLogic logic,
        LevelSimState initial,
        long startKey,
        LevelDir[] dirs,
        int minMoves,
        int moveLimit,
        int maxStates,
        int maxStore,
        bool requireOpenedDoor,
        List<List<LevelDir>> outPaths,
        ref int enumExpansions,
        LevelSolveResult limitResult)
    {
        if (maxStore < 1)
        {
            return;
        }
        for (int cost = minMoves; cost <= moveLimit; cost++)
        {
            if (outPaths.Count >= maxStore)
            {
                return;
            }
            LevelSolveResult tmp = new LevelSolveResult();
            bool stop = false;
            List<LevelDir> path = new List<LevelDir>(cost + 4);
            HashSet<long> visitedOnPath = new HashSet<long>();
            visitedOnPath.Add(startKey);
            int doorMode = requireOpenedDoor ? 1 : 0;
            EnumerateDistinctSequences(
                logic,
                initial,
                path,
                visitedOnPath,
                dirs,
                cost,
                maxStates,
                maxStore - outPaths.Count,
                tmp,
                ref enumExpansions,
                ref stop,
                doorMode,
                initial.ClosedDoorMask);
            if (tmp.StateLimitReached)
            {
                limitResult.StateLimitReached = true;
                return;
            }
            for (int i = 0; i < tmp.OptimalSolutions.Count; i++)
            {
                if (outPaths.Count >= maxStore)
                {
                    return;
                }
                if (!ContainsSequence(outPaths, tmp.OptimalSolutions[i]))
                {
                    outPaths.Add(CopyNewPath(tmp.OptimalSolutions[i]));
                }
            }
        }
    }

    private static int PathMoveCost(LevelLogic logic, LevelSimState initial, List<LevelDir> path)
    {
        LevelSimState st = initial;
        for (int i = 0; i < path.Count; i++)
        {
            logic.TryMove(ref st, path[i], false);
            if (st.Won || (st.Dead && !st.Won))
            {
                break;
            }
        }
        return st.MovesUsed;
    }

    private static bool FindMinMovesBfs(
        LevelLogic logic,
        LevelSimState initial,
        int moveLimit,
        int maxStates,
        LevelSolveResult result,
        ref int expansions,
        ref int minMoves,
        List<LevelDir> outPath)
    {
        outPath.Clear();
        if (initial.Won)
        {
            minMoves = initial.MovesUsed;
            return true;
        }

        List<SearchNode> nodes = new List<SearchNode>(4096);
        Dictionary<long, int> bestMoves = new Dictionary<long, int>(4096);
        Queue<int>[] buckets = new Queue<int>[moveLimit + 1];
        for (int i = 0; i <= moveLimit; i++)
        {
            buckets[i] = new Queue<int>();
        }

        SearchNode root = new SearchNode();
        root.State = initial;
        root.Parent = -1;
        root.Dir = LevelDir.None;
        nodes.Add(root);
        long startKey = logic.PackStateKey(ref initial);
        bestMoves[startKey] = initial.MovesUsed;
        buckets[initial.MovesUsed].Enqueue(0);

        LevelDir[] dirs = { LevelDir.Up, LevelDir.Right, LevelDir.Down, LevelDir.Left };
        int cursor = 0;
        int winNode = -1;

        while (cursor <= moveLimit)
        {
            Queue<int> bucket = buckets[cursor];
            if (bucket.Count == 0)
            {
                cursor++;
                continue;
            }

            int index = bucket.Dequeue();
            SearchNode node = nodes[index];
            LevelSimState state = node.State;
            if (state.MovesUsed != cursor)
            {
                continue;
            }

            expansions++;
            if (expansions >= maxStates)
            {
                result.StateLimitReached = true;
                break;
            }

            if (state.Won)
            {
                minMoves = state.MovesUsed;
                winNode = index;
                break;
            }

            if (state.Dead || state.MovesUsed >= moveLimit)
            {
                continue;
            }

            for (int d = 0; d < 4; d++)
            {
                LevelSimState next = state;
                logic.TryMove(ref next, dirs[d], true);
                if (next.Dead && !next.Won)
                {
                    continue;
                }
                if (logic.StatesEqual(ref next, ref state) && !next.Won)
                {
                    continue;
                }
                if (next.MovesUsed > moveLimit)
                {
                    continue;
                }

                long key = logic.PackStateKey(ref next);
                int known;
                if (bestMoves.TryGetValue(key, out known))
                {
                    if (next.MovesUsed >= known)
                    {
                        continue;
                    }
                }

                bestMoves[key] = next.MovesUsed;
                SearchNode child = new SearchNode();
                child.State = next;
                child.Parent = index;
                child.Dir = dirs[d];
                int childIndex = nodes.Count;
                nodes.Add(child);
                buckets[next.MovesUsed].Enqueue(childIndex);
            }
        }

        if (winNode < 0)
        {
            return false;
        }

        List<LevelDir> rev = new List<LevelDir>(32);
        int walk = winNode;
        while (walk > 0)
        {
            SearchNode n = nodes[walk];
            if (n.Dir != LevelDir.None)
            {
                rev.Add(n.Dir);
            }
            walk = n.Parent;
        }
        for (int i = rev.Count - 1; i >= 0; i--)
        {
            outPath.Add(rev[i]);
        }
        return true;
    }

    private static void EnumerateDistinctSequences(
        LevelLogic logic,
        LevelSimState state,
        List<LevelDir> path,
        HashSet<long> visitedOnPath,
        LevelDir[] dirs,
        int targetMoves,
        int maxStates,
        int maxStoredSolutions,
        LevelSolveResult result,
        ref int expansions,
        ref bool stop,
        int doorMode,
        ulong startClosedDoorMask)
    {
        if (stop)
        {
            return;
        }
        if (expansions >= maxStates)
        {
            result.StateLimitReached = true;
            stop = true;
            return;
        }
        expansions++;

        if (state.Won)
        {
            if (state.MovesUsed == targetMoves)
            {
                bool opened = state.ClosedDoorMask != startClosedDoorMask;
                if (doorMode == 1 && !opened)
                {
                    return;
                }
                if (doorMode == 0 && opened)
                {
                    return;
                }
                TryStoreDistinctSequence(path, maxStoredSolutions, result, ref stop);
            }
            return;
        }

        if (state.Dead || state.MovesUsed >= targetMoves)
        {
            return;
        }

        for (int d = 0; d < 4; d++)
        {
            if (stop)
            {
                return;
            }

            LevelSimState nextState = state;
            logic.TryMove(ref nextState, dirs[d], true);
            if (nextState.Dead && !nextState.Won)
            {
                continue;
            }
            if (logic.StatesEqual(ref nextState, ref state) && !nextState.Won)
            {
                continue;
            }
            if (nextState.MovesUsed > targetMoves)
            {
                continue;
            }

            long key = logic.PackStateKey(ref nextState);
            if (visitedOnPath.Contains(key))
            {
                continue;
            }

            path.Add(dirs[d]);
            visitedOnPath.Add(key);
            EnumerateDistinctSequences(
                logic,
                nextState,
                path,
                visitedOnPath,
                dirs,
                targetMoves,
                maxStates,
                maxStoredSolutions,
                result,
                ref expansions,
                ref stop,
                doorMode,
                startClosedDoorMask);
            visitedOnPath.Remove(key);
            path.RemoveAt(path.Count - 1);
        }
    }

    private static void TryStoreDistinctSequence(
        List<LevelDir> path,
        int maxStoredSolutions,
        LevelSolveResult result,
        ref bool stop)
    {
        if (ContainsSequence(result.OptimalSolutions, path))
        {
            return;
        }
        if (result.OptimalSolutions.Count >= maxStoredSolutions)
        {
            result.OptimalSolutionsCapped = true;
            stop = true;
            return;
        }
        result.OptimalSolutions.Add(CopyNewPath(path));
        if (result.OptimalSolutions.Count >= maxStoredSolutions)
        {
            stop = true;
        }
    }

    private static bool ContainsSequence(List<List<LevelDir>> stored, List<LevelDir> path)
    {
        for (int i = 0; i < stored.Count; i++)
        {
            if (SequencesEqual(stored[i], path))
            {
                return true;
            }
        }
        return false;
    }

    private static bool SequencesEqual(List<LevelDir> a, List<LevelDir> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    private static List<LevelDir> CopyNewPath(List<LevelDir> source)
    {
        List<LevelDir> copy = new List<LevelDir>(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            copy.Add(source[i]);
        }
        return copy;
    }

    private static void CopyPath(List<LevelDir> source, List<LevelDir> dest)
    {
        dest.Clear();
        for (int i = 0; i < source.Count; i++)
        {
            dest.Add(source[i]);
        }
    }
}
