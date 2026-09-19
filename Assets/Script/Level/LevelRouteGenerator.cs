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
/// Path-first generator: pick Start→Zones→Goal route, then install objects ON that route.
/// Each object must change OptimalCost or appear as Push/Kick/Spike on the optimal path.
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

        Topology topo;
        string topoError;
        if (!TryBuildTopology(baseCopy, out topo, out topoError))
        {
            Object.DestroyImmediate(baseCopy);
            result.Message = "Generation Failed: " + topoError;
            return result;
        }

        for (int z = 0; z < topo.Zones.Count; z++)
        {
            if (!ZoneReachable(topo, topo.Zones[z]))
            {
                Object.DestroyImmediate(baseCopy);
                result.Message = "Generation Failed: Required Zone " + z + " unreachable.";
                return result;
            }
        }

        SearchBudget budget = new SearchBudget(genParams.StateLimit, genParams.TimeLimitMs);
        CountResult baseSolve = CountSolutions(baseCopy, 16, budget, 0);
        if (baseSolve.Status != CountStatus.Ok || baseSolve.MinimumMoves < 0)
        {
            Object.DestroyImmediate(baseCopy);
            result.Message = "Generation Failed: base map must be solvable before placing objects.";
            return result;
        }

        int baseCost = baseSolve.MinimumMoves;
        int costCap = baseCost + costSlack;
        result.BaseShortest = topo.ShortestLen;

        System.Random rng = genParams.Seed != 0
            ? new System.Random(genParams.Seed)
            : new System.Random();

        List<List<int>> skeletons = BuildRouteSkeletons(topo, rng);
        if (skeletons.Count == 0)
        {
            Object.DestroyImmediate(baseCopy);
            result.Message = "Generation Failed: no route skeletons through Required Zones.";
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

        while (attempt < genParams.MaxAttempts && found.Count < maxKeep)
        {
            attempt++;
            result.AttemptsUsed = attempt;
            List<int> skeleton = skeletons[skelIndex % skeletons.Count];
            skelIndex++;

            LevelData candidate = baseCopy.CreateRuntimeCopy();
            StripGeneratedTypes(candidate, protectedCell);

            PlacementState place = new PlacementState(topo.CellCount);
            SeedUsed(candidate, place, protectedCell);

            CountResult built;
            string placeError;
            if (!PathFirstPlace(
                candidate, topo, skeleton, genParams, rng, place, budget, costCap, baseCost,
                out built, out placeError))
            {
                Object.DestroyImmediate(candidate);
                lastError = placeError;
                continue;
            }

            if (!CountsWithinMax(candidate, genParams, out placeError))
            {
                Object.DestroyImmediate(candidate);
                lastError = placeError;
                continue;
            }

            LevelValidationResult v = LevelValidator.Validate(candidate);
            if (!v.IsValid)
            {
                Object.DestroyImmediate(candidate);
                lastError = v.Issues.Count > 0 ? v.Issues[0].Message : "invalid";
                continue;
            }

            int impactCount;
            string impactError;
            if (!AllGeneratedHaveImpact(candidate, protectedCell, budget, costCap, out impactCount, out impactError))
            {
                Object.DestroyImmediate(candidate);
                lastError = "Impact: " + impactError;
                continue;
            }

            if (!OptimalPathUsesPlacedObjects(candidate, built.SamplePath, out impactError))
            {
                Object.DestroyImmediate(candidate);
                lastError = "Path: " + impactError;
                continue;
            }

            int maxSols = genParams.MaxTargetSolutions;
            if (maxSols < 1) maxSols = 1;
            if (built.Total > maxSols)
            {
                Object.DestroyImmediate(candidate);
                lastError = "SolutionCount " + built.Total + " > MaxTargetSolutions " + maxSols + ".";
                continue;
            }

            int nearCount = 0;
            int nearCost = -1;
            ProbeNearMiss(candidate, built.MinimumMoves, budget, costCap + 2, out nearCount, out nearCost);

            int depth = MeasureDependencyDepth(candidate, built.SamplePath);
            int decisions = CountDecisionPoints(candidate, built.SamplePath);
            int diversity = EstimateRouteDiversity(topo, skeleton);
            int score = ScoreCandidate(
                built.MinimumMoves, 0, built.Total, depth, decisions,
                nearCount, nearCost, impactCount, diversity, 0);
            score += (built.MinimumMoves - baseCost) * 8;
            score += depth * 15;

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
            info.Summary = "opt=" + built.MinimumMoves + " base=" + baseCost
                + " sols=" + built.Total + " impact=" + impactCount + " depth=" + depth;
            InsertCandidate(found, info, maxKeep);
        }

        if (found.Count == 0)
        {
            Object.DestroyImmediate(baseCopy);
            result.Message = "Generation Failed after " + genParams.MaxAttempts
                + " attempt(s). Last: " + lastError
                + " Tip: narrower corridors, lower counts, or raise CostSlack.";
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
        result.PatternUsed = "PathFirst";
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
        if (solutions >= 1 && solutions <= 5) score += 25;
        else if (solutions > 8) score -= (solutions - 8) * 4;
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
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type == LevelObjectType.Enemy
                || obj.Type == LevelObjectType.Rock
                || obj.Type == LevelObjectType.Spike)
            {
                objects.RemoveAt(i);
                continue;
            }
            if (obj.Type == LevelObjectType.Floor || obj.Type == LevelObjectType.RequiredZone)
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
            objects.RemoveAt(i);
        }
        level.SyncDerivedFields();
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
        public readonly List<int> Intersection = new List<int>();
        public readonly List<int> DeadEnd = new List<int>();
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
        }

        // Zones from LevelLogic connected components.
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
        List<int[]> orders = BuildZoneOrders(topo.ZoneRep.Count, rng);
        for (int o = 0; o < orders.Count; o++)
        {
            int[] order = orders[o];
            List<int> waypoints = new List<int>();
            waypoints.Add(topo.Start);
            for (int i = 0; i < order.Length; i++)
            {
                waypoints.Add(topo.ZoneRep[order[i]]);
            }
            waypoints.Add(topo.Goal);

            List<int> path = ConnectWaypoints(topo, waypoints, false);
            if (path != null && path.Count > 0)
            {
                skeletons.Add(path);
            }
            List<int> biased = ConnectWaypoints(topo, waypoints, true);
            if (biased != null && biased.Count > 0 && !SamePath(path, biased))
            {
                skeletons.Add(biased);
            }
        }
        // Also a start→goal detour through a choke/intersection if no zones.
        if (topo.ZoneRep.Count == 0)
        {
            List<int> direct = new List<int>();
            if (BuildPath(topo, topo.Walkable, topo.Start, topo.Goal, direct, false))
            {
                skeletons.Add(direct);
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
        if (IsOnList(topo.Choke, cell)) s += 3;
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
        out CountResult finalSolve,
        out string error)
    {
        finalSolve = null;
        error = null;

        List<int> pathSlots = BuildPathSlots(topo, skeleton, place);
        if (pathSlots.Count == 0)
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
        // Spikes first (cheap +1 each) so count can fill before Rock eats the budget.
        PlaceOnPath(
            level, topo, place, pathSlots, skeleton, LevelObjectType.Spike, needSpike,
            LevelActionResult.SpikePenalty, false, rng, budget, costCap, ref currentCost);
        PlaceOnPath(
            level, topo, place, pathSlots, skeleton, LevelObjectType.Rock, needRock,
            LevelActionResult.Pushed, true, rng, budget, costCap, ref currentCost);
        PlaceOnPath(
            level, topo, place, pathSlots, skeleton, LevelObjectType.Enemy, needEnemy,
            LevelActionResult.Kicked, true, rng, budget, costCap, ref currentCost);

        int placedTotal = place.Rock + place.Enemy + place.Spike;
        int wantTotal = needRock + needEnemy + needSpike;
        if (wantTotal > 0 && placedTotal == 0)
        {
            error = "No meaningful objects placed on route (need choke/bridge cells).";
            return false;
        }

        level.SyncDerivedFields();
        finalSolve = CountSolutions(level, 32, budget, costCap);
        if (finalSolve.Status != CountStatus.Ok || finalSolve.MinimumMoves < 0)
        {
            error = "Final solve failed within CostSlack.";
            return false;
        }

        // Spikes placed later can reroute away from Kick — strip unused interactions.
        int stripped = StripUnusedInteractions(level, finalSolve.SamplePath, place);
        stripped += StripZeroImpactObjects(level, place, budget, costCap, finalSolve.MinimumMoves);
        if (stripped > 0)
        {
            finalSolve = CountSolutions(level, 32, budget, costCap);
            if (finalSolve.Status != CountStatus.Ok || finalSolve.MinimumMoves < 0)
            {
                error = "Solve failed after stripping unused objects.";
                return false;
            }
        }

        placedTotal = place.Rock + place.Enemy + place.Spike;
        if (wantTotal > 0 && placedTotal == 0)
        {
            error = "No meaningful objects remained on optimal path.";
            return false;
        }

        // Budget = CostSlack only (softCap shortLen/2 was rejecting Spike=3 after Rock +5).
        if (finalSolve.MinimumMoves > costCap)
        {
            error = "OptimalCost " + finalSolve.MinimumMoves + " exceeds costCap " + costCap
                + " (base=" + baseCost + ").";
            return false;
        }

        return true;
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
        PreferInteresting(topo, slots);
        return slots;
    }

    private static void PlaceOnPath(
        LevelData level, Topology topo, PlacementState place, List<int> pathSlots, List<int> skeleton,
        LevelObjectType type, int want, LevelActionResult requiredAction, bool requirePushAxis,
        System.Random rng, SearchBudget budget, int costCap, ref int currentCost)
    {
        if (want <= 0) return;

        List<int> pool = new List<int>();
        for (int i = 0; i < pathSlots.Count; i++)
        {
            int c = pathSlots[i];
            if (place.Used[c]) continue;
            if (requirePushAxis && !HasPushAxis(topo, place, c)) continue;
            pool.Add(c);
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
        }
        // Include low-degree corridor cells even if not marked choke.
        for (int i = 0; i < topo.CellCount; i++)
        {
            if (!topo.Walkable[i] || place.Used[i]) continue;
            if (i == topo.Start || i == topo.Goal) continue;
            if (Degree(topo, i) > 2) continue;
            if (requirePushAxis && !HasPushAxis(topo, place, i)) continue;
            if (!IsOnList(pool, i)) pool.Add(i);
        }

        PreferBridges(topo, pool);
        PreferInteresting(topo, pool);
        PreferPathOrder(pool, skeleton, rng);
        ShuffleTop(pool, rng, pool.Count < 4 ? pool.Count : 4);

        int placed = 0;
        for (int i = 0; i < pool.Count && placed < want; i++)
        {
            int cell = pool[i];
            if (place.Used[cell]) continue;
            int costBefore = currentCost;
            if (!TryPlace(level, topo, place, cell, type)) continue;
            level.SyncDerivedFields();

            CountResult probe = CountSolutions(level, 8, budget, costCap);
            if (probe.Status != CountStatus.Ok || probe.MinimumMoves < 0 || probe.MinimumMoves > costCap)
            {
                UndoLastPlace(level, place, cell, type);
                continue;
            }

            bool costUp = probe.MinimumMoves > costBefore;
            bool actionHit = SamplePathHasAction(level, probe.SamplePath, requiredAction);

            // Every object must raise OptimalCost.
            if (!costUp)
            {
                UndoLastPlace(level, place, cell, type);
                continue;
            }
            // Reject huge detours (log: Rock +5/+6 ate softCap and blocked Spike count).
            int delta = probe.MinimumMoves - costBefore;
            int maxDelta = type == LevelObjectType.Spike ? 2 : 3;
            if (delta > maxDelta)
            {
                UndoLastPlace(level, place, cell, type);
                continue;
            }
            if (type == LevelObjectType.Rock || type == LevelObjectType.Enemy)
            {
                if (!actionHit)
                {
                    UndoLastPlace(level, place, cell, type);
                    continue;
                }
            }

            currentCost = probe.MinimumMoves;
            placed++;
        }
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

    private static int StripUnusedInteractions(
        LevelData level, List<LevelDir> path, PlacementState place)
    {
        bool hasPush = SamplePathHasAction(level, path, LevelActionResult.Pushed);
        bool hasKick = SamplePathHasAction(level, path, LevelActionResult.Kicked);
        bool hasSpike = SamplePathHasAction(level, path, LevelActionResult.SpikePenalty);
        int stripped = 0;
        List<LevelObjectData> objects = level.Objects;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            LevelObjectData obj = objects[i];
            bool drop = false;
            if (obj.Type == LevelObjectType.Enemy && !hasKick) drop = true;
            else if (obj.Type == LevelObjectType.Rock && !hasPush) drop = true;
            else if (obj.Type == LevelObjectType.Spike && !hasSpike) drop = true;
            if (!drop) continue;
            int cell = obj.Y * level.Width + obj.X;
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

    private static int StripZeroImpactObjects(
        LevelData level, PlacementState place, SearchBudget budget, int costCap, int optWithAll)
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
            LevelObjectData held = objects[i];
            objects.RemoveAt(i);
            level.SyncDerivedFields();
            CountResult without = CountSolutions(level, 8, budget, costCap);
            if (without.Status == CountStatus.Ok && without.MinimumMoves == optWithAll)
            {
                int cell = held.Y * level.Width + held.X;
                if (cell >= 0 && cell < place.Used.Length) place.Used[cell] = false;
                if (held.Type == LevelObjectType.Enemy && place.Enemy > 0) place.Enemy--;
                else if (held.Type == LevelObjectType.Rock && place.Rock > 0) place.Rock--;
                else if (held.Type == LevelObjectType.Spike && place.Spike > 0) place.Spike--;
                stripped++;
                continue;
            }
            objects.Insert(i, held);
            level.SyncDerivedFields();
        }
        return stripped;
    }

    private static bool OptimalPathUsesPlacedObjects(LevelData level, List<LevelDir> path, out string error)
    {
        error = null;
        int rocks = 0;
        int enemies = 0;
        int spikes = 0;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Type == LevelObjectType.Rock) rocks++;
            else if (objects[i].Type == LevelObjectType.Enemy) enemies++;
            else if (objects[i].Type == LevelObjectType.Spike) spikes++;
        }

        if (rocks > 0 && !SamplePathHasAction(level, path, LevelActionResult.Pushed))
        {
            error = "Optimal path never Pushes a Rock.";
            return false;
        }
        if (enemies > 0 && !SamplePathHasAction(level, path, LevelActionResult.Kicked))
        {
            error = "Optimal path never Kicks an Enemy.";
            return false;
        }
        if (spikes > 0 && !SamplePathHasAction(level, path, LevelActionResult.SpikePenalty))
        {
            error = "Optimal path never steps a Spike.";
            return false;
        }
        return true;
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

    private static bool CountsWithinMax(
        LevelData level, LevelGenerateParams p, out string error)
    {
        error = null;
        int e = 0, r = 0, s = 0;
        List<LevelObjectData> objects = level.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Type == LevelObjectType.Enemy) e++;
            else if (objects[i].Type == LevelObjectType.Rock) r++;
            else if (objects[i].Type == LevelObjectType.Spike) s++;
        }
        if (e > p.EnemyCount)
        {
            error = "Enemy count " + e + " > max " + p.EnemyCount + ".";
            return false;
        }
        if (r > p.RockCount)
        {
            error = "Rock count " + r + " > max " + p.RockCount + ".";
            return false;
        }
        if (s > p.SpikeCount)
        {
            error = "Spike count " + s + " > max " + p.SpikeCount + ".";
            return false;
        }
        return true;
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

        CountResult withAll = CountSolutions(level, 16, budget, costCap);
        if (withAll.MinimumMoves < 0)
        {
            error = "Unsolvable before impact test.";
            return false;
        }

        for (int i = 0; i < generated.Count; i++)
        {
            LevelObjectData obj = generated[i];
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
            CountResult without = CountSolutions(level, 16, budget, costCap);
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

    private static void ProbeNearMiss(
        LevelData level, int optimal, SearchBudget budget, int costCap,
        out int nearCount, out int nearCost)
    {
        nearCount = 0;
        nearCost = -1;
        if (optimal < 0) return;
        int probeCap = costCap;
        if (probeCap < optimal + 2) probeCap = optimal + 2;
        CountResult open = CountSolutions(level, 16, budget, probeCap);
        int target = optimal + 1;
        int c;
        if (open.ByMoves.TryGetValue(target, out c) && c > 0)
        {
            nearCount = c;
            nearCost = target;
            return;
        }
        foreach (KeyValuePair<int, int> pair in open.ByMoves)
        {
            if (pair.Key > optimal && pair.Value > 0)
            {
                if (nearCost < 0 || pair.Key < nearCost)
                {
                    nearCost = pair.Key;
                    nearCount = pair.Value;
                }
            }
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
        if (maxStates < 10000)
        {
            maxStates = 10000;
        }
        LevelSolveResult solve = LevelSolver.Solve(level, maxStates, false, abortAbove);
        level.MoveLimit = savedLimit;

        if (solve.MinimumMoves >= 0 && solve.Success)
        {
            result.MinimumMoves = solve.MinimumMoves;
            result.Total = solve.SolutionCountAtMinimum;
            if (result.Total < 1)
            {
                result.Total = 1;
            }
            result.ByMoves[solve.MinimumMoves] = result.Total;
            result.SamplePath.Clear();
            for (int i = 0; i < solve.OptimalPath.Count; i++)
            {
                result.SamplePath.Add(solve.OptimalPath[i]);
            }
            result.Status = CountStatus.Ok;
            return result;
        }

        if (solve.StateLimitReached && solve.MinimumMoves >= 0)
        {
            result.MinimumMoves = solve.MinimumMoves;
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
