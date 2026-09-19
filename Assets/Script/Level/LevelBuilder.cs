using System.Collections.Generic;
using UnityEngine;

public sealed class LevelBuildResult
{
    public GameObject Root;
    public LevelPlayController PlayController;
    public Transform ObjectsRoot;
    public readonly Dictionary<int, GameObject> ObjectViews = new Dictionary<int, GameObject>();
}

public static class LevelBuilder
{
    private static Sprite s_PixelSprite;

    public static LevelBuildResult Build(LevelData data, Transform parent)
    {
        LevelBuildResult result = new LevelBuildResult();
        if (data == null)
        {
            return result;
        }

        GameObject root = new GameObject("LevelRuntime");
        if (parent != null)
        {
            root.transform.SetParent(parent, false);
        }
        result.Root = root;

        GameObject gridGo = new GameObject("Grid");
        gridGo.transform.SetParent(root.transform, false);

        GameObject objectsGo = new GameObject("Objects");
        objectsGo.transform.SetParent(root.transform, false);
        result.ObjectsRoot = objectsGo.transform;

        float originX = -(data.Width - 1) * 0.5f;
        float originY = -(data.Height - 1) * 0.5f;

        List<LevelObjectData> objects = data.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type != LevelObjectType.Floor)
            {
                continue;
            }
            CreateCellView(gridGo.transform, obj, originX, originY, GetColor(obj.Type), 0);
        }

        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.Type == LevelObjectType.Floor)
            {
                continue;
            }
            GameObject view = CreateCellView(objectsGo.transform, obj, originX, originY, GetColor(obj.Type), 1);
            result.ObjectViews[obj.Id] = view;
        }

        LevelPlayController controller = root.AddComponent<LevelPlayController>();
        controller.Initialize(data, originX, originY, result.ObjectViews);
        result.PlayController = controller;
        return result;
    }

    public static void Clear(GameObject root)
    {
        if (root == null)
        {
            return;
        }
        Object.Destroy(root);
    }

    public static void ClearImmediate(GameObject root)
    {
        if (root == null)
        {
            return;
        }
        Object.DestroyImmediate(root);
    }

    private static GameObject CreateCellView(Transform parent, LevelObjectData obj, float originX, float originY, Color color, int sortingOrder)
    {
        GameObject go = new GameObject(obj.Type + "_" + obj.Id);
        go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(originX + obj.X, originY + obj.Y, 0f);
        SpriteRenderer renderer = go.AddComponent<SpriteRenderer>();
        renderer.sprite = GetPixelSprite();
        renderer.color = color;
        renderer.sortingOrder = sortingOrder;
        float scale = obj.Type == LevelObjectType.Floor ? 0.95f : 0.7f;
        go.transform.localScale = new Vector3(scale, scale, 1f);
        return go;
    }

    public static Sprite GetSharedSprite()
    {
        return GetPixelSprite();
    }

    public static Color GetColor(LevelObjectType type)
    {
        switch (type)
        {
            case LevelObjectType.Floor: return new Color(0.25f, 0.25f, 0.28f, 1f);
            case LevelObjectType.Wall: return new Color(0.55f, 0.55f, 0.6f, 1f);
            case LevelObjectType.Boundary: return new Color(0.15f, 0.15f, 0.18f, 1f);
            case LevelObjectType.PlayerStart: return new Color(0.2f, 0.75f, 0.35f, 1f);
            case LevelObjectType.Enemy: return new Color(0.85f, 0.25f, 0.25f, 1f);
            case LevelObjectType.Rock: return new Color(0.62f, 0.58f, 0.52f, 1f);
            case LevelObjectType.Spike: return new Color(0.9f, 0.5f, 0.1f, 1f);
            case LevelObjectType.Key: return new Color(0.95f, 0.85f, 0.2f, 1f);
            case LevelObjectType.Door: return new Color(0.45f, 0.3f, 0.15f, 1f);
            case LevelObjectType.Goal: return new Color(0.3f, 0.55f, 0.95f, 1f);
            case LevelObjectType.RequiredZone: return new Color(0.55f, 0.25f, 0.75f, 0.55f);
            default: return Color.magenta;
        }
    }

    private static Sprite GetPixelSprite()
    {
        if (s_PixelSprite != null)
        {
            return s_PixelSprite;
        }
        Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        tex.filterMode = FilterMode.Point;
        s_PixelSprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
        return s_PixelSprite;
    }
}
