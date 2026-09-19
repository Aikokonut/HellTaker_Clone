using System.Collections.Generic;
using UnityEngine;

public sealed class LevelDeadlockInfo
{
    public LevelObjectType ObjectType;
    public Vector2Int Cell;
    public string Reason;
}

public sealed class LevelObjectCounts
{
    public int Floor;
    public int Wall;
    public int Boundary;
    public int PlayerStart;
    public int Enemy;
    public int Rock;
    public int Spike;
    public int Key;
    public int Door;
    public int Goal;
    public int RequiredZone;
}

public sealed class LevelAnalysisResult
{
    public bool Valid;
    public bool Solvable;
    public int MinimumMoves = -1;
    public int MoveLimit;
    public int MoveSlack = int.MinValue;
    public int SolutionCountAtMinimum = -1;
    public int NearSolutionsPlus1 = -1;
    public int NearSolutionsPlus2 = -1;
    public int StoredOptimalSolutionCount;
    public bool OptimalSolutionsCapped;
    public bool MetricsUnavailable;
    public string UnavailableReason;
    public readonly List<LevelValidationIssue> ValidationIssues = new List<LevelValidationIssue>();
    public readonly List<LevelDeadlockInfo> Deadlocks = new List<LevelDeadlockInfo>();
    public LevelObjectCounts Counts = new LevelObjectCounts();
    public readonly List<LevelDir> OptimalPath = new List<LevelDir>();
    public readonly List<List<LevelDir>> OptimalSolutions = new List<List<LevelDir>>();
}

public sealed class LevelCalibrateResult
{
    public bool Success;
    public string Message;
    public int BaseCost = -1;
    public int RefCost = -1;
    public int CostSlack = -1;
    public int BaseSolutions = -1;
    public int RefSolutions = -1;
    public int EnemyCount;
    public int RockCount;
    public int SpikeCount;
    public int RecommendedAttempts = 80;
    public int RecommendedMaxTargetSolutions = 1;
}

public static class LevelAnalyzer
{
    public static LevelCalibrateResult CalibrateFromReference(LevelData reference)
    {
        LevelCalibrateResult result = new LevelCalibrateResult();
        if (reference == null)
        {
            result.Message = "Reference level required.";
            return result;
        }

        LevelData full = reference.CreateRuntimeCopy();
        full.MoveLimit = 0;
        LevelData empty = reference.CreateRuntimeCopy();
        empty.MoveLimit = 0;
        StripPushables(empty);

        LevelSolveResult baseSolve = LevelSolver.Solve(empty, 200000, false, LevelSolver.DefaultMaxStoredSolutions);
        LevelSolveResult refSolve = LevelSolver.Solve(full, 200000, false, LevelSolver.DefaultMaxStoredSolutions);

        if (!baseSolve.Success || baseSolve.MinimumMoves < 0)
        {
            Object.DestroyImmediate(full);
            Object.DestroyImmediate(empty);
            result.Message = "Empty map (no Enemy/Rock/Spike) not solvable.";
            return result;
        }
        if (!refSolve.Success || refSolve.MinimumMoves < 0)
        {
            Object.DestroyImmediate(full);
            Object.DestroyImmediate(empty);
            result.Message = "Reference level not solvable.";
            return result;
        }

        result.BaseCost = baseSolve.MinimumMoves;
        result.RefCost = refSolve.MinimumMoves;
        result.CostSlack = result.RefCost - result.BaseCost;
        if (result.CostSlack < 0) result.CostSlack = 0;
        result.BaseSolutions = baseSolve.SolutionCountAtMinimum;
        if (result.BaseSolutions < 0)
        {
            result.BaseSolutions = baseSolve.StoredOptimalSolutionCount;
        }
        result.RefSolutions = refSolve.SolutionCountAtMinimum;
        if (result.RefSolutions < 0)
        {
            result.RefSolutions = refSolve.StoredOptimalSolutionCount;
        }

        List<LevelObjectData> objs = reference.Objects;
        for (int i = 0; i < objs.Count; i++)
        {
            if (objs[i].Type == LevelObjectType.Enemy) result.EnemyCount++;
            else if (objs[i].Type == LevelObjectType.Rock) result.RockCount++;
            else if (objs[i].Type == LevelObjectType.Spike) result.SpikeCount++;
        }

        result.RecommendedMaxTargetSolutions = result.RefSolutions;
        if (result.RecommendedMaxTargetSolutions < 1)
        {
            result.RecommendedMaxTargetSolutions = 1;
        }
        if (result.RecommendedMaxTargetSolutions > 8)
        {
            result.RecommendedMaxTargetSolutions = 8;
        }

        int objects = result.EnemyCount + result.RockCount + result.SpikeCount;
        result.RecommendedAttempts = 40 + objects * 25 + result.CostSlack * 5;
        if (result.RecommendedAttempts < 60) result.RecommendedAttempts = 60;
        if (result.RecommendedAttempts > 300) result.RecommendedAttempts = 300;

        result.Success = true;
        result.Message = "BaseCost=" + result.BaseCost
            + " RefCost=" + result.RefCost
            + " → CostSlack=" + result.CostSlack
            + " | E/R/S=" + result.EnemyCount + "/" + result.RockCount + "/" + result.SpikeCount
            + " | Sols empty=" + result.BaseSolutions + " ref=" + result.RefSolutions
            + " → MaxTargetSolutions=" + result.RecommendedMaxTargetSolutions
            + " Attempts≈" + result.RecommendedAttempts;

        Object.DestroyImmediate(full);
        Object.DestroyImmediate(empty);
        return result;
    }

    private static void StripPushables(LevelData level)
    {
        List<LevelObjectData> objects = level.Objects;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            LevelObjectType t = objects[i].Type;
            if (t == LevelObjectType.Enemy || t == LevelObjectType.Rock || t == LevelObjectType.Spike)
            {
                objects.RemoveAt(i);
            }
        }
        level.SyncDerivedFields();
    }

    public static int MaxStoredSolutions = LevelSolver.DefaultMaxStoredSolutions;

    public static int ImpactProbeMaxStates = 12000;

    public static bool HasGameplayImpact(LevelData data, LevelObjectData obj)
    {
        return HasGameplayImpact(data, obj, ImpactProbeMaxStates);
    }

    public static bool HasGameplayImpact(LevelData data, LevelObjectData obj, int maxStates)
    {
        if (data == null || obj == null)
        {
            return false;
        }
        if (obj.Type != LevelObjectType.Enemy
            && obj.Type != LevelObjectType.Rock
            && obj.Type != LevelObjectType.Spike)
        {
            return true;
        }
        if (!InGrid(data, obj.X, obj.Y))
        {
            return false;
        }

        LevelLogic logic = new LevelLogic(data);
        LevelSimState state = logic.CreateInitialState(data);
        int index = logic.ToIndex(obj.X, obj.Y);
        if (index == logic.StartIndex || index == logic.GoalIndex)
        {
            return false;
        }
        if (!IsOnOrNearCriticalRoute(logic, index))
        {
            return false;
        }
        if (obj.Type == LevelObjectType.Enemy && IsEnemyInUselessCorner(logic, ref state, index))
        {
            return false;
        }
        if (obj.Type == LevelObjectType.Rock && IsRockInUselessCorner(logic, ref state, index))
        {
            return false;
        }
        return CounterfactualChangesSolutions(data, obj, maxStates);
    }

    public static bool AllPuzzleObjectsHaveImpact(LevelData data, bool[] protectedCell, out string reason)
    {
        return AllPuzzleObjectsHaveImpact(data, protectedCell, ImpactProbeMaxStates, out reason);
    }

    public static bool AllPuzzleObjectsHaveImpact(
        LevelData data, bool[] protectedCell, int maxStates, out string reason)
    {
        reason = null;
        if (data == null)
        {
            reason = "LevelData is null.";
            return false;
        }
        List<LevelObjectData> objects = data.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Enemy
                && obj.Type != LevelObjectType.Rock
                && obj.Type != LevelObjectType.Spike)
            {
                continue;
            }
            int cell = obj.Y * data.Width + obj.X;
            if (protectedCell != null && cell >= 0 && cell < protectedCell.Length && protectedCell[cell])
            {
                continue;
            }
            if (!HasGameplayImpact(data, obj, maxStates))
            {
                reason = obj.Type + " at " + obj.X + "," + obj.Y
                    + " is USELESS (no counterfactual solution impact).";
                return false;
            }
        }
        return true;
    }

    private static bool CounterfactualChangesSolutions(LevelData data, LevelObjectData obj, int maxStates)
    {
        if (maxStates < 2000)
        {
            maxStates = 2000;
        }

        LevelSolveResult withObj = LevelSolver.Solve(data, maxStates, false, 8);

        int removeAt = -1;
        List<LevelObjectData> objects = data.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Id == obj.Id)
            {
                removeAt = i;
                break;
            }
        }
        if (removeAt < 0)
        {
            return false;
        }

        int depWith = CountPathInteractions(data, withObj.OptimalPath);
        int taxWith = MoveTax(withObj);

        LevelObjectData held = objects[removeAt];
        objects.RemoveAt(removeAt);
        data.SyncDerivedFields();

        LevelSolveResult without = LevelSolver.Solve(data, maxStates, false, 8);
        int depWithout = CountPathInteractions(data, without.OptimalPath);
        int taxWithout = MoveTax(without);

        objects.Insert(removeAt, held);
        data.SyncDerivedFields();

        if (!withObj.Success)
        {
            return without.Success;
        }
        if (!without.Success)
        {
            return true;
        }
        if (withObj.MinimumMoves != without.MinimumMoves)
        {
            return true;
        }
        if (withObj.SolutionCountAtMinimum >= 0
            && without.SolutionCountAtMinimum >= 0
            && withObj.SolutionCountAtMinimum != without.SolutionCountAtMinimum)
        {
            return true;
        }
        if (taxWith != taxWithout)
        {
            return true;
        }
        if (depWith != depWithout)
        {
            return true;
        }
        return false;
    }

    private static int MoveTax(LevelSolveResult solve)
    {
        if (solve == null || solve.MinimumMoves < 0 || solve.OptimalPath == null)
        {
            return -1;
        }
        return solve.MinimumMoves - solve.OptimalPath.Count;
    }

    private static int CountPathInteractions(LevelData data, List<LevelDir> path)
    {
        if (data == null || path == null || path.Count == 0)
        {
            return 0;
        }
        LevelLogic logic = new LevelLogic(data);
        LevelSimState state = logic.CreateInitialState(data);
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
            if (state.Dead && !state.Won)
            {
                break;
            }
        }
        return depth;
    }

    private static bool IsOnOrNearCriticalRoute(LevelLogic logic, int index)
    {
        if (CellOnTerrainShortest(logic, index))
        {
            return true;
        }
        if (IsTerrainChoke(logic, index))
        {
            return true;
        }
        if (IsAdjacentToTerrainShortest(logic, index))
        {
            return true;
        }
        // Allow forced-detour cells (e.g. Key branch) that are start-reachable.
        int cellCount = logic.CellCount;
        bool[] walk = new bool[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            walk[i] = logic.IsFloor(i) && !logic.IsSolid(i);
        }
        int[] distStart = new int[cellCount];
        FillBfs(logic, walk, logic.StartIndex, distStart);
        return index >= 0 && distStart[index] >= 0;
    }

    private static bool IsTerrainChoke(LevelLogic logic, int index)
    {
        if (index < 0 || index == logic.StartIndex || index == logic.GoalIndex)
        {
            return false;
        }
        if (!logic.IsFloor(index) || logic.IsSolid(index))
        {
            return false;
        }
        int cellCount = logic.CellCount;
        bool[] walk = new bool[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            walk[i] = logic.IsFloor(i) && !logic.IsSolid(i) && i != index;
        }
        int[] dist = new int[cellCount];
        FillBfs(logic, walk, logic.StartIndex, dist);
        return logic.GoalIndex >= 0 && dist[logic.GoalIndex] < 0;
    }

    private static bool InGrid(LevelData data, int x, int y)
    {
        return x >= 0 && y >= 0 && x < data.Width && y < data.Height;
    }

    private static bool CellOnTerrainShortest(LevelLogic logic, int index)
    {
        if (index < 0)
        {
            return false;
        }
        int cellCount = logic.CellCount;
        bool[] walk = new bool[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            walk[i] = logic.IsFloor(i) && !logic.IsSolid(i);
        }
        int[] distStart = new int[cellCount];
        int[] distGoal = new int[cellCount];
        FillBfs(logic, walk, logic.StartIndex, distStart);
        FillBfs(logic, walk, logic.GoalIndex, distGoal);
        if (logic.GoalIndex < 0 || distStart[logic.GoalIndex] < 0)
        {
            return false;
        }
        if (distStart[index] < 0 || distGoal[index] < 0)
        {
            return false;
        }
        return distStart[index] + distGoal[index] == distStart[logic.GoalIndex];
    }

    private static bool IsAdjacentToTerrainShortest(LevelLogic logic, int index)
    {
        int x;
        int y;
        logic.FromIndex(index, out x, out y);
        return CellOnTerrainShortest(logic, SafeIndex(logic, x + 1, y))
            || CellOnTerrainShortest(logic, SafeIndex(logic, x - 1, y))
            || CellOnTerrainShortest(logic, SafeIndex(logic, x, y + 1))
            || CellOnTerrainShortest(logic, SafeIndex(logic, x, y - 1));
    }

    private static int SafeIndex(LevelLogic logic, int x, int y)
    {
        if (!logic.InBounds(x, y))
        {
            return -1;
        }
        return logic.ToIndex(x, y);
    }

    private static void FillBfs(LevelLogic logic, bool[] walkable, int origin, int[] dist)
    {
        for (int i = 0; i < dist.Length; i++)
        {
            dist[i] = -1;
        }
        if (origin < 0 || origin >= walkable.Length || !walkable[origin])
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
            TryBfsEnqueue(logic, walkable, dist, q, x + 1, y, dist[cur] + 1);
            TryBfsEnqueue(logic, walkable, dist, q, x - 1, y, dist[cur] + 1);
            TryBfsEnqueue(logic, walkable, dist, q, x, y + 1, dist[cur] + 1);
            TryBfsEnqueue(logic, walkable, dist, q, x, y - 1, dist[cur] + 1);
        }
    }

    private static void TryBfsEnqueue(
        LevelLogic logic, bool[] walkable, int[] dist, Queue<int> q, int x, int y, int nd)
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

    public static LevelAnalysisResult Analyze(LevelData data)
    {
        return Analyze(data, MaxStoredSolutions);
    }

    public static LevelAnalysisResult Analyze(LevelData data, int maxStoredSolutions)
    {
        LevelAnalysisResult analysis = new LevelAnalysisResult();
        if (data == null)
        {
            analysis.Valid = false;
            analysis.MetricsUnavailable = true;
            analysis.UnavailableReason = "LevelData is null.";
            return analysis;
        }

        analysis.MoveLimit = data.MoveLimit;
        CountObjects(data, analysis.Counts);

        LevelValidationResult validation = LevelValidator.Validate(data);
        analysis.Valid = validation.IsValid;
        for (int i = 0; i < validation.Issues.Count; i++)
        {
            analysis.ValidationIssues.Add(validation.Issues[i]);
        }

        DetectDeadlocks(data, analysis.Deadlocks);

        if (!validation.IsValid)
        {
            analysis.Solvable = false;
            analysis.MetricsUnavailable = true;
            analysis.UnavailableReason = "Validation failed.";
            return analysis;
        }

        LevelSolveResult solve = LevelSolver.Solve(data, 2000000, true, maxStoredSolutions);
        CopySolutions(solve, analysis);

        if (solve.TimedOut || solve.StateLimitReached)
        {
            analysis.MetricsUnavailable = true;
            analysis.UnavailableReason = solve.Message;
            if (solve.MinimumMoves >= 0)
            {
                analysis.MinimumMoves = solve.MinimumMoves;
                analysis.SolutionCountAtMinimum = solve.SolutionCountAtMinimum;
                analysis.NearSolutionsPlus1 = solve.NearPlus1Count;
                analysis.NearSolutionsPlus2 = solve.NearPlus2Count;
                analysis.Solvable = data.MoveLimit <= 0 || solve.MinimumMoves <= data.MoveLimit;
                if (data.MoveLimit > 0 && solve.MinimumMoves >= 0)
                {
                    analysis.MoveSlack = data.MoveLimit - solve.MinimumMoves;
                }
            }
            return analysis;
        }

        if (!solve.Success && solve.MinimumMoves < 0)
        {
            analysis.Solvable = false;
            analysis.MinimumMoves = -1;
            analysis.UnavailableReason = solve.Message;
            return analysis;
        }

        analysis.MinimumMoves = solve.MinimumMoves;
        analysis.SolutionCountAtMinimum = solve.SolutionCountAtMinimum;
        analysis.NearSolutionsPlus1 = solve.NearPlus1Count;
        analysis.NearSolutionsPlus2 = solve.NearPlus2Count;
        analysis.Solvable = solve.Success && (data.MoveLimit <= 0 || solve.MinimumMoves <= data.MoveLimit);
        if (data.MoveLimit > 0 && solve.MinimumMoves >= 0)
        {
            analysis.MoveSlack = data.MoveLimit - solve.MinimumMoves;
        }
        return analysis;
    }

    private static void CopySolutions(LevelSolveResult solve, LevelAnalysisResult analysis)
    {
        analysis.OptimalPath.Clear();
        analysis.OptimalSolutions.Clear();
        analysis.StoredOptimalSolutionCount = solve.StoredOptimalSolutionCount;
        analysis.OptimalSolutionsCapped = solve.OptimalSolutionsCapped;
        for (int i = 0; i < solve.OptimalPath.Count; i++)
        {
            analysis.OptimalPath.Add(solve.OptimalPath[i]);
        }
        for (int i = 0; i < solve.OptimalSolutions.Count; i++)
        {
            List<LevelDir> src = solve.OptimalSolutions[i];
            List<LevelDir> copy = new List<LevelDir>(src.Count);
            for (int j = 0; j < src.Count; j++)
            {
                copy.Add(src[j]);
            }
            analysis.OptimalSolutions.Add(copy);
        }
    }

    private static void CountObjects(LevelData data, LevelObjectCounts counts)
    {
        List<LevelObjectData> objects = data.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            switch (objects[i].Type)
            {
                case LevelObjectType.Floor: counts.Floor++; break;
                case LevelObjectType.Wall: counts.Wall++; break;
                case LevelObjectType.Boundary: counts.Boundary++; break;
                case LevelObjectType.PlayerStart: counts.PlayerStart++; break;
                case LevelObjectType.Enemy: counts.Enemy++; break;
                case LevelObjectType.Rock: counts.Rock++; break;
                case LevelObjectType.Spike: counts.Spike++; break;
                case LevelObjectType.Key: counts.Key++; break;
                case LevelObjectType.Door: counts.Door++; break;
                case LevelObjectType.Goal: counts.Goal++; break;
                case LevelObjectType.RequiredZone: counts.RequiredZone++; break;
            }
        }
    }

    public static void DetectDeadlocks(LevelData data, List<LevelDeadlockInfo> deadlocks)
    {
        deadlocks.Clear();
        if (data == null)
        {
            return;
        }

        LevelLogic logic = new LevelLogic(data);
        LevelSimState state = logic.CreateInitialState(data);
        List<LevelObjectData> objects = data.Objects;

        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Enemy)
            {
                continue;
            }
            if (!logic.InBounds(obj.X, obj.Y))
            {
                continue;
            }
            int index = logic.ToIndex(obj.X, obj.Y);
            if (IsEnemyInUselessCorner(logic, ref state, index))
            {
                LevelDeadlockInfo info = new LevelDeadlockInfo();
                info.ObjectType = LevelObjectType.Enemy;
                info.Cell = new Vector2Int(obj.X, obj.Y);
                info.Reason = "Pushable enemy in unusable corner (cannot be pushed out; kick may be forced).";
                deadlocks.Add(info);
            }
        }

        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Rock)
            {
                continue;
            }
            if (!logic.InBounds(obj.X, obj.Y))
            {
                continue;
            }
            int index = logic.ToIndex(obj.X, obj.Y);
            if (IsRockInUselessCorner(logic, ref state, index))
            {
                LevelDeadlockInfo info = new LevelDeadlockInfo();
                info.ObjectType = LevelObjectType.Rock;
                info.Cell = new Vector2Int(obj.X, obj.Y);
                info.Reason = "Rock stuck in unusable corner (cannot be destroyed; may soft-lock).";
                deadlocks.Add(info);
            }
        }

        if (logic.GoalIndex >= 0 && logic.IsSpike(logic.GoalIndex))
        {
            LevelDeadlockInfo info = new LevelDeadlockInfo();
            info.ObjectType = LevelObjectType.Goal;
            info.Cell = data.Goal;
            info.Reason = "Goal is on a spike; winning still works, but spike move tax may apply on other entries.";
            deadlocks.Add(info);
        }

        if (logic.StartIndex >= 0 && logic.IsSpike(logic.StartIndex))
        {
            LevelDeadlockInfo info = new LevelDeadlockInfo();
            info.ObjectType = LevelObjectType.PlayerStart;
            info.Cell = data.PlayerStart;
            info.Reason = "Player starts on spike (extra move tax applies when entering spikes, not at spawn).";
            deadlocks.Add(info);
        }

        if (logic.GoalIndex >= 0)
        {
            bool goalSealed = logic.IsSolid(logic.GoalIndex) || !logic.IsFloor(logic.GoalIndex);
            if (goalSealed)
            {
                LevelDeadlockInfo info = new LevelDeadlockInfo();
                info.ObjectType = LevelObjectType.Goal;
                info.Cell = data.Goal;
                info.Reason = "Goal cell is solid or missing floor.";
                deadlocks.Add(info);
            }
        }

        int closedDoors = 0;
        for (int i = 0; i < logic.DoorCount; i++)
        {
            closedDoors++;
        }
        if (closedDoors > logic.KeyCount)
        {
            LevelDeadlockInfo info = new LevelDeadlockInfo();
            info.ObjectType = LevelObjectType.Door;
            info.Cell = new Vector2Int(-1, -1);
            info.Reason = "Not enough keys to open all doors.";
            deadlocks.Add(info);
        }
    }

    private static bool IsEnemyInUselessCorner(LevelLogic logic, ref LevelSimState state, int enemyIndex)
    {
        int x;
        int y;
        logic.FromIndex(enemyIndex, out x, out y);
        bool blockU = IsPushBlocked(logic, ref state, x, y + 1);
        bool blockD = IsPushBlocked(logic, ref state, x, y - 1);
        bool blockL = IsPushBlocked(logic, ref state, x - 1, y);
        bool blockR = IsPushBlocked(logic, ref state, x + 1, y);
        int blockedAxes = 0;
        if (blockU && blockD) blockedAxes++;
        if (blockL && blockR) blockedAxes++;
        if (blockU && blockL) return true;
        if (blockU && blockR) return true;
        if (blockD && blockL) return true;
        if (blockD && blockR) return true;
        return blockedAxes >= 1 && (blockU || blockD) && (blockL || blockR);
    }

    private static bool IsRockInUselessCorner(LevelLogic logic, ref LevelSimState state, int rockIndex)
    {
        int x;
        int y;
        logic.FromIndex(rockIndex, out x, out y);
        bool blockU = IsRockPushBlocked(logic, ref state, x, y + 1);
        bool blockD = IsRockPushBlocked(logic, ref state, x, y - 1);
        bool blockL = IsRockPushBlocked(logic, ref state, x - 1, y);
        bool blockR = IsRockPushBlocked(logic, ref state, x + 1, y);
        if (blockU && blockL) return true;
        if (blockU && blockR) return true;
        if (blockD && blockL) return true;
        if (blockD && blockR) return true;
        return false;
    }

    private static bool IsPushBlocked(LevelLogic logic, ref LevelSimState state, int x, int y)
    {
        if (!logic.InBounds(x, y))
        {
            return true;
        }
        int index = logic.ToIndex(x, y);
        if (logic.IsSolid(index) || !logic.IsFloor(index) || logic.IsSpike(index))
        {
            return true;
        }
        int door = logic.GetDoorIndex(index);
        if (door >= 0 && ((state.ClosedDoorMask & (1UL << door)) != 0UL))
        {
            return true;
        }
        if (LevelLogic.HasEnemy(ref state, index))
        {
            return true;
        }
        if (LevelLogic.HasRock(ref state, index))
        {
            return true;
        }
        return false;
    }

    private static bool IsRockPushBlocked(LevelLogic logic, ref LevelSimState state, int x, int y)
    {
        if (!logic.InBounds(x, y))
        {
            return true;
        }
        int index = logic.ToIndex(x, y);
        if (logic.IsSolid(index) || !logic.IsFloor(index))
        {
            return true;
        }
        int door = logic.GetDoorIndex(index);
        if (door >= 0 && ((state.ClosedDoorMask & (1UL << door)) != 0UL))
        {
            return true;
        }
        if (LevelLogic.HasEnemy(ref state, index) || LevelLogic.HasRock(ref state, index))
        {
            return true;
        }
        return false;
    }
}
