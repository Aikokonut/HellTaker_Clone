using System.Collections.Generic;
using UnityEngine;

public sealed class LevelGenerateParams
{
    public LevelData BaseMap;
    public int Width = 8;
    public int Height = 8;
    public int EnemyCount = 2;
    public int RockCount = 2;
    public int SpikeCount = 3;
    public int MaxAttempts = 24;
    public int Seed = 0;
    public int MaxCandidates = 3;
    public int CostSlack = 12;
    public int MaxTargetSolutions = 1;
    public int StateLimit = 100000;
    public int TimeLimitMs = 500;
}

public sealed class LevelSolutionBucket
{
    public int Moves;
    public int Count;
}

public sealed class LevelCandidateInfo
{
    public LevelData Level;
    public bool IsBest;
    public int OptimalCost = -1;
    public int MoveLimit;
    public int MoveSlack = int.MinValue;
    public int SolutionCount = -1;
    public int NearMissCost = -1;
    public int NearMissCount = -1;
    public int DecisionPoints = -1;
    public int DependencyDepth = -1;
    public int ObjectImpactCount = -1;
    public int RouteDiversity = -1;
    public int Score;
    public string Summary;
}

public sealed class LevelGenerateResult
{
    public bool Success;
    public LevelData Level;
    public int AttemptsUsed;
    public string Message;
    public readonly List<LevelCandidateInfo> Candidates = new List<LevelCandidateInfo>();

    public int OptimalCost = -1;
    public int MoveSlack = int.MinValue;
    public int SolutionCount = -1;
    public int NearMissCount = -1;
    public int NearMissCost = -1;
    public int DependencyDepth = -1;
    public int DecisionPoints = -1;
    public int EstimatedDifficulty = -1;
    public int BaseShortest = -1;
    public int ObjectImpactCount = -1;
    public int RouteDiversity = -1;
    public string ChainSummary;
    public readonly List<LevelSolutionBucket> SolutionsByMoves = new List<LevelSolutionBucket>();
    public readonly List<LevelDeadlockInfo> Deadlocks = new List<LevelDeadlockInfo>();

    public int TotalSolutions { get { return SolutionCount; } set { SolutionCount = value; } }
    public int MinimumMoves { get { return OptimalCost; } set { OptimalCost = value; } }
    public int PathExtra = -1;
    public int ActionTax = -1;
    public string PatternUsed;
}

/// <summary>
/// Path-first + optional trap polish (gate/bait). Fail-fast on repeated errors; attempts capped.
/// </summary>
public static class LevelRouteGenerator
{
    public static LevelGenerateResult Generate(LevelGenerateParams genParams)
    {
        LevelGenerateResult result = new LevelGenerateResult();
        if (genParams == null || genParams.BaseMap == null)
        {
            result.Message = "Generation Failed: base map required.";
            return result;
        }
        if (genParams.EnemyCount < 0 || genParams.RockCount < 0 || genParams.SpikeCount < 0)
        {
            result.Message = "Generation Failed: object counts cannot be negative.";
            return result;
        }
        if (genParams.MaxAttempts < 1)
        {
            result.Message = "Generation Failed: MaxAttempts must be >= 1.";
            return result;
        }

        int costSlack = genParams.CostSlack;
        if (costSlack < 0)
        {
            costSlack = 0;
        }

        LevelData baseCopy;
        bool[] protectedCell;
        string baseError;
        if (!TryCreatePreservedBase(genParams.BaseMap, out baseCopy, out protectedCell, out baseError))
        {
            result.Message = "Generation Failed: " + baseError;
            return result;
        }

        StripGeneratedTypes(baseCopy, protectedCell);
        baseCopy.SyncDerivedFields();

        Topology topo;
        string topoError;
        if (!TryBuildTopology(baseCopy, out topo, out topoError))
        {
            Object.DestroyImmediate(baseCopy);
            result.Message = "Generation Failed: " + topoError;
            return result;
        }

        SearchBudget budget = new SearchBudget(genParams.StateLimit, genParams.TimeLimitMs);
        CountResult baseSolve = CountSolutions(baseCopy, 4, budget, 0);
        if (baseSolve.MinimumMoves < 0
            || (baseSolve.Status != CountStatus.Ok && baseSolve.Status != CountStatus.Inconclusive))
        {
            Object.DestroyImmediate(baseCopy);
            result.Message = "Generation Failed: base map must be solvable before placing objects.";
            return result;
        }

        int baseCost = baseSolve.MinimumMoves;
        int objectSlack = genParams.SpikeCount + genParams.RockCount * 3 + genParams.EnemyCount * 3;
        if (costSlack < objectSlack)
        {
            costSlack = objectSlack;
        }
        int costCap = baseCost + costSlack;
        result.BaseShortest = topo.ShortestLen;

        if (topo.Choke.Count + topo.SoftBridge.Count < 1)
        {
            Object.DestroyImmediate(baseCopy);
            result.Message = "Generation Failed: base has no hard/soft choke. Narrow corridors, then retry.";
            return result;
        }

        System.Random rng = genParams.Seed != 0
            ? new System.Random(genParams.Seed)
            : new System.Random();

        List<List<int>> skeletons = BuildRouteSkeletons(topo, rng);
        if (skeletons.Count == 0)
        {
            Object.DestroyImmediate(baseCopy);
            result.Message = "Generation Failed: no Start→Goal route skeletons.";
            return result;
        }

        string lastError = "No valid candidate.";
        int maxKeep = genParams.MaxCandidates;
        if (maxKeep < 1)
        {
            maxKeep = 1;
        }

        List<LevelCandidateInfo> found = new List<LevelCandidateInfo>();
        int attempt = 0;
        int skelIndex = 0;
        int sameErrorStreak = 0;
        string streakError = null;
        int maxAttempts = genParams.MaxAttempts;
        if (maxAttempts > 60)
        {
            maxAttempts = 60;
        }

        while (attempt < maxAttempts)
        {
            attempt++;
            result.AttemptsUsed = attempt;
            if (found.Count >= maxKeep)
            {
                break;
            }
            List<int> skeleton = skeletons[skelIndex % skeletons.Count];
            skelIndex++;

            LevelData candidate = baseCopy.CreateRuntimeCopy();
            StripGeneratedTypes(candidate, protectedCell);

            PlacementState place = new PlacementState(topo.CellCount);
            SeedUsed(candidate, place, protectedCell);
            TrimHandPlacedToTargets(candidate, place, protectedCell, genParams);

            CountResult built;
            string placeError;
            if (!PathFirstPlace(
                candidate, topo, skeleton, genParams, rng, place, budget, costCap, baseCost,
                protectedCell, out built, out placeError))
            {
                Object.DestroyImmediate(candidate);
                lastError = placeError;
                if (FailFastStreak(ref sameErrorStreak, ref streakError, lastError, 8))
                {
                    break;
                }
                continue;
            }

            if (!CountsExact(candidate, genParams, out placeError))
            {
                Object.DestroyImmediate(candidate);
                lastError = placeError;
                if (FailFastStreak(ref sameErrorStreak, ref streakError, lastError, 6))
                {
                    break;
                }
                continue;
            }

            LevelValidationResult v = LevelValidator.Validate(candidate);
            if (!v.IsValid)
            {
                Object.DestroyImmediate(candidate);
                lastError = v.Issues.Count > 0 ? v.Issues[0].Message : "invalid";
                if (FailFastStreak(ref sameErrorStreak, ref streakError, lastError, 8))
                {
                    break;
                }
                continue;
            }

            int filterCap = costCap;
            if (built.MinimumMoves > filterCap) filterCap = built.MinimumMoves;

            int maxSols = genParams.MaxTargetSolutions;
            if (maxSols < 1) maxSols = 1;
            if (maxSols > 2) maxSols = 2;

            CountResult verified = CountSolutionsAccept(candidate, budget, filterCap, maxSols);
            if (!AcceptSolCount(verified, maxSols))
            {
                TryCutAlternateRoutes(
                    candidate, topo, protectedCell, budget, filterCap, maxSols, rng, ref verified);
            }
            if (!AcceptSolCount(verified, maxSols))
            {
                Object.DestroyImmediate(candidate);
                lastError = "SolutionCount "
                    + (verified.Total > 0 ? verified.Total.ToString() : "?")
                    + " > MaxTargetSolutions " + maxSols
                    + " (strict). Narrow corridors / add Rock on soft-choke.";
                if (FailFastStreak(ref sameErrorStreak, ref streakError, lastError, 8))
                {
                    break;
                }
                continue;
            }
            built = verified;
            filterCap = built.MinimumMoves;

            string useError;
            if (!AllObjectsUsedOnOptPaths(candidate, budget, filterCap, maxSols, out useError))
            {
                TryRelocateUnusedOntoPath(candidate, topo, built.SamplePath, protectedCell, rng);
                if (!AllObjectsUsedOnOptPaths(candidate, budget, filterCap, maxSols, out useError))
                {
                    Object.DestroyImmediate(candidate);
                    lastError = "Unused: " + useError;
                    if (FailFastStreak(ref sameErrorStreak, ref streakError, lastError, 8))
                    {
                        break;
                    }
                    continue;
                }
                verified = CountSolutionsAccept(candidate, budget, filterCap, maxSols);
                if (!AcceptSolCount(verified, maxSols))
                {
                    Object.DestroyImmediate(candidate);
                    lastError = "SolutionCount broke after relocate.";
                    continue;
                }
                built = verified;
                filterCap = built.MinimumMoves;
            }

            if (!EachPlacedObjectContributes(candidate, built.SamplePath, protectedCell, out useError))
            {
                Object.DestroyImmediate(candidate);
                lastError = "Path: " + useError;
                if (FailFastStreak(ref sameErrorStreak, ref streakError, lastError, 8))
                {
                    break;
                }
                continue;
            }

            int impactCount;
            string impactError;
            if (!AllGeneratedHaveImpact(candidate, protectedCell, budget, filterCap, out impactCount, out impactError))
            {
                Object.DestroyImmediate(candidate);
                lastError = "Impact: " + impactError;
                if (FailFastStreak(ref sameErrorStreak, ref streakError, lastError, 8))
                {
                    break;
                }
                continue;
            }

            int minTax = genParams.SpikeCount + genParams.RockCount * 2 + genParams.EnemyCount * 2;
            if (minTax > 0 && built.MinimumMoves < baseCost + 1)
            {
                Object.DestroyImmediate(candidate);
                lastError = "Too easy: opt=" + built.MinimumMoves + " base=" + baseCost
                    + " (objects must raise cost).";
                if (FailFastStreak(ref sameErrorStreak, ref streakError, lastError, 8))
                {
                    break;
                }
                continue;
            }

            if (IsDuplicateLayout(found, candidate))
            {
                Object.DestroyImmediate(candidate);
                continue;
            }

            sameErrorStreak = 0;
            streakError = null;

            int spikesOnPath;
            if (!EnoughSpikesOnPath(candidate, built.SamplePath, protectedCell, out spikesOnPath, out impactError))
            {
                Object.DestroyImmediate(candidate);
                lastError = "Spike: " + impactError;
                if (FailFastStreak(ref sameErrorStreak, ref streakError, lastError, 8))
                {
                    break;
                }
                continue;
            }

            int chokeHits = CountObjectsOnChokes(candidate, topo);

            int gateCount = 0;
            int nearCount = 0;
            int nearCost = -1;
            PolishTrapBestEffort(
                candidate, topo, protectedCell, budget, filterCap, rng,
                ref built, out gateCount, out nearCount, out nearCost);

            verified = CountSolutionsAccept(candidate, budget, filterCap, maxSols);
            if (!AcceptSolCount(verified, maxSols))
            {
                Object.DestroyImmediate(candidate);
                lastError = "SolutionCount after polish > MaxTargetSolutions " + maxSols + ".";
                continue;
            }
            built = verified;

            if (!AllObjectsUsedOnOptPaths(candidate, budget, built.MinimumMoves, maxSols, out useError))
            {
                Object.DestroyImmediate(candidate);
                lastError = "Unused after polish: " + useError;
                continue;
            }

            int depth = MeasureDependencyDepth(candidate, built.SamplePath);
            int decisions = CountDecisionPoints(candidate, built.SamplePath);
            int diversity = EstimateRouteDiversity(topo, skeleton);
            int score = ScoreCandidate(
                built.MinimumMoves, 0, built.Total, depth, decisions,
                nearCount, nearCost, impactCount, diversity, 0);
            score += (built.MinimumMoves - baseCost) * 8;
            score += depth * 15;
            score += chokeHits * 20;
            score += spikesOnPath * 6;
            score += gateCount * 50;
            if (nearCost == built.MinimumMoves + 1) score += 80;
            else if (nearCost > built.MinimumMoves) score += 20;
            if (built.Total == 1) score += 100;
            else if (built.Total == 2) score += 25;
            else score -= (built.Total - 2) * 20;

            LevelCandidateInfo info = new LevelCandidateInfo();
            info.Level = candidate;
            candidate.MoveLimit = built.MinimumMoves;
            info.OptimalCost = built.MinimumMoves;
            info.MoveLimit = built.MinimumMoves;
            info.MoveSlack = 0;
            info.SolutionCount = built.Total;
            info.NearMissCost = nearCost;
            info.NearMissCount = nearCount;
            info.DecisionPoints = decisions;
            info.DependencyDepth = depth;
            info.ObjectImpactCount = impactCount;
            info.RouteDiversity = diversity;
            info.Score = score;
            info.Summary = "opt=" + built.MinimumMoves + " sols=" + built.Total
                + " gates=" + gateCount + " bait=" + nearCost
                + " impact=" + impactCount + " choke=" + chokeHits;
            InsertCandidate(found, info, maxKeep);
        }

        if (found.Count == 0)
        {
            Object.DestroyImmediate(baseCopy);
            result.Message = "Generation Failed after " + result.AttemptsUsed
                + " attempt(s). Last: " + lastError
                + (sameErrorStreak >= 3 ? " [fail-fast: same error repeated]" : "")
                + " Tip: narrow corridors, MaxTarget=1, Rock/Enemy>=1. Trap/bait is optional polish now.";
            return result;
        }

        for (int i = 0; i < found.Count; i++)
        {
            found[i].IsBest = (i == 0);
            result.Candidates.Add(found[i]);
        }

        LevelCandidateInfo best = found[0];
        result.Success = true;
        result.Level = best.Level;
        result.OptimalCost = best.OptimalCost;
        result.MoveSlack = best.MoveSlack;
        result.SolutionCount = best.SolutionCount;
        result.NearMissCost = best.NearMissCost;
        result.NearMissCount = best.NearMissCount;
        result.DependencyDepth = best.DependencyDepth;
        result.DecisionPoints = best.DecisionPoints;
        result.ObjectImpactCount = best.ObjectImpactCount;
        result.RouteDiversity = best.RouteDiversity;
        result.PathExtra = best.OptimalCost - baseCost;
        result.PatternUsed = "PathFirstTrap";
        result.ChainSummary = best.Summary;
        result.EstimatedDifficulty = EstimateDifficulty(best, best.OptimalCost);
        result.Deadlocks.Clear();
        LevelAnalyzer.DetectDeadlocks(best.Level, result.Deadlocks);
        result.Message = "Path-first generated " + found.Count + " candidate(s). Best: " + best.Summary
            + " (baseCost=" + baseCost + ", cap=" + costCap + ").";

        Object.DestroyImmediate(baseCopy);
        return result;
    }

    private static void InsertCandidate(List<LevelCandidateInfo> found, LevelCandidateInfo info, int maxKeep)
    {
        int insertAt = found.Count;
        for (int i = 0; i < found.Count; i++)
        {
            if (info.Score > found[i].Score)
            {
                insertAt = i;
                break;
            }
        }
        found.Insert(insertAt, info);
        while (found.Count > maxKeep)
        {
            LevelCandidateInfo drop = found[found.Count - 1];
            found.RemoveAt(found.Count - 1);
            if (drop.Level != null)
            {
                Object.DestroyImmediate(drop.Level);
            }
        }
    }

    private static bool IsDuplicateLayout(List<LevelCandidateInfo> found, LevelData level)
    {
        for (int i = 0; i < found.Count; i++)
        {
            if (SamePuzzleLayout(found[i].Level, level))
            {
                return true;
            }
        }
        return false;
    }

    private static bool SamePuzzleLayout(LevelData a, LevelData b)
    {
        if (a == null || b == null) return false;
        if (a.Width != b.Width || a.Height != b.Height) return false;
        if (a.MoveLimit != b.MoveLimit) return false;
        List<LevelObjectData> ao = a.Objects;
        List<LevelObjectData> bo = b.Objects;
        int ae = 0, ar = 0, as_ = 0, be = 0, br = 0, bs = 0;
        for (int i = 0; i < ao.Count; i++)
        {
            if (ao[i].Type == LevelObjectType.Enemy) ae++;
            else if (ao[i].Type == LevelObjectType.Rock) ar++;
            else if (ao[i].Type == LevelObjectType.Spike) as_++;
        }
        for (int i = 0; i < bo.Count; i++)
        {
            if (bo[i].Type == LevelObjectType.Enemy) be++;
            else if (bo[i].Type == LevelObjectType.Rock) br++;
            else if (bo[i].Type == LevelObjectType.Spike) bs++;
        }
        if (ae != be || ar != br || as_ != bs) return false;

        int cells = a.Width * a.Height;
        int[] maskA = new int[cells];
        int[] maskB = new int[cells];
        for (int i = 0; i < ao.Count; i++)
        {
            LevelObjectData obj = ao[i];
            if (obj.Type != LevelObjectType.Enemy && obj.Type != LevelObjectType.Rock
                && obj.Type != LevelObjectType.Spike)
            {
                continue;
            }
            int c = obj.Y * a.Width + obj.X;
            if (c < 0 || c >= cells) continue;
            maskA[c] |= PuzzleTypeBit(obj.Type);
        }
        for (int i = 0; i < bo.Count; i++)
        {
            LevelObjectData obj = bo[i];
            if (obj.Type != LevelObjectType.Enemy && obj.Type != LevelObjectType.Rock
                && obj.Type != LevelObjectType.Spike)
            {
                continue;
            }
            int c = obj.Y * b.Width + obj.X;
            if (c < 0 || c >= cells) continue;
            maskB[c] |= PuzzleTypeBit(obj.Type);
        }
        for (int i = 0; i < cells; i++)
        {
            if (maskA[i] != maskB[i]) return false;
        }
        return true;
    }

    private static int PuzzleTypeBit(LevelObjectType type)
    {
        if (type == LevelObjectType.Enemy) return 1;
        if (type == LevelObjectType.Rock) return 2;
        if (type == LevelObjectType.Spike) return 4;
        return 0;
    }

    private static int ScoreCandidate(
        int optimal, int slack, int solutions, int depth, int decisions,
        int nearCount, int nearCost, int impact, int diversity, int moveLimit)
    {
        int score = 100 + impact * 30 + depth * 20 + diversity * 5 + optimal;
        if (moveLimit > 0)
        {
            int preferMin = (moveLimit * 7) / 10;
            if (optimal >= preferMin) score += 40;
            if (optimal >= moveLimit - 2) score += 35;
            score -= slack * 8;
        }
        if (nearCount > 0)
        {
            score += 35;
            if (nearCost == optimal + 1) score += 40;
            else if (moveLimit > 0 && nearCost == moveLimit + 1) score += 40;
        }
        if (solutions == 1) score += 60;
        else if (solutions == 2) score += 15;
        else if (solutions > 2) score -= (solutions - 2) * 25;
        if (decisions >= 2) score += 10;
        return score;
    }

    private static int EstimateDifficulty(LevelCandidateInfo c, int moveLimit)
    {
        int score = 1;
        if (c.MoveSlack >= 0 && c.MoveSlack <= 2) score++;
        if (c.DependencyDepth >= 2) score++;
        if (c.DecisionPoints >= 2) score++;
        if (c.NearMissCount > 0) score++;
        if (c.ObjectImpactCount >= 3) score++;
        if (moveLimit > 0 && c.OptimalCost * 10 >= moveLimit * 8) score++;
        if (c.SolutionCount == 1) score++;
        if (score > 10) score = 10;
        if (score < 1) score = 1;
        return score;
    }

    // -------------------------------------------------------------------------
    // Base / topology
    // -------------------------------------------------------------------------

    private static bool TryCreatePreservedBase(
        LevelData source, out LevelData copy, out bool[] protectedCell, out string error)
    {
        copy = null;
        protectedCell = null;
        error = null;
        LevelData working = source.CreateRuntimeCopy();
        working.MoveLimit = source.MoveLimit;
        working.SyncDerivedFields();
        LevelValidationResult v = LevelValidator.Validate(working);
        if (!v.IsValid)
        {
            Object.DestroyImmediate(working);
            error = v.Issues.Count > 0 ? v.Issues[0].Message : "invalid base";
            return false;
        }
        if (working.PlayerStart.x < 0 || working.Goal.x < 0)
        {
            Object.DestroyImmediate(working);
            error = "Base needs PlayerStart and Goal.";
            return false;
        }
        int cells = working.Width * working.Height;
        protectedCell = new bool[cells];
        List<LevelObjectData> objects = working.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type == LevelObjectType.Floor || obj.Type == LevelObjectType.RequiredZone)
            {
                continue;
            }
            if (obj.Type == LevelObjectType.Enemy || obj.Type == LevelObjectType.Rock
                || obj.Type == LevelObjectType.Spike)
            {
                continue;
            }
            if (obj.X < 0 || obj.Y < 0 || obj.X >= working.Width || obj.Y >= working.Height)
            {
                continue;
            }
            protectedCell[obj.Y * working.Width + obj.X] = true;
        }
        working.SyncDerivedFields();
        copy = working;
        return true;
    }

    private static void StripGeneratedTypes(LevelData level, bool[] protectedCell)
    {
        List<LevelObjectData> objects = level.Objects;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Enemy && obj.Type != LevelObjectType.Rock
                && obj.Type != LevelObjectType.Spike)
            {
                continue;
            }
            int cell = obj.Y * level.Width + obj.X;
            if (protectedCell != null && cell >= 0 && cell < protectedCell.Length && protectedCell[cell])
            {
                continue;
            }
            objects.RemoveAt(i);
        }
        level.SyncDerivedFields();
    }

    private static void TrimHandPlacedToTargets(
        LevelData level, PlacementState place, bool[] protectedCell, LevelGenerateParams p)
    {
        TrimTypeToTarget(level, place, protectedCell, LevelObjectType.Enemy, p.EnemyCount);
        TrimTypeToTarget(level, place, protectedCell, LevelObjectType.Rock, p.RockCount);
        TrimTypeToTarget(level, place, protectedCell, LevelObjectType.Spike, p.SpikeCount);
        place.Enemy = 0;
        place.Rock = 0;
        place.Spike = 0;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectType t = objects[i].Type;
            if (t == LevelObjectType.Enemy) place.Enemy++;
            else if (t == LevelObjectType.Rock) place.Rock++;
            else if (t == LevelObjectType.Spike) place.Spike++;
        }
        level.SyncDerivedFields();
    }

    private static void TrimTypeToTarget(
        LevelData level, PlacementState place, bool[] protectedCell,
        LevelObjectType type, int target)
    {
        int count = 0;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Type == type) count++;
        }
        if (count <= target) return;

        for (int i = objects.Count - 1; i >= 0 && count > target; i--)
        {
            if (objects[i].Type != type) continue;
            int cell = objects[i].Y * level.Width + objects[i].X;
            if (protectedCell != null && cell >= 0 && cell < protectedCell.Length)
            {
                protectedCell[cell] = false;
            }
            if (cell >= 0 && cell < place.Used.Length)
            {
                place.Used[cell] = false;
            }
            objects.RemoveAt(i);
            count--;
        }
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
        public readonly List<int> Shortest = new List<int>();
        public readonly List<int> Choke = new List<int>();
        public readonly List<int> SoftBridge = new List<int>();
        public readonly List<int> Intersection = new List<int>();
        public readonly List<int> DeadEnd = new List<int>();
        public readonly List<int> GateCells = new List<int>();
        public readonly List<List<int>> Zones = new List<List<int>>();
        public readonly List<int> ZoneRep = new List<int>();
        public int ShortestLen = -1;
        public LevelLogic Logic;
    }

    private sealed class PlacementState
    {
        public bool[] Used;
        public int Enemy;
        public int Rock;
        public int Spike;

        public PlacementState(int cells)
        {
            Used = new bool[cells];
        }
    }

    private static void SeedUsed(LevelData level, PlacementState place, bool[] protectedCell)
    {
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type == LevelObjectType.Floor || obj.Type == LevelObjectType.RequiredZone)
            {
                continue;
            }
            if (obj.X < 0 || obj.Y < 0 || obj.X >= level.Width || obj.Y >= level.Height)
            {
                continue;
            }
            int cell = obj.Y * level.Width + obj.X;
            place.Used[cell] = true;
            if (obj.Type == LevelObjectType.Enemy) place.Enemy++;
            else if (obj.Type == LevelObjectType.Rock) place.Rock++;
            else if (obj.Type == LevelObjectType.Spike) place.Spike++;
        }
        if (protectedCell != null)
        {
            int n = place.Used.Length < protectedCell.Length ? place.Used.Length : protectedCell.Length;
            for (int i = 0; i < n; i++)
            {
                if (protectedCell[i]) place.Used[i] = true;
            }
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
        }
        if (topo.Start < 0 || topo.Goal < 0)
        {
            error = "Missing Start/Goal.";
            return false;
        }
        Bfs(logic, topo.Walkable, topo.Start, topo.DistStart);
        Bfs(logic, topo.Walkable, topo.Goal, topo.DistGoal);
        if (topo.DistStart[topo.Goal] < 0)
        {
            error = "Goal unreachable.";
            return false;
        }
        topo.ShortestLen = topo.DistStart[topo.Goal];

        for (int i = 0; i < topo.CellCount; i++)
        {
            if (!topo.Walkable[i] || i == topo.Start || i == topo.Goal) continue;
            if (topo.DistStart[i] >= 0 && topo.DistGoal[i] >= 0
                && topo.DistStart[i] + topo.DistGoal[i] == topo.ShortestLen)
            {
                topo.Shortest.Add(i);
            }
            int deg = Degree(topo, i);
            if (deg >= 3) topo.Intersection.Add(i);
            if (deg == 1) topo.DeadEnd.Add(i);
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
            if (dist[topo.Goal] < 0) topo.Choke.Add(cell);
            else if (dist[topo.Goal] >= topo.ShortestLen + 2) topo.SoftBridge.Add(cell);
        }

        for (int i = 0; i < topo.CellCount; i++)
        {
            if (!topo.Walkable[i] || i == topo.Start || i == topo.Goal) continue;
            if (IsOnList(topo.Choke, i) || IsOnList(topo.SoftBridge, i)) continue;
            if (IsBridgeCell(topo, i)) topo.SoftBridge.Add(i);
        }

        List<LevelObjectData> mapObjs = map.Objects;
        for (int i = 0; i < mapObjs.Count; i++)
        {
            LevelObjectData obj = mapObjs[i];
            if (obj.Type != LevelObjectType.Enemy && obj.Type != LevelObjectType.Rock)
            {
                continue;
            }
            if (obj.X < 0 || obj.Y < 0 || obj.X >= map.Width || obj.Y >= map.Height) continue;
            int gi = obj.Y * map.Width + obj.X;
            if (gi == topo.Start || gi == topo.Goal) continue;
            if (!IsOnList(topo.GateCells, gi)) topo.GateCells.Add(gi);
        }

        // Zones from LevelLogic connected components (optional soft waypoints only).
        int zc = logic.ZoneCount;
        for (int z = 0; z < zc; z++)
        {
            List<int> cells = new List<int>();
            for (int i = 0; i < topo.CellCount; i++)
            {
                if (logic.GetZoneIndex(i) == z) cells.Add(i);
            }
            if (cells.Count > 0)
            {
                topo.Zones.Add(cells);
                topo.ZoneRep.Add(PickRep(topo, cells));
            }
        }
        return true;
    }

    private static int PickRep(Topology topo, List<int> cells)
    {
        int best = cells[0];
        int bestScore = int.MinValue;
        for (int i = 0; i < cells.Count; i++)
        {
            int c = cells[i];
            int score = 0;
            if (IsOnList(topo.Intersection, c)) score += 3;
            if (IsOnList(topo.Choke, c)) score += 2;
            if (topo.DistStart[c] >= 0) score += 1;
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best;
    }

    private static bool ZoneReachable(Topology topo, List<int> zone)
    {
        for (int i = 0; i < zone.Count; i++)
        {
            if (topo.DistStart[zone[i]] >= 0) return true;
        }
        return false;
    }

    private static int Degree(Topology topo, int cell)
    {
        int x, y;
        topo.Logic.FromIndex(cell, out x, out y);
        int d = 0;
        if (Walk(topo, x + 1, y)) d++;
        if (Walk(topo, x - 1, y)) d++;
        if (Walk(topo, x, y + 1)) d++;
        if (Walk(topo, x, y - 1)) d++;
        return d;
    }

    private static bool Walk(Topology topo, int x, int y)
    {
        if (!topo.Logic.InBounds(x, y)) return false;
        return topo.Walkable[topo.Logic.ToIndex(x, y)];
    }

    // -------------------------------------------------------------------------
    // Route skeletons
    // -------------------------------------------------------------------------

    private static List<List<int>> BuildRouteSkeletons(Topology topo, System.Random rng)
    {
        List<List<int>> skeletons = new List<List<int>>();

        List<int> direct = new List<int>();
        if (BuildPath(topo, topo.Walkable, topo.Start, topo.Goal, direct, false))
        {
            skeletons.Add(direct);
        }
        List<int> biased = new List<int>();
        if (BuildPath(topo, topo.Walkable, topo.Start, topo.Goal, biased, true)
            && !SamePath(direct, biased))
        {
            skeletons.Add(biased);
        }
        if (topo.Choke.Count > 0)
        {
            List<int> via = new List<int>();
            via.Add(topo.Start);
            via.Add(topo.Choke[rng.Next(0, topo.Choke.Count)]);
            via.Add(topo.Goal);
            List<int> p = ConnectWaypoints(topo, via, true);
            if (p != null) skeletons.Add(p);
        }
        if (topo.SoftBridge.Count > 0)
        {
            List<int> via = new List<int>();
            via.Add(topo.Start);
            via.Add(topo.SoftBridge[rng.Next(0, topo.SoftBridge.Count)]);
            via.Add(topo.Goal);
            List<int> p = ConnectWaypoints(topo, via, true);
            if (p != null) skeletons.Add(p);
        }
        if (topo.Intersection.Count > 0)
        {
            List<int> via = new List<int>();
            via.Add(topo.Start);
            via.Add(topo.Intersection[rng.Next(0, topo.Intersection.Count)]);
            via.Add(topo.Goal);
            List<int> p = ConnectWaypoints(topo, via, true);
            if (p != null) skeletons.Add(p);
        }
        for (int g = 0; g < topo.GateCells.Count; g++)
        {
            List<int> via = new List<int>();
            via.Add(topo.Start);
            via.Add(topo.GateCells[g]);
            via.Add(topo.Goal);
            List<int> p = ConnectWaypoints(topo, via, true);
            if (p != null) skeletons.Add(p);
        }

        // RequiredZone is soft: extra skeletons only, never required.
        if (topo.ZoneRep.Count > 0)
        {
            List<int[]> orders = BuildZoneOrders(topo.ZoneRep.Count, rng);
            int maxOrders = orders.Count;
            if (maxOrders > 4) maxOrders = 4;
            for (int o = 0; o < maxOrders; o++)
            {
                int[] order = orders[o];
                List<int> waypoints = new List<int>();
                waypoints.Add(topo.Start);
                for (int i = 0; i < order.Length; i++)
                {
                    int rep = topo.ZoneRep[order[i]];
                    if (topo.DistStart[rep] < 0) continue;
                    waypoints.Add(rep);
                }
                waypoints.Add(topo.Goal);
                if (waypoints.Count < 2) continue;
                List<int> path = ConnectWaypoints(topo, waypoints, false);
                if (path != null && path.Count > 0) skeletons.Add(path);
            }
        }
        return skeletons;
    }

    private static List<int[]> BuildZoneOrders(int n, System.Random rng)
    {
        List<int[]> orders = new List<int[]>();
        if (n <= 0)
        {
            orders.Add(new int[0]);
            return orders;
        }
        if (n == 1)
        {
            orders.Add(new int[] { 0 });
            return orders;
        }
        if (n == 2)
        {
            orders.Add(new int[] { 0, 1 });
            orders.Add(new int[] { 1, 0 });
            return orders;
        }
        if (n == 3)
        {
            int[][] all =
            {
                new int[] { 0, 1, 2 }, new int[] { 0, 2, 1 }, new int[] { 1, 0, 2 },
                new int[] { 1, 2, 0 }, new int[] { 2, 0, 1 }, new int[] { 2, 1, 0 }
            };
            for (int i = 0; i < all.Length; i++) orders.Add(all[i]);
            return orders;
        }
        // Sample permutations for larger n.
        int[] baseOrder = new int[n];
        for (int i = 0; i < n; i++) baseOrder[i] = i;
        for (int s = 0; s < 8; s++)
        {
            int[] copy = new int[n];
            for (int i = 0; i < n; i++) copy[i] = baseOrder[i];
            for (int i = n - 1; i > 0; i--)
            {
                int j = rng.Next(0, i + 1);
                int tmp = copy[i];
                copy[i] = copy[j];
                copy[j] = tmp;
            }
            orders.Add(copy);
        }
        return orders;
    }

    private static List<int> ConnectWaypoints(Topology topo, List<int> waypoints, bool preferInteresting)
    {
        List<int> full = new List<int>();
        for (int i = 0; i < waypoints.Count - 1; i++)
        {
            List<int> seg = new List<int>();
            if (!BuildPath(topo, topo.Walkable, waypoints[i], waypoints[i + 1], seg, preferInteresting))
            {
                return null;
            }
            int start = (i == 0) ? 0 : 1;
            for (int s = start; s < seg.Count; s++)
            {
                full.Add(seg[s]);
            }
        }
        return full;
    }

    private static bool SamePath(List<int> a, List<int> b)
    {
        if (a == null || b == null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static bool BuildPath(
        Topology topo, bool[] walkable, int from, int to, List<int> path, bool preferInteresting)
    {
        path.Clear();
        if (from < 0 || to < 0 || !walkable[from] || !walkable[to]) return false;
        int[] dist = new int[topo.CellCount];
        int[] parent = new int[topo.CellCount];
        for (int i = 0; i < topo.CellCount; i++)
        {
            dist[i] = -1;
            parent[i] = -1;
        }
        Queue<int> q = new Queue<int>();
        dist[from] = 0;
        q.Enqueue(from);
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            if (cur == to) break;
            int x, y;
            topo.Logic.FromIndex(cur, out x, out y);
            // Prefer interesting neighbors first when reconstructing via ordered enqueue bias.
            int[] ox = { 1, -1, 0, 0 };
            int[] oy = { 0, 0, 1, -1 };
            if (preferInteresting)
            {
                // Sort offsets by interest of neighbor cell.
                for (int a = 0; a < 3; a++)
                {
                    for (int b = a + 1; b < 4; b++)
                    {
                        int ia = Safe(topo, x + ox[a], y + oy[a]);
                        int ib = Safe(topo, x + ox[b], y + oy[b]);
                        if (Interest(topo, ib) > Interest(topo, ia))
                        {
                            int tx = ox[a]; ox[a] = ox[b]; ox[b] = tx;
                            int ty = oy[a]; oy[a] = oy[b]; oy[b] = ty;
                        }
                    }
                }
            }
            for (int d = 0; d < 4; d++)
            {
                TryParent(topo, walkable, dist, parent, q, x + ox[d], y + oy[d], cur);
            }
        }
        if (dist[to] < 0) return false;
        int walk = to;
        List<int> rev = new List<int>();
        while (walk >= 0)
        {
            rev.Add(walk);
            if (walk == from) break;
            walk = parent[walk];
        }
        for (int i = rev.Count - 1; i >= 0; i--) path.Add(rev[i]);
        return true;
    }

    private static int Interest(Topology topo, int cell)
    {
        if (cell < 0) return -1;
        int s = 0;
        if (IsOnList(topo.Choke, cell)) s += 5;
        if (IsOnList(topo.SoftBridge, cell)) s += 4;
        if (IsOnList(topo.Intersection, cell)) s += 2;
        if (IsOnList(topo.DeadEnd, cell)) s += 1;
        return s;
    }

    private static int Safe(Topology topo, int x, int y)
    {
        if (!topo.Logic.InBounds(x, y)) return -1;
        return topo.Logic.ToIndex(x, y);
    }

    private static void TryParent(
        Topology topo, bool[] walkable, int[] dist, int[] parent, Queue<int> q, int x, int y, int from)
    {
        if (!topo.Logic.InBounds(x, y)) return;
        int i = topo.Logic.ToIndex(x, y);
        if (!walkable[i] || dist[i] >= 0) return;
        dist[i] = dist[from] + 1;
        parent[i] = from;
        q.Enqueue(i);
    }

    private static void Bfs(LevelLogic logic, bool[] walkable, int origin, int[] dist)
    {
        for (int i = 0; i < dist.Length; i++) dist[i] = -1;
        if (origin < 0 || !walkable[origin]) return;
        Queue<int> q = new Queue<int>();
        dist[origin] = 0;
        q.Enqueue(origin);
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            int x, y;
            logic.FromIndex(cur, out x, out y);
            TryEnq(logic, walkable, dist, q, x + 1, y, dist[cur] + 1);
            TryEnq(logic, walkable, dist, q, x - 1, y, dist[cur] + 1);
            TryEnq(logic, walkable, dist, q, x, y + 1, dist[cur] + 1);
            TryEnq(logic, walkable, dist, q, x, y - 1, dist[cur] + 1);
        }
    }

    private static void TryEnq(LevelLogic logic, bool[] walkable, int[] dist, Queue<int> q, int x, int y, int nd)
    {
        if (!logic.InBounds(x, y)) return;
        int i = logic.ToIndex(x, y);
        if (!walkable[i] || dist[i] >= 0) return;
        dist[i] = nd;
        q.Enqueue(i);
    }

    // -------------------------------------------------------------------------
    // Path-first object placement
    // -------------------------------------------------------------------------

    private static bool PathFirstPlace(
        LevelData level,
        Topology topo,
        List<int> skeleton,
        LevelGenerateParams p,
        System.Random rng,
        PlacementState place,
        SearchBudget budget,
        int costCap,
        int baseCost,
        bool[] protectedCell,
        out CountResult finalSolve,
        out string error)
    {
        finalSolve = null;
        error = null;

        List<int> pathSlots = BuildPathSlots(topo, skeleton, place);
        if (pathSlots.Count == 0 && p.RockCount + p.EnemyCount + p.SpikeCount > place.Rock + place.Enemy + place.Spike)
        {
            error = "Route has no placeable path slots.";
            return false;
        }

        int needRock = p.RockCount - place.Rock;
        int needEnemy = p.EnemyCount - place.Enemy;
        int needSpike = p.SpikeCount - place.Spike;
        if (needRock < 0) needRock = 0;
        if (needEnemy < 0) needEnemy = 0;
        if (needSpike < 0) needSpike = 0;

        int currentCost = baseCost;
        int placeCap = costCap;
        int wantSlack = needSpike + needRock * 3 + needEnemy * 3;
        if (placeCap < baseCost + wantSlack)
        {
            placeCap = baseCost + wantSlack;
        }

        PlaceOnPath(
            level, topo, place, pathSlots, skeleton, LevelObjectType.Rock, needRock,
            true, true, rng, budget, placeCap, ref currentCost);
        if (place.Rock < p.RockCount)
        {
            PlaceOnPath(
                level, topo, place, pathSlots, skeleton, LevelObjectType.Rock,
                p.RockCount - place.Rock, true, false, rng, budget, placeCap, ref currentCost);
        }
        PlaceOnPath(
            level, topo, place, pathSlots, skeleton, LevelObjectType.Enemy, needEnemy,
            true, true, rng, budget, placeCap, ref currentCost);
        if (place.Enemy < p.EnemyCount)
        {
            PlaceOnPath(
                level, topo, place, pathSlots, skeleton, LevelObjectType.Enemy,
                p.EnemyCount - place.Enemy, true, false, rng, budget, placeCap, ref currentCost);
        }

        level.SyncDerivedFields();
        finalSolve = CountSolutions(level, 8, budget, placeCap);
        if (!SolveUsable(finalSolve) || finalSolve.MinimumMoves < 0)
        {
            error = "Final solve failed within CostSlack.";
            return false;
        }
        if (finalSolve.MinimumMoves > placeCap)
        {
            placeCap = finalSolve.MinimumMoves;
        }

        int stripped = StripUnusedGates(level, finalSolve.SamplePath, place, protectedCell);
        stripped += StripZeroImpactGates(level, place, budget, placeCap, finalSolve.MinimumMoves, protectedCell);
        if (stripped > 0)
        {
            finalSolve = CountSolutions(level, 8, budget, placeCap);
            if (!SolveUsable(finalSolve) || finalSolve.MinimumMoves < 0)
            {
                error = "Solve failed after stripping unused gates.";
                return false;
            }
        }

        if (place.Rock < p.RockCount || place.Enemy < p.EnemyCount)
        {
            currentCost = finalSolve.MinimumMoves >= 0 ? finalSolve.MinimumMoves : currentCost;
            pathSlots = BuildPathSlots(topo, skeleton, place);
            if (place.Rock < p.RockCount)
            {
                PlaceOnPath(
                    level, topo, place, pathSlots, skeleton, LevelObjectType.Rock,
                    p.RockCount - place.Rock, true, false, rng, budget, placeCap, ref currentCost);
            }
            if (place.Enemy < p.EnemyCount)
            {
                PlaceOnPath(
                    level, topo, place, pathSlots, skeleton, LevelObjectType.Enemy,
                    p.EnemyCount - place.Enemy, true, false, rng, budget, placeCap, ref currentCost);
            }
            level.SyncDerivedFields();
            finalSolve = CountSolutions(level, 8, budget, placeCap);
            if (!SolveUsable(finalSolve) || finalSolve.MinimumMoves < 0)
            {
                error = "Solve failed after gate refill.";
                return false;
            }
        }

        if (!FillSpikesOnOptimalPath(
            level, topo, place, protectedCell, finalSolve.SamplePath, p.SpikeCount, rng, budget, placeCap,
            out finalSolve, out error))
        {
            return false;
        }

        if (place.Rock < p.RockCount || place.Enemy < p.EnemyCount)
        {
            currentCost = finalSolve.MinimumMoves >= 0 ? finalSolve.MinimumMoves : currentCost;
            pathSlots = BuildPathSlotsFromSolve(level, topo, place, finalSolve.SamplePath);
            if (place.Rock < p.RockCount)
            {
                PlaceOnPath(
                    level, topo, place, pathSlots, skeleton, LevelObjectType.Rock,
                    p.RockCount - place.Rock, false, false, rng, budget, placeCap, ref currentCost);
            }
            if (place.Enemy < p.EnemyCount)
            {
                PlaceOnPath(
                    level, topo, place, pathSlots, skeleton, LevelObjectType.Enemy,
                    p.EnemyCount - place.Enemy, false, false, rng, budget, placeCap, ref currentCost);
            }
            level.SyncDerivedFields();
            finalSolve = CountSolutions(level, 8, budget, placeCap);
            if (!SolveUsable(finalSolve) || finalSolve.MinimumMoves < 0)
            {
                error = "Solve failed after post-spike gate refill.";
                return false;
            }
        }

        if (place.Enemy != p.EnemyCount || place.Rock != p.RockCount || place.Spike != p.SpikeCount)
        {
            error = "placed E/R/S=" + place.Enemy + "/" + place.Rock + "/" + place.Spike
                + " need " + p.EnemyCount + "/" + p.RockCount + "/" + p.SpikeCount
                + " (could not place remaining Rock/Enemy on path; add push-lanes / chokes).";
            return false;
        }

        return true;
    }

    private static List<int> BuildPathSlotsFromSolve(
        LevelData level, Topology topo, PlacementState place, List<LevelDir> path)
    {
        List<int> slots = new List<int>();
        if (path != null && level != null)
        {
            List<int> cells = new List<int>();
            CollectPathCells(level, path, cells);
            for (int i = 0; i < cells.Count; i++)
            {
                int c = cells[i];
                if (c == topo.Start || c == topo.Goal) continue;
                if (place.Used[c]) continue;
                if (!IsOnList(slots, c)) slots.Add(c);
            }
        }
        for (int i = 0; i < topo.Shortest.Count; i++)
        {
            int c = topo.Shortest[i];
            if (c == topo.Start || c == topo.Goal) continue;
            if (place.Used[c]) continue;
            if (!IsOnList(slots, c)) slots.Add(c);
        }
        for (int i = 0; i < topo.Choke.Count; i++)
        {
            int c = topo.Choke[i];
            if (place.Used[c] || IsOnList(slots, c)) continue;
            slots.Add(c);
        }
        for (int i = 0; i < topo.SoftBridge.Count; i++)
        {
            int c = topo.SoftBridge[i];
            if (place.Used[c] || IsOnList(slots, c)) continue;
            slots.Add(c);
        }
        for (int i = 0; i < topo.CellCount; i++)
        {
            if (!topo.Walkable[i] || place.Used[i]) continue;
            if (i == topo.Start || i == topo.Goal) continue;
            if (Degree(topo, i) > 2) continue;
            if (!IsOnList(slots, i)) slots.Add(i);
        }
        PreferChokeSlots(topo, place, slots);
        return slots;
    }

    private static bool FillSpikesOnOptimalPath(
        LevelData level, Topology topo, PlacementState place, bool[] protectedCell,
        List<LevelDir> seedPath, int wantSpike, System.Random rng, SearchBudget budget, int placeCap,
        out CountResult finalSolve, out string error)
    {
        finalSolve = null;
        error = null;
        if (wantSpike < 0) wantSpike = 0;

        List<LevelDir> path = seedPath;
        for (int round = 0; round < 4; round++)
        {
            if (place.Spike > wantSpike)
            {
                TrimSpikesToCount(level, place, protectedCell, wantSpike);
            }

            if (place.Spike < wantSpike)
            {
                CarpetSpikesOnPath(level, topo, place, protectedCell, path, wantSpike - place.Spike, rng);
            }

            level.SyncDerivedFields();
            finalSolve = CountSolutions(level, 8, budget, placeCap);
            if (!SolveUsable(finalSolve) || finalSolve.MinimumMoves < 0)
            {
                TrimSpikesUntilSolvable(level, place, budget, placeCap, 0, protectedCell);
                finalSolve = CountSolutions(level, 8, budget, placeCap);
            }
            if (!SolveUsable(finalSolve) || finalSolve.MinimumMoves < 0)
            {
                error = "Unsolvable after spike fill.";
                return false;
            }
            path = finalSolve.SamplePath;

            RelocateOffPathSpikesOntoPath(level, topo, place, protectedCell, path, rng);
            level.SyncDerivedFields();
            finalSolve = CountSolutions(level, 8, budget, placeCap);
            if (SolveUsable(finalSolve) && finalSolve.SamplePath != null)
            {
                path = finalSolve.SamplePath;
            }

            if (place.Spike >= wantSpike && AllSpikesOnPath(level, path))
            {
                return true;
            }
        }

        finalSolve = CountSolutions(level, 8, budget, placeCap);
        if (!SolveUsable(finalSolve))
        {
            error = "Unsolvable after spike fill.";
            return false;
        }
        return true;
    }

    private static void CarpetSpikesOnPath(
        LevelData level, Topology topo, PlacementState place, bool[] protectedCell,
        List<LevelDir> path, int want, System.Random rng)
    {
        if (want <= 0 || path == null) return;

        List<int> pathCells = new List<int>();
        CollectPathCells(level, path, pathCells);

        List<int> pool = new List<int>();
        for (int i = 0; i < pathCells.Count; i++)
        {
            int c = pathCells[i];
            if (c == topo.Start || c == topo.Goal) continue;
            if (place.Used[c]) continue;
            if (protectedCell != null && c >= 0 && c < protectedCell.Length && protectedCell[c]) continue;
            if (!IsOnList(pool, c)) pool.Add(c);
        }
        for (int i = 0; i < topo.Shortest.Count; i++)
        {
            int c = topo.Shortest[i];
            if (c == topo.Start || c == topo.Goal) continue;
            if (place.Used[c] || IsOnList(pool, c)) continue;
            if (protectedCell != null && c >= 0 && c < protectedCell.Length && protectedCell[c]) continue;
            pool.Add(c);
        }
        for (int i = 0; i < topo.SoftBridge.Count; i++)
        {
            int c = topo.SoftBridge[i];
            if (place.Used[c] || IsOnList(pool, c)) continue;
            if (protectedCell != null && c >= 0 && c < protectedCell.Length && protectedCell[c]) continue;
            pool.Add(c);
        }
        for (int i = 0; i < topo.Choke.Count; i++)
        {
            int c = topo.Choke[i];
            if (place.Used[c] || IsOnList(pool, c)) continue;
            if (protectedCell != null && c >= 0 && c < protectedCell.Length && protectedCell[c]) continue;
            pool.Add(c);
        }

        PreferVisitedCellsFirst(pool, pathCells);
        ShuffleTop(pool, rng, pool.Count < 10 ? pool.Count : 10);

        int placed = 0;
        for (int i = 0; i < pool.Count && placed < want; i++)
        {
            int cell = pool[i];
            if (place.Used[cell]) continue;
            if (!TryPlace(level, topo, place, cell, LevelObjectType.Spike)) continue;
            placed++;
        }
        level.SyncDerivedFields();
    }

    private static void PreferVisitedCellsFirst(List<int> pool, List<int> pathCells)
    {
        for (int i = 0; i < pool.Count; i++)
        {
            int best = i;
            for (int j = i + 1; j < pool.Count; j++)
            {
                bool jOn = IsOnList(pathCells, pool[j]);
                bool bOn = IsOnList(pathCells, pool[best]);
                if (jOn && !bOn) best = j;
            }
            if (best != i)
            {
                int tmp = pool[i];
                pool[i] = pool[best];
                pool[best] = tmp;
            }
        }
    }

    private static void RelocateOffPathSpikesOntoPath(
        LevelData level, Topology topo, PlacementState place, bool[] protectedCell,
        List<LevelDir> path, System.Random rng)
    {
        if (path == null) return;
        List<int> pathCells = new List<int>();
        CollectPathCells(level, path, pathCells);

        List<int> free = new List<int>();
        for (int i = 0; i < pathCells.Count; i++)
        {
            int c = pathCells[i];
            if (c == topo.Start || c == topo.Goal) continue;
            if (place.Used[c]) continue;
            if (protectedCell != null && c >= 0 && c < protectedCell.Length && protectedCell[c]) continue;
            if (!CellEmptyForObject(level, c)) continue;
            free.Add(c);
        }
        if (free.Count == 0) return;
        ShuffleTop(free, rng, free.Count < 8 ? free.Count : 8);

        List<LevelObjectData> objects = level.Objects;
        int slot = 0;
        for (int i = 0; i < objects.Count && slot < free.Count; i++)
        {
            if (objects[i].Type != LevelObjectType.Spike) continue;
            int cell = objects[i].Y * level.Width + objects[i].X;
            if (IsOnList(pathCells, cell)) continue;
            if (protectedCell != null && cell >= 0 && cell < protectedCell.Length && protectedCell[cell])
            {
                continue;
            }
            int dest = free[slot];
            slot++;
            if (cell >= 0 && cell < place.Used.Length) place.Used[cell] = false;
            objects[i].X = dest % level.Width;
            objects[i].Y = dest / level.Width;
            if (dest >= 0 && dest < place.Used.Length) place.Used[dest] = true;
        }
        level.SyncDerivedFields();
    }

    private static bool AllSpikesOnPath(LevelData level, List<LevelDir> path)
    {
        if (path == null) return false;
        List<int> pathCells = new List<int>();
        CollectPathCells(level, path, pathCells);
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Type != LevelObjectType.Spike) continue;
            int cell = objects[i].Y * level.Width + objects[i].X;
            if (!IsOnList(pathCells, cell)) return false;
        }
        return true;
    }

    private static void TrimSpikesToCount(
        LevelData level, PlacementState place, bool[] protectedCell, int keep)
    {
        if (keep < 0) keep = 0;
        List<LevelObjectData> objects = level.Objects;
        for (int i = objects.Count - 1; i >= 0 && place.Spike > keep; i--)
        {
            if (objects[i].Type != LevelObjectType.Spike) continue;
            int cell = objects[i].Y * level.Width + objects[i].X;
            if (protectedCell != null && cell >= 0 && cell < protectedCell.Length && protectedCell[cell])
            {
                continue;
            }
            if (cell >= 0 && cell < place.Used.Length) place.Used[cell] = false;
            objects.RemoveAt(i);
            if (place.Spike > 0) place.Spike--;
        }
        level.SyncDerivedFields();
    }

    private static void StripUnusedGatesOnly(
        LevelData level, List<LevelDir> path, PlacementState place, bool[] protectedCell)
    {
        StripUnusedGates(level, path, place, protectedCell);
    }

    private static int CountFreePathSlots(
        LevelData level, Topology topo, List<LevelDir> path, bool[] protectedCell)
    {
        if (path == null) return 0;
        List<int> pathCells = new List<int>();
        CollectPathCells(level, path, pathCells);
        int n = 0;
        for (int i = 0; i < pathCells.Count; i++)
        {
            int c = pathCells[i];
            if (c == topo.Start || c == topo.Goal) continue;
            if (protectedCell != null && c >= 0 && c < protectedCell.Length && protectedCell[c]) continue;
            if (CellEmptyForObject(level, c)) n++;
        }
        return n;
    }

    private static bool SolveUsable(CountResult solve)
    {
        if (solve == null) return false;
        if (solve.MinimumMoves < 0) return false;
        return solve.Status == CountStatus.Ok || solve.Status == CountStatus.Inconclusive;
    }

    private static void CarpetSpikes(
        LevelData level, Topology topo, PlacementState place, List<int> skeleton,
        int want, System.Random rng)
    {
        if (want <= 0) return;

        List<int> pool = new List<int>();
        for (int i = 0; i < skeleton.Count; i++)
        {
            int c = skeleton[i];
            if (c == topo.Start || c == topo.Goal) continue;
            if (place.Used[c]) continue;
            if (!IsOnList(pool, c)) pool.Add(c);
        }
        for (int i = 0; i < topo.Shortest.Count; i++)
        {
            int c = topo.Shortest[i];
            if (c == topo.Start || c == topo.Goal) continue;
            if (place.Used[c] || IsOnList(pool, c)) continue;
            pool.Add(c);
        }
        for (int i = 0; i < topo.SoftBridge.Count; i++)
        {
            int c = topo.SoftBridge[i];
            if (place.Used[c] || IsOnList(pool, c)) continue;
            pool.Add(c);
        }
        for (int i = 0; i < topo.Choke.Count; i++)
        {
            int c = topo.Choke[i];
            if (place.Used[c] || IsOnList(pool, c)) continue;
            pool.Add(c);
        }

        PreferChokeSlots(topo, place, pool);
        ShuffleTop(pool, rng, pool.Count < 8 ? pool.Count : 8);

        int placed = 0;
        for (int i = 0; i < pool.Count && placed < want; i++)
        {
            int cell = pool[i];
            if (place.Used[cell]) continue;
            if (!TryPlace(level, topo, place, cell, LevelObjectType.Spike)) continue;
            placed++;
        }
        level.SyncDerivedFields();
    }

    private static void TrimSpikesUntilSolvable(
        LevelData level, PlacementState place, SearchBudget budget, int costCap, int minKeep,
        bool[] protectedCell)
    {
        if (minKeep < 0) minKeep = 0;
        for (int guard = 0; guard < 16; guard++)
        {
            CountResult probe = CountSolutions(level, 8, budget, costCap);
            if (SolveUsable(probe) && probe.MinimumMoves >= 0 && probe.MinimumMoves <= costCap)
            {
                return;
            }
            if (place.Spike <= minKeep) return;
            if (!RemoveOneSpike(level, place, protectedCell)) return;
            level.SyncDerivedFields();
        }
    }

    private static bool RemoveOneSpike(LevelData level, PlacementState place, bool[] protectedCell)
    {
        List<LevelObjectData> objects = level.Objects;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            if (objects[i].Type != LevelObjectType.Spike) continue;
            int cell = objects[i].Y * level.Width + objects[i].X;
            if (protectedCell != null && cell >= 0 && cell < protectedCell.Length && protectedCell[cell])
            {
                continue;
            }
            if (cell >= 0 && cell < place.Used.Length) place.Used[cell] = false;
            if (place.Spike > 0) place.Spike--;
            objects.RemoveAt(i);
            return true;
        }
        return false;
    }

    private static int StripUnusedGates(
        LevelData level, List<LevelDir> path, PlacementState place, bool[] protectedCell)
    {
        int stripped = 0;
        List<LevelObjectData> objects = level.Objects;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Enemy && obj.Type != LevelObjectType.Rock)
            {
                continue;
            }
            int cell = obj.Y * level.Width + obj.X;
            if (protectedCell != null && cell >= 0 && cell < protectedCell.Length && protectedCell[cell])
            {
                continue;
            }
            if (SamplePathUsesObject(level, path, obj.Type, cell))
            {
                continue;
            }
            if (cell >= 0 && cell < place.Used.Length) place.Used[cell] = false;
            if (obj.Type == LevelObjectType.Enemy && place.Enemy > 0) place.Enemy--;
            else if (obj.Type == LevelObjectType.Rock && place.Rock > 0) place.Rock--;
            objects.RemoveAt(i);
            stripped++;
        }
        if (stripped > 0) level.SyncDerivedFields();
        return stripped;
    }

    private static List<int> BuildPathSlots(Topology topo, List<int> skeleton, PlacementState place)
    {
        List<int> slots = new List<int>();
        for (int i = 0; i < skeleton.Count; i++)
        {
            int c = skeleton[i];
            if (c == topo.Start || c == topo.Goal) continue;
            if (place.Used[c]) continue;
            if (!IsOnList(slots, c)) slots.Add(c);
        }
        for (int i = 0; i < topo.Choke.Count; i++)
        {
            int c = topo.Choke[i];
            if (place.Used[c] || IsOnList(slots, c)) continue;
            slots.Add(c);
        }
        for (int i = 0; i < topo.SoftBridge.Count; i++)
        {
            int c = topo.SoftBridge[i];
            if (place.Used[c] || IsOnList(slots, c)) continue;
            slots.Add(c);
        }
        PreferChokeSlots(topo, place, slots);
        return slots;
    }

    private static void PreferChokeSlots(Topology topo, PlacementState place, List<int> pool)
    {
        for (int i = 0; i < pool.Count; i++)
        {
            int best = i;
            for (int j = i + 1; j < pool.Count; j++)
            {
                if (SlotPriority(topo, place, pool[j]) > SlotPriority(topo, place, pool[best]))
                {
                    best = j;
                }
            }
            if (best != i)
            {
                int tmp = pool[i];
                pool[i] = pool[best];
                pool[best] = tmp;
            }
        }
    }

    private static int SlotPriority(Topology topo, PlacementState place, int cell)
    {
        int s = Interest(topo, cell);
        if (Degree(topo, cell) <= 2 && HasPushAxis(topo, place, cell)) s += 20;
        if (IsOnList(topo.Shortest, cell)) s += 8;
        return s;
    }

    private static void PlaceOnPath(
        LevelData level, Topology topo, PlacementState place, List<int> pathSlots, List<int> skeleton,
        LevelObjectType type, int want, bool requireCostUp, bool requirePushAxis,
        System.Random rng, SearchBudget budget, int costCap, ref int currentCost)
    {
        if (want <= 0) return;

        List<int> pool = new List<int>();
        for (int i = 0; i < pathSlots.Count; i++)
        {
            int c = pathSlots[i];
            if (place.Used[c] && !CanStackSpikeRock(level, topo, c, type)) continue;
            if (requirePushAxis && !HasPushAxis(topo, place, c)) continue;
            if (!IsOnList(pool, c)) pool.Add(c);
        }
        if (type == LevelObjectType.Spike)
        {
            for (int i = 0; i < topo.Shortest.Count; i++)
            {
                int c = topo.Shortest[i];
                if (c == topo.Start || c == topo.Goal) continue;
                if (place.Used[c] || IsOnList(pool, c)) continue;
                pool.Add(c);
            }
            for (int i = 0; i < topo.SoftBridge.Count; i++)
            {
                int c = topo.SoftBridge[i];
                if (place.Used[c] || IsOnList(pool, c)) continue;
                pool.Add(c);
            }
            for (int i = 0; i < topo.Choke.Count; i++)
            {
                int c = topo.Choke[i];
                if (place.Used[c] || IsOnList(pool, c)) continue;
                pool.Add(c);
            }
            for (int i = 0; i < topo.CellCount; i++)
            {
                if (!topo.Walkable[i] || place.Used[i]) continue;
                if (i == topo.Start || i == topo.Goal) continue;
                if (!IsOnList(pool, i)) pool.Add(i);
            }
        }
        else
        {
            for (int i = 0; i < topo.CellCount; i++)
            {
                if (!topo.Walkable[i] || place.Used[i]) continue;
                if (i == topo.Start || i == topo.Goal) continue;
                if (Degree(topo, i) > 2 && !IsOnList(topo.Choke, i) && !IsOnList(topo.SoftBridge, i))
                {
                    continue;
                }
                if (requirePushAxis && !HasPushAxis(topo, place, i)) continue;
                if (!IsOnList(pool, i)) pool.Add(i);
            }
        }

        PreferChokeSlots(topo, place, pool);
        PreferBridges(topo, pool);
        PreferPathOrder(pool, skeleton, rng);
        ShuffleTop(pool, rng, pool.Count < 6 ? pool.Count : 6);

        int placed = 0;
        for (int i = 0; i < pool.Count && placed < want; i++)
        {
            int cell = pool[i];
            if (place.Used[cell] && !CanStackSpikeRock(level, topo, cell, type)) continue;
            int costBefore = currentCost;
            if (!TryPlace(level, topo, place, cell, type)) continue;
            level.SyncDerivedFields();

            CountResult probe = CountSolutions(level, 8, budget, costCap);
            if (!SolveUsable(probe) || probe.MinimumMoves > costCap)
            {
                UndoLastPlace(level, place, cell, type);
                continue;
            }

            bool costUp = probe.MinimumMoves > costBefore;
            bool usedOnPath = SamplePathUsesObject(level, probe.SamplePath, type, cell);
            bool onRoute = IsOnList(topo.Shortest, cell)
                || IsOnList(topo.SoftBridge, cell)
                || IsOnList(topo.Choke, cell)
                || IsOnList(skeleton, cell)
                || Degree(topo, cell) <= 2;

            if (type == LevelObjectType.Spike)
            {
                if (!usedOnPath && !onRoute)
                {
                    UndoLastPlace(level, place, cell, type);
                    continue;
                }
            }
            else
            {
                if (requireCostUp && !costUp)
                {
                    UndoLastPlace(level, place, cell, type);
                    continue;
                }
                if (!usedOnPath)
                {
                    if (requireCostUp || !onRoute)
                    {
                        UndoLastPlace(level, place, cell, type);
                        continue;
                    }
                }
                int delta = probe.MinimumMoves - costBefore;
                if (requireCostUp && delta > MaxPlaceDelta(topo, cell, type))
                {
                    UndoLastPlace(level, place, cell, type);
                    continue;
                }
            }

            if (costUp)
            {
                currentCost = probe.MinimumMoves;
            }
            placed++;
        }
    }

    private static int MaxPlaceDelta(Topology topo, int cell, LevelObjectType type)
    {
        bool choke = IsOnList(topo.Choke, cell) || IsOnList(topo.SoftBridge, cell) || IsBridgeCell(topo, cell);
        if (choke)
        {
            return type == LevelObjectType.Spike ? 4 : 10;
        }
        return type == LevelObjectType.Spike ? 2 : 5;
    }

    private static bool IsBridgeCell(Topology topo, int cell)
    {
        if (cell < 0 || cell >= topo.CellCount) return false;
        if (cell == topo.Start || cell == topo.Goal) return false;
        if (!topo.Walkable[cell]) return false;
        bool[] blocked = new bool[topo.CellCount];
        for (int i = 0; i < topo.CellCount; i++) blocked[i] = topo.Walkable[i];
        blocked[cell] = false;
        int[] dist = new int[topo.CellCount];
        Bfs(topo.Logic, blocked, topo.Start, dist);
        if (dist[topo.Goal] < 0) return true;
        return dist[topo.Goal] >= topo.ShortestLen + 2;
    }

    private static void PreferBridges(Topology topo, List<int> pool)
    {
        for (int i = 0; i < pool.Count; i++)
        {
            int best = i;
            for (int j = i + 1; j < pool.Count; j++)
            {
                bool bj = IsBridgeCell(topo, pool[j]);
                bool bb = IsBridgeCell(topo, pool[best]);
                if (bj && !bb) best = j;
            }
            if (best != i)
            {
                int tmp = pool[i];
                pool[i] = pool[best];
                pool[best] = tmp;
            }
        }
    }

    private static void ShuffleTop(List<int> pool, System.Random rng, int top)
    {
        if (top > pool.Count) top = pool.Count;
        for (int i = top - 1; i > 0; i--)
        {
            int j = rng.Next(0, i + 1);
            int tmp = pool[i];
            pool[i] = pool[j];
            pool[j] = tmp;
        }
    }

    private static bool HasAdjacentObject(LevelData level, Topology topo, int cell)
    {
        int x, y;
        topo.Logic.FromIndex(cell, out x, out y);
        int[] dx = { 1, -1, 0, 0 };
        int[] dy = { 0, 0, 1, -1 };
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < 4; i++)
        {
            int nx = x + dx[i];
            int ny = y + dy[i];
            if (!topo.Logic.InBounds(nx, ny)) continue;
            for (int o = 0; o < objects.Count; o++)
            {
                LevelObjectData obj = objects[o];
                if (obj.X != nx || obj.Y != ny) continue;
                if (obj.Type == LevelObjectType.Enemy || obj.Type == LevelObjectType.Rock
                    || obj.Type == LevelObjectType.Spike)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void PreferPathOrder(List<int> pool, List<int> skeleton, System.Random rng)
    {
        // Bias toward middle of the route (better push/kick gates).
        if (skeleton.Count < 3 || pool.Count < 2) return;
        int mid = skeleton[skeleton.Count / 2];
        for (int i = 0; i < pool.Count; i++)
        {
            int best = i;
            for (int j = i + 1; j < pool.Count; j++)
            {
                int dj = CellDistApprox(pool[j], mid, skeleton);
                int db = CellDistApprox(pool[best], mid, skeleton);
                if (dj < db) best = j;
            }
            if (best != i)
            {
                int tmp = pool[i];
                pool[i] = pool[best];
                pool[best] = tmp;
            }
        }
        // Light shuffle of top third so attempts differ.
        int top = pool.Count / 3;
        if (top < 2) top = pool.Count;
        for (int i = top - 1; i > 0; i--)
        {
            int j = rng.Next(0, i + 1);
            int tmp = pool[i];
            pool[i] = pool[j];
            pool[j] = tmp;
        }
    }

    private static int CellDistApprox(int a, int b, List<int> skeleton)
    {
        int ia = -1;
        int ib = -1;
        for (int i = 0; i < skeleton.Count; i++)
        {
            if (skeleton[i] == a) ia = i;
            if (skeleton[i] == b) ib = i;
        }
        if (ia >= 0 && ib >= 0)
        {
            int d = ia - ib;
            return d < 0 ? -d : d;
        }
        return 99;
    }

    private static bool SamplePathHasAction(LevelData level, List<LevelDir> path, LevelActionResult want)
    {
        if (path == null || path.Count == 0) return false;
        LevelLogic logic = new LevelLogic(level);
        LevelSimState state = logic.CreateInitialState(level);
        for (int i = 0; i < path.Count; i++)
        {
            LevelActionResult a = logic.TryMove(ref state, path[i], false);
            if (a == want) return true;
            if (state.Dead && !state.Won) break;
        }
        return false;
    }

    private static bool SamplePathUsesObject(
        LevelData level, List<LevelDir> path, LevelObjectType type, int startCell)
    {
        if (path == null || path.Count == 0 || startCell < 0) return false;
        LevelLogic logic = new LevelLogic(level);
        LevelSimState state = logic.CreateInitialState(level);
        int tracked = startCell;

        for (int i = 0; i < path.Count; i++)
        {
            LevelDir dir = path[i];
            if (state.PlayerIndex < 0) break;
            int px;
            int py;
            logic.FromIndex(state.PlayerIndex, out px, out py);
            int nx = px + LevelLogic.DirToDx(dir);
            int ny = py + LevelLogic.DirToDy(dir);
            if (!logic.InBounds(nx, ny))
            {
                logic.TryMove(ref state, dir, false);
                if (state.Dead && !state.Won) break;
                continue;
            }
            int next = logic.ToIndex(nx, ny);

            if (type == LevelObjectType.Spike && next == tracked && logic.IsSpike(tracked))
            {
                LevelActionResult a = logic.TryMove(ref state, dir, false);
                if (a == LevelActionResult.SpikePenalty) return true;
                if (state.Dead && !state.Won) break;
                continue;
            }

            if (type == LevelObjectType.Enemy && tracked >= 0 && LevelLogic.HasEnemy(ref state, tracked)
                && next == tracked)
            {
                LevelActionResult a = logic.TryMove(ref state, dir, false);
                if (a == LevelActionResult.Kicked || a == LevelActionResult.Pushed) return true;
                if (state.Dead && !state.Won) break;
                continue;
            }

            if (type == LevelObjectType.Rock && tracked >= 0 && LevelLogic.HasRock(ref state, tracked)
                && next == tracked)
            {
                LevelActionResult a = logic.TryMove(ref state, dir, false);
                if (a == LevelActionResult.Pushed) return true;
                if (state.Dead && !state.Won) break;
                continue;
            }

            logic.TryMove(ref state, dir, false);
            if (state.Dead && !state.Won) break;
        }
        return false;
    }

    private static int StripObjectsNotUsedOnPath(
        LevelData level, List<LevelDir> path, PlacementState place, Topology topo)
    {
        int stripped = 0;
        List<LevelObjectData> objects = level.Objects;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Enemy && obj.Type != LevelObjectType.Rock
                && obj.Type != LevelObjectType.Spike)
            {
                continue;
            }
            int cell = obj.Y * level.Width + obj.X;
            if (ObjectContributes(level, path, topo, obj.Type, cell))
            {
                continue;
            }
            if (cell >= 0 && cell < place.Used.Length) place.Used[cell] = false;
            if (obj.Type == LevelObjectType.Enemy && place.Enemy > 0) place.Enemy--;
            else if (obj.Type == LevelObjectType.Rock && place.Rock > 0) place.Rock--;
            else if (obj.Type == LevelObjectType.Spike && place.Spike > 0) place.Spike--;
            objects.RemoveAt(i);
            stripped++;
        }
        if (stripped > 0) level.SyncDerivedFields();
        return stripped;
    }

    private static bool ObjectContributes(
        LevelData level, List<LevelDir> path, Topology topo, LevelObjectType type, int cell)
    {
        if (type == LevelObjectType.Spike)
        {
            return PathVisitsCell(level, path, cell)
                || SamplePathUsesObject(level, path, type, cell);
        }
        return SamplePathUsesObject(level, path, type, cell);
    }

    private static bool PathVisitsCell(LevelData level, List<LevelDir> path, int cell)
    {
        if (path == null || cell < 0) return false;
        List<int> cells = new List<int>();
        CollectPathCells(level, path, cells);
        return IsOnList(cells, cell);
    }

    private static bool AllObjectsUsedOnOptPaths(
        LevelData level, SearchBudget budget, int costCap, int maxSols, out string error)
    {
        error = null;
        if (level == null)
        {
            error = "null level.";
            return false;
        }
        int slots = maxSols;
        if (slots < 1) slots = 1;
        if (slots > 4) slots = 4;

        int saved = level.MoveLimit;
        int cap = costCap;
        if (cap <= 0) cap = level.Width * level.Height + 32;
        level.MoveLimit = cap;
        int maxStates = budget.StateLimit;
        if (maxStates < 80000) maxStates = 80000;
        if (maxStates > 250000) maxStates = 250000;
        LevelSolveResult solve = LevelSolver.Solve(level, maxStates, false, slots);
        level.MoveLimit = saved;

        if (!solve.Success || solve.MinimumMoves < 0 || solve.OptimalSolutions.Count == 0)
        {
            error = "no optimal paths to verify object use.";
            return false;
        }

        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Enemy && obj.Type != LevelObjectType.Rock
                && obj.Type != LevelObjectType.Spike)
            {
                continue;
            }
            int cell = obj.Y * level.Width + obj.X;
            bool used = false;
            for (int p = 0; p < solve.OptimalSolutions.Count; p++)
            {
                if (obj.Type == LevelObjectType.Spike)
                {
                    if (PathVisitsCell(level, solve.OptimalSolutions[p], cell)
                        || SamplePathUsesObject(level, solve.OptimalSolutions[p], obj.Type, cell))
                    {
                        used = true;
                        break;
                    }
                }
                else if (SamplePathUsesObject(level, solve.OptimalSolutions[p], obj.Type, cell))
                {
                    used = true;
                    break;
                }
            }
            if (!used)
            {
                error = obj.Type + " @" + obj.X + "," + obj.Y + " unused on all optimal solutions.";
                return false;
            }
        }
        return true;
    }

    private static void TryRelocateUnusedOntoPath(
        LevelData level, Topology topo, List<LevelDir> samplePath,
        bool[] protectedCell, System.Random rng)
    {
        if (level == null || samplePath == null || topo == null) return;

        List<int> pathCells = new List<int>();
        CollectPathCells(level, samplePath, pathCells);

        List<int> freeOnPath = new List<int>();
        for (int i = 0; i < pathCells.Count; i++)
        {
            int c = pathCells[i];
            if (c == topo.Start || c == topo.Goal) continue;
            if (protectedCell != null && c >= 0 && c < protectedCell.Length && protectedCell[c]) continue;
            if (!CellEmptyForObject(level, c)) continue;
            freeOnPath.Add(c);
        }
        if (freeOnPath.Count == 0) return;
        ShuffleTop(freeOnPath, rng, freeOnPath.Count < 6 ? freeOnPath.Count : 6);

        List<LevelObjectData> objects = level.Objects;
        int slot = 0;
        for (int i = 0; i < objects.Count && slot < freeOnPath.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Enemy && obj.Type != LevelObjectType.Rock
                && obj.Type != LevelObjectType.Spike)
            {
                continue;
            }
            int cell = obj.Y * level.Width + obj.X;
            if (obj.Type == LevelObjectType.Spike)
            {
                if (PathVisitsCell(level, samplePath, cell)
                    || SamplePathUsesObject(level, samplePath, obj.Type, cell))
                {
                    continue;
                }
            }
            else if (SamplePathUsesObject(level, samplePath, obj.Type, cell))
            {
                continue;
            }
            if (protectedCell != null && cell >= 0 && cell < protectedCell.Length && protectedCell[cell])
            {
                continue;
            }

            int dest = freeOnPath[slot];
            slot++;
            objects[i].X = dest % level.Width;
            objects[i].Y = dest / level.Width;
        }
        level.SyncDerivedFields();
    }

    private static int StripZeroImpactGates(
        LevelData level, PlacementState place, SearchBudget budget, int costCap, int optWithAll,
        bool[] protectedCell)
    {
        int stripped = 0;
        List<LevelObjectData> objects = level.Objects;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Enemy && obj.Type != LevelObjectType.Rock)
            {
                continue;
            }
            int cell = obj.Y * level.Width + obj.X;
            if (protectedCell != null && cell >= 0 && cell < protectedCell.Length && protectedCell[cell])
            {
                continue;
            }
            LevelObjectData held = objects[i];
            objects.RemoveAt(i);
            level.SyncDerivedFields();
            CountResult without = CountSolutions(level, 8, budget, costCap);
            if (without.Status == CountStatus.Ok && without.MinimumMoves == optWithAll)
            {
                if (cell >= 0 && cell < place.Used.Length) place.Used[cell] = false;
                if (held.Type == LevelObjectType.Enemy && place.Enemy > 0) place.Enemy--;
                else if (held.Type == LevelObjectType.Rock && place.Rock > 0) place.Rock--;
                stripped++;
                continue;
            }
            objects.Insert(i, held);
            level.SyncDerivedFields();
        }
        return stripped;
    }

    private static bool EachPlacedObjectContributes(
        LevelData level, List<LevelDir> path, bool[] protectedCell, out string error)
    {
        error = null;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Enemy && obj.Type != LevelObjectType.Rock)
            {
                continue;
            }
            int cell = obj.Y * level.Width + obj.X;
            if (!SamplePathUsesObject(level, path, obj.Type, cell))
            {
                bool hand = protectedCell != null && cell >= 0 && cell < protectedCell.Length
                    && protectedCell[cell];
                error = obj.Type + " @" + obj.X + "," + obj.Y
                    + (hand ? " (hand-placed)" : "")
                    + " not used on optimal path.";
                return false;
            }
        }
        return true;
    }

    private static bool EnoughSpikesOnPath(
        LevelData level, List<LevelDir> path, bool[] protectedCell,
        out int onPath, out string error)
    {
        onPath = 0;
        error = null;
        int total = 0;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Spike) continue;
            total++;
            int cell = obj.Y * level.Width + obj.X;
            if (SamplePathUsesObject(level, path, LevelObjectType.Spike, cell)
                || PathVisitsCell(level, path, cell))
            {
                onPath++;
            }
        }
        if (total <= 0) return true;
        if (onPath < total)
        {
            error = "only " + onPath + "/" + total + " spikes on optimal path (need all).";
            return false;
        }
        return true;
    }

    private static bool CountsExact(LevelData level, LevelGenerateParams p, out string error)
    {
        error = null;
        int e = 0;
        int r = 0;
        int s = 0;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Type == LevelObjectType.Enemy) e++;
            else if (objects[i].Type == LevelObjectType.Rock) r++;
            else if (objects[i].Type == LevelObjectType.Spike) s++;
        }
        if (e != p.EnemyCount || r != p.RockCount || s != p.SpikeCount)
        {
            error = "Count mismatch E/R/S=" + e + "/" + r + "/" + s
                + " need " + p.EnemyCount + "/" + p.RockCount + "/" + p.SpikeCount + ".";
            return false;
        }
        return true;
    }

    private static int CountObjectsOnChokes(LevelData level, Topology topo)
    {
        int n = 0;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Enemy && obj.Type != LevelObjectType.Rock
                && obj.Type != LevelObjectType.Spike)
            {
                continue;
            }
            int cell = obj.Y * level.Width + obj.X;
            if (IsOnList(topo.Choke, cell) || IsOnList(topo.SoftBridge, cell))
            {
                n++;
            }
        }
        return n;
    }

    private static void UndoLastPlace(LevelData level, PlacementState place, int cell, LevelObjectType type)
    {
        List<LevelObjectData> objects = level.Objects;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != type) continue;
            int idx = obj.Y * level.Width + obj.X;
            if (idx != cell) continue;
            objects.RemoveAt(i);
            break;
        }
        place.Used[cell] = false;
        if (type == LevelObjectType.Enemy && place.Enemy > 0) place.Enemy--;
        else if (type == LevelObjectType.Rock && place.Rock > 0) place.Rock--;
        else if (type == LevelObjectType.Spike && place.Spike > 0) place.Spike--;
        level.SyncDerivedFields();
    }

    private static bool HasPushAxis(Topology topo, PlacementState place, int cell)
    {
        int x, y;
        topo.Logic.FromIndex(cell, out x, out y);
        return AxisOk(topo, place, x, y, 1, 0)
            || AxisOk(topo, place, x, y, -1, 0)
            || AxisOk(topo, place, x, y, 0, 1)
            || AxisOk(topo, place, x, y, 0, -1);
    }

    private static bool AxisOk(Topology topo, PlacementState place, int x, int y, int dx, int dy)
    {
        int ax = x - dx;
        int ay = y - dy;
        int bx = x + dx;
        int by = y + dy;
        if (!topo.Logic.InBounds(ax, ay)) return false;
        int approach = topo.Logic.ToIndex(ax, ay);
        if (!topo.Walkable[approach] || place.Used[approach]) return false;
        if (!topo.Logic.InBounds(bx, by))
        {
            return true; // enemy kick-destroy axis
        }
        int behind = topo.Logic.ToIndex(bx, by);
        return topo.Walkable[behind] && !place.Used[behind];
    }

    private static bool TryPlace(
        LevelData level, Topology topo, PlacementState place, int cell, LevelObjectType type)
    {
        if (cell < 0 || cell == topo.Start || cell == topo.Goal) return false;
        if (place.Used[cell])
        {
            if (!CanStackSpikeRock(level, topo, cell, type)) return false;
        }
        else
        {
            place.Used[cell] = true;
        }
        if (type == LevelObjectType.Enemy) place.Enemy++;
        else if (type == LevelObjectType.Rock) place.Rock++;
        else if (type == LevelObjectType.Spike) place.Spike++;
        int x = cell % topo.Width;
        int y = cell / topo.Width;
        level.Objects.Add(new LevelObjectData(level.AllocateObjectId(), type, x, y));
        return true;
    }

    private static bool CanStackSpikeRock(
        LevelData level, Topology topo, int cell, LevelObjectType type)
    {
        if (type != LevelObjectType.Rock && type != LevelObjectType.Spike)
        {
            return false;
        }
        int x = cell % topo.Width;
        int y = cell / topo.Width;
        bool hasSpike = false;
        bool hasRock = false;
        bool hasOther = false;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.X != x || obj.Y != y)
            {
                continue;
            }
            if (obj.Type == LevelObjectType.Floor)
            {
                continue;
            }
            if (obj.Type == LevelObjectType.Spike) hasSpike = true;
            else if (obj.Type == LevelObjectType.Rock) hasRock = true;
            else hasOther = true;
        }
        if (hasOther)
        {
            return false;
        }
        if (type == LevelObjectType.Rock)
        {
            return hasSpike && !hasRock;
        }
        return hasRock && !hasSpike;
    }

    private static void PreferInteresting(Topology topo, List<int> pool)
    {
        for (int i = 0; i < pool.Count; i++)
        {
            int best = i;
            for (int j = i + 1; j < pool.Count; j++)
            {
                if (Interest(topo, pool[j]) > Interest(topo, pool[best])) best = j;
            }
            if (best != i)
            {
                int tmp = pool[i];
                pool[i] = pool[best];
                pool[best] = tmp;
            }
        }
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

    private static bool IsOnList(List<int> list, int cell)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == cell) return true;
        }
        return false;
    }

    private static int EstimateRouteDiversity(Topology topo, List<int> skeleton)
    {
        int d = 0;
        for (int i = 0; i < skeleton.Count; i++)
        {
            if (IsOnList(topo.Intersection, skeleton[i])) d++;
            if (IsOnList(topo.Choke, skeleton[i])) d++;
            if (IsOnList(topo.SoftBridge, skeleton[i])) d++;
        }
        return d;
    }

    // -------------------------------------------------------------------------
    // Impact / solve
    // -------------------------------------------------------------------------

    private static bool AllGeneratedHaveImpact(
        LevelData level, bool[] protectedCell, SearchBudget budget, int costCap,
        out int impactCount, out string error)
    {
        error = null;
        impactCount = 0;
        List<LevelObjectData> objects = level.Objects;
        List<LevelObjectData> generated = new List<LevelObjectData>();
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Enemy && obj.Type != LevelObjectType.Rock
                && obj.Type != LevelObjectType.Spike)
            {
                continue;
            }
            int cell = obj.Y * level.Width + obj.X;
            if (protectedCell != null && cell >= 0 && cell < protectedCell.Length && protectedCell[cell])
            {
                continue;
            }
            generated.Add(obj);
        }

        if (generated.Count == 0)
        {
            return true;
        }

        CountResult withAll = CountSolutions(level, 8, budget, costCap);
        if (withAll.MinimumMoves < 0)
        {
            error = "Unsolvable before impact test.";
            return false;
        }

        for (int i = 0; i < generated.Count; i++)
        {
            LevelObjectData obj = generated[i];
            int cell = obj.Y * level.Width + obj.X;
            if (protectedCell != null && cell >= 0 && cell < protectedCell.Length && protectedCell[cell])
            {
                impactCount++;
                continue;
            }
            if (obj.Type == LevelObjectType.Spike)
            {
                if (!PathVisitsCell(level, withAll.SamplePath, cell)
                    && !SamplePathUsesObject(level, withAll.SamplePath, LevelObjectType.Spike, cell))
                {
                    error = "Spike @" + obj.X + "," + obj.Y + " not on optimal path.";
                    return false;
                }
                impactCount++;
                continue;
            }
            int removeAt = -1;
            for (int j = 0; j < level.Objects.Count; j++)
            {
                if (level.Objects[j].Id == obj.Id)
                {
                    removeAt = j;
                    break;
                }
            }
            if (removeAt < 0) continue;
            LevelObjectData held = level.Objects[removeAt];
            level.Objects.RemoveAt(removeAt);
            level.SyncDerivedFields();
            CountResult without = CountSolutions(level, 8, budget, costCap);
            level.Objects.Insert(removeAt, held);
            level.SyncDerivedFields();

            bool impacts = false;
            if (without.MinimumMoves < 0 || without.Total <= 0)
            {
                impacts = true;
            }
            else if (without.MinimumMoves != withAll.MinimumMoves)
            {
                impacts = true;
            }
            else if (without.Total != withAll.Total)
            {
                impacts = true;
            }
            else
            {
                int taxWith = MoveTax(withAll);
                int taxWithout = MoveTax(without);
                if (taxWith != taxWithout) impacts = true;
            }

            if (!impacts)
            {
                error = held.Type + " @" + held.X + "," + held.Y + " has no solution impact.";
                return false;
            }
            impactCount++;
        }
        return true;
    }

    private static int MoveTax(CountResult c)
    {
        if (c.SamplePath == null || c.MinimumMoves < 0) return -1;
        return c.MinimumMoves - c.SamplePath.Count;
    }

    private static string ErrorKey(string error)
    {
        if (string.IsNullOrEmpty(error)) return "";
        int colon = -1;
        for (int i = 0; i < error.Length; i++)
        {
            if (error[i] == ':')
            {
                colon = i;
                break;
            }
        }
        if (colon > 0 && colon <= 24)
        {
            return error.Substring(0, colon);
        }
        int cut = error.Length;
        for (int i = 0; i < error.Length; i++)
        {
            char c = error[i];
            if ((c >= '0' && c <= '9') || c == '@' || c == '=')
            {
                cut = i;
                break;
            }
        }
        if (cut > 0 && cut < error.Length)
        {
            while (cut > 0 && error[cut - 1] == ' ') cut--;
            return error.Substring(0, cut);
        }
        return error;
    }

    private static bool FailFastStreak(
        ref int streak, ref string streakError, string error, int limit)
    {
        if (string.IsNullOrEmpty(error))
        {
            streak = 0;
            streakError = null;
            return false;
        }
        string key = ErrorKey(error);
        if (streakError != null && streakError == key)
        {
            streak++;
        }
        else
        {
            streakError = key;
            streak = 1;
        }
        return streak >= limit;
    }

    private static void PolishTrapBestEffort(
        LevelData level,
        Topology topo,
        bool[] protectedCell,
        SearchBudget budget,
        int filterCap,
        System.Random rng,
        ref CountResult built,
        out int gateCount,
        out int nearCount,
        out int nearCost)
    {
        gateCount = 0;
        nearCount = 0;
        nearCost = -1;
        if (built == null || built.MinimumMoves < 0)
        {
            return;
        }

        int opt = built.MinimumMoves;
        bool hasGateObj = false;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Type == LevelObjectType.Rock || objects[i].Type == LevelObjectType.Enemy)
            {
                hasGateObj = true;
                break;
            }
        }
        if (hasGateObj)
        {
            gateCount = CountTrueGatesFast(level, protectedCell, budget, filterCap, opt);
        }

        bool canBait = topo.SoftBridge.Count > 0 || topo.Intersection.Count > 0;
        if (!canBait)
        {
            return;
        }

        int probeCap = filterCap;
        if (probeCap < opt + 2) probeCap = opt + 2;
        ProbeNearMissLight(level, opt, budget, probeCap, out nearCount, out nearCost);

        if (nearCost == opt + 1)
        {
            return;
        }

        if (!TryInstallBait(level, topo, protectedCell, built.SamplePath, budget, opt, probeCap, rng))
        {
            return;
        }

        CountResult again = CountSolutions(level, 8, budget, probeCap);
        if (!SolveUsable(again) || again.MinimumMoves < 0)
        {
            return;
        }
        built = again;
        opt = built.MinimumMoves;
        if (hasGateObj)
        {
            gateCount = CountTrueGatesFast(level, protectedCell, budget, filterCap, opt);
        }
        ProbeNearMissLight(level, opt, budget, probeCap, out nearCount, out nearCost);
    }

    private static int CountTrueGatesFast(
        LevelData level, bool[] protectedCell, SearchBudget budget, int costCap, int optWithAll)
    {
        int gates = 0;
        int checkedGates = 0;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Rock && obj.Type != LevelObjectType.Enemy)
            {
                continue;
            }
            if (checkedGates >= 2)
            {
                // Enough signal for scoring; avoid N full solves.
                if (gates == 0) gates = 1;
                break;
            }
            checkedGates++;
            LevelObjectData held = objects[i];
            objects.RemoveAt(i);
            level.SyncDerivedFields();
            CountResult without = CountSolutions(level, 8, budget, costCap);
            objects.Insert(i, held);
            level.SyncDerivedFields();

            if (without.MinimumMoves < 0 || without.MinimumMoves < optWithAll)
            {
                gates++;
            }
        }
        return gates;
    }

    private static bool TryInstallBait(
        LevelData level, Topology topo, bool[] protectedCell, List<LevelDir> optPath,
        SearchBudget budget, int opt, int probeCap, System.Random rng)
    {
        int donor = FindSpikeDonor(level, protectedCell, optPath);
        if (donor < 0)
        {
            return false;
        }

        List<int> baitCells = new List<int>();
        for (int i = 0; i < topo.SoftBridge.Count && baitCells.Count < 6; i++)
        {
            int c = topo.SoftBridge[i];
            if (!IsCellFreeForBait(level, topo, protectedCell, c)) continue;
            baitCells.Add(c);
        }
        for (int i = 0; i < topo.Intersection.Count && baitCells.Count < 6; i++)
        {
            int c = topo.Intersection[i];
            if (!IsCellFreeForBait(level, topo, protectedCell, c)) continue;
            if (!IsOnList(baitCells, c)) baitCells.Add(c);
        }
        if (baitCells.Count == 0)
        {
            for (int i = 0; i < topo.CellCount && baitCells.Count < 4; i++)
            {
                if (!topo.Walkable[i] || i == topo.Start || i == topo.Goal) continue;
                if (Degree(topo, i) > 2) continue;
                if (IsOnList(topo.Shortest, i)) continue;
                if (!IsCellFreeForBait(level, topo, protectedCell, i)) continue;
                baitCells.Add(i);
            }
        }
        if (baitCells.Count == 0) return false;

        ShuffleTop(baitCells, rng, baitCells.Count < 3 ? baitCells.Count : 3);

        int donorCell = -1;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Id != donor) continue;
            donorCell = objects[i].Y * level.Width + objects[i].X;
            break;
        }
        if (donorCell < 0) return false;

        int maxTries = baitCells.Count;
        if (maxTries > 3) maxTries = 3;
        for (int b = 0; b < maxTries; b++)
        {
            int target = baitCells[b];
            if (target == donorCell) continue;
            if (!MoveSpikeIdToCell(level, donor, target)) continue;
            level.SyncDerivedFields();

            CountResult probe = CountSolutions(level, 8, budget, probeCap);
            if (!SolveUsable(probe) || probe.MinimumMoves < 0 || probe.MinimumMoves > opt)
            {
                MoveSpikeIdToCell(level, donor, donorCell);
                level.SyncDerivedFields();
                continue;
            }
            int nc;
            int nn;
            ProbeNearMissLight(level, probe.MinimumMoves, budget, probeCap, out nn, out nc);
            if (nc == probe.MinimumMoves + 1)
            {
                return true;
            }
            MoveSpikeIdToCell(level, donor, donorCell);
            level.SyncDerivedFields();
        }
        return false;
    }

    private static bool IsCellFreeForBait(
        LevelData level, Topology topo, bool[] protectedCell, int cell)
    {
        if (cell < 0 || cell >= topo.CellCount) return false;
        if (cell == topo.Start || cell == topo.Goal) return false;
        if (!topo.Walkable[cell]) return false;
        if (protectedCell != null && cell < protectedCell.Length && protectedCell[cell]) return false;
        int x = cell % topo.Width;
        int y = cell / topo.Width;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.X != x || obj.Y != y) continue;
            if (obj.Type == LevelObjectType.Floor || obj.Type == LevelObjectType.RequiredZone) continue;
            return false;
        }
        return true;
    }

    private static int FindSpikeDonor(LevelData level, bool[] protectedCell, List<LevelDir> optPath)
    {
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Spike) continue;
            int cell = obj.Y * level.Width + obj.X;
            if (protectedCell != null && cell >= 0 && cell < protectedCell.Length && protectedCell[cell])
            {
                continue;
            }
            if (!SamplePathUsesObject(level, optPath, LevelObjectType.Spike, cell))
            {
                return obj.Id;
            }
        }
        return -1;
    }

    private static bool MoveSpikeIdToCell(LevelData level, int spikeId, int cell)
    {
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Id != spikeId) continue;
            if (objects[i].Type != LevelObjectType.Spike) return false;
            objects[i].X = cell % level.Width;
            objects[i].Y = cell / level.Width;
            return true;
        }
        return false;
    }

    private static void ProbeNearMissLight(
        LevelData level, int optimal, SearchBudget budget, int costCap,
        out int nearCount, out int nearCost)
    {
        nearCount = 0;
        nearCost = -1;
        if (optimal < 0 || level == null) return;

        int savedLimit = level.MoveLimit;
        level.MoveLimit = optimal + 1;
        int maxStates = 25000;
        if (budget.StateLimit > 0 && budget.StateLimit < maxStates)
        {
            maxStates = budget.StateLimit;
        }
        if (maxStates < 8000) maxStates = 8000;
        LevelSolveResult solve = LevelSolver.Solve(level, maxStates, true, 8);
        level.MoveLimit = savedLimit;

        if (solve.MinimumMoves < 0) return;
        if (solve.NearPlus1Count > 0)
        {
            nearCount = solve.NearPlus1Count;
            nearCost = solve.MinimumMoves + 1;
            return;
        }
        if (solve.NearPlus2Count > 0)
        {
            nearCount = solve.NearPlus2Count;
            nearCost = solve.MinimumMoves + 2;
        }
    }

    private enum CountStatus
    {
        Ok = 0,
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
            Deadline = System.DateTime.UtcNow.Ticks + (long)TimeLimitMs * 10000L;
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

    private static bool AcceptSolCount(CountResult solve, int maxSols)
    {
        if (solve == null) return false;
        if (solve.Status != CountStatus.Ok) return false;
        if (solve.MinimumMoves < 0) return false;
        if (solve.Total < 1) return false;
        if (solve.Total > maxSols) return false;
        return true;
    }

    private static CountResult CountSolutionsAccept(
        LevelData level, SearchBudget budget, int costCap, int maxSols)
    {
        CountResult result = new CountResult();
        if (level == null)
        {
            result.Status = CountStatus.Unsolvable;
            return result;
        }
        int slots = maxSols + 1;
        if (slots < 2) slots = 2;
        if (slots > 8) slots = 8;

        budget.Begin();
        int savedLimit = level.MoveLimit;
        int cap = costCap;
        if (cap <= 0)
        {
            cap = level.Width * level.Height + 32;
        }
        level.MoveLimit = cap;
        int maxStates = budget.StateLimit;
        if (maxStates < 120000) maxStates = 120000;
        if (maxStates > 400000) maxStates = 400000;
        LevelSolveResult solve = LevelSolver.Solve(level, maxStates, false, slots);
        level.MoveLimit = savedLimit;

        if (!solve.Success || solve.MinimumMoves < 0)
        {
            result.Status = CountStatus.Unsolvable;
            return result;
        }

        result.MinimumMoves = solve.MinimumMoves;
        result.Total = solve.SolutionCountAtMinimum;
        if (result.Total < 1) result.Total = 1;
        result.SamplePath.Clear();
        for (int i = 0; i < solve.OptimalPath.Count; i++)
        {
            result.SamplePath.Add(solve.OptimalPath[i]);
        }

        if (solve.OptimalSolutionsCapped || solve.StateLimitReached)
        {
            if (result.Total <= maxSols) result.Total = maxSols + 1;
            result.Status = CountStatus.Inconclusive;
            return result;
        }

        result.ByMoves[solve.MinimumMoves] = result.Total;
        result.Status = CountStatus.Ok;
        return result;
    }

    private static void TryCutAlternateRoutes(
        LevelData level,
        Topology topo,
        bool[] protectedCell,
        SearchBudget budget,
        int costCap,
        int maxSols,
        System.Random rng,
        ref CountResult solve)
    {
        if (solve == null || AcceptSolCount(solve, maxSols)) return;

        List<int> optCells = new List<int>();
        CollectPathCells(level, solve.SamplePath, optCells);

        List<int> cutCells = new List<int>();
        AppendFreeCutCells(topo, level, protectedCell, optCells, topo.SoftBridge, cutCells, 8);
        AppendFreeCutCells(topo, level, protectedCell, optCells, topo.Choke, cutCells, 8);
        AppendFreeCutCells(topo, level, protectedCell, optCells, topo.Intersection, cutCells, 8);
        if (cutCells.Count == 0) return;

        ShuffleTop(cutCells, rng, cutCells.Count < 4 ? cutCells.Count : 4);

        List<LevelObjectData> objects = level.Objects;
        List<int> movers = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectType t = objects[i].Type;
            if (t != LevelObjectType.Rock && t != LevelObjectType.Enemy && t != LevelObjectType.Spike)
            {
                continue;
            }
            int cell = objects[i].Y * level.Width + objects[i].X;
            if (protectedCell != null && cell >= 0 && cell < protectedCell.Length && protectedCell[cell])
            {
                continue;
            }
            movers.Add(objects[i].Id);
        }
        if (movers.Count == 0) return;

        ShuffleTop(movers, rng, movers.Count < 4 ? movers.Count : 4);
        int bestTotal = solve.Total;
        if (bestTotal < 1) bestTotal = 999;
        CountResult best = solve;
        int tries = 0;
        for (int m = 0; m < movers.Count && tries < 8; m++)
        {
            int id = movers[m];
            int fromCell = -1;
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i].Id != id) continue;
                fromCell = objects[i].Y * level.Width + objects[i].X;
                break;
            }
            if (fromCell < 0) continue;

            for (int c = 0; c < cutCells.Count && tries < 8; c++)
            {
                int toCell = cutCells[c];
                if (toCell == fromCell) continue;
                if (!CellEmptyForObject(level, toCell)) continue;
                if (!MoveObjectIdToCell(level, id, toCell)) continue;
                level.SyncDerivedFields();
                tries++;

                CountResult probe = CountSolutionsAccept(level, budget, costCap, maxSols);
                if (AcceptSolCount(probe, maxSols))
                {
                    solve = probe;
                    return;
                }
                if (probe.MinimumMoves >= 0 && probe.Total > 0 && probe.Total < bestTotal
                    && probe.Status != CountStatus.Unsolvable)
                {
                    bestTotal = probe.Total;
                    best = probe;
                    fromCell = toCell;
                    continue;
                }
                MoveObjectIdToCell(level, id, fromCell);
                level.SyncDerivedFields();
            }
        }
        if (best != null && best.MinimumMoves >= 0)
        {
            solve = best;
        }
    }

    private static void AppendFreeCutCells(
        Topology topo, LevelData level, bool[] protectedCell, List<int> optCells,
        List<int> source, List<int> dest, int cap)
    {
        for (int i = 0; i < source.Count && dest.Count < cap; i++)
        {
            int cell = source[i];
            if (cell == topo.Start || cell == topo.Goal) continue;
            if (IsOnList(optCells, cell)) continue;
            if (protectedCell != null && cell >= 0 && cell < protectedCell.Length && protectedCell[cell])
            {
                continue;
            }
            if (!CellEmptyForObject(level, cell)) continue;
            if (!IsOnList(dest, cell)) dest.Add(cell);
        }
    }

    private static bool CellEmptyForObject(LevelData level, int cell)
    {
        int x = cell % level.Width;
        int y = cell / level.Width;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.X != x || obj.Y != y) continue;
            if (obj.Type == LevelObjectType.Floor || obj.Type == LevelObjectType.RequiredZone) continue;
            return false;
        }
        return true;
    }

    private static bool MoveObjectIdToCell(LevelData level, int id, int cell)
    {
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Id != id) continue;
            objects[i].X = cell % level.Width;
            objects[i].Y = cell / level.Width;
            return true;
        }
        return false;
    }

    private static void CollectPathCells(LevelData level, List<LevelDir> path, List<int> cells)
    {
        cells.Clear();
        if (path == null || level == null) return;
        LevelLogic logic = new LevelLogic(level);
        LevelSimState state = logic.CreateInitialState(level);
        if (state.PlayerIndex >= 0) cells.Add(state.PlayerIndex);
        for (int i = 0; i < path.Count; i++)
        {
            logic.TryMove(ref state, path[i], false);
            if (state.PlayerIndex >= 0 && !IsOnList(cells, state.PlayerIndex))
            {
                cells.Add(state.PlayerIndex);
            }
            if (state.Dead && !state.Won) break;
        }
    }

    private static CountResult CountSolutions(LevelData level, int abortAbove, SearchBudget budget, int costCap)
    {
        CountResult result = new CountResult();
        budget.Begin();
        int savedLimit = level.MoveLimit;
        int cap = costCap;
        if (cap <= 0)
        {
            cap = level.Width * level.Height + 32;
        }
        level.MoveLimit = cap;
        int maxStates = budget.StateLimit;
        if (maxStates > 40000) maxStates = 40000;
        if (maxStates < 8000) maxStates = 8000;
        if (abortAbove < 2) abortAbove = 2;
        LevelSolveResult solve = LevelSolver.Solve(level, maxStates, false, abortAbove);
        level.MoveLimit = savedLimit;

        if (solve.MinimumMoves >= 0 && solve.Success)
        {
            result.MinimumMoves = solve.MinimumMoves;
            result.Total = solve.SolutionCountAtMinimum;
            if (result.Total < 1) result.Total = 1;
            if (solve.OptimalSolutionsCapped && result.Total < abortAbove)
            {
                result.Total = abortAbove;
            }
            result.ByMoves[solve.MinimumMoves] = result.Total;
            result.SamplePath.Clear();
            for (int i = 0; i < solve.OptimalPath.Count; i++)
            {
                result.SamplePath.Add(solve.OptimalPath[i]);
            }
            if (solve.OptimalSolutionsCapped || solve.StateLimitReached)
            {
                result.Status = CountStatus.Inconclusive;
            }
            else
            {
                result.Status = CountStatus.Ok;
            }
            return result;
        }

        if (solve.StateLimitReached && solve.MinimumMoves >= 0)
        {
            result.MinimumMoves = solve.MinimumMoves;
            result.Total = abortAbove > 0 ? abortAbove : 999;
            result.Status = CountStatus.Inconclusive;
            return result;
        }

        result.Status = CountStatus.Unsolvable;
        return result;
    }

    private static int MeasureDependencyDepth(LevelData level, List<LevelDir> path)
    {
        if (path == null || path.Count == 0) return 0;
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
            if (state.Dead && !state.Won) break;
        }
        return depth;
    }

    private static int CountDecisionPoints(LevelData level, List<LevelDir> path)
    {
        if (path == null || path.Count == 0) return 0;
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
                if (probe.Dead && !probe.Won) continue;
                if (logic.StatesEqual(ref probe, ref state) && !probe.Won) continue;
                options++;
            }
            if (options >= 2) decisions++;
            logic.TryMove(ref state, path[i], false);
            if (state.Dead && !state.Won) break;
        }
        return decisions;
    }
}
