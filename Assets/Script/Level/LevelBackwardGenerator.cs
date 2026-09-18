using System.Collections.Generic;
using UnityEngine;

public sealed class LevelGenerateParams
{
    public LevelData BaseMap;
    public int Width = 8;
    public int Height = 8;
    public bool FreeGen = true;
    public int TargetSolutions = 1;
    public int MoveLimit = 10;
    public int MoveLimitSlack = 2;
    public int MinPathExtra = 2;
    public int SolutionCountCap = 48;
    public int MaxAttempts = 120;
    public int Seed = 0;
    public int MaxEnemy = 3;
    public int MaxRock = 4;
    public int MaxSpike = 8;
    public int StateLimit = 60000;
    public int TimeLimitMs = 120;
}

public sealed class LevelSolutionBucket
{
    public int Moves;
    public int Count;
}

public sealed class LevelGenerateResult
{
    public bool Success;
    public LevelData Level;
    public int AttemptsUsed;
    public string Message;

    public int TotalSolutions = -1;
    public int MinimumMoves = -1;
    public int MoveSlack = int.MinValue;
    public int DependencyDepth = -1;
    public int DecisionPoints = -1;
    public int EstimatedDifficulty = -1;
    public int BaseShortest = -1;
    public int PathExtra = -1;
    public int ActionTax = -1;
    public string PatternUsed;
    public readonly List<LevelSolutionBucket> SolutionsByMoves = new List<LevelSolutionBucket>();
    public readonly List<LevelDeadlockInfo> Deadlocks = new List<LevelDeadlockInfo>();
}

/// <summary>
/// Pattern generator: GD draws Floor/Wall/Start/Goal; FreeGen composes KeyDoor+Spike+Enemy/Rock on critical path.
/// </summary>
public static class LevelBackwardGenerator
{
    private enum PatternId
    {
        PushChain = 0,
        Blocker = 1,
        Fork = 2,
        SpikeTax = 3,
        KeyDoor = 4,
        Compose = 5
    }

    public static LevelGenerateResult Generate(LevelGenerateParams genParams)
    {
        LevelGenerateResult result = new LevelGenerateResult();
        if (genParams == null)
        {
            result.Message = "Generation Failed: params required.";
            return result;
        }
        if (genParams.BaseMap == null)
        {
            result.Message = "Generation Failed: draw a base map first.";
            return result;
        }

        string paramError;
        if (!ValidateParams(genParams, out paramError))
        {
            result.Message = "Generation Failed: " + paramError;
            return result;
        }

        if (genParams.BaseMap.Width != genParams.Width || genParams.BaseMap.Height != genParams.Height)
        {
            result.Message = "Generation Failed: base map size "
                + genParams.BaseMap.Width + "x" + genParams.BaseMap.Height
                + " != input " + genParams.Width + "x" + genParams.Height + ".";
            return result;
        }

        LevelData stripped;
        string baseError;
        if (!TryCreateStrippedBase(genParams.BaseMap, out stripped, out baseError))
        {
            result.Message = "Generation Failed: " + baseError;
            return result;
        }

        Topology topo;
        string topoError;
        if (!TryBuildTopology(stripped, out topo, out topoError))
        {
            Object.DestroyImmediate(stripped);
            result.Message = "Generation Failed: " + topoError;
            return result;
        }

        if (topo.ShortestLen > genParams.MoveLimit && genParams.MoveLimit > 0 && !genParams.FreeGen)
        {
            Object.DestroyImmediate(stripped);
            result.Message = "Generation Failed: base shortest path ("
                + topo.ShortestLen + ") exceeds MoveLimit (" + genParams.MoveLimit + ").";
            return result;
        }

        System.Random rng = genParams.Seed != 0
            ? new System.Random(genParams.Seed)
            : new System.Random();

        PatternId[] exactPatterns =
        {
            PatternId.PushChain,
            PatternId.Blocker,
            PatternId.Fork,
            PatternId.SpikeTax,
            PatternId.KeyDoor
        };

        string lastError = "No attempt ran.";
        SearchBudget budget = new SearchBudget(genParams.StateLimit, genParams.TimeLimitMs);

        for (int attempt = 1; attempt <= genParams.MaxAttempts; attempt++)
        {
            result.AttemptsUsed = attempt;
            PatternId pattern = genParams.FreeGen
                ? PatternId.Compose
                : exactPatterns[rng.Next(0, exactPatterns.Length)];

            LevelData candidate = stripped.CreateRuntimeCopy();

            int searchMoveLimit;
            if (genParams.FreeGen)
            {
                searchMoveLimit = topo.ShortestLen + 40 + genParams.MaxSpike * 2;
                if (searchMoveLimit < 16)
                {
                    searchMoveLimit = 16;
                }
            }
            else
            {
                searchMoveLimit = genParams.MoveLimit;
            }
            candidate.MoveLimit = searchMoveLimit;

            PlacementState place = new PlacementState(topo.CellCount);
            string placeError;
            if (genParams.FreeGen)
            {
                if (!PlaceCompose(candidate, topo, genParams, rng, place, out placeError))
                {
                    Object.DestroyImmediate(candidate);
                    lastError = "Compose: " + placeError;
                    continue;
                }
            }
            else
            {
                if (!TryApplyPattern(candidate, topo, pattern, genParams, rng, place, out placeError))
                {
                    Object.DestroyImmediate(candidate);
                    lastError = pattern + ": " + placeError;
                    continue;
                }
                TryAddOptionalAccents(candidate, topo, genParams, rng, place, pattern);
            }

            string cheapError;
            if (!CheapValidate(candidate, topo, genParams, out cheapError))
            {
                Object.DestroyImmediate(candidate);
                lastError = "Pre-check: " + cheapError;
                continue;
            }

            int abortAbove = genParams.FreeGen
                ? genParams.SolutionCountCap
                : genParams.TargetSolutions;

            CountResult count = CountSolutions(candidate, abortAbove, budget);

            if (genParams.FreeGen)
            {
                if (count.MinimumMoves < 0 || count.Total <= 0)
                {
                    Object.DestroyImmediate(candidate);
                    lastError = count.Status == CountStatus.Inconclusive
                        ? "Solver budget exhausted before any solution."
                        : "Unsolvable.";
                    continue;
                }

                int pathExtra = count.MinimumMoves - topo.ShortestLen;
                int minExtra = genParams.MinPathExtra;
                if (minExtra < 0)
                {
                    minExtra = 0;
                }
                if (pathExtra < minExtra)
                {
                    Object.DestroyImmediate(candidate);
                    lastError = "Too easy: pathExtra=" + pathExtra + " < " + minExtra + ".";
                    continue;
                }

                int depth = MeasureDependencyDepth(candidate, count.SamplePath);
                if (depth < 1)
                {
                    Object.DestroyImmediate(candidate);
                    lastError = "Too easy: no forced interactions on solution.";
                    continue;
                }

                int slack = genParams.MoveLimitSlack;
                if (slack < 0)
                {
                    slack = 0;
                }
                int finalLimit = count.MinimumMoves + slack;
                candidate.MoveLimit = finalLimit;

                int within = CountWithinMoveLimit(count, finalLimit);
                count.Total = within;
                TrimBuckets(count, finalLimit);
                if (within <= 0)
                {
                    Object.DestroyImmediate(candidate);
                    lastError = "No solutions within final MoveLimit.";
                    continue;
                }

                FillSuccess(result, candidate, count, pattern, attempt, finalLimit, true, topo.ShortestLen);
                Object.DestroyImmediate(stripped);
                return result;
            }

            if (count.Status == CountStatus.TooMany)
            {
                Object.DestroyImmediate(candidate);
                lastError = "Too many solutions (>" + genParams.TargetSolutions + ").";
                continue;
            }
            if (count.Status == CountStatus.Unsolvable)
            {
                Object.DestroyImmediate(candidate);
                lastError = "Unsolvable within MoveLimit.";
                continue;
            }
            if (count.Status == CountStatus.Inconclusive)
            {
                Object.DestroyImmediate(candidate);
                lastError = "Solver budget exhausted (solutions found=" + count.Total + ").";
                continue;
            }
            if (count.Total != genParams.TargetSolutions)
            {
                Object.DestroyImmediate(candidate);
                lastError = "Got " + count.Total + " solutions, target " + genParams.TargetSolutions + ".";
                continue;
            }

            candidate.MoveLimit = genParams.MoveLimit;
            FillSuccess(result, candidate, count, pattern, attempt, genParams.MoveLimit, false, topo.ShortestLen);
            Object.DestroyImmediate(stripped);
            return result;
        }

        Object.DestroyImmediate(stripped);
        result.Message = "Generation Failed after " + genParams.MaxAttempts
            + " attempt(s). Inputs unchanged. Last: " + lastError;
        return result;
    }

    private static int CountWithinMoveLimit(CountResult count, int moveLimit)
    {
        int total = 0;
        foreach (KeyValuePair<int, int> pair in count.ByMoves)
        {
            if (pair.Key <= moveLimit)
            {
                total += pair.Value;
            }
        }
        return total;
    }

    private static void TrimBuckets(CountResult count, int moveLimit)
    {
        List<int> remove = new List<int>();
        foreach (KeyValuePair<int, int> pair in count.ByMoves)
        {
            if (pair.Key > moveLimit)
            {
                remove.Add(pair.Key);
            }
        }
        for (int i = 0; i < remove.Count; i++)
        {
            count.ByMoves.Remove(remove[i]);
        }
    }

    private static void FillSuccess(
        LevelGenerateResult result,
        LevelData level,
        CountResult count,
        PatternId pattern,
        int attempt,
        int moveLimit,
        bool freeGen,
        int baseShortest)
    {
        result.Success = true;
        result.Level = level;
        result.TotalSolutions = count.Total;
        result.MinimumMoves = count.MinimumMoves;
        result.MoveSlack = moveLimit - count.MinimumMoves;
        result.BaseShortest = baseShortest;
        result.PathExtra = count.MinimumMoves - baseShortest;
        result.ActionTax = count.SamplePath != null
            ? count.MinimumMoves - count.SamplePath.Count
            : -1;
        result.PatternUsed = pattern.ToString();
        result.DependencyDepth = MeasureDependencyDepth(level, count.SamplePath);
        result.DecisionPoints = CountDecisionPoints(level, count.SamplePath);
        result.Deadlocks.Clear();
        LevelAnalyzer.DetectDeadlocks(level, result.Deadlocks);
        result.EstimatedDifficulty = EstimateDifficulty(result, moveLimit);

        result.SolutionsByMoves.Clear();
        List<int> keys = new List<int>();
        foreach (KeyValuePair<int, int> pair in count.ByMoves)
        {
            keys.Add(pair.Key);
        }
        keys.Sort();
        for (int i = 0; i < keys.Count; i++)
        {
            LevelSolutionBucket b = new LevelSolutionBucket();
            b.Moves = keys[i];
            b.Count = count.ByMoves[keys[i]];
            result.SolutionsByMoves.Add(b);
        }

        result.Message = "Generated (mode=" + (freeGen ? "FreeGen" : "Exact")
            + ", attempts=" + attempt
            + ", pattern=" + result.PatternUsed
            + ", solutions=" + result.TotalSolutions
            + ", minMoves=" + result.MinimumMoves
            + ", moveLimit=" + moveLimit
            + ", pathExtra=" + result.PathExtra
            + ", tax=" + result.ActionTax
            + ", depth=" + result.DependencyDepth
            + ", difficulty=" + result.EstimatedDifficulty + "/10).";
    }

    private static bool ValidateParams(LevelGenerateParams p, out string error)
    {
        error = null;
        if (p.Width < 1 || p.Height < 1)
        {
            error = "Width/Height must be >= 1.";
            return false;
        }
        if (p.Width * p.Height > LevelLogic.MaxCells)
        {
            error = "Grid exceeds max cells.";
            return false;
        }
        if (!p.FreeGen && p.TargetSolutions < 1)
        {
            error = "Target Solutions must be >= 1.";
            return false;
        }
        if (p.FreeGen && p.SolutionCountCap < 1)
        {
            error = "SolutionCountCap must be >= 1.";
            return false;
        }
        if (p.MoveLimitSlack < 0)
        {
            error = "MoveLimitSlack cannot be negative.";
            return false;
        }
        if (p.MinPathExtra < 0)
        {
            error = "MinPathExtra cannot be negative.";
            return false;
        }
        if (!p.FreeGen && p.MoveLimit < 0)
        {
            error = "MoveLimit cannot be negative.";
            return false;
        }
        if (p.MaxAttempts < 1)
        {
            error = "MaxAttempts must be >= 1.";
            return false;
        }
        if (p.MaxEnemy < 0 || p.MaxRock < 0 || p.MaxSpike < 0)
        {
            error = "Max Enemy/Rock/Spike cannot be negative.";
            return false;
        }
        if (p.StateLimit < 1 || p.TimeLimitMs < 1)
        {
            error = "StateLimit/TimeLimitMs must be >= 1.";
            return false;
        }
        return true;
    }

    private static bool TryCreateStrippedBase(LevelData source, out LevelData stripped, out string error)
    {
        stripped = null;
        error = null;
        LevelData copy = source.CreateRuntimeCopy();
        List<LevelObjectData> objects = copy.Objects;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            LevelObjectType t = objects[i].Type;
            if (t == LevelObjectType.Enemy || t == LevelObjectType.Rock || t == LevelObjectType.Spike
                || t == LevelObjectType.Key || t == LevelObjectType.Door)
            {
                objects.RemoveAt(i);
            }
        }
        copy.SyncDerivedFields();
        LevelValidationResult v = LevelValidator.Validate(copy);
        if (!v.IsValid)
        {
            string detail = v.Issues.Count > 0 ? v.Issues[0].Message : "invalid";
            Object.DestroyImmediate(copy);
            error = "Base map invalid: " + detail;
            return false;
        }
        if (copy.PlayerStart.x < 0 || copy.Goal.x < 0)
        {
            Object.DestroyImmediate(copy);
            error = "Base map needs PlayerStart and Goal.";
            return false;
        }
        stripped = copy;
        return true;
    }

    private sealed class Topology
    {
        public int CellCount;
        public int Width;
        public int Height;
        public int Start = -1;
        public int Goal = -1;
        public bool[] Walkable;
        public int[] DistStart;
        public int[] DistGoal;
        public readonly List<int> Free = new List<int>();
        public readonly List<int> Shortest = new List<int>();
        public readonly List<int> Side = new List<int>();
        public readonly List<int> Choke = new List<int>();
        public int ShortestLen = -1;
        public LevelLogic Logic;
    }

    private sealed class PlacementState
    {
        public bool[] Used;
        public int Enemy;
        public int Rock;
        public int Spike;
        public int Key;
        public int Door;

        public PlacementState(int cells)
        {
            Used = new bool[cells];
        }
    }

    private static bool TryBuildTopology(LevelData map, out Topology topo, out string error)
    {
        topo = new Topology();
        error = null;
        LevelLogic logic = new LevelLogic(map);
        topo.Logic = logic;
        topo.CellCount = logic.CellCount;
        topo.Width = map.Width;
        topo.Height = map.Height;
        topo.Start = logic.StartIndex;
        topo.Goal = logic.GoalIndex;
        topo.Walkable = new bool[topo.CellCount];
        topo.DistStart = new int[topo.CellCount];
        topo.DistGoal = new int[topo.CellCount];

        for (int i = 0; i < topo.CellCount; i++)
        {
            topo.Walkable[i] = logic.IsFloor(i) && !logic.IsSolid(i);
            topo.DistStart[i] = -1;
            topo.DistGoal[i] = -1;
        }

        if (topo.Start < 0 || topo.Goal < 0 || !topo.Walkable[topo.Start] || !topo.Walkable[topo.Goal])
        {
            error = "Start/Goal missing or not on walkable floor.";
            return false;
        }

        Bfs(logic, topo.Walkable, topo.Start, topo.DistStart);
        Bfs(logic, topo.Walkable, topo.Goal, topo.DistGoal);
        if (topo.DistStart[topo.Goal] < 0)
        {
            error = "Goal unreachable on base topology.";
            return false;
        }

        topo.ShortestLen = topo.DistStart[topo.Goal];
        for (int i = 0; i < topo.CellCount; i++)
        {
            if (!topo.Walkable[i] || i == topo.Start || i == topo.Goal)
            {
                continue;
            }
            topo.Free.Add(i);
            if (topo.DistStart[i] >= 0 && topo.DistGoal[i] >= 0
                && topo.DistStart[i] + topo.DistGoal[i] == topo.ShortestLen)
            {
                topo.Shortest.Add(i);
            }
            else if (topo.DistStart[i] >= 0)
            {
                topo.Side.Add(i);
            }
        }

        for (int i = 0; i < topo.Shortest.Count; i++)
        {
            int cell = topo.Shortest[i];
            bool[] walk = new bool[topo.CellCount];
            for (int c = 0; c < topo.CellCount; c++)
            {
                walk[c] = topo.Walkable[c] && c != cell;
            }
            int[] dist = new int[topo.CellCount];
            Bfs(logic, walk, topo.Start, dist);
            if (dist[topo.Goal] < 0)
            {
                topo.Choke.Add(cell);
            }
        }
        return true;
    }

    private static void Bfs(LevelLogic logic, bool[] walkable, int origin, int[] dist)
    {
        for (int i = 0; i < dist.Length; i++)
        {
            dist[i] = -1;
        }
        if (origin < 0 || !walkable[origin])
        {
            return;
        }
        Queue<int> q = new Queue<int>();
        dist[origin] = 0;
        q.Enqueue(origin);
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            int x;
            int y;
            logic.FromIndex(cur, out x, out y);
            TryEnqueue(logic, walkable, dist, q, x + 1, y, dist[cur] + 1);
            TryEnqueue(logic, walkable, dist, q, x - 1, y, dist[cur] + 1);
            TryEnqueue(logic, walkable, dist, q, x, y + 1, dist[cur] + 1);
            TryEnqueue(logic, walkable, dist, q, x, y - 1, dist[cur] + 1);
        }
    }

    private static void TryEnqueue(LevelLogic logic, bool[] walkable, int[] dist, Queue<int> q, int x, int y, int nd)
    {
        if (!logic.InBounds(x, y))
        {
            return;
        }
        int i = logic.ToIndex(x, y);
        if (!walkable[i] || dist[i] >= 0)
        {
            return;
        }
        dist[i] = nd;
        q.Enqueue(i);
    }

    private static bool TryApplyPattern(
        LevelData level,
        Topology topo,
        PatternId pattern,
        LevelGenerateParams genParams,
        System.Random rng,
        PlacementState place,
        out string error)
    {
        error = null;
        switch (pattern)
        {
            case PatternId.PushChain:
                return PlacePushChain(level, topo, genParams, rng, place, out error);
            case PatternId.Blocker:
                return PlaceBlocker(level, topo, genParams, rng, place, out error);
            case PatternId.Fork:
                return PlaceFork(level, topo, genParams, rng, place, out error);
            case PatternId.SpikeTax:
                return PlaceSpikeTax(level, topo, genParams, rng, place, out error);
            case PatternId.KeyDoor:
                return PlaceKeyDoor(level, topo, genParams, rng, place, out error);
            case PatternId.Compose:
                return PlaceCompose(level, topo, genParams, rng, place, out error);
            default:
                error = "Unknown pattern.";
                return false;
        }
    }

    private static bool PlaceCompose(
        LevelData level, Topology topo, LevelGenerateParams p, System.Random rng, PlacementState place, out string error)
    {
        error = null;
        if (!TryPlaceKeyDoorDetour(level, topo, rng, place, out error))
        {
            return false;
        }

        if (p.MaxSpike > 0)
        {
            int spikeWant = p.MaxSpike;
            if (p.MaxSpike >= 2)
            {
                int half = (p.MaxSpike + 1) / 2;
                spikeWant = half + rng.Next(0, p.MaxSpike - half + 1);
            }
            PlaceSpikesOnPath(level, topo, p, rng, place, spikeWant);
        }

        int pushBudget = p.MaxEnemy + p.MaxRock;
        if (pushBudget > 0)
        {
            int pushWant = 1;
            if (pushBudget >= 2)
            {
                int hi = pushBudget < 4 ? pushBudget : 4;
                pushWant = 1 + rng.Next(0, hi);
            }
            if (pushWant > pushBudget)
            {
                pushWant = pushBudget;
            }
            PlacePushablesOnPath(level, topo, p, rng, place, pushWant);
        }

        if (place.Door < 1)
        {
            error = "Compose needs a door.";
            return false;
        }
        if (place.Spike < 1 && place.Enemy + place.Rock < 1)
        {
            error = "Compose needs spikes or pushables on path.";
            return false;
        }

        level.SyncDerivedFields();
        return true;
    }

    private static bool TryPlaceKeyDoorDetour(
        LevelData level, Topology topo, System.Random rng, PlacementState place, out string error)
    {
        error = null;
        List<int> doorPool = new List<int>();
        List<int> src = topo.Choke.Count > 0 ? topo.Choke : topo.Shortest;
        for (int i = 0; i < src.Count; i++)
        {
            if (!place.Used[src[i]])
            {
                doorPool.Add(src[i]);
            }
        }
        if (doorPool.Count == 0)
        {
            error = "No door cell on path.";
            return false;
        }

        // Prefer door near goal (high DistStart).
        for (int i = 0; i < doorPool.Count; i++)
        {
            int best = i;
            for (int j = i + 1; j < doorPool.Count; j++)
            {
                if (topo.DistStart[doorPool[j]] > topo.DistStart[doorPool[best]])
                {
                    best = j;
                }
            }
            if (best != i)
            {
                int tmp = doorPool[i];
                doorPool[i] = doorPool[best];
                doorPool[best] = tmp;
            }
        }

        int doorPick = doorPool.Count < 3 ? doorPool.Count : 3;
        int doorCell = doorPool[rng.Next(0, doorPick)];

        List<int> keyPool = new List<int>();
        for (int i = 0; i < topo.Side.Count; i++)
        {
            int cell = topo.Side[i];
            if (cell == doorCell || place.Used[cell])
            {
                continue;
            }
            if (topo.DistStart[cell] >= 0)
            {
                keyPool.Add(cell);
            }
        }
        if (keyPool.Count == 0)
        {
            for (int i = 0; i < topo.Free.Count; i++)
            {
                int cell = topo.Free[i];
                if (cell == doorCell || place.Used[cell] || IsOnList(topo.Shortest, cell))
                {
                    continue;
                }
                if (topo.DistStart[cell] >= 0)
                {
                    keyPool.Add(cell);
                }
            }
        }
        if (keyPool.Count == 0)
        {
            for (int i = 0; i < topo.Free.Count; i++)
            {
                int cell = topo.Free[i];
                if (cell == doorCell || place.Used[cell])
                {
                    continue;
                }
                if (topo.DistStart[cell] >= 0)
                {
                    keyPool.Add(cell);
                }
            }
        }
        if (keyPool.Count == 0)
        {
            error = "No key cell for detour.";
            return false;
        }

        // Prefer farthest keys (detour).
        for (int i = 0; i < keyPool.Count; i++)
        {
            int best = i;
            for (int j = i + 1; j < keyPool.Count; j++)
            {
                if (topo.DistStart[keyPool[j]] > topo.DistStart[keyPool[best]])
                {
                    best = j;
                }
            }
            if (best != i)
            {
                int tmp = keyPool[i];
                keyPool[i] = keyPool[best];
                keyPool[best] = tmp;
            }
        }

        int keyPick = keyPool.Count < 4 ? keyPool.Count : 4;
        int keyCell = keyPool[rng.Next(0, keyPick)];

        place.Used[doorCell] = true;
        place.Door++;
        AddObj(level, LevelObjectType.Door, doorCell, topo.Width);
        place.Used[keyCell] = true;
        place.Key++;
        AddObj(level, LevelObjectType.Key, keyCell, topo.Width);
        return true;
    }

    private static void PlaceSpikesOnPath(
        LevelData level, Topology topo, LevelGenerateParams p, System.Random rng, PlacementState place, int want)
    {
        List<int> pool = new List<int>();
        for (int i = 0; i < topo.Shortest.Count; i++)
        {
            int cell = topo.Shortest[i];
            if (!place.Used[cell])
            {
                pool.Add(cell);
            }
        }
        if (pool.Count == 0)
        {
            for (int i = 0; i < topo.Free.Count; i++)
            {
                int cell = topo.Free[i];
                if (!place.Used[cell] && IsAdjacentToAny(topo, cell, topo.Shortest))
                {
                    pool.Add(cell);
                }
            }
        }
        Shuffle(pool, rng);
        int placed = 0;
        for (int i = 0; i < pool.Count && placed < want; i++)
        {
            if (TryPlaceExclusive(level, topo, place, pool[i], LevelObjectType.Spike, p))
            {
                placed++;
            }
        }
    }

    private static void PlacePushablesOnPath(
        LevelData level, Topology topo, LevelGenerateParams p, System.Random rng, PlacementState place, int want)
    {
        List<int> pool = new List<int>();
        for (int i = 0; i < topo.Shortest.Count; i++)
        {
            int cell = topo.Shortest[i];
            if (!place.Used[cell] && HasPushSpace(topo, cell))
            {
                pool.Add(cell);
            }
        }
        if (pool.Count == 0)
        {
            for (int i = 0; i < topo.Free.Count; i++)
            {
                int cell = topo.Free[i];
                if (!place.Used[cell] && HasPushSpace(topo, cell))
                {
                    pool.Add(cell);
                }
            }
        }
        Shuffle(pool, rng);
        int placed = 0;
        for (int i = 0; i < pool.Count && placed < want; i++)
        {
            bool preferEnemy = place.Enemy < p.MaxEnemy
                && (place.Rock >= p.MaxRock || rng.Next(0, 2) == 0);
            LevelObjectType type = preferEnemy ? LevelObjectType.Enemy : LevelObjectType.Rock;
            if (type == LevelObjectType.Enemy && place.Enemy >= p.MaxEnemy)
            {
                type = LevelObjectType.Rock;
            }
            if (type == LevelObjectType.Rock && place.Rock >= p.MaxRock)
            {
                type = LevelObjectType.Enemy;
            }
            if (TryPlaceExclusive(level, topo, place, pool[i], type, p))
            {
                placed++;
            }
        }
    }

    private static bool PlacePushChain(
        LevelData level, Topology topo, LevelGenerateParams p, System.Random rng, PlacementState place, out string error)
    {
        error = null;
        if (p.MaxEnemy < 1 && p.MaxRock < 1)
        {
            error = "Need MaxEnemy or MaxRock >= 1.";
            return false;
        }
        List<int> pool = new List<int>();
        for (int i = 0; i < topo.Shortest.Count; i++)
        {
            int cell = topo.Shortest[i];
            if (HasPushSpace(topo, cell))
            {
                pool.Add(cell);
            }
        }
        if (pool.Count == 0)
        {
            error = "No pushable cell on shortest path.";
            return false;
        }
        Shuffle(pool, rng);
        int count = 1 + rng.Next(0, 2);
        if (p.MaxEnemy + p.MaxRock < count)
        {
            count = p.MaxEnemy + p.MaxRock;
        }
        int placed = 0;
        for (int i = 0; i < pool.Count && placed < count; i++)
        {
            bool preferEnemy = p.MaxEnemy > place.Enemy && (p.MaxRock <= place.Rock || rng.Next(0, 2) == 0);
            LevelObjectType type = preferEnemy ? LevelObjectType.Enemy : LevelObjectType.Rock;
            if (type == LevelObjectType.Enemy && place.Enemy >= p.MaxEnemy)
            {
                type = LevelObjectType.Rock;
            }
            if (type == LevelObjectType.Rock && place.Rock >= p.MaxRock)
            {
                type = LevelObjectType.Enemy;
            }
            if (!TryPlaceExclusive(level, topo, place, pool[i], type, p))
            {
                continue;
            }
            placed++;
        }
        if (placed < 1)
        {
            error = "Could not place push chain objects.";
            return false;
        }
        return true;
    }

    private static bool PlaceBlocker(
        LevelData level, Topology topo, LevelGenerateParams p, System.Random rng, PlacementState place, out string error)
    {
        error = null;
        if (p.MaxEnemy < 1 && p.MaxRock < 1)
        {
            error = "Need MaxEnemy or MaxRock >= 1.";
            return false;
        }
        List<int> pool = new List<int>();
        for (int i = 0; i < topo.Side.Count; i++)
        {
            int cell = topo.Side[i];
            if (IsAdjacentToAny(topo, cell, topo.Shortest))
            {
                pool.Add(cell);
            }
        }
        if (pool.Count == 0)
        {
            // Fallback: any free side cell.
            for (int i = 0; i < topo.Side.Count; i++)
            {
                pool.Add(topo.Side[i]);
            }
        }
        if (pool.Count == 0)
        {
            error = "No side cells to block.";
            return false;
        }
        Shuffle(pool, rng);
        int want = 1 + rng.Next(0, 3);
        int maxBlock = p.MaxEnemy + p.MaxRock;
        if (want > maxBlock)
        {
            want = maxBlock;
        }
        int placed = 0;
        for (int i = 0; i < pool.Count && placed < want; i++)
        {
            LevelObjectType type = (place.Rock < p.MaxRock && (place.Enemy >= p.MaxEnemy || rng.Next(0, 2) == 0))
                ? LevelObjectType.Rock
                : LevelObjectType.Enemy;
            if (!TryPlaceExclusive(level, topo, place, pool[i], type, p))
            {
                continue;
            }
            placed++;
        }
        if (placed < 1)
        {
            error = "Could not place blockers.";
            return false;
        }
        return true;
    }

    private static bool PlaceFork(
        LevelData level, Topology topo, LevelGenerateParams p, System.Random rng, PlacementState place, out string error)
    {
        error = null;
        // Seal most side exits, leave at least one alternate open for multi-solution chance.
        List<int> pool = new List<int>();
        for (int i = 0; i < topo.Side.Count; i++)
        {
            if (IsAdjacentToAny(topo, topo.Side[i], topo.Shortest))
            {
                pool.Add(topo.Side[i]);
            }
        }
        if (pool.Count < 2)
        {
            error = "Not enough side branches for Fork.";
            return false;
        }
        Shuffle(pool, rng);
        // Leave first cell open as alternate branch.
        int open = pool[0];
        int placed = 0;
        for (int i = 1; i < pool.Count; i++)
        {
            if (pool[i] == open)
            {
                continue;
            }
            if (place.Enemy >= p.MaxEnemy && place.Rock >= p.MaxRock)
            {
                break;
            }
            LevelObjectType type = place.Rock < p.MaxRock ? LevelObjectType.Rock : LevelObjectType.Enemy;
            if (TryPlaceExclusive(level, topo, place, pool[i], type, p))
            {
                placed++;
            }
        }
        if (placed < 1)
        {
            error = "Could not seal enough forks.";
            return false;
        }
        return true;
    }

    private static bool PlaceSpikeTax(
        LevelData level, Topology topo, LevelGenerateParams p, System.Random rng, PlacementState place, out string error)
    {
        error = null;
        if (p.MaxSpike < 1)
        {
            error = "Need MaxSpike >= 1.";
            return false;
        }
        List<int> pool = new List<int>();
        for (int i = 0; i < topo.Side.Count; i++)
        {
            if (IsAdjacentToAny(topo, topo.Side[i], topo.Shortest))
            {
                pool.Add(topo.Side[i]);
            }
        }
        if (pool.Count == 0)
        {
            for (int i = 0; i < topo.Free.Count; i++)
            {
                if (!IsOnList(topo.Shortest, topo.Free[i]))
                {
                    pool.Add(topo.Free[i]);
                }
            }
        }
        if (pool.Count == 0)
        {
            error = "No cells for spikes.";
            return false;
        }
        Shuffle(pool, rng);
        int want = 1 + rng.Next(0, Mathf.Min(3, p.MaxSpike));
        if (want > p.MaxSpike)
        {
            want = p.MaxSpike;
        }
        int placed = 0;
        for (int i = 0; i < pool.Count && placed < want; i++)
        {
            if (TryPlaceExclusive(level, topo, place, pool[i], LevelObjectType.Spike, p))
            {
                placed++;
            }
        }
        if (placed < 1)
        {
            error = "Could not place spikes.";
            return false;
        }
        return true;
    }

    private static bool PlaceKeyDoor(
        LevelData level, Topology topo, LevelGenerateParams p, System.Random rng, PlacementState place, out string error)
    {
        error = null;
        List<int> doorPool = new List<int>();
        List<int> src = topo.Choke.Count > 0 ? topo.Choke : topo.Shortest;
        for (int i = 0; i < src.Count; i++)
        {
            doorPool.Add(src[i]);
        }
        if (doorPool.Count == 0)
        {
            error = "No door cell on path.";
            return false;
        }
        Shuffle(doorPool, rng);
        int doorCell = -1;
        for (int i = 0; i < doorPool.Count; i++)
        {
            if (!place.Used[doorPool[i]])
            {
                doorCell = doorPool[i];
                break;
            }
        }
        if (doorCell < 0)
        {
            error = "No free door cell.";
            return false;
        }

        List<int> keyPool = new List<int>();
        for (int i = 0; i < topo.Free.Count; i++)
        {
            int cell = topo.Free[i];
            if (cell == doorCell || place.Used[cell])
            {
                continue;
            }
            if (topo.DistStart[cell] >= 0 && topo.DistStart[doorCell] >= 0
                && topo.DistStart[cell] <= topo.DistStart[doorCell])
            {
                keyPool.Add(cell);
            }
        }
        if (keyPool.Count == 0)
        {
            error = "No key cell before door.";
            return false;
        }
        Shuffle(keyPool, rng);
        int keyCell = keyPool[rng.Next(0, keyPool.Count)];

        place.Used[doorCell] = true;
        place.Door++;
        AddObj(level, LevelObjectType.Door, doorCell, topo.Width);
        place.Key++;
        AddObj(level, LevelObjectType.Key, keyCell, topo.Width);
        // Key may share cell with future objects; mark used only for exclusive later.
        return true;
    }

    private static void TryAddOptionalAccents(
        LevelData level,
        Topology topo,
        LevelGenerateParams p,
        System.Random rng,
        PlacementState place,
        PatternId main)
    {
        if (main != PatternId.SpikeTax && place.Spike < p.MaxSpike && rng.Next(0, 100) < 40)
        {
            List<int> pool = new List<int>();
            for (int i = 0; i < topo.Side.Count; i++)
            {
                if (!place.Used[topo.Side[i]] && IsAdjacentToAny(topo, topo.Side[i], topo.Shortest))
                {
                    pool.Add(topo.Side[i]);
                }
            }
            if (pool.Count > 0)
            {
                TryPlaceExclusive(level, topo, place, pool[rng.Next(0, pool.Count)], LevelObjectType.Spike, p);
            }
        }
        if (main != PatternId.Blocker && (place.Rock < p.MaxRock || place.Enemy < p.MaxEnemy) && rng.Next(0, 100) < 35)
        {
            List<int> pool = new List<int>();
            for (int i = 0; i < topo.Side.Count; i++)
            {
                if (!place.Used[topo.Side[i]])
                {
                    pool.Add(topo.Side[i]);
                }
            }
            if (pool.Count > 0)
            {
                LevelObjectType type = place.Rock < p.MaxRock ? LevelObjectType.Rock : LevelObjectType.Enemy;
                TryPlaceExclusive(level, topo, place, pool[rng.Next(0, pool.Count)], type, p);
            }
        }
        level.SyncDerivedFields();
    }

    private static bool TryPlaceExclusive(
        LevelData level,
        Topology topo,
        PlacementState place,
        int cell,
        LevelObjectType type,
        LevelGenerateParams p)
    {
        if (cell < 0 || cell >= place.Used.Length || place.Used[cell])
        {
            return false;
        }
        if (cell == topo.Start || cell == topo.Goal)
        {
            return false;
        }
        if (type == LevelObjectType.Enemy && place.Enemy >= p.MaxEnemy)
        {
            return false;
        }
        if (type == LevelObjectType.Rock && place.Rock >= p.MaxRock)
        {
            return false;
        }
        if (type == LevelObjectType.Spike && place.Spike >= p.MaxSpike)
        {
            return false;
        }
        place.Used[cell] = true;
        if (type == LevelObjectType.Enemy)
        {
            place.Enemy++;
        }
        else if (type == LevelObjectType.Rock)
        {
            place.Rock++;
        }
        else if (type == LevelObjectType.Spike)
        {
            place.Spike++;
        }
        AddObj(level, type, cell, topo.Width);
        return true;
    }

    private static void AddObj(LevelData level, LevelObjectType type, int cell, int width)
    {
        int x = cell % width;
        int y = cell / width;
        level.Objects.Add(new LevelObjectData(level.AllocateObjectId(), type, x, y));
    }

    private static bool HasPushSpace(Topology topo, int cell)
    {
        int x;
        int y;
        topo.Logic.FromIndex(cell, out x, out y);
        return IsOpenFloor(topo, x + 1, y) || IsOpenFloor(topo, x - 1, y)
            || IsOpenFloor(topo, x, y + 1) || IsOpenFloor(topo, x, y - 1);
    }

    private static bool IsOpenFloor(Topology topo, int x, int y)
    {
        if (!topo.Logic.InBounds(x, y))
        {
            return false;
        }
        int i = topo.Logic.ToIndex(x, y);
        return topo.Walkable[i];
    }

    private static bool IsAdjacentToAny(Topology topo, int cell, List<int> list)
    {
        int x;
        int y;
        topo.Logic.FromIndex(cell, out x, out y);
        return IsOnList(list, SafeIndex(topo, x + 1, y))
            || IsOnList(list, SafeIndex(topo, x - 1, y))
            || IsOnList(list, SafeIndex(topo, x, y + 1))
            || IsOnList(list, SafeIndex(topo, x, y - 1));
    }

    private static int SafeIndex(Topology topo, int x, int y)
    {
        if (!topo.Logic.InBounds(x, y))
        {
            return -1;
        }
        return topo.Logic.ToIndex(x, y);
    }

    private static bool IsOnList(List<int> list, int cell)
    {
        if (cell < 0)
        {
            return false;
        }
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == cell)
            {
                return true;
            }
        }
        return false;
    }

    private static bool CheapValidate(LevelData level, Topology topo, LevelGenerateParams p, out string error)
    {
        error = null;
        LevelValidationResult v = LevelValidator.Validate(level);
        if (!v.IsValid)
        {
            error = v.Issues.Count > 0 ? v.Issues[0].Message : "invalid";
            return false;
        }

        LevelLogic logic = new LevelLogic(level);
        LevelSimState init = logic.CreateInitialState(level);
        int doors = 0;
        int keys = 0;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Type == LevelObjectType.Door)
            {
                doors++;
            }
            if (objects[i].Type == LevelObjectType.Key)
            {
                keys++;
            }
        }
        if (doors > keys)
        {
            error = "More doors than keys.";
            return false;
        }

        // Walk-only reachability treating pushables/closed doors as blocked.
        bool[] walk = new bool[logic.CellCount];
        for (int i = 0; i < logic.CellCount; i++)
        {
            if (!logic.IsFloor(i) || logic.IsSolid(i))
            {
                continue;
            }
            if (LevelLogic.HasEnemy(ref init, i) || LevelLogic.HasRock(ref init, i))
            {
                continue;
            }
            int door = logic.GetDoorIndex(i);
            if (door >= 0 && ((init.ClosedDoorMask & (1UL << door)) != 0UL))
            {
                continue;
            }
            walk[i] = true;
        }
        int[] dist = new int[logic.CellCount];
        Bfs(logic, walk, logic.StartIndex, dist);
        if (dist[logic.GoalIndex] < 0 && doors == 0)
        {
            // Must have at least one pushable with open neighbor, else impossible cheaply.
            bool pushOk = false;
            for (int i = 0; i < logic.CellCount; i++)
            {
                if (!LevelLogic.HasEnemy(ref init, i) && !LevelLogic.HasRock(ref init, i))
                {
                    continue;
                }
                int x;
                int y;
                logic.FromIndex(i, out x, out y);
                if (IsOpenNeighbor(logic, ref init, x, y))
                {
                    pushOk = true;
                    break;
                }
            }
            if (!pushOk)
            {
                error = "Goal blocked with no push/door option.";
                return false;
            }
        }

        List<LevelDeadlockInfo> deadlocks = new List<LevelDeadlockInfo>();
        LevelAnalyzer.DetectDeadlocks(level, deadlocks);
        for (int i = 0; i < deadlocks.Count; i++)
        {
            if (deadlocks[i].ObjectType == LevelObjectType.Door
                && deadlocks[i].Reason != null
                && deadlocks[i].Reason.IndexOf("Not enough keys") >= 0)
            {
                error = deadlocks[i].Reason;
                return false;
            }
        }
        return true;
    }

    private static bool IsOpenNeighbor(LevelLogic logic, ref LevelSimState state, int x, int y)
    {
        return IsOpenCell(logic, ref state, x + 1, y)
            || IsOpenCell(logic, ref state, x - 1, y)
            || IsOpenCell(logic, ref state, x, y + 1)
            || IsOpenCell(logic, ref state, x, y - 1);
    }

    private static bool IsOpenCell(LevelLogic logic, ref LevelSimState state, int x, int y)
    {
        if (!logic.InBounds(x, y))
        {
            return false;
        }
        int i = logic.ToIndex(x, y);
        if (logic.IsSolid(i) || !logic.IsFloor(i))
        {
            return false;
        }
        if (LevelLogic.HasEnemy(ref state, i) || LevelLogic.HasRock(ref state, i))
        {
            return false;
        }
        int door = logic.GetDoorIndex(i);
        if (door >= 0 && ((state.ClosedDoorMask & (1UL << door)) != 0UL))
        {
            return false;
        }
        return true;
    }

    private enum CountStatus
    {
        ExactOrComplete = 0,
        TooMany = 1,
        Unsolvable = 2,
        Inconclusive = 3
    }

    private sealed class CountResult
    {
        public CountStatus Status;
        public int Total;
        public int MinimumMoves = -1;
        public readonly Dictionary<int, int> ByMoves = new Dictionary<int, int>();
        public List<LevelDir> SamplePath = new List<LevelDir>();
        public bool Exhaustive;
    }

    private sealed class SearchBudget
    {
        public int StateLimit;
        public int TimeLimitMs;
        public int Expansions;
        public long Deadline;
        public bool HitLimit;

        public SearchBudget(int stateLimit, int timeLimitMs)
        {
            StateLimit = stateLimit;
            TimeLimitMs = timeLimitMs;
        }

        public void Begin()
        {
            Expansions = 0;
            HitLimit = false;
            Deadline = System.DateTime.UtcNow.Ticks + (long)TimeLimitMs * System.TimeSpan.TicksPerMillisecond;
        }

        public bool Expired()
        {
            if (Expansions >= StateLimit || System.DateTime.UtcNow.Ticks >= Deadline)
            {
                HitLimit = true;
                return true;
            }
            return false;
        }
    }

    private static CountResult CountSolutions(LevelData level, int targetSolutions, SearchBudget budget)
    {
        CountResult result = new CountResult();
        budget.Begin();
        LevelLogic logic = new LevelLogic(level);
        LevelSimState initial = logic.CreateInitialState(level);
        if (initial.PlayerIndex < 0 || logic.GoalIndex < 0)
        {
            result.Status = CountStatus.Unsolvable;
            return result;
        }

        int moveLimit = level.MoveLimit;
        if (moveLimit <= 0)
        {
            moveLimit = level.Width * level.Height + 64;
        }

        List<LevelDir> path = new List<LevelDir>(32);
        HashSet<long> visited = new HashSet<long>();
        visited.Add(logic.PackStateKey(ref initial));
        LevelDir[] dirs = { LevelDir.Up, LevelDir.Right, LevelDir.Down, LevelDir.Left };
        bool tooMany = false;

        Search(logic, initial, path, visited, dirs, moveLimit, targetSolutions, budget, result, ref tooMany);

        if (tooMany)
        {
            result.Status = CountStatus.TooMany;
            result.Exhaustive = false;
            return result;
        }
        if (budget.HitLimit)
        {
            result.Status = CountStatus.Inconclusive;
            result.Exhaustive = false;
            return result;
        }

        result.Exhaustive = true;
        if (result.Total <= 0)
        {
            result.Status = CountStatus.Unsolvable;
        }
        else
        {
            result.Status = CountStatus.ExactOrComplete;
        }
        return result;
    }

    private static void Search(
        LevelLogic logic,
        LevelSimState state,
        List<LevelDir> path,
        HashSet<long> visited,
        LevelDir[] dirs,
        int moveLimit,
        int targetSolutions,
        SearchBudget budget,
        CountResult result,
        ref bool tooMany)
    {
        if (tooMany || budget.Expired())
        {
            return;
        }
        budget.Expansions++;

        if (state.Won)
        {
            if (state.MovesUsed <= moveLimit)
            {
                result.Total++;
                int moves = state.MovesUsed;
                if (result.MinimumMoves < 0 || moves < result.MinimumMoves)
                {
                    result.MinimumMoves = moves;
                    result.SamplePath.Clear();
                    for (int i = 0; i < path.Count; i++)
                    {
                        result.SamplePath.Add(path[i]);
                    }
                }
                int c;
                if (result.ByMoves.TryGetValue(moves, out c))
                {
                    result.ByMoves[moves] = c + 1;
                }
                else
                {
                    result.ByMoves[moves] = 1;
                }
                if (result.Total > targetSolutions)
                {
                    tooMany = true;
                }
            }
            return;
        }

        if (state.Dead || state.MovesUsed >= moveLimit)
        {
            return;
        }

        for (int d = 0; d < 4; d++)
        {
            if (tooMany || budget.Expired())
            {
                return;
            }
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
            long key = logic.PackStateKey(ref next);
            if (visited.Contains(key))
            {
                continue;
            }
            path.Add(dirs[d]);
            visited.Add(key);
            Search(logic, next, path, visited, dirs, moveLimit, targetSolutions, budget, result, ref tooMany);
            visited.Remove(key);
            path.RemoveAt(path.Count - 1);
        }
    }

    private static int MeasureDependencyDepth(LevelData level, List<LevelDir> path)
    {
        if (path == null || path.Count == 0)
        {
            return 0;
        }
        LevelLogic logic = new LevelLogic(level);
        LevelSimState state = logic.CreateInitialState(level);
        int depth = 0;
        for (int i = 0; i < path.Count; i++)
        {
            LevelActionResult a = logic.TryMove(ref state, path[i], false);
            if (a == LevelActionResult.Pushed || a == LevelActionResult.Kicked
                || a == LevelActionResult.OpenedDoor || a == LevelActionResult.CollectedKey
                || a == LevelActionResult.SpikePenalty)
            {
                depth++;
            }
        }
        return depth;
    }

    private static int CountDecisionPoints(LevelData level, List<LevelDir> path)
    {
        if (path == null || path.Count == 0)
        {
            return 0;
        }
        LevelLogic logic = new LevelLogic(level);
        LevelSimState state = logic.CreateInitialState(level);
        LevelDir[] dirs = { LevelDir.Up, LevelDir.Right, LevelDir.Down, LevelDir.Left };
        int decisions = 0;
        for (int i = 0; i < path.Count; i++)
        {
            int options = 0;
            for (int d = 0; d < 4; d++)
            {
                LevelSimState probe = state;
                logic.TryMove(ref probe, dirs[d], false);
                if (probe.Dead && !probe.Won)
                {
                    continue;
                }
                if (logic.StatesEqual(ref probe, ref state) && !probe.Won)
                {
                    continue;
                }
                options++;
            }
            if (options >= 2)
            {
                decisions++;
            }
            logic.TryMove(ref state, path[i], false);
            if (state.Dead && !state.Won)
            {
                break;
            }
        }
        return decisions;
    }

    private static int EstimateDifficulty(LevelGenerateResult r, int moveLimit)
    {
        int score = 1;
        if (r.PathExtra >= 2) score++;
        if (r.PathExtra >= 6) score++;
        if (r.PathExtra >= 12) score++;
        if (r.ActionTax >= 2) score++;
        if (r.ActionTax >= 5) score++;
        if (r.DependencyDepth >= 1) score++;
        if (r.DependencyDepth >= 3) score++;
        if (r.DecisionPoints >= 2) score++;
        if (r.DecisionPoints >= 5) score++;
        if (r.Deadlocks.Count > 0) score++;
        if (moveLimit > 0 && r.MoveSlack <= 2) score++;
        if (moveLimit > 0 && r.MoveSlack <= 0) score++;
        if (r.TotalSolutions == 1) score++;
        if (r.TotalSolutions >= 2 && r.TotalSolutions <= 4) score++;
        if (score < 1) score = 1;
        if (score > 10) score = 10;
        return score;
    }

    private static void Shuffle(List<int> list, System.Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(0, i + 1);
            int tmp = list[i];
            list[i] = list[j];
            list[j] = tmp;
        }
    }
}
