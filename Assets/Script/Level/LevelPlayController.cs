using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public sealed class LevelPlayController : MonoBehaviour
{
    [SerializeField] private LevelData sourceData;
    [SerializeField] private int movesUsed;
    [SerializeField] private int moveLimit;
    [SerializeField] private bool isWon;
    [SerializeField] private bool isFailed;
    [SerializeField] private bool inputEnabled = true;
    [SerializeField] private float swipeThresholdPixels = 50f;
    [SerializeField] private bool enableKeyboardInput;

    private LevelData runtimeData;
    private LevelLogic logic;
    private LevelSimState state;
    private float originX;
    private float originY;
    private Dictionary<int, GameObject> objectViews;
    private Transform playerView;
    private readonly List<LevelDir> solutionPlayback = new List<LevelDir>();
    private int solutionIndex;
    private float solutionTimer;
    private bool playingSolution;
    private Vector2 swipeStart;
    private bool swipeActive;
    private bool swipeConsumed;

    public int MovesUsed { get { return movesUsed; } }
    public int MoveLimit { get { return moveLimit; } }
    public bool IsWon { get { return isWon; } }
    public bool IsFailed { get { return isFailed; } }
    public LevelSimState State { get { return state; } }

    public void Initialize(LevelData data, float originX, float originY, Dictionary<int, GameObject> objectViews)
    {
        sourceData = data;
        this.originX = originX;
        this.originY = originY;
        this.objectViews = objectViews;
        RestartFromSource();
    }

    public void RestartFromSource()
    {
        if (sourceData == null)
        {
            return;
        }
        runtimeData = sourceData.CreateRuntimeCopy();
        logic = new LevelLogic(runtimeData);
        state = logic.CreateInitialState(runtimeData);
        moveLimit = runtimeData.MoveLimit;
        movesUsed = 0;
        isWon = false;
        isFailed = false;
        playingSolution = false;
        solutionPlayback.Clear();
        solutionIndex = 0;
        ResetSwipe();
        EnsurePlayerView();
        RefreshViews();
    }

    public void SetInputEnabled(bool enabled)
    {
        inputEnabled = enabled;
    }

    public void PlaySolution(List<LevelDir> path)
    {
        solutionPlayback.Clear();
        if (path != null)
        {
            for (int i = 0; i < path.Count; i++)
            {
                solutionPlayback.Add(path[i]);
            }
        }
        RestartFromSource();
        solutionIndex = 0;
        solutionTimer = 0f;
        playingSolution = solutionPlayback.Count > 0;
        inputEnabled = false;
    }

    private void Update()
    {
        if (playingSolution)
        {
            TickSolution();
            return;
        }
        if (!inputEnabled || isWon || isFailed)
        {
            ResetSwipe();
            return;
        }
        PollSwipe();
        if (enableKeyboardInput)
        {
            PollKeyboard();
        }
    }

    private void PollSwipe()
    {
        Pointer pointer = Pointer.current;
        if (pointer == null)
        {
            return;
        }

        if (pointer.press.wasPressedThisFrame)
        {
            swipeActive = true;
            swipeConsumed = false;
            swipeStart = pointer.position.ReadValue();
        }

        if (!swipeActive)
        {
            return;
        }

        if (!swipeConsumed)
        {
            Vector2 delta = pointer.position.ReadValue() - swipeStart;
            float threshold = swipeThresholdPixels;
            if (delta.sqrMagnitude >= threshold * threshold)
            {
                LevelDir dir = DominantSwipeDir(delta);
                if (dir != LevelDir.None)
                {
                    ApplyDir(dir);
                    swipeConsumed = true;
                }
            }
        }

        if (pointer.press.wasReleasedThisFrame)
        {
            ResetSwipe();
        }
    }

    private void PollKeyboard()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return;
        }
        LevelDir dir = LevelDir.None;
        if (keyboard.wKey.wasPressedThisFrame || keyboard.upArrowKey.wasPressedThisFrame) dir = LevelDir.Up;
        else if (keyboard.sKey.wasPressedThisFrame || keyboard.downArrowKey.wasPressedThisFrame) dir = LevelDir.Down;
        else if (keyboard.aKey.wasPressedThisFrame || keyboard.leftArrowKey.wasPressedThisFrame) dir = LevelDir.Left;
        else if (keyboard.dKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame) dir = LevelDir.Right;
        if (dir != LevelDir.None)
        {
            ApplyDir(dir);
        }
    }

    private static LevelDir DominantSwipeDir(Vector2 delta)
    {
        float absX = delta.x < 0f ? -delta.x : delta.x;
        float absY = delta.y < 0f ? -delta.y : delta.y;
        if (absX > absY)
        {
            return delta.x < 0f ? LevelDir.Left : LevelDir.Right;
        }
        if (absY > absX)
        {
            return delta.y < 0f ? LevelDir.Down : LevelDir.Up;
        }
        return LevelDir.None;
    }

    private void ResetSwipe()
    {
        swipeActive = false;
        swipeConsumed = false;
        swipeStart = Vector2.zero;
    }

    private void TickSolution()
    {
        solutionTimer += Time.unscaledDeltaTime;
        if (solutionTimer < 0.25f)
        {
            return;
        }
        solutionTimer = 0f;
        if (solutionIndex >= solutionPlayback.Count)
        {
            playingSolution = false;
            return;
        }
        ApplyDir(solutionPlayback[solutionIndex]);
        solutionIndex++;
    }

    private void ApplyDir(LevelDir dir)
    {
        logic.TryMove(ref state, dir, true);
        movesUsed = state.MovesUsed;
        isWon = state.Won;
        isFailed = state.Dead && !state.Won;
        RefreshViews();
    }

    private void EnsurePlayerView()
    {
        if (playerView != null)
        {
            return;
        }
        GameObject go = new GameObject("Player");
        go.transform.SetParent(transform, false);
        SpriteRenderer renderer = go.AddComponent<SpriteRenderer>();
        renderer.sprite = LevelBuilder.GetSharedSprite();
        renderer.color = LevelBuilder.GetColor(LevelObjectType.PlayerStart);
        renderer.sortingOrder = 5;
        go.transform.localScale = new Vector3(0.55f, 0.55f, 1f);
        playerView = go.transform;
    }

    private void RefreshViews()
    {
        if (logic == null)
        {
            return;
        }
        if (playerView != null && state.PlayerIndex >= 0)
        {
            int px;
            int py;
            logic.FromIndex(state.PlayerIndex, out px, out py);
            playerView.position = new Vector3(originX + px, originY + py, 0f);
        }

        if (objectViews == null || runtimeData == null)
        {
            return;
        }

        List<LevelObjectData> objects = runtimeData.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            GameObject view;
            if (!objectViews.TryGetValue(obj.Id, out view) || view == null)
            {
                continue;
            }
            if (obj.Type == LevelObjectType.Enemy || obj.Type == LevelObjectType.Rock)
            {
                continue;
            }
            if (obj.Type == LevelObjectType.Key)
            {
                int keyIndex = logic.GetKeyIndex(logic.ToIndex(obj.X, obj.Y));
                bool present = keyIndex >= 0 && ((state.KeyPresentMask & (1UL << keyIndex)) != 0UL);
                view.SetActive(present);
            }
            else if (obj.Type == LevelObjectType.Door)
            {
                int doorIndex = logic.GetDoorIndex(logic.ToIndex(obj.X, obj.Y));
                bool closed = doorIndex >= 0 && ((state.ClosedDoorMask & (1UL << doorIndex)) != 0UL);
                view.SetActive(true);
                SpriteRenderer sr = view.GetComponent<SpriteRenderer>();
                if (sr != null)
                {
                    Color c = LevelBuilder.GetColor(LevelObjectType.Door);
                    if (!closed)
                    {
                        c.a = 0.35f;
                    }
                    sr.color = c;
                }
            }
            else if (obj.Type == LevelObjectType.PlayerStart)
            {
                view.SetActive(false);
            }
        }

        SyncEnemyViews();
        SyncRockViews();
    }

    private void SyncEnemyViews()
    {
        List<GameObject> enemyViews = new List<GameObject>();
        List<LevelObjectData> objects = runtimeData.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Type != LevelObjectType.Enemy)
            {
                continue;
            }
            GameObject view;
            if (objectViews.TryGetValue(objects[i].Id, out view) && view != null)
            {
                enemyViews.Add(view);
                view.SetActive(false);
            }
        }
        int viewIndex = 0;
        for (int c = 0; c < logic.CellCount; c++)
        {
            if (!LevelLogic.HasEnemy(ref state, c))
            {
                continue;
            }
            if (viewIndex >= enemyViews.Count)
            {
                break;
            }
            int ex;
            int ey;
            logic.FromIndex(c, out ex, out ey);
            GameObject view = enemyViews[viewIndex];
            view.SetActive(true);
            view.transform.position = new Vector3(originX + ex, originY + ey, 0f);
            viewIndex++;
        }
    }

    private void SyncRockViews()
    {
        List<GameObject> rockViews = new List<GameObject>();
        List<LevelObjectData> objects = runtimeData.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i].Type != LevelObjectType.Rock)
            {
                continue;
            }
            GameObject view;
            if (objectViews.TryGetValue(objects[i].Id, out view) && view != null)
            {
                rockViews.Add(view);
                view.SetActive(false);
            }
        }
        int viewIndex = 0;
        for (int c = 0; c < logic.CellCount; c++)
        {
            if (!LevelLogic.HasRock(ref state, c))
            {
                continue;
            }
            if (viewIndex >= rockViews.Count)
            {
                break;
            }
            int rx;
            int ry;
            logic.FromIndex(c, out rx, out ry);
            GameObject view = rockViews[viewIndex];
            view.SetActive(true);
            view.transform.position = new Vector3(originX + rx, originY + ry, 0f);
            viewIndex++;
        }
    }

    private void OnGUI()
    {
        if (!Application.isPlaying)
        {
            return;
        }
        GUILayout.BeginArea(new Rect(12f, 12f, 320f, 160f));
        GUILayout.Label("Moves: " + movesUsed + " / " + moveLimit);
        if (isWon)
        {
            GUILayout.Label("WIN");
        }
        else if (isFailed)
        {
            GUILayout.Label("FAIL");
        }
        if (GUILayout.Button("Restart"))
        {
            RestartFromSource();
            inputEnabled = true;
        }
        if (GUILayout.Button("Exit Test"))
        {
            TestSession.RequestExitPlayMode();
        }
        GUILayout.EndArea();
    }
}
