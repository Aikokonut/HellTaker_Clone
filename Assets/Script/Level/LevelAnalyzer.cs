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

public static class LevelAnalyzer
{
    public static int MaxStoredSolutions = LevelSolver.DefaultMaxStoredSolutions;

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

        LevelSolveResult solve = LevelSolver.Solve(data, 500000, true, maxStoredSolutions);
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
