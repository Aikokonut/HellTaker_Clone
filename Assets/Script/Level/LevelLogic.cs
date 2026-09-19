using System;

public enum LevelActionResult
{
    Moved = 0,
    Pushed = 1,
    Kicked = 2,
    Bumped = 3,
    Died = 4,
    Won = 5,
    BlockedDoor = 6,
    OpenedDoor = 7,
    CollectedKey = 8,
    SpikePenalty = 9
}

public enum LevelDir
{
    None = -1,
    Up = 0,
    Right = 1,
    Down = 2,
    Left = 3
}

public struct LevelSimState
{
    public int PlayerIndex;
    public ulong EnemyMask0;
    public ulong EnemyMask1;
    public ulong EnemyMask2;
    public ulong EnemyMask3;
    public ulong RockMask0;
    public ulong RockMask1;
    public ulong RockMask2;
    public ulong RockMask3;
    public int KeysCollected;
    public ulong ClosedDoorMask;
    public ulong KeyPresentMask;
    public ulong ZoneVisitedMask;
    public int MovesUsed;
    public bool Dead;
    public bool Won;
}

public sealed class LevelLogic
{
    public const int MaxCells = 256;
    public const int MaxDoors = 64;
    public const int MaxKeys = 64;
    public const int MaxZones = 64;

    private readonly int width;
    private readonly int height;
    private readonly int cellCount;
    private readonly int moveLimit;
    private readonly bool[] solid;
    private readonly bool[] floor;
    private readonly bool[] spike;
    private readonly int[] doorIndexByCell;
    private readonly int[] keyIndexByCell;
    private readonly int[] zoneIndexByCell;
    private readonly int goalIndex;
    private readonly int startIndex;
    private readonly int doorCount;
    private readonly int keyCount;
    private readonly int zoneCount;
    private readonly int[] doorCells;
    private readonly int[] keyCells;

    public int Width { get { return width; } }
    public int Height { get { return height; } }
    public int CellCount { get { return cellCount; } }
    public int MoveLimit { get { return moveLimit; } }
    public int GoalIndex { get { return goalIndex; } }
    public int StartIndex { get { return startIndex; } }
    public int DoorCount { get { return doorCount; } }
    public int KeyCount { get { return keyCount; } }
    public int ZoneCount { get { return zoneCount; } }

    public LevelLogic(LevelData data)
    {
        width = data.Width;
        height = data.Height;
        cellCount = width * height;
        moveLimit = data.MoveLimit;
        solid = new bool[cellCount];
        floor = new bool[cellCount];
        spike = new bool[cellCount];
        doorIndexByCell = new int[cellCount];
        keyIndexByCell = new int[cellCount];
        zoneIndexByCell = new int[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            doorIndexByCell[i] = -1;
            keyIndexByCell[i] = -1;
            zoneIndexByCell[i] = -1;
        }

        int tempDoorCount = 0;
        int tempKeyCount = 0;
        int tempGoal = -1;
        int tempStart = -1;
        System.Collections.Generic.List<int> zoneCells = new System.Collections.Generic.List<int>();
        System.Collections.Generic.List<LevelObjectData> objects = data.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (!InBounds(obj.X, obj.Y))
            {
                continue;
            }
            int index = ToIndex(obj.X, obj.Y);
            LevelObjectType type = obj.Type;
            if (type == LevelObjectType.Floor)
            {
                floor[index] = true;
            }
            else if (type == LevelObjectType.Wall || type == LevelObjectType.Boundary)
            {
                solid[index] = true;
            }
            else if (type == LevelObjectType.Spike)
            {
                spike[index] = true;
                floor[index] = true;
            }
            else if (type == LevelObjectType.Door)
            {
                if (tempDoorCount < MaxDoors)
                {
                    doorIndexByCell[index] = tempDoorCount;
                    tempDoorCount++;
                }
                floor[index] = true;
            }
            else if (type == LevelObjectType.Key)
            {
                if (tempKeyCount < MaxKeys)
                {
                    keyIndexByCell[index] = tempKeyCount;
                    tempKeyCount++;
                }
                floor[index] = true;
            }
            else if (type == LevelObjectType.Goal)
            {
                tempGoal = index;
                floor[index] = true;
            }
            else if (type == LevelObjectType.PlayerStart)
            {
                tempStart = index;
                floor[index] = true;
            }
            else if (type == LevelObjectType.Enemy)
            {
                floor[index] = true;
            }
            else if (type == LevelObjectType.Rock)
            {
                floor[index] = true;
            }
            else if (type == LevelObjectType.RequiredZone)
            {
                floor[index] = true;
                zoneCells.Add(index);
            }
        }

        doorCount = tempDoorCount;
        keyCount = tempKeyCount;
        goalIndex = tempGoal;
        startIndex = tempStart;
        doorCells = new int[doorCount];
        keyCells = new int[keyCount];
        for (int i = 0; i < cellCount; i++)
        {
            int d = doorIndexByCell[i];
            if (d >= 0)
            {
                doorCells[d] = i;
            }
            int k = keyIndexByCell[i];
            if (k >= 0)
            {
                keyCells[k] = i;
            }
        }

        zoneCount = BuildRequiredZones(zoneCells);
    }

    private int BuildRequiredZones(System.Collections.Generic.List<int> zoneCells)
    {
        if (zoneCells == null || zoneCells.Count == 0)
        {
            return 0;
        }
        bool[] marked = new bool[cellCount];
        int zones = 0;
        for (int i = 0; i < zoneCells.Count; i++)
        {
            int seed = zoneCells[i];
            if (marked[seed] || zones >= MaxZones)
            {
                continue;
            }
            System.Collections.Generic.Queue<int> q = new System.Collections.Generic.Queue<int>();
            q.Enqueue(seed);
            marked[seed] = true;
            zoneIndexByCell[seed] = zones;
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                int x;
                int y;
                FromIndex(cur, out x, out y);
                TryZoneFlood(zoneCells, marked, q, zones, x + 1, y);
                TryZoneFlood(zoneCells, marked, q, zones, x - 1, y);
                TryZoneFlood(zoneCells, marked, q, zones, x, y + 1);
                TryZoneFlood(zoneCells, marked, q, zones, x, y - 1);
            }
            zones++;
        }
        return zones;
    }

    private void TryZoneFlood(
        System.Collections.Generic.List<int> zoneCells,
        bool[] marked,
        System.Collections.Generic.Queue<int> q,
        int zoneId,
        int x,
        int y)
    {
        if (!InBounds(x, y))
        {
            return;
        }
        int i = ToIndex(x, y);
        if (marked[i])
        {
            return;
        }
        bool isZone = false;
        for (int z = 0; z < zoneCells.Count; z++)
        {
            if (zoneCells[z] == i)
            {
                isZone = true;
                break;
            }
        }
        if (!isZone)
        {
            return;
        }
        marked[i] = true;
        zoneIndexByCell[i] = zoneId;
        q.Enqueue(i);
    }

    public bool InBounds(int x, int y)
    {
        return x >= 0 && y >= 0 && x < width && y < height;
    }

    public int ToIndex(int x, int y)
    {
        return y * width + x;
    }

    public void FromIndex(int index, out int x, out int y)
    {
        x = index % width;
        y = index / width;
    }

    public bool IsSolid(int index)
    {
        return solid[index];
    }

    public bool IsFloor(int index)
    {
        return floor[index];
    }

    public bool IsSpike(int index)
    {
        return spike[index];
    }

    public int GetDoorIndex(int cell)
    {
        return doorIndexByCell[cell];
    }

    public int GetKeyIndex(int cell)
    {
        return keyIndexByCell[cell];
    }

    public int GetDoorCell(int doorIndex)
    {
        return doorCells[doorIndex];
    }

    public int GetKeyCell(int keyIndex)
    {
        return keyCells[keyIndex];
    }

    public LevelSimState CreateInitialState(LevelData data)
    {
        LevelSimState state = new LevelSimState();
        state.PlayerIndex = startIndex;
        state.KeysCollected = 0;
        state.MovesUsed = 0;
        state.Dead = false;
        state.Won = false;
        state.ClosedDoorMask = 0UL;
        for (int i = 0; i < doorCount; i++)
        {
            state.ClosedDoorMask |= (1UL << i);
        }
        state.KeyPresentMask = 0UL;
        for (int i = 0; i < keyCount; i++)
        {
            state.KeyPresentMask |= (1UL << i);
        }
        state.EnemyMask0 = 0UL;
        state.EnemyMask1 = 0UL;
        state.EnemyMask2 = 0UL;
        state.EnemyMask3 = 0UL;
        state.RockMask0 = 0UL;
        state.RockMask1 = 0UL;
        state.RockMask2 = 0UL;
        state.RockMask3 = 0UL;
        System.Collections.Generic.List<LevelObjectData> objects = data.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (!InBounds(obj.X, obj.Y))
            {
                continue;
            }
            if (obj.Type == LevelObjectType.Enemy)
            {
                SetEnemy(ref state, ToIndex(obj.X, obj.Y), true);
            }
            else if (obj.Type == LevelObjectType.Rock)
            {
                SetRock(ref state, ToIndex(obj.X, obj.Y), true);
            }
        }
        state.ZoneVisitedMask = 0UL;
        if (state.PlayerIndex >= 0)
        {
            MarkZoneVisit(ref state, state.PlayerIndex);
        }
        return state;
    }

    public int GetZoneIndex(int cell)
    {
        if (cell < 0 || cell >= cellCount)
        {
            return -1;
        }
        return zoneIndexByCell[cell];
    }

    public bool AllZonesVisited(ref LevelSimState state)
    {
        if (zoneCount <= 0)
        {
            return true;
        }
        ulong need = zoneCount >= 64 ? ulong.MaxValue : ((1UL << zoneCount) - 1UL);
        return (state.ZoneVisitedMask & need) == need;
    }

    private void MarkZoneVisit(ref LevelSimState state, int cell)
    {
        if (cell < 0 || cell >= cellCount)
        {
            return;
        }
        int z = zoneIndexByCell[cell];
        if (z >= 0 && z < MaxZones)
        {
            state.ZoneVisitedMask |= (1UL << z);
        }
    }

    public static bool HasEnemy(ref LevelSimState state, int index)
    {
        int lane = index >> 6;
        ulong bit = 1UL << (index & 63);
        if (lane == 0) return (state.EnemyMask0 & bit) != 0UL;
        if (lane == 1) return (state.EnemyMask1 & bit) != 0UL;
        if (lane == 2) return (state.EnemyMask2 & bit) != 0UL;
        return (state.EnemyMask3 & bit) != 0UL;
    }

    public static void SetEnemy(ref LevelSimState state, int index, bool present)
    {
        int lane = index >> 6;
        ulong bit = 1UL << (index & 63);
        if (lane == 0)
        {
            if (present) state.EnemyMask0 |= bit;
            else state.EnemyMask0 &= ~bit;
        }
        else if (lane == 1)
        {
            if (present) state.EnemyMask1 |= bit;
            else state.EnemyMask1 &= ~bit;
        }
        else if (lane == 2)
        {
            if (present) state.EnemyMask2 |= bit;
            else state.EnemyMask2 &= ~bit;
        }
        else
        {
            if (present) state.EnemyMask3 |= bit;
            else state.EnemyMask3 &= ~bit;
        }
    }

    public static bool HasRock(ref LevelSimState state, int index)
    {
        int lane = index >> 6;
        ulong bit = 1UL << (index & 63);
        if (lane == 0) return (state.RockMask0 & bit) != 0UL;
        if (lane == 1) return (state.RockMask1 & bit) != 0UL;
        if (lane == 2) return (state.RockMask2 & bit) != 0UL;
        return (state.RockMask3 & bit) != 0UL;
    }

    public static void SetRock(ref LevelSimState state, int index, bool present)
    {
        int lane = index >> 6;
        ulong bit = 1UL << (index & 63);
        if (lane == 0)
        {
            if (present) state.RockMask0 |= bit;
            else state.RockMask0 &= ~bit;
        }
        else if (lane == 1)
        {
            if (present) state.RockMask1 |= bit;
            else state.RockMask1 &= ~bit;
        }
        else if (lane == 2)
        {
            if (present) state.RockMask2 |= bit;
            else state.RockMask2 &= ~bit;
        }
        else
        {
            if (present) state.RockMask3 |= bit;
            else state.RockMask3 &= ~bit;
        }
    }

    public static int DirToDx(LevelDir dir)
    {
        if (dir == LevelDir.Left) return -1;
        if (dir == LevelDir.Right) return 1;
        return 0;
    }

    public static int DirToDy(LevelDir dir)
    {
        if (dir == LevelDir.Up) return 1;
        if (dir == LevelDir.Down) return -1;
        return 0;
    }

    public bool IsCellBlockedForEntity(ref LevelSimState state, int index, bool ignorePlayer)
    {
        if (index < 0 || index >= cellCount)
        {
            return true;
        }
        if (solid[index])
        {
            return true;
        }
        if (!floor[index])
        {
            return true;
        }
        int door = doorIndexByCell[index];
        if (door >= 0 && ((state.ClosedDoorMask & (1UL << door)) != 0UL))
        {
            return true;
        }
        if (HasEnemy(ref state, index))
        {
            return true;
        }
        if (HasRock(ref state, index))
        {
            return true;
        }
        if (!ignorePlayer && index == state.PlayerIndex)
        {
            return true;
        }
        return false;
    }

    public LevelActionResult TryMove(ref LevelSimState state, LevelDir dir)
    {
        return TryMove(ref state, dir, true);
    }

    public LevelActionResult TryMove(ref LevelSimState state, LevelDir dir, bool enforceMoveLimit)
    {
        if (state.Dead || state.Won)
        {
            return LevelActionResult.Bumped;
        }
        if (state.PlayerIndex < 0)
        {
            state.Dead = true;
            return LevelActionResult.Died;
        }

        int px;
        int py;
        FromIndex(state.PlayerIndex, out px, out py);
        int nx = px + DirToDx(dir);
        int ny = py + DirToDy(dir);

        // Helltaker: invalid bump into void/wall spends no turn.
        if (!InBounds(nx, ny))
        {
            return LevelActionResult.Bumped;
        }
        int next = ToIndex(nx, ny);
        if (solid[next] || !floor[next])
        {
            return LevelActionResult.Bumped;
        }

        int door = doorIndexByCell[next];
        if (door >= 0 && ((state.ClosedDoorMask & (1UL << door)) != 0UL))
        {
            // Locked door without key: no turn.
            if (state.KeysCollected <= 0)
            {
                return LevelActionResult.BlockedDoor;
            }
            state.MovesUsed++;
            state.KeysCollected--;
            state.ClosedDoorMask &= ~(1UL << door);
            state.PlayerIndex = next;
            return FinishEnter(ref state, LevelActionResult.OpenedDoor, enforceMoveLimit);
        }

        if (HasEnemy(ref state, next))
        {
            int bx = nx + DirToDx(dir);
            int by = ny + DirToDy(dir);
            if (!InBounds(bx, by))
            {
                // Kick into map edge: destroys enemy, player stays, costs 1 turn.
                state.MovesUsed++;
                SetEnemy(ref state, next, false);
                return FinishStayAction(ref state, LevelActionResult.Kicked, enforceMoveLimit);
            }
            int behind = ToIndex(bx, by);
            // Blocked by another enemy/rock: no turn (cannot attack).
            if (HasEnemy(ref state, behind) || HasRock(ref state, behind))
            {
                return LevelActionResult.Bumped;
            }
            if (IsEnemyDestroyDestination(ref state, behind))
            {
                state.MovesUsed++;
                SetEnemy(ref state, next, false);
                return FinishStayAction(ref state, LevelActionResult.Kicked, enforceMoveLimit);
            }
            // Push enemy one tile; player stays; costs 1 turn.
            state.MovesUsed++;
            SetEnemy(ref state, next, false);
            SetEnemy(ref state, behind, true);
            return FinishStayAction(ref state, LevelActionResult.Pushed, enforceMoveLimit);
        }

        if (HasRock(ref state, next))
        {
            int bx = nx + DirToDx(dir);
            int by = ny + DirToDy(dir);
            if (!InBounds(bx, by))
            {
                return LevelActionResult.Bumped;
            }
            int behind = ToIndex(bx, by);
            if (!CanPushRockInto(ref state, behind))
            {
                // Cannot push: no turn.
                return LevelActionResult.Bumped;
            }
            state.MovesUsed++;
            SetRock(ref state, next, false);
            SetRock(ref state, behind, true);
            return FinishStayAction(ref state, LevelActionResult.Pushed, enforceMoveLimit);
        }

        // Normal walk onto empty floor/open door cell.
        state.MovesUsed++;
        state.PlayerIndex = next;
        return FinishEnter(ref state, LevelActionResult.Moved, enforceMoveLimit);
    }

    private bool CanPushRockInto(ref LevelSimState state, int index)
    {
        if (index < 0 || index >= cellCount)
        {
            return false;
        }
        if (solid[index] || !floor[index])
        {
            return false;
        }
        int door = doorIndexByCell[index];
        if (door >= 0 && ((state.ClosedDoorMask & (1UL << door)) != 0UL))
        {
            return false;
        }
        if (HasEnemy(ref state, index) || HasRock(ref state, index))
        {
            return false;
        }
        return true;
    }

    private bool IsEnemyDestroyDestination(ref LevelSimState state, int index)
    {
        if (index < 0 || index >= cellCount)
        {
            return true;
        }
        if (solid[index] || !floor[index] || spike[index])
        {
            return true;
        }
        int door = doorIndexByCell[index];
        if (door >= 0 && ((state.ClosedDoorMask & (1UL << door)) != 0UL))
        {
            return true;
        }
        return false;
    }

    private LevelActionResult FinishStayAction(ref LevelSimState state, LevelActionResult baseResult, bool enforceMoveLimit)
    {
        // Helltaker: acting while standing on spikes takes spike damage once (+1 move).
        if (state.PlayerIndex >= 0 && spike[state.PlayerIndex])
        {
            state.MovesUsed++;
            return ApplyMoveBudget(ref state, LevelActionResult.SpikePenalty, enforceMoveLimit);
        }
        return ApplyMoveBudget(ref state, baseResult, enforceMoveLimit);
    }

    private LevelActionResult ApplyMoveBudget(ref LevelSimState state, LevelActionResult result, bool enforceMoveLimit)
    {
        if (enforceMoveLimit && moveLimit > 0 && state.MovesUsed >= moveLimit && !state.Won)
        {
            state.Dead = true;
            return LevelActionResult.Died;
        }
        return result;
    }

    private LevelActionResult FinishEnter(ref LevelSimState state, LevelActionResult baseResult, bool enforceMoveLimit)
    {
        int cell = state.PlayerIndex;
        MarkZoneVisit(ref state, cell);
        int key = keyIndexByCell[cell];
        if (key >= 0 && ((state.KeyPresentMask & (1UL << key)) != 0UL))
        {
            state.KeyPresentMask &= ~(1UL << key);
            state.KeysCollected++;
            baseResult = LevelActionResult.CollectedKey;
        }
        if (cell == goalIndex && AllZonesVisited(ref state))
        {
            state.Won = true;
            return LevelActionResult.Won;
        }
        if (spike[cell])
        {
            state.MovesUsed++;
            return ApplyMoveBudget(ref state, LevelActionResult.SpikePenalty, enforceMoveLimit);
        }
        return ApplyMoveBudget(ref state, baseResult, enforceMoveLimit);
    }

    public long PackStateKey(ref LevelSimState state)
    {
        unchecked
        {
            long hash = 1469598103934665603L;
            hash = (hash ^ state.PlayerIndex) * 1099511628211L;
            hash = (hash ^ (long)state.EnemyMask0) * 1099511628211L;
            hash = (hash ^ (long)state.EnemyMask1) * 1099511628211L;
            hash = (hash ^ (long)state.EnemyMask2) * 1099511628211L;
            hash = (hash ^ (long)state.EnemyMask3) * 1099511628211L;
            hash = (hash ^ (long)state.RockMask0) * 1099511628211L;
            hash = (hash ^ (long)state.RockMask1) * 1099511628211L;
            hash = (hash ^ (long)state.RockMask2) * 1099511628211L;
            hash = (hash ^ (long)state.RockMask3) * 1099511628211L;
            hash = (hash ^ state.KeysCollected) * 1099511628211L;
            hash = (hash ^ (long)state.ClosedDoorMask) * 1099511628211L;
            hash = (hash ^ (long)state.KeyPresentMask) * 1099511628211L;
            hash = (hash ^ (long)state.ZoneVisitedMask) * 1099511628211L;
            return hash;
        }
    }

    public bool StatesEqual(ref LevelSimState a, ref LevelSimState b)
    {
        return a.PlayerIndex == b.PlayerIndex
            && a.EnemyMask0 == b.EnemyMask0
            && a.EnemyMask1 == b.EnemyMask1
            && a.EnemyMask2 == b.EnemyMask2
            && a.EnemyMask3 == b.EnemyMask3
            && a.RockMask0 == b.RockMask0
            && a.RockMask1 == b.RockMask1
            && a.RockMask2 == b.RockMask2
            && a.RockMask3 == b.RockMask3
            && a.KeysCollected == b.KeysCollected
            && a.ClosedDoorMask == b.ClosedDoorMask
            && a.KeyPresentMask == b.KeyPresentMask
            && a.ZoneVisitedMask == b.ZoneVisitedMask;
    }
}
