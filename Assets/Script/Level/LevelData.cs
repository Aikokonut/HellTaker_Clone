using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "LevelData", menuName = "Level Design/Level Data")]
public class LevelData : ScriptableObject
{
    [SerializeField] private int width = 8;
    [SerializeField] private int height = 8;
    [SerializeField] private int moveLimit = 20;
    [SerializeField] private Vector2Int playerStart = new Vector2Int(-1, -1);
    [SerializeField] private Vector2Int goal = new Vector2Int(-1, -1);
    [SerializeField] private List<LevelObjectData> objects = new List<LevelObjectData>();
    [SerializeField] private int nextObjectId = 1;

    public int Width
    {
        get { return width; }
        set { width = value; }
    }

    public int Height
    {
        get { return height; }
        set { height = value; }
    }

    public int MoveLimit
    {
        get { return moveLimit; }
        set { moveLimit = value; }
    }

    public Vector2Int PlayerStart
    {
        get { return playerStart; }
        set { playerStart = value; }
    }

    public Vector2Int Goal
    {
        get { return goal; }
        set { goal = value; }
    }

    public List<LevelObjectData> Objects
    {
        get { return objects; }
    }

    public int NextObjectId
    {
        get { return nextObjectId; }
        set { nextObjectId = value; }
    }

    public int AllocateObjectId()
    {
        int id = nextObjectId;
        nextObjectId++;
        return id;
    }

    public void SyncDerivedFields()
    {
        playerStart = new Vector2Int(-1, -1);
        goal = new Vector2Int(-1, -1);
        int count = objects.Count;
        for (int i = 0; i < count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type == LevelObjectType.PlayerStart)
            {
                playerStart = new Vector2Int(obj.X, obj.Y);
            }
            else if (obj.Type == LevelObjectType.Goal)
            {
                goal = new Vector2Int(obj.X, obj.Y);
            }
        }
    }

    public bool AddRowTop()
    {
        if (height >= 32 || width * (height + 1) > LevelLogic.MaxCells)
        {
            return false;
        }
        height++;
        OffsetObjects(0, 1);
        SyncDerivedFields();
        return true;
    }

    public bool RemoveRowTop()
    {
        if (height <= 1)
        {
            return false;
        }
        RemoveObjectsInRow(0);
        OffsetObjects(0, -1);
        height--;
        SyncDerivedFields();
        return true;
    }

    public bool AddRowBottom()
    {
        if (height >= 32 || width * (height + 1) > LevelLogic.MaxCells)
        {
            return false;
        }
        height++;
        SyncDerivedFields();
        return true;
    }

    public bool RemoveRowBottom()
    {
        if (height <= 1)
        {
            return false;
        }
        RemoveObjectsInRow(height - 1);
        height--;
        SyncDerivedFields();
        return true;
    }

    public bool AddColumnLeft()
    {
        if (width >= 32 || (width + 1) * height > LevelLogic.MaxCells)
        {
            return false;
        }
        width++;
        OffsetObjects(1, 0);
        SyncDerivedFields();
        return true;
    }

    public bool RemoveColumnLeft()
    {
        if (width <= 1)
        {
            return false;
        }
        RemoveObjectsInColumn(0);
        OffsetObjects(-1, 0);
        width--;
        SyncDerivedFields();
        return true;
    }

    public bool AddColumnRight()
    {
        if (width >= 32 || (width + 1) * height > LevelLogic.MaxCells)
        {
            return false;
        }
        width++;
        SyncDerivedFields();
        return true;
    }

    public bool RemoveColumnRight()
    {
        if (width <= 1)
        {
            return false;
        }
        RemoveObjectsInColumn(width - 1);
        width--;
        SyncDerivedFields();
        return true;
    }

    private void OffsetObjects(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            obj.X = obj.X + dx;
            obj.Y = obj.Y + dy;
        }
    }

    private void RemoveObjectsInRow(int rowY)
    {
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            if (objects[i].Y == rowY)
            {
                objects.RemoveAt(i);
            }
        }
    }

    private void RemoveObjectsInColumn(int colX)
    {
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            if (objects[i].X == colX)
            {
                objects.RemoveAt(i);
            }
        }
    }

    public LevelData CreateRuntimeCopy()
    {
        LevelData copy = CreateInstance<LevelData>();
        copy.width = width;
        copy.height = height;
        copy.moveLimit = moveLimit;
        copy.playerStart = playerStart;
        copy.goal = goal;
        copy.nextObjectId = nextObjectId;
        copy.objects = new List<LevelObjectData>(objects.Count);
        for (int i = 0; i < objects.Count; i++)
        {
            copy.objects.Add(objects[i].Clone());
        }
        return copy;
    }

    public void CopyFrom(LevelData source)
    {
        width = source.width;
        height = source.height;
        moveLimit = source.moveLimit;
        playerStart = source.playerStart;
        goal = source.goal;
        nextObjectId = source.nextObjectId;
        objects.Clear();
        List<LevelObjectData> sourceObjects = source.objects;
        for (int i = 0; i < sourceObjects.Count; i++)
        {
            objects.Add(sourceObjects[i].Clone());
        }
    }

    public byte[] ToSnapshotBytes()
    {
        List<byte> bytes = new List<byte>(64 + objects.Count * 16);
        WriteInt(bytes, width);
        WriteInt(bytes, height);
        WriteInt(bytes, moveLimit);
        WriteInt(bytes, playerStart.x);
        WriteInt(bytes, playerStart.y);
        WriteInt(bytes, goal.x);
        WriteInt(bytes, goal.y);
        WriteInt(bytes, nextObjectId);
        WriteInt(bytes, objects.Count);
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            WriteInt(bytes, obj.Id);
            WriteInt(bytes, (int)obj.Type);
            WriteInt(bytes, obj.X);
            WriteInt(bytes, obj.Y);
        }
        return bytes.ToArray();
    }

    public void FromSnapshotBytes(byte[] data)
    {
        if (data == null || data.Length < 36)
        {
            return;
        }
        int offset = 0;
        width = ReadInt(data, ref offset);
        height = ReadInt(data, ref offset);
        moveLimit = ReadInt(data, ref offset);
        playerStart = new Vector2Int(ReadInt(data, ref offset), ReadInt(data, ref offset));
        goal = new Vector2Int(ReadInt(data, ref offset), ReadInt(data, ref offset));
        nextObjectId = ReadInt(data, ref offset);
        int count = ReadInt(data, ref offset);
        objects.Clear();
        for (int i = 0; i < count; i++)
        {
            int id = ReadInt(data, ref offset);
            LevelObjectType type = (LevelObjectType)ReadInt(data, ref offset);
            int x = ReadInt(data, ref offset);
            int y = ReadInt(data, ref offset);
            objects.Add(new LevelObjectData(id, type, x, y));
        }
    }

    private static void WriteInt(List<byte> bytes, int value)
    {
        bytes.Add((byte)(value & 255));
        bytes.Add((byte)((value >> 8) & 255));
        bytes.Add((byte)((value >> 16) & 255));
        bytes.Add((byte)((value >> 24) & 255));
    }

    private static int ReadInt(byte[] data, ref int offset)
    {
        int value = data[offset]
            | (data[offset + 1] << 8)
            | (data[offset + 2] << 16)
            | (data[offset + 3] << 24);
        offset += 4;
        return value;
    }
}
