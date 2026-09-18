using System.Collections.Generic;
using UnityEngine;

public sealed class LevelValidationIssue
{
    [SerializeField] private string message;
    [SerializeField] private Vector2Int cell;
    [SerializeField] private bool isError;

    public string Message
    {
        get { return message; }
        set { message = value; }
    }

    public Vector2Int Cell
    {
        get { return cell; }
        set { cell = value; }
    }

    public bool IsError
    {
        get { return isError; }
        set { isError = value; }
    }

    public LevelValidationIssue(string message, Vector2Int cell, bool isError)
    {
        this.message = message;
        this.cell = cell;
        this.isError = isError;
    }
}

public sealed class LevelValidationResult
{
    public bool IsValid;
    public readonly List<LevelValidationIssue> Issues = new List<LevelValidationIssue>();
}

public static class LevelValidator
{
    public static LevelValidationResult Validate(LevelData data)
    {
        LevelValidationResult result = new LevelValidationResult();
        result.IsValid = true;
        if (data == null)
        {
            result.IsValid = false;
            result.Issues.Add(new LevelValidationIssue("LevelData is null.", new Vector2Int(-1, -1), true));
            return result;
        }

        if (data.Width <= 0 || data.Height <= 0)
        {
            AddError(result, "Grid size must be positive.", new Vector2Int(-1, -1));
        }
        if (data.Width * data.Height > LevelLogic.MaxCells)
        {
            AddError(result, "Grid exceeds solver max cell count (" + LevelLogic.MaxCells + ").", new Vector2Int(-1, -1));
        }
        if (data.MoveLimit < 0)
        {
            AddError(result, "MoveLimit cannot be negative.", new Vector2Int(-1, -1));
        }

        int playerCount = 0;
        int goalCount = 0;
        int doorCount = 0;
        int keyCount = 0;
        List<LevelObjectData> objects = data.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            Vector2Int cell = new Vector2Int(obj.X, obj.Y);
            if (obj.X < 0 || obj.Y < 0 || obj.X >= data.Width || obj.Y >= data.Height)
            {
                AddError(result, obj.Type + " outside grid.", cell);
                continue;
            }

            if (obj.Type == LevelObjectType.PlayerStart)
            {
                playerCount++;
            }
            else if (obj.Type == LevelObjectType.Goal)
            {
                goalCount++;
            }
            else if (obj.Type == LevelObjectType.Door)
            {
                doorCount++;
            }
            else if (obj.Type == LevelObjectType.Key)
            {
                keyCount++;
            }

            if (obj.Type == LevelObjectType.Floor)
            {
                continue;
            }
            for (int j = 0; j < i; j++)
            {
                LevelObjectData other = objects[j];
                if (other.Type == LevelObjectType.Floor)
                {
                    continue;
                }
                if (other.X != obj.X || other.Y != obj.Y)
                {
                    continue;
                }
                if (PlacementConflicts(obj.Type, other.Type))
                {
                    AddError(result, "Invalid overlap: " + obj.Type + " with " + other.Type + ".", cell);
                    break;
                }
            }
        }

        if (playerCount == 0)
        {
            AddError(result, "Exactly 1 Player Start is required.", new Vector2Int(-1, -1));
        }
        else if (playerCount > 1)
        {
            AddError(result, "Exactly 1 Player Start is required (found " + playerCount + ").", new Vector2Int(-1, -1));
        }

        if (goalCount == 0)
        {
            AddError(result, "Goal is required.", new Vector2Int(-1, -1));
        }
        else if (goalCount > 1)
        {
            AddError(result, "Only one Goal is allowed.", new Vector2Int(-1, -1));
        }

        if (doorCount > keyCount)
        {
            AddWarning(result, "More doors than keys (" + doorCount + " doors, " + keyCount + " keys).", new Vector2Int(-1, -1));
        }

        data.SyncDerivedFields();
        if (result.IsValid)
        {
            LevelLogic logic = new LevelLogic(data);
            if (logic.StartIndex < 0 || logic.GoalIndex < 0)
            {
                AddError(result, "Player Start or Goal missing after sync.", new Vector2Int(-1, -1));
            }
            else
            {
                CheckActorOnFloor(result, data, logic);
                CheckStaticReachability(result, logic, data);
            }
        }

        return result;
    }

    private static void CheckActorOnFloor(LevelValidationResult result, LevelData data, LevelLogic logic)
    {
        List<LevelObjectData> objects = data.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type == LevelObjectType.Floor || obj.Type == LevelObjectType.Wall || obj.Type == LevelObjectType.Boundary)
            {
                continue;
            }
            if (!logic.InBounds(obj.X, obj.Y))
            {
                continue;
            }
            int index = logic.ToIndex(obj.X, obj.Y);
            if (!logic.IsFloor(index) && !logic.IsSolid(index))
            {
                AddWarning(result, obj.Type + " is not on a floor cell.", new Vector2Int(obj.X, obj.Y));
            }
            if (logic.IsSolid(index) && obj.Type != LevelObjectType.Wall && obj.Type != LevelObjectType.Boundary)
            {
                AddError(result, obj.Type + " overlaps solid terrain.", new Vector2Int(obj.X, obj.Y));
            }
        }
    }

    private static void CheckStaticReachability(LevelValidationResult result, LevelLogic logic, LevelData data)
    {
        int cellCount = logic.CellCount;
        bool[] blocked = new bool[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            blocked[i] = logic.IsSolid(i) || !logic.IsFloor(i);
        }
        List<LevelObjectData> objects = data.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type == LevelObjectType.Door && logic.InBounds(obj.X, obj.Y))
            {
                blocked[logic.ToIndex(obj.X, obj.Y)] = true;
            }
        }

        bool[] visited = new bool[cellCount];
        int[] queue = new int[cellCount];
        int head = 0;
        int tail = 0;
        queue[tail++] = logic.StartIndex;
        visited[logic.StartIndex] = true;
        int[] dx = { 0, 1, 0, -1 };
        int[] dy = { 1, 0, -1, 0 };
        while (head < tail)
        {
            int current = queue[head++];
            int cx;
            int cy;
            logic.FromIndex(current, out cx, out cy);
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + dx[d];
                int ny = cy + dy[d];
                if (!logic.InBounds(nx, ny))
                {
                    continue;
                }
                int ni = logic.ToIndex(nx, ny);
                if (visited[ni] || blocked[ni])
                {
                    continue;
                }
                visited[ni] = true;
                queue[tail++] = ni;
            }
        }

        if (!visited[logic.GoalIndex])
        {
            bool goalNeedsKeyPath = false;
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i].Type == LevelObjectType.Door)
                {
                    goalNeedsKeyPath = true;
                    break;
                }
            }
            if (!goalNeedsKeyPath)
            {
                AddWarning(result, "Goal appears unreachable ignoring pushables (static floors/walls).", data.Goal);
            }
            else
            {
                AddWarning(result, "Goal unreachable without opening doors (check key access).", data.Goal);
            }
        }

        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Key)
            {
                continue;
            }
            if (!logic.InBounds(obj.X, obj.Y))
            {
                continue;
            }
            int index = logic.ToIndex(obj.X, obj.Y);
            if (!visited[index] && !blocked[index])
            {
                AddWarning(result, "Key may be unreachable from Player Start.", new Vector2Int(obj.X, obj.Y));
            }
            else if (blocked[index])
            {
                AddWarning(result, "Key is behind a closed door or blocked cell.", new Vector2Int(obj.X, obj.Y));
            }
        }
    }

    public static bool PlacementConflicts(LevelObjectType a, LevelObjectType b)
    {
        if (a == LevelObjectType.Floor || b == LevelObjectType.Floor)
        {
            return false;
        }
        if (a == LevelObjectType.Wall || a == LevelObjectType.Boundary
            || b == LevelObjectType.Wall || b == LevelObjectType.Boundary)
        {
            return true;
        }
        if (a == LevelObjectType.Key && AllowsKeyCoexistence(b))
        {
            return false;
        }
        if (b == LevelObjectType.Key && AllowsKeyCoexistence(a))
        {
            return false;
        }
        return true;
    }

    private static bool AllowsKeyCoexistence(LevelObjectType type)
    {
        return type == LevelObjectType.Enemy
            || type == LevelObjectType.Rock
            || type == LevelObjectType.Spike;
    }

    private static void AddError(LevelValidationResult result, string message, Vector2Int cell)
    {
        result.IsValid = false;
        result.Issues.Add(new LevelValidationIssue(message, cell, true));
    }

    private static void AddWarning(LevelValidationResult result, string message, Vector2Int cell)
    {
        result.Issues.Add(new LevelValidationIssue(message, cell, false));
    }
}
