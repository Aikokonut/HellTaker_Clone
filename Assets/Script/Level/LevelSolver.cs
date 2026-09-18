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
}

public static class LevelSolver
{
    private const int DefaultMaxStates = 500000;
    private const int DefaultMaxDepthPad = 64;
    public const int DefaultMaxStoredSolutions = 32;
    private const int MaxPathCountEnumerate = 1000000;

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

        List<LevelDir> path = new List<LevelDir>(32);
        HashSet<long> visitedOnPath = new HashSet<long>();
        long startKey = logic.PackStateKey(ref initial);
        visitedOnPath.Add(startKey);

        int totalSolutions = 0;
        int minMoves = -1;
        int expansions = 0;
        bool countCapped = false;
        LevelDir[] dirs = { LevelDir.Up, LevelDir.Right, LevelDir.Down, LevelDir.Left };

        Search(
            logic,
            initial,
            path,
            visitedOnPath,
            dirs,
            moveLimit,
            maxStates,
            maxStoredSolutions,
            result,
            ref expansions,
            ref totalSolutions,
            ref minMoves,
            ref countCapped);

        result.MinimumMoves = minMoves;
        result.StoredOptimalSolutionCount = result.OptimalSolutions.Count;
        if (countCapped || result.StateLimitReached)
        {
            result.OptimalSolutionsCapped = true;
            result.SolutionCountAtMinimum = countCapped ? -1 : totalSolutions;
            if (result.StateLimitReached)
            {
                result.TimedOut = totalSolutions <= 0;
                result.Message = totalSolutions > 0
                    ? "Partial: state expansion limit reached."
                    : "No solution found before state limit.";
            }
            else
            {
                result.Message = "Solution count capped.";
            }
        }
        else
        {
            result.SolutionCountAtMinimum = totalSolutions;
            result.Message = totalSolutions > 0 ? "Solved." : "No solution within MoveLimit.";
        }

        if (result.OptimalSolutions.Count > 0)
        {
            CopyPath(result.OptimalSolutions[0], result.OptimalPath);
        }

        result.Success = totalSolutions > 0;
        if (!result.Success && string.IsNullOrEmpty(result.Message))
        {
            result.Message = "No solution within MoveLimit.";
        }
        else if (result.Success && !result.StateLimitReached && !countCapped)
        {
            result.Message = "Solved.";
        }

        return result;
    }

    private static void Search(
        LevelLogic logic,
        LevelSimState state,
        List<LevelDir> path,
        HashSet<long> visitedOnPath,
        LevelDir[] dirs,
        int moveLimit,
        int maxStates,
        int maxStoredSolutions,
        LevelSolveResult result,
        ref int expansions,
        ref int totalSolutions,
        ref int minMoves,
        ref bool countCapped)
    {
        if (result.StateLimitReached || countCapped)
        {
            return;
        }
        if (expansions >= maxStates)
        {
            result.StateLimitReached = true;
            return;
        }
        expansions++;

        if (state.Won)
        {
            if (state.MovesUsed <= moveLimit)
            {
                RecordSolution(path, state.MovesUsed, maxStoredSolutions, result, ref totalSolutions, ref minMoves, ref countCapped);
            }
            return;
        }

        if (state.Dead)
        {
            return;
        }
        if (state.MovesUsed >= moveLimit)
        {
            return;
        }

        for (int d = 0; d < 4; d++)
        {
            if (result.StateLimitReached || countCapped)
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

            long key = logic.PackStateKey(ref nextState);
            if (visitedOnPath.Contains(key))
            {
                continue;
            }

            path.Add(dirs[d]);
            visitedOnPath.Add(key);
            Search(
                logic,
                nextState,
                path,
                visitedOnPath,
                dirs,
                moveLimit,
                maxStates,
                maxStoredSolutions,
                result,
                ref expansions,
                ref totalSolutions,
                ref minMoves,
                ref countCapped);
            visitedOnPath.Remove(key);
            path.RemoveAt(path.Count - 1);
        }
    }

    private static void RecordSolution(
        List<LevelDir> path,
        int movesUsed,
        int maxStoredSolutions,
        LevelSolveResult result,
        ref int totalSolutions,
        ref int minMoves,
        ref bool countCapped)
    {
        totalSolutions++;
        if (minMoves < 0 || movesUsed < minMoves)
        {
            minMoves = movesUsed;
        }

        if (result.OptimalSolutions.Count < maxStoredSolutions)
        {
            List<LevelDir> copy = new List<LevelDir>(path.Count);
            for (int i = 0; i < path.Count; i++)
            {
                copy.Add(path[i]);
            }
            result.OptimalSolutions.Add(copy);
        }
        else
        {
            result.OptimalSolutionsCapped = true;
        }

        if (totalSolutions >= MaxPathCountEnumerate)
        {
            countCapped = true;
            result.OptimalSolutionsCapped = true;
        }
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
