using System.Collections.Generic;
using UnityEngine;

public static class TestSession
{
    private const string KeyActive = "LevelDesign.TestActive";
    private const string KeyPath = "LevelDesign.TestAssetPath";
    private const string KeySolution = "LevelDesign.TestSolution";

    private static LevelData s_PendingLevel;
    private static LevelData s_ActiveSourceAsset;
    private static GameObject s_RuntimeRoot;
    private static LevelPlayController s_ActiveController;
    private static List<LevelDir> s_PendingSolution;
    private static bool s_TestActive;

    public static bool IsTestActive
    {
        get { return s_TestActive; }
    }

    public static LevelData ActiveSourceAsset
    {
        get { return s_ActiveSourceAsset; }
    }

    public static LevelPlayController ActiveController
    {
        get { return s_ActiveController; }
    }

    public static void BeginTest(LevelData sourceAsset)
    {
        BeginTest(sourceAsset, null);
    }

    public static void BeginTest(LevelData sourceAsset, List<LevelDir> solutionToPlay)
    {
        if (sourceAsset == null)
        {
            Debug.LogWarning("TestSession: LevelData required.");
            return;
        }
        s_ActiveSourceAsset = sourceAsset;
        s_PendingLevel = sourceAsset.CreateRuntimeCopy();
        s_PendingLevel.name = sourceAsset.name + "_TestCopy";
        s_PendingSolution = null;
        if (solutionToPlay != null)
        {
            s_PendingSolution = new List<LevelDir>(solutionToPlay.Count);
            for (int i = 0; i < solutionToPlay.Count; i++)
            {
                s_PendingSolution.Add(solutionToPlay[i]);
            }
        }
        s_TestActive = true;
#if UNITY_EDITOR
        string path = UnityEditor.AssetDatabase.GetAssetPath(sourceAsset);
        UnityEditor.SessionState.SetBool(KeyActive, true);
        UnityEditor.SessionState.SetString(KeyPath, path != null ? path : "");
        UnityEditor.SessionState.SetString(KeySolution, EncodeSolution(s_PendingSolution));
        if (!Application.isPlaying)
        {
            UnityEditor.EditorApplication.isPlaying = true;
        }
        else
        {
            SpawnPendingLevel();
        }
#else
        SpawnPendingLevel();
#endif
    }

    public static void RequestExitPlayMode()
    {
#if UNITY_EDITOR
        UnityEditor.SessionState.SetBool(KeyActive, false);
        UnityEditor.SessionState.SetString(KeyPath, "");
        UnityEditor.SessionState.SetString(KeySolution, "");
        if (Application.isPlaying)
        {
            UnityEditor.EditorApplication.isPlaying = false;
        }
#endif
        CleanupRuntime();
        s_TestActive = false;
        s_PendingLevel = null;
        s_PendingSolution = null;
        s_ActiveSourceAsset = null;
    }

    public static void RestartActiveTest()
    {
        if (s_ActiveSourceAsset == null)
        {
            return;
        }
        CleanupRuntime();
        s_PendingLevel = s_ActiveSourceAsset.CreateRuntimeCopy();
        SpawnPendingLevel();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void OnAfterSceneLoad()
    {
#if UNITY_EDITOR
        if (!UnityEditor.SessionState.GetBool(KeyActive, false))
        {
            return;
        }
        string path = UnityEditor.SessionState.GetString(KeyPath, "");
        if (!string.IsNullOrEmpty(path))
        {
            LevelData asset = UnityEditor.AssetDatabase.LoadAssetAtPath<LevelData>(path);
            if (asset != null)
            {
                s_ActiveSourceAsset = asset;
                s_PendingLevel = asset.CreateRuntimeCopy();
                s_PendingSolution = DecodeSolution(UnityEditor.SessionState.GetString(KeySolution, ""));
                s_TestActive = true;
            }
        }
#endif
        if (!s_TestActive || s_PendingLevel == null)
        {
            return;
        }
        SpawnPendingLevel();
    }

    private static void SpawnPendingLevel()
    {
        CleanupRuntime();
        if (s_PendingLevel == null)
        {
            return;
        }
        LevelBuildResult build = LevelBuilder.Build(s_PendingLevel, null);
        s_RuntimeRoot = build.Root;
        s_ActiveController = build.PlayController;
        if (s_PendingSolution != null && s_ActiveController != null && s_PendingSolution.Count > 0)
        {
            s_ActiveController.PlaySolution(s_PendingSolution);
        }
    }

    private static void CleanupRuntime()
    {
        s_ActiveController = null;
        if (s_RuntimeRoot != null)
        {
            if (Application.isPlaying)
            {
                LevelBuilder.Clear(s_RuntimeRoot);
            }
            else
            {
                LevelBuilder.ClearImmediate(s_RuntimeRoot);
            }
            s_RuntimeRoot = null;
        }
    }

    private static string EncodeSolution(List<LevelDir> path)
    {
        if (path == null || path.Count == 0)
        {
            return "";
        }
        char[] chars = new char[path.Count];
        for (int i = 0; i < path.Count; i++)
        {
            chars[i] = (char)('0' + (int)path[i]);
        }
        return new string(chars);
    }

    private static List<LevelDir> DecodeSolution(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return null;
        }
        List<LevelDir> path = new List<LevelDir>(encoded.Length);
        for (int i = 0; i < encoded.Length; i++)
        {
            int value = encoded[i] - '0';
            if (value >= 0 && value <= 3)
            {
                path.Add((LevelDir)value);
            }
        }
        return path;
    }

#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoadMethod]
    private static void EditorInit()
    {
        UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    private static void OnPlayModeChanged(UnityEditor.PlayModeStateChange state)
    {
        if (state == UnityEditor.PlayModeStateChange.EnteredEditMode)
        {
            CleanupRuntime();
            s_TestActive = false;
            s_PendingLevel = null;
            s_PendingSolution = null;
            UnityEditor.SessionState.SetBool(KeyActive, false);
            UnityEditor.SessionState.SetString(KeyPath, "");
            UnityEditor.SessionState.SetString(KeySolution, "");
        }
    }
#endif
}
