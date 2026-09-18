using System;
using UnityEngine;

[Serializable]
public class LevelObjectData
{
    [SerializeField] private int id;
    [SerializeField] private LevelObjectType type;
    [SerializeField] private int x;
    [SerializeField] private int y;

    public int Id
    {
        get { return id; }
        set { id = value; }
    }

    public LevelObjectType Type
    {
        get { return type; }
        set { type = value; }
    }

    public int X
    {
        get { return x; }
        set { x = value; }
    }

    public int Y
    {
        get { return y; }
        set { y = value; }
    }

    public LevelObjectData()
    {
    }

    public LevelObjectData(int id, LevelObjectType type, int x, int y)
    {
        this.id = id;
        this.type = type;
        this.x = x;
        this.y = y;
    }

    public LevelObjectData Clone()
    {
        return new LevelObjectData(id, type, x, y);
    }
}
