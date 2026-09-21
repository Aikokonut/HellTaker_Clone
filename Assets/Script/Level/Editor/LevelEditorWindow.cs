using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public enum LevelEditorTool
{
    Select = 0,
    Move = 1,
    Delete = 2,
    Duplicate = 3,
    Place = 4
}

public sealed class LevelEditorWindow : EditorWindow
{
    private LevelData levelData;
    private string assetPath = "";
    private LevelEditorTool tool = LevelEditorTool.Place;
    private LevelObjectType placeType = LevelObjectType.Floor;
    private int selectedObjectId = -1;
    private Vector2Int hoverCell = new Vector2Int(-1, -1);
    private Vector2 scroll;
    private Vector2 paletteScroll;
    private Vector2 analysisScroll;
    private float cellSize = 28f;
    private float mapZoom = 1f;
    private float leftPanelWidth = 240f;
    private float rightPanelWidth = 320f;
    private int splitDrag; // 0=none, 1=left, 2=right
    private bool isDragging;
    private Vector2Int dragOffset;
    private readonly List<byte[]> undoStack = new List<byte[]>();
    private readonly List<byte[]> redoStack = new List<byte[]>();
    private LevelAnalysisResult analysis;
    private readonly List<LevelDir> shownSolution = new List<LevelDir>();
    private int selectedSolutionIndex;
    private int maxStoredSolutions = LevelSolver.DefaultMaxStoredSolutions;
    private bool showSolution;
    private int solutionRevealSteps;
    private string statusMessage = "";
    private readonly HashSet<long> invalidCells = new HashSet<long>();
    private Vector2 actionScroll;
    private int genWidth = 8;
    private int genHeight = 8;
    private int genMaxAttempts = 24;
    private int genSeed;
    private int genEnemyCount = 1;
    private int genRockCount = 1;
    private int genSpikeCount = 1;
    private int genMaxCandidates = 3;
    private int genCostSlack = 8;
    private int genMaxTargetSolutions = 1;
    private int genStateLimit = 100000;
    private int genTimeLimitMs = 500;
    private LevelGenerateResult lastGenerateResult;
    private string lastCalibrateMessage = "";
    private const string PrefLastLevelDir = "LevelEditor.LastLevelDataDir";

    [MenuItem("Tools/Level Editor")]
    public static void Open()
    {
        LevelEditorWindow window = GetWindow<LevelEditorWindow>("Level Editor");
        window.minSize = new Vector2(900f, 600f);
        window.Show();
    }

    private void OnEnable()
    {
        titleContent = new GUIContent("Level Editor");
        leftPanelWidth = EditorPrefs.GetFloat("LevelEditor.LeftPanelWidth", 240f);
        rightPanelWidth = EditorPrefs.GetFloat("LevelEditor.RightPanelWidth", 320f);
        mapZoom = EditorPrefs.GetFloat("LevelEditor.MapZoom", 1f);
        cellSize = EditorPrefs.GetFloat("LevelEditor.CellSize", 28f);
    }

    private void OnDisable()
    {
        EditorPrefs.SetFloat("LevelEditor.LeftPanelWidth", leftPanelWidth);
        EditorPrefs.SetFloat("LevelEditor.RightPanelWidth", rightPanelWidth);
        EditorPrefs.SetFloat("LevelEditor.MapZoom", mapZoom);
        EditorPrefs.SetFloat("LevelEditor.CellSize", cellSize);
        DestroyOrphanCandidates();
        lastGenerateResult = null;
    }

    private void OnGUI()
    {
        DrawToolbar();
        EditorGUILayout.BeginHorizontal();
        DrawPalette();
        DrawVerticalSplitter(1);
        DrawGridArea();
        DrawVerticalSplitter(2);
        DrawSidePanel();
        EditorGUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(statusMessage))
        {
            EditorGUILayout.HelpBox(statusMessage, MessageType.Info);
        }
    }

    private void DrawVerticalSplitter(int which)
    {
        Rect rect = GUILayoutUtility.GetRect(5f, 5f, GUILayout.ExpandHeight(true), GUILayout.Width(5f));
        EditorGUI.DrawRect(rect, new Color(0.18f, 0.18f, 0.2f, 1f));
        EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);

        Event e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
        {
            splitDrag = which;
            e.Use();
        }
        if (splitDrag == which && e.type == EventType.MouseDrag)
        {
            float minLeft = 180f;
            float minRight = 220f;
            float minCenter = 200f;
            float maxLeft = position.width - minRight - minCenter - 20f;
            float maxRight = position.width - minLeft - minCenter - 20f;
            if (which == 1)
            {
                leftPanelWidth = Mathf.Clamp(leftPanelWidth + e.delta.x, minLeft, Mathf.Max(minLeft, maxLeft));
            }
            else
            {
                rightPanelWidth = Mathf.Clamp(rightPanelWidth - e.delta.x, minRight, Mathf.Max(minRight, maxRight));
            }
            e.Use();
            Repaint();
        }
        if (e.type == EventType.MouseUp && e.button == 0 && splitDrag != 0)
        {
            splitDrag = 0;
            e.Use();
        }
    }

    private float MapCellSize()
    {
        return Mathf.Clamp(cellSize * mapZoom, 8f, 96f);
    }

    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        if (ToolbarButton("New"))
        {
            NewLevel();
        }
        if (ToolbarButton("Save"))
        {
            SaveLevel(false);
        }
        if (ToolbarButton("Save As"))
        {
            SaveLevel(true);
        }
        if (ToolbarButton("Load"))
        {
            LoadLevel();
        }
        GUILayout.Space(8f);
        if (ToolbarButton("Undo"))
        {
            UndoEdit();
        }
        if (ToolbarButton("Redo"))
        {
            RedoEdit();
        }
        GUILayout.Space(8f);
        if (ToolbarButton("Test"))
        {
            StartTest();
        }
        if (ToolbarButton("Analyze"))
        {
            RunAnalyze();
        }
        if (ToolbarButton("Show Solution"))
        {
            ToggleShowSolution();
        }
        if (ToolbarButton("Play Solution"))
        {
            PlaySolution();
        }
        if (ToolbarButton("Generate Level"))
        {
            GenerateLevel();
        }
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
    }

    private bool ToolbarButton(string label)
    {
        return GUILayout.Button(label, EditorStyles.toolbarButton, GUILayout.Width(90f));
    }

    private void DrawPalette()
    {
        EditorGUILayout.BeginVertical(GUILayout.Width(leftPanelWidth), GUILayout.MaxWidth(leftPanelWidth), GUILayout.MinWidth(180f));
        paletteScroll = EditorGUILayout.BeginScrollView(paletteScroll);
        GUILayout.Label("Tools", EditorStyles.boldLabel);
        if (GUILayout.Toggle(tool == LevelEditorTool.Select, "Select", "Button")) tool = LevelEditorTool.Select;
        if (GUILayout.Toggle(tool == LevelEditorTool.Move, "Move", "Button")) tool = LevelEditorTool.Move;
        if (GUILayout.Toggle(tool == LevelEditorTool.Delete, "Delete", "Button")) tool = LevelEditorTool.Delete;
        if (GUILayout.Toggle(tool == LevelEditorTool.Duplicate, "Duplicate", "Button")) tool = LevelEditorTool.Duplicate;

        GUILayout.Space(8f);
        GUILayout.Label("Terrain", EditorStyles.boldLabel);
        PaletteButton(LevelObjectType.Floor);
        PaletteButton(LevelObjectType.Wall);
        PaletteButton(LevelObjectType.Boundary);
        GUILayout.Label("Actor", EditorStyles.boldLabel);
        PaletteButton(LevelObjectType.PlayerStart);
        PaletteButton(LevelObjectType.Enemy);
        PaletteButton(LevelObjectType.Rock);
        GUILayout.Label("Hazard", EditorStyles.boldLabel);
        PaletteButton(LevelObjectType.Spike);
        GUILayout.Label("Puzzle", EditorStyles.boldLabel);
        PaletteButton(LevelObjectType.Key);
        PaletteButton(LevelObjectType.Door);
        PaletteButton(LevelObjectType.Goal);
        PaletteButton(LevelObjectType.RequiredZone);

        GUILayout.Space(8f);
        if (levelData != null)
        {
            GUILayout.Label("Grid Size", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            int newWidth = EditorGUILayout.IntField("Width", levelData.Width);
            int newHeight = EditorGUILayout.IntField("Height", levelData.Height);
            if (EditorGUI.EndChangeCheck())
            {
                ApplyGridSize(newWidth, newHeight);
                genWidth = levelData.Width;
                genHeight = levelData.Height;
            }
            EditorGUI.BeginChangeCheck();
            int limit = EditorGUILayout.IntField("Level MoveLimit", levelData.MoveLimit);
            if (EditorGUI.EndChangeCheck())
            {
                PushUndo();
                levelData.MoveLimit = limit;
                EditorUtility.SetDirty(levelData);
            }
            GUILayout.Label("Grid Edges", EditorStyles.boldLabel);
            DrawGridResizeControls();
            cellSize = EditorGUILayout.Slider("Base Cell Size", cellSize, 12f, 48f);
            maxStoredSolutions = EditorGUILayout.IntField("Max Stored (Analyze)", maxStoredSolutions);
            EditorGUILayout.HelpBox(
                "Max Stored chỉ giới hạn số path lưu khi Analyze — không làm puzzle chỉ còn 1 lời giải.",
                MessageType.None);
            GUILayout.Label("Hover: " + hoverCell.x + ", " + hoverCell.y);
            GUILayout.Label("Cell: " + DescribeHoverCell(), EditorStyles.wordWrappedLabel);
            GUILayout.Label("Selected Id: " + selectedObjectId);
        }

        GUILayout.Space(8f);
        DrawGeneratorPanel();
        DrawLastGenerateReport();
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();
    }

    private string DescribeHoverCell()
    {
        if (levelData == null || hoverCell.x < 0 || hoverCell.y < 0)
        {
            return "(none)";
        }
        if (hoverCell.x >= levelData.Width || hoverCell.y >= levelData.Height)
        {
            return "(out)";
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
        List<LevelObjectData> objects = levelData.Objects;
        int n = 0;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (obj.X != hoverCell.x || obj.Y != hoverCell.y)
            {
                continue;
            }
            if (n > 0) sb.Append(" + ");
            sb.Append(obj.Type.ToString());
            sb.Append("#");
            sb.Append(obj.Id);
            n++;
        }
        if (n == 0) return "Empty";
        return sb.ToString();
    }

    private static void SuggestGenParams(
        LevelData data, out int rock, out int enemy, out int spike, out int slack)
    {
        rock = 1;
        enemy = 0;
        spike = 1;
        slack = 6;
        if (data == null) return;
        LevelLogic logic = new LevelLogic(data);
        int cells = logic.CellCount;
        int floors = 0;
        int choke = 0;
        for (int i = 0; i < cells; i++)
        {
            if (!logic.IsFloor(i) || logic.IsSolid(i)) continue;
            floors++;
            int x, y;
            logic.FromIndex(i, out x, out y);
            int deg = 0;
            if (logic.InBounds(x + 1, y) && logic.IsFloor(logic.ToIndex(x + 1, y)) && !logic.IsSolid(logic.ToIndex(x + 1, y))) deg++;
            if (logic.InBounds(x - 1, y) && logic.IsFloor(logic.ToIndex(x - 1, y)) && !logic.IsSolid(logic.ToIndex(x - 1, y))) deg++;
            if (logic.InBounds(x, y + 1) && logic.IsFloor(logic.ToIndex(x, y + 1)) && !logic.IsSolid(logic.ToIndex(x, y + 1))) deg++;
            if (logic.InBounds(x, y - 1) && logic.IsFloor(logic.ToIndex(x, y - 1)) && !logic.IsSolid(logic.ToIndex(x, y - 1))) deg++;
            if (deg > 0 && deg <= 2) choke++;
        }
        int shortLen = 12;
        if (logic.StartIndex >= 0 && logic.GoalIndex >= 0)
        {
            // Approximate: manhattan as lower bound; prefer choke-based caps.
            int sx, sy, gx, gy;
            logic.FromIndex(logic.StartIndex, out sx, out sy);
            logic.FromIndex(logic.GoalIndex, out gx, out gy);
            int dx = sx - gx;
            if (dx < 0) dx = -dx;
            int dy = sy - gy;
            if (dy < 0) dy = -dy;
            shortLen = dx + dy;
            if (shortLen < 4) shortLen = 4;
        }
        // Open map (few chokes): keep counts tiny.
        if (choke < 3)
        {
            rock = 1;
            enemy = 0;
            spike = 1;
            slack = shortLen / 2;
            if (slack < 4) slack = 4;
            if (slack > 8) slack = 8;
            return;
        }
        rock = choke / 4;
        if (rock < 1) rock = 1;
        if (rock > 2) rock = 2;
        enemy = choke / 5;
        if (enemy > 2) enemy = 2;
        spike = 1;
        if (choke >= 8) spike = 2;
        slack = shortLen / 2;
        if (slack < 6) slack = 6;
        if (slack > 12) slack = 12;
    }

    private void DrawGeneratorPanel()
    {
        GUILayout.Label("Generator (Path-First)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Max Target Solutions = mục tiêu mềm (ít sols = điểm cao hơn), không khóa.\n"
            + "Spike ≥ nửa trên path. Calibrate chỉ lấy count E/R/S từ gốc.",
            MessageType.Info);

        EditorGUI.BeginChangeCheck();
        genWidth = EditorGUILayout.IntField("Width", genWidth);
        genHeight = EditorGUILayout.IntField("Height", genHeight);
        if (EditorGUI.EndChangeCheck() && levelData != null)
        {
            ApplyGridSize(genWidth, genHeight);
            genWidth = levelData.Width;
            genHeight = levelData.Height;
        }

        GUILayout.Label("Object Counts (max)", EditorStyles.boldLabel);
        if (levelData != null)
        {
            int sugR, sugE, sugS, sugSlack;
            SuggestGenParams(levelData, out sugR, out sugE, out sugS, out sugSlack);
            EditorGUILayout.HelpBox(
                "Gợi ý topology: Rock≤" + sugR + " Enemy≤" + sugE + " Spike≤" + sugS
                + " CostSlack≈" + sugSlack
                + "\nCalibrate từ level Helltaker đầy đủ (có E/R/S) chính xác hơn.",
                MessageType.Info);
            if (GUILayout.Button("Apply Suggested Counts"))
            {
                genRockCount = sugR;
                genEnemyCount = sugE;
                genSpikeCount = sugS;
                genCostSlack = sugSlack;
            }
            if (GUILayout.Button("Calibrate From This Level (strip E/R/S)"))
            {
                LevelCalibrateResult cal = LevelAnalyzer.CalibrateFromReference(levelData);
                lastCalibrateMessage = cal.Message;
                if (cal.Success)
                {
                    genEnemyCount = cal.EnemyCount;
                    genRockCount = cal.RockCount;
                    genSpikeCount = cal.SpikeCount;
                    genCostSlack = cal.CostSlack;
                    genMaxTargetSolutions = cal.RecommendedMaxTargetSolutions;
                    if (genMaxTargetSolutions < 1) genMaxTargetSolutions = 1;
                    genMaxAttempts = cal.RecommendedAttempts;
                    statusMessage = cal.Message;
                }
                else
                {
                    statusMessage = cal.Message;
                }
            }
            if (!string.IsNullOrEmpty(lastCalibrateMessage))
            {
                EditorGUILayout.HelpBox(lastCalibrateMessage, MessageType.None);
            }
        }
        genEnemyCount = EditorGUILayout.IntField("Enemy Count", genEnemyCount);
        genRockCount = EditorGUILayout.IntField("Rock Count", genRockCount);
        genSpikeCount = EditorGUILayout.IntField("Spike Count", genSpikeCount);
        GUILayout.Label("Budget", EditorStyles.boldLabel);
        genCostSlack = EditorGUILayout.IntField("Cost Slack", genCostSlack);
        EditorGUILayout.HelpBox(
            "E/R/S đã vẽ trên map được GIỮ. Count = tổng cuối (phải >= số sẵn có).\n"
            + "Max Target Solutions ≤3 (1 = khó nhất). Spike cần ≥ nửa số nằm trên optimal path.",
            MessageType.None);
        genMaxCandidates = EditorGUILayout.IntField("Max Unique Candidates", genMaxCandidates);
        if (genMaxCandidates < 1) genMaxCandidates = 1;
        EditorGUILayout.HelpBox(
            "Chỉ giữ layout khác nhau. Có bao nhiêu unique thì hiện bấy nhiêu (≤ max).",
            MessageType.None);
        genMaxTargetSolutions = EditorGUILayout.IntField("Max Target Solutions (prefer)", genMaxTargetSolutions);
        if (genMaxTargetSolutions < 1) genMaxTargetSolutions = 1;
        EditorGUILayout.HelpBox(
            "Max Target Solutions = số lời giải tối ưu tối đa (1 = unique).\n"
            + "Candidate bị reject nếu sols > giá trị này. Thêm Rock/Enemy trên choke để cắt nhánh.",
            MessageType.None);
        genMaxAttempts = EditorGUILayout.IntField("Max Attempts", genMaxAttempts);
        if (genMaxAttempts > 60) genMaxAttempts = 60;
        if (genMaxAttempts < 1) genMaxAttempts = 1;
        genSeed = EditorGUILayout.IntField("Seed (0=random)", genSeed);
        GUILayout.Label("Performance", EditorStyles.boldLabel);
        genStateLimit = EditorGUILayout.IntField("StateLimit", genStateLimit);
        genTimeLimitMs = EditorGUILayout.IntField("TimeLimitMs", genTimeLimitMs);
        if (GUILayout.Button("Generate Level", GUILayout.Height(28f)))
        {
            GenerateLevel();
        }
    }

    private void DrawLastGenerateReport()
    {
        if (lastGenerateResult == null)
        {
            return;
        }
        GUILayout.Space(8f);
        GUILayout.Label("Last Generate Output", EditorStyles.boldLabel);
        if (!lastGenerateResult.Success)
        {
            EditorGUILayout.HelpBox(lastGenerateResult.Message, MessageType.Warning);
            return;
        }
        GUILayout.Label("Attempts: " + lastGenerateResult.AttemptsUsed);
        if (!string.IsNullOrEmpty(lastGenerateResult.ChainSummary))
        {
            GUILayout.Label("Best: " + lastGenerateResult.ChainSummary);
        }
        List<LevelCandidateInfo> candidates = lastGenerateResult.Candidates;
        for (int i = 0; i < candidates.Count; i++)
        {
            LevelCandidateInfo c = candidates[i];
            string title = c.IsBest ? "[BEST] Candidate " + (i + 1) : "Candidate " + (i + 1);
            GUILayout.Label(title, c.IsBest ? EditorStyles.boldLabel : EditorStyles.label);
            GUILayout.Label("  OptimalCost: " + c.OptimalCost);
            GUILayout.Label("  MoveLimit: " + c.MoveLimit);
            GUILayout.Label("  MoveSlack: " + c.MoveSlack);
            GUILayout.Label("  SolutionCount: " + c.SolutionCount);
            GUILayout.Label("  NearMissCost: " + c.NearMissCost);
            GUILayout.Label("  DecisionPoints: " + c.DecisionPoints);
            GUILayout.Label("  DependencyDepth: " + c.DependencyDepth);
            GUILayout.Label("  ObjectImpactCount: " + c.ObjectImpactCount);
            GUILayout.Label("  RouteDiversity: " + c.RouteDiversity);
            GUILayout.Label("  Score: " + c.Score);
            if (!c.IsBest && c.Level != null && GUILayout.Button("Apply Candidate " + (i + 1)))
            {
                ApplyGenerateCandidate(c);
            }
        }
    }

    private void DrawGridResizeControls()
    {
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+Top"))
        {
            ResizeGrid(true, true, true);
        }
        if (GUILayout.Button("-Top"))
        {
            ResizeGrid(true, true, false);
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+Bottom"))
        {
            ResizeGrid(true, false, true);
        }
        if (GUILayout.Button("-Bottom"))
        {
            ResizeGrid(true, false, false);
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+Left"))
        {
            ResizeGrid(false, true, true);
        }
        if (GUILayout.Button("-Left"))
        {
            ResizeGrid(false, true, false);
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+Right"))
        {
            ResizeGrid(false, false, true);
        }
        if (GUILayout.Button("-Right"))
        {
            ResizeGrid(false, false, false);
        }
        EditorGUILayout.EndHorizontal();
    }

    private void ResizeGrid(bool row, bool topOrLeft, bool add)
    {
        if (levelData == null)
        {
            return;
        }
        PushUndo();
        bool ok;
        if (row)
        {
            if (topOrLeft)
            {
                ok = add ? levelData.AddRowTop() : levelData.RemoveRowTop();
            }
            else
            {
                ok = add ? levelData.AddRowBottom() : levelData.RemoveRowBottom();
            }
        }
        else
        {
            if (topOrLeft)
            {
                ok = add ? levelData.AddColumnLeft() : levelData.RemoveColumnLeft();
            }
            else
            {
                ok = add ? levelData.AddColumnRight() : levelData.RemoveColumnRight();
            }
        }
        if (!ok)
        {
            if (undoStack.Count > 0)
            {
                undoStack.RemoveAt(undoStack.Count - 1);
            }
            statusMessage = "Grid resize blocked (min size or max cells).";
            return;
        }
        EditorUtility.SetDirty(levelData);
        analysis = null;
        shownSolution.Clear();
        selectedSolutionIndex = 0;
        statusMessage = "Grid resized to " + levelData.Width + "x" + levelData.Height + ".";
        Repaint();
    }

    private void PaletteButton(LevelObjectType type)
    {
        Color prev = GUI.backgroundColor;
        if (tool == LevelEditorTool.Place && placeType == type)
        {
            GUI.backgroundColor = LevelBuilder.GetColor(type);
        }
        if (GUILayout.Button(type.ToString()))
        {
            tool = LevelEditorTool.Place;
            placeType = type;
        }
        GUI.backgroundColor = prev;
    }

    private void DrawGridArea()
    {
        EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("Map", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("−", EditorStyles.toolbarButton, GUILayout.Width(24f)))
        {
            SetMapZoom(mapZoom / 1.15f);
        }
        GUILayout.Label((Mathf.RoundToInt(mapZoom * 100f)) + "%", GUILayout.Width(44f));
        if (GUILayout.Button("+", EditorStyles.toolbarButton, GUILayout.Width(24f)))
        {
            SetMapZoom(mapZoom * 1.15f);
        }
        if (GUILayout.Button("100%", EditorStyles.toolbarButton, GUILayout.Width(44f)))
        {
            SetMapZoom(1f);
        }
        mapZoom = EditorGUILayout.Slider(mapZoom, 0.4f, 4f, GUILayout.Width(100f));
        GUILayout.Label("Ctrl+Scroll zoom · Mid-drag pan", EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();

        Rect area = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        EditorGUI.DrawRect(area, new Color(0.12f, 0.12f, 0.14f, 1f));
        if (levelData == null)
        {
            GUI.Label(area, "New or Load a LevelData asset.", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndVertical();
            return;
        }

        Event e = Event.current;
        float cs = MapCellSize();
        float pad = 16f;
        float gridW = levelData.Width * cs;
        float gridH = levelData.Height * cs;
        Rect viewRect = new Rect(0f, 0f, Mathf.Max(area.width, gridW + pad * 2f), Mathf.Max(area.height, gridH + pad * 2f));

        if (area.Contains(e.mousePosition))
        {
            if (e.type == EventType.ScrollWheel && (e.control || e.command))
            {
                float oldZoom = mapZoom;
                float factor = e.delta.y > 0f ? 0.9f : 1.111111f;
                SetMapZoom(mapZoom * factor);
                // Keep cursor-anchored zoom roughly stable.
                float ratio = mapZoom / Mathf.Max(0.0001f, oldZoom);
                Vector2 local = e.mousePosition - area.position;
                scroll = (scroll + local) * ratio - local;
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseDrag && e.button == 2)
            {
                scroll -= e.delta;
                e.Use();
                Repaint();
            }
        }

        scroll = GUI.BeginScrollView(area, scroll, viewRect);

        float startX = (viewRect.width - gridW) * 0.5f;
        float startY = (viewRect.height - gridH) * 0.5f;
        if (startX < pad)
        {
            startX = pad;
        }
        if (startY < pad)
        {
            startY = pad;
        }

        for (int y = 0; y < levelData.Height; y++)
        {
            for (int x = 0; x < levelData.Width; x++)
            {
                Rect cellRect = new Rect(startX + x * cs, startY + (levelData.Height - 1 - y) * cs, cs - 1f, cs - 1f);
                EditorGUI.DrawRect(cellRect, new Color(0.18f, 0.18f, 0.2f, 1f));
                long pack = PackCell(x, y);
                if (invalidCells.Contains(pack))
                {
                    EditorGUI.DrawRect(cellRect, new Color(0.8f, 0.15f, 0.15f, 0.35f));
                }
            }
        }

        List<LevelObjectData> objects = levelData.Objects;
        for (int i = 0; i < objects.Count; i++)
        {
            LevelObjectData obj = objects[i];
            if (!InGrid(obj.X, obj.Y))
            {
                continue;
            }
            Rect cellRect = new Rect(startX + obj.X * cs, startY + (levelData.Height - 1 - obj.Y) * cs, cs - 1f, cs - 1f);
            Color color = LevelBuilder.GetColor(obj.Type);
            if (obj.Type == LevelObjectType.Floor)
            {
                color.a = 0.85f;
            }
            if (obj.Type == LevelObjectType.RequiredZone)
            {
                Color floorTint = new Color(0.25f, 0.25f, 0.28f, 0.85f);
                EditorGUI.DrawRect(cellRect, floorTint);
                color.a = 0.55f;
            }
            EditorGUI.DrawRect(cellRect, color);
            if (obj.Id == selectedObjectId)
            {
                DrawRectBorder(cellRect, Color.white);
            }
        }

        if (showSolution && shownSolution.Count > 0)
        {
            DrawSolutionOverlay(startX, startY, cs);
        }

        Vector2 mouse = e.mousePosition;
        Rect gridBounds = new Rect(startX, startY, gridW, gridH);
        if (gridBounds.Contains(mouse))
        {
            int cx = Mathf.FloorToInt((mouse.x - startX) / cs);
            int cyFromTop = Mathf.FloorToInt((mouse.y - startY) / cs);
            int cy = levelData.Height - 1 - cyFromTop;
            if (InGrid(cx, cy))
            {
                hoverCell = new Vector2Int(cx, cy);
                Rect hoverRect = new Rect(startX + cx * cs, startY + (levelData.Height - 1 - cy) * cs, cs - 1f, cs - 1f);
                DrawRectBorder(hoverRect, Color.yellow);
                HandleGridInput(e, cx, cy);
            }
            else
            {
                hoverCell = new Vector2Int(-1, -1);
            }
        }

        GUI.EndScrollView();
        EditorGUILayout.EndVertical();
    }

    private void SetMapZoom(float zoom)
    {
        mapZoom = Mathf.Clamp(zoom, 0.4f, 4f);
    }

    private void ApplyGridSize(int newWidth, int newHeight)
    {
        if (levelData == null)
        {
            return;
        }
        if (newWidth < 1)
        {
            newWidth = 1;
        }
        if (newHeight < 1)
        {
            newHeight = 1;
        }
        if (newWidth > 32)
        {
            newWidth = 32;
        }
        if (newHeight > 32)
        {
            newHeight = 32;
        }
        if (newWidth * newHeight > LevelLogic.MaxCells)
        {
            statusMessage = "Grid exceeds max cells.";
            return;
        }
        if (newWidth == levelData.Width && newHeight == levelData.Height)
        {
            return;
        }

        PushUndo();
        while (levelData.Width < newWidth)
        {
            if (!levelData.AddColumnRight())
            {
                break;
            }
        }
        while (levelData.Width > newWidth)
        {
            if (!levelData.RemoveColumnRight())
            {
                break;
            }
        }
        while (levelData.Height < newHeight)
        {
            if (!levelData.AddRowBottom())
            {
                break;
            }
        }
        while (levelData.Height > newHeight)
        {
            if (!levelData.RemoveRowBottom())
            {
                break;
            }
        }
        EditorUtility.SetDirty(levelData);
        analysis = null;
        shownSolution.Clear();
        statusMessage = "Grid size set to " + levelData.Width + "x" + levelData.Height + ".";
        Repaint();
    }

    private void DrawSolutionOverlay(float startX, float startY, float cs)
    {
        if (levelData == null || shownSolution.Count == 0)
        {
            return;
        }

        LevelLogic logic = new LevelLogic(levelData);
        LevelSimState state = logic.CreateInitialState(levelData);
        if (state.PlayerIndex < 0)
        {
            return;
        }

        int reveal = solutionRevealSteps;
        if (reveal < 1)
        {
            reveal = shownSolution.Count;
        }
        if (reveal > shownSolution.Count)
        {
            reveal = shownSolution.Count;
        }

        GUIStyle stepStyle = new GUIStyle(EditorStyles.boldLabel);
        stepStyle.alignment = TextAnchor.MiddleCenter;
        stepStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(cs * 0.35f), 9, 14);
        stepStyle.normal.textColor = Color.white;

        int x;
        int y;
        logic.FromIndex(state.PlayerIndex, out x, out y);

        {
            Rect startRect = CellOverlayRect(startX, startY, x, y, cs);
            EditorGUI.DrawRect(startRect, new Color(0.15f, 0.85f, 1f, 0.55f));
            GUI.Label(startRect, "S", stepStyle);
        }

        int drawnSteps = 0;
        for (int i = 0; i < reveal; i++)
        {
            LevelDir dir = shownSolution[i];
            int prevIndex = state.PlayerIndex;
            int px = x;
            int py = y;

            logic.TryMove(ref state, dir, false);
            if (state.Dead && !state.Won)
            {
                break;
            }
            if (state.PlayerIndex < 0)
            {
                break;
            }
            logic.FromIndex(state.PlayerIndex, out x, out y);
            drawnSteps++;

            float t = shownSolution.Count <= 1 ? 1f : (float)(i + 1) / (float)shownSolution.Count;
            Color stepColor = Color.Lerp(new Color(1f, 0.9f, 0.2f, 1f), new Color(0.2f, 1f, 0.45f, 1f), t);

            if (state.PlayerIndex != prevIndex)
            {
                Rect cellRect = CellOverlayRect(startX, startY, x, y, cs);
                Color fill = stepColor;
                fill.a = 0.22f;
                EditorGUI.DrawRect(cellRect, fill);

                float ox = (i % 3 - 1) * 2f;
                float oy = ((i / 3) % 3 - 1) * 2f;
                Vector2 a = CellCenter(startX, startY, px, py, cs) + new Vector2(ox, oy);
                Vector2 b = CellCenter(startX, startY, x, y, cs) + new Vector2(ox, oy);

                Handles.BeginGUI();
                Handles.color = stepColor;
                Handles.DrawAAPolyLine(3f, a, b);
                DrawArrowHead(b, b - a, stepColor);
                Handles.EndGUI();

                GUI.Label(cellRect, (i + 1).ToString(), stepStyle);
            }
            else
            {
                int tx = px + LevelLogic.DirToDx(dir);
                int ty = py + LevelLogic.DirToDy(dir);
                if (InGrid(tx, ty))
                {
                    Rect jabRect = CellOverlayRect(startX, startY, tx, ty, cs);
                    EditorGUI.DrawRect(jabRect, new Color(1f, 0.55f, 0.1f, 0.35f));
                    Vector2 a = CellCenter(startX, startY, px, py, cs);
                    Vector2 b = CellCenter(startX, startY, tx, ty, cs);
                    Handles.BeginGUI();
                    Handles.color = new Color(1f, 0.6f, 0.1f, 1f);
                    Handles.DrawAAPolyLine(2f, a, b);
                    DrawArrowHead(b, b - a, new Color(1f, 0.6f, 0.1f, 1f));
                    Handles.EndGUI();
                    GUI.Label(jabRect, "A" + (i + 1), stepStyle);
                }
            }

            if (state.Won)
            {
                Rect endRect = CellOverlayRect(startX, startY, x, y, cs);
                EditorGUI.DrawRect(endRect, new Color(1f, 0.3f, 0.85f, 0.45f));
                GUI.Label(endRect, "E", stepStyle);
                break;
            }
        }

        if (!state.Won && drawnSteps > 0)
        {
            Rect curRect = CellOverlayRect(startX, startY, x, y, cs);
            EditorGUI.DrawRect(curRect, new Color(1f, 0.3f, 0.85f, 0.35f));
            GUI.Label(curRect, "E", stepStyle);
        }
    }

    private Rect CellOverlayRect(float startX, float startY, int x, int y, float cs)
    {
        return new Rect(startX + x * cs, startY + (levelData.Height - 1 - y) * cs, cs - 1f, cs - 1f);
    }

    private Vector2 CellCenter(float startX, float startY, int x, int y, float cs)
    {
        return new Vector2(
            startX + x * cs + cs * 0.5f,
            startY + (levelData.Height - 1 - y) * cs + cs * 0.5f);
    }

    private static void DrawArrowHead(Vector2 tip, Vector2 direction, Color color)
    {
        if (direction.sqrMagnitude < 0.01f)
        {
            return;
        }
        direction.Normalize();
        Vector2 ortho = new Vector2(-direction.y, direction.x);
        float size = 5f;
        Vector2 p1 = tip - direction * size + ortho * (size * 0.55f);
        Vector2 p2 = tip - direction * size - ortho * (size * 0.55f);
        Handles.color = color;
        Handles.DrawAAConvexPolygon(tip, p1, p2);
    }

    private void HandleGridInput(Event e, int x, int y)
    {
        if (e.type == EventType.MouseDown && e.button == 0)
        {
            OnLeftDown(x, y);
            e.Use();
        }
        else if (e.type == EventType.MouseDown && e.button == 1)
        {
            PushUndo();
            DeleteAt(x, y, true);
            e.Use();
        }
        else if (e.type == EventType.MouseDrag && e.button == 0)
        {
            OnLeftDrag(x, y);
            e.Use();
        }
        else if (e.type == EventType.MouseUp && e.button == 0)
        {
            isDragging = false;
            e.Use();
        }
    }

    private void OnLeftDown(int x, int y)
    {
        if (tool == LevelEditorTool.Place)
        {
            PushUndo();
            PlaceAt(x, y, placeType);
            return;
        }
        if (tool == LevelEditorTool.Delete)
        {
            PushUndo();
            DeleteAt(x, y, true);
            return;
        }
        if (tool == LevelEditorTool.Duplicate)
        {
            LevelObjectData src = FindTopObject(x, y);
            if (src != null)
            {
                selectedObjectId = src.Id;
                PushUndo();
                DuplicateSelected(x, y);
            }
            return;
        }
        if (tool == LevelEditorTool.Select || tool == LevelEditorTool.Move)
        {
            LevelObjectData obj = FindTopObject(x, y);
            selectedObjectId = obj != null ? obj.Id : -1;
            if (tool == LevelEditorTool.Move && obj != null)
            {
                isDragging = true;
                dragOffset = new Vector2Int(obj.X - x, obj.Y - y);
                PushUndo();
            }
        }
    }

    private void OnLeftDrag(int x, int y)
    {
        if (tool == LevelEditorTool.Place)
        {
            PlaceAt(x, y, placeType);
            return;
        }
        if (tool == LevelEditorTool.Delete)
        {
            DeleteAt(x, y, true);
            return;
        }
        if (tool == LevelEditorTool.Move && isDragging && selectedObjectId >= 0)
        {
            LevelObjectData obj = FindById(selectedObjectId);
            if (obj == null)
            {
                return;
            }
            int nx = x;
            int ny = y;
            if (!InGrid(nx, ny))
            {
                return;
            }
            if (!CanPlace(obj.Type, nx, ny, obj.Id))
            {
                invalidCells.Add(PackCell(nx, ny));
                Repaint();
                return;
            }
            obj.X = nx;
            obj.Y = ny;
            levelData.SyncDerivedFields();
            EditorUtility.SetDirty(levelData);
            Repaint();
        }
    }

    private void DrawSidePanel()
    {
        EditorGUILayout.BeginVertical(GUILayout.Width(rightPanelWidth), GUILayout.MaxWidth(rightPanelWidth), GUILayout.MinWidth(220f));
        GUILayout.Label("Analysis", EditorStyles.boldLabel);
        analysisScroll = EditorGUILayout.BeginScrollView(analysisScroll);
        if (analysis == null)
        {
            GUILayout.Label("Press Analyze.");
        }
        else
        {
            GUILayout.Label("Valid: " + analysis.Valid);
            GUILayout.Label("Solvable: " + analysis.Solvable);
            GUILayout.Label("Minimum Moves: " + FormatMetric(analysis.MinimumMoves));
            GUILayout.Label("Move Limit: " + analysis.MoveLimit);
            GUILayout.Label("Move Slack: " + (analysis.MoveSlack == int.MinValue ? "n/a" : analysis.MoveSlack.ToString()));
            GUILayout.Label("Total Solutions: " + FormatMetric(analysis.SolutionCountAtMinimum));
            GUILayout.Label("Stored: " + analysis.StoredOptimalSolutionCount + (analysis.OptimalSolutionsCapped ? " (capped)" : ""));
            if (analysis.MetricsUnavailable)
            {
                EditorGUILayout.HelpBox(analysis.UnavailableReason, MessageType.Warning);
            }

            GUILayout.Space(6f);
            GUILayout.Label("Solutions", EditorStyles.boldLabel);
            int stored = analysis.OptimalSolutions.Count;
            if (stored <= 0)
            {
                GUILayout.Label("No stored optimal solutions.");
            }
            else
            {
                if (selectedSolutionIndex < 0)
                {
                    selectedSolutionIndex = 0;
                }
                if (selectedSolutionIndex >= stored)
                {
                    selectedSolutionIndex = stored - 1;
                }
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginDisabledGroup(selectedSolutionIndex <= 0);
                if (GUILayout.Button("Previous"))
                {
                    selectedSolutionIndex--;
                    ApplySelectedSolution();
                }
                EditorGUI.EndDisabledGroup();
                GUILayout.Label((selectedSolutionIndex + 1) + " / " + stored, GUILayout.Width(64f));
                EditorGUI.BeginDisabledGroup(selectedSolutionIndex >= stored - 1);
                if (GUILayout.Button("Next"))
                {
                    selectedSolutionIndex++;
                    ApplySelectedSolution();
                }
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.EndHorizontal();
                showSolution = EditorGUILayout.Toggle("Show on Grid", showSolution);
                if (showSolution && shownSolution.Count > 0)
                {
                    if (solutionRevealSteps < 1 || solutionRevealSteps > shownSolution.Count)
                    {
                        solutionRevealSteps = shownSolution.Count;
                    }
                    solutionRevealSteps = EditorGUILayout.IntSlider(
                        "Reveal Steps",
                        solutionRevealSteps,
                        1,
                        shownSolution.Count);
                    EditorGUILayout.HelpBox("S=start, E=end, số=thứ tự bước, A#=attack/push. Kéo Reveal Steps để xem từng bước.", MessageType.None);
                }
                int actionSteps = shownSolution.Count;
                int moveCost = ComputeSolutionMoveCost(shownSolution);
                GUILayout.Label("Steps: " + actionSteps + " actions");
                GUILayout.Label("Moves used: " + (moveCost >= 0 ? moveCost.ToString() : "n/a"));
                GUILayout.Label("Action Sequence", EditorStyles.boldLabel);
                actionScroll = EditorGUILayout.BeginScrollView(actionScroll, GUILayout.Height(80f));
                GUILayout.Label(FormatActionSequence(shownSolution), EditorStyles.wordWrappedLabel);
                EditorGUILayout.EndScrollView();
            }

            GUILayout.Space(6f);
            GUILayout.Label("Object Counts", EditorStyles.boldLabel);
            GUILayout.Label("Floor " + analysis.Counts.Floor + " Wall " + analysis.Counts.Wall + " Boundary " + analysis.Counts.Boundary);
            GUILayout.Label("Player " + analysis.Counts.PlayerStart + " Enemy " + analysis.Counts.Enemy + " Rock " + analysis.Counts.Rock);
            GUILayout.Label("Spike " + analysis.Counts.Spike + " Key " + analysis.Counts.Key + " Door " + analysis.Counts.Door + " Goal " + analysis.Counts.Goal);
            GUILayout.Label("RequiredZone " + analysis.Counts.RequiredZone);
            GUILayout.Space(6f);
            GUILayout.Label("Validation Issues", EditorStyles.boldLabel);
            for (int i = 0; i < analysis.ValidationIssues.Count; i++)
            {
                LevelValidationIssue issue = analysis.ValidationIssues[i];
                string prefix = issue.IsError ? "[E] " : "[W] ";
                GUILayout.Label(prefix + issue.Message + " @ " + issue.Cell.x + "," + issue.Cell.y);
            }
            GUILayout.Space(6f);
            GUILayout.Label("Deadlocks", EditorStyles.boldLabel);
            if (analysis.Deadlocks.Count == 0)
            {
                GUILayout.Label("None detected.");
            }
            for (int i = 0; i < analysis.Deadlocks.Count; i++)
            {
                LevelDeadlockInfo d = analysis.Deadlocks[i];
                GUILayout.Label(d.ObjectType + " @ " + d.Cell.x + "," + d.Cell.y);
                GUILayout.Label(d.Reason, EditorStyles.wordWrappedMiniLabel);
            }
        }
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();
    }

    private static string FormatMetric(int value)
    {
        if (value < 0)
        {
            return "unavailable";
        }
        return value.ToString();
    }

    private static string FormatActionSequence(List<LevelDir> path)
    {
        if (path == null || path.Count == 0)
        {
            return "(empty)";
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder(path.Count * 3);
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            LevelDir dir = path[i];
            if (dir == LevelDir.Up) sb.Append('U');
            else if (dir == LevelDir.Right) sb.Append('R');
            else if (dir == LevelDir.Down) sb.Append('D');
            else if (dir == LevelDir.Left) sb.Append('L');
            else sb.Append('?');
        }
        return sb.ToString();
    }

    private int ComputeSolutionMoveCost(List<LevelDir> path)
    {
        if (levelData == null || path == null || path.Count == 0)
        {
            return -1;
        }
        LevelLogic logic = new LevelLogic(levelData);
        LevelSimState state = logic.CreateInitialState(levelData);
        for (int i = 0; i < path.Count; i++)
        {
            logic.TryMove(ref state, path[i], false);
            if (state.Dead && !state.Won)
            {
                return -1;
            }
        }
        return state.MovesUsed;
    }

    private void ApplySelectedSolution()
    {
        shownSolution.Clear();
        if (analysis == null || analysis.OptimalSolutions.Count == 0)
        {
            return;
        }
        if (selectedSolutionIndex < 0 || selectedSolutionIndex >= analysis.OptimalSolutions.Count)
        {
            selectedSolutionIndex = 0;
        }
        List<LevelDir> path = analysis.OptimalSolutions[selectedSolutionIndex];
        for (int i = 0; i < path.Count; i++)
        {
            shownSolution.Add(path[i]);
        }
        solutionRevealSteps = shownSolution.Count;
        showSolution = true;
        Repaint();
    }

    private void NewLevel()
    {
        string path = EditorUtility.SaveFilePanelInProject("New LevelData", "LevelData", "asset", "Create level asset");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        LevelData asset = CreateInstance<LevelData>();
        asset.Width = 8;
        asset.Height = 8;
        asset.MoveLimit = 20;
        AssetDatabase.CreateAsset(asset, path);
        AssetDatabase.SaveAssets();
        levelData = asset;
        assetPath = path;
        genWidth = levelData.Width;
        genHeight = levelData.Height;
        undoStack.Clear();
        redoStack.Clear();
        analysis = null;
        statusMessage = "Created " + path;
        RememberLevelDir(path);
    }

    private void GenerateLevel()
    {
        if (levelData == null)
        {
            string msg = "Generation Failed: draw/load a base map first.";
            statusMessage = msg;
            EditorUtility.DisplayDialog("Generate Level Failed", msg, "OK");
            return;
        }

        genWidth = levelData.Width;
        genHeight = levelData.Height;

        LevelGenerateParams genParams = new LevelGenerateParams();
        genParams.BaseMap = levelData;
        genParams.Width = genWidth;
        genParams.Height = genHeight;
        genParams.MaxAttempts = genMaxAttempts;
        genParams.Seed = genSeed;
        genParams.EnemyCount = genEnemyCount;
        genParams.RockCount = genRockCount;
        genParams.SpikeCount = genSpikeCount;
        genParams.MaxCandidates = genMaxCandidates;
        genParams.CostSlack = genCostSlack;
        genParams.MaxTargetSolutions = genMaxTargetSolutions;
        genParams.StateLimit = genStateLimit;
        genParams.TimeLimitMs = genTimeLimitMs;

        EditorUtility.DisplayProgressBar("Generate Level", "Path-first: route → objects on path → solve...", 0.5f);
        LevelGenerateResult generated;
        try
        {
            generated = LevelRouteGenerator.Generate(genParams);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        DestroyOrphanCandidates();
        lastGenerateResult = generated;

        if (!generated.Success || generated.Level == null)
        {
            statusMessage = generated.Message;
            EditorUtility.DisplayDialog("Generate Level Failed", generated.Message, "OK");
            Debug.LogWarning(generated.Message);
            Repaint();
            return;
        }

        PushUndo();
        levelData.CopyFrom(generated.Level);
        EditorUtility.SetDirty(levelData);

        analysis = null;
        shownSolution.Clear();
        selectedSolutionIndex = 0;
        showSolution = false;
        RunAnalyze();
        statusMessage = generated.Message;
        EditorUtility.DisplayDialog("Generate Level", generated.Message, "OK");
        Debug.Log(generated.Message);
        Repaint();
    }

    private void ApplyGenerateCandidate(LevelCandidateInfo candidate)
    {
        if (levelData == null || candidate == null || candidate.Level == null || lastGenerateResult == null)
        {
            return;
        }
        PushUndo();
        levelData.CopyFrom(candidate.Level);
        EditorUtility.SetDirty(levelData);
        for (int i = 0; i < lastGenerateResult.Candidates.Count; i++)
        {
            lastGenerateResult.Candidates[i].IsBest = (lastGenerateResult.Candidates[i] == candidate);
        }
        analysis = null;
        shownSolution.Clear();
        selectedSolutionIndex = 0;
        showSolution = false;
        RunAnalyze();
        statusMessage = "Applied candidate: " + candidate.Summary;
        Repaint();
    }

    private void DestroyOrphanCandidates()
    {
        if (lastGenerateResult == null)
        {
            return;
        }
        for (int i = 0; i < lastGenerateResult.Candidates.Count; i++)
        {
            LevelCandidateInfo c = lastGenerateResult.Candidates[i];
            if (c.Level != null)
            {
                Object.DestroyImmediate(c.Level);
                c.Level = null;
            }
        }
        lastGenerateResult.Candidates.Clear();
        lastGenerateResult.Level = null;
    }

    private void SaveLevel(bool saveAs)
    {
        if (levelData == null)
        {
            statusMessage = "No level loaded.";
            return;
        }
        levelData.SyncDerivedFields();
        if (saveAs || string.IsNullOrEmpty(assetPath))
        {
            string path = EditorUtility.SaveFilePanelInProject("Save LevelData", levelData.name, "asset", "Save level asset");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            if (AssetDatabase.Contains(levelData))
            {
                LevelData clone = CreateInstance<LevelData>();
                clone.CopyFrom(levelData);
                AssetDatabase.CreateAsset(clone, path);
                levelData = clone;
            }
            else
            {
                AssetDatabase.CreateAsset(levelData, path);
            }
            assetPath = path;
            RememberLevelDir(path);
        }
        EditorUtility.SetDirty(levelData);
        AssetDatabase.SaveAssets();
        statusMessage = "Saved " + assetPath;
    }

    private void LoadLevel()
    {
        string startDir = EditorPrefs.GetString(PrefLastLevelDir, Application.dataPath);
        if (string.IsNullOrEmpty(startDir) || !System.IO.Directory.Exists(startDir))
        {
            startDir = Application.dataPath;
        }
        string path = EditorUtility.OpenFilePanel("Load LevelData", startDir, "asset");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        RememberAbsoluteDir(path);
        if (path.StartsWith(Application.dataPath))
        {
            path = "Assets" + path.Substring(Application.dataPath.Length);
        }
        LevelData asset = AssetDatabase.LoadAssetAtPath<LevelData>(path);
        if (asset == null)
        {
            statusMessage = "Not a LevelData asset.";
            return;
        }
        levelData = asset;
        assetPath = path;
        genWidth = levelData.Width;
        genHeight = levelData.Height;
        undoStack.Clear();
        redoStack.Clear();
        analysis = null;
        shownSolution.Clear();
        selectedSolutionIndex = 0;
        statusMessage = "Loaded " + path;
    }

    private static void RememberLevelDir(string assetOrAbsolutePath)
    {
        if (string.IsNullOrEmpty(assetOrAbsolutePath))
        {
            return;
        }
        string absolute = assetOrAbsolutePath;
        if (assetOrAbsolutePath.StartsWith("Assets"))
        {
            absolute = Application.dataPath + assetOrAbsolutePath.Substring("Assets".Length);
        }
        RememberAbsoluteDir(absolute);
    }

    private static void RememberAbsoluteDir(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
        {
            return;
        }
        string dir = System.IO.Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir))
        {
            EditorPrefs.SetString(PrefLastLevelDir, dir);
        }
    }

    private void PushUndo()
    {
        if (levelData == null)
        {
            return;
        }
        undoStack.Add(levelData.ToSnapshotBytes());
        if (undoStack.Count > 100)
        {
            undoStack.RemoveAt(0);
        }
        redoStack.Clear();
    }

    private void UndoEdit()
    {
        if (levelData == null || undoStack.Count == 0)
        {
            return;
        }
        redoStack.Add(levelData.ToSnapshotBytes());
        byte[] snap = undoStack[undoStack.Count - 1];
        undoStack.RemoveAt(undoStack.Count - 1);
        levelData.FromSnapshotBytes(snap);
        EditorUtility.SetDirty(levelData);
        Repaint();
    }

    private void RedoEdit()
    {
        if (levelData == null || redoStack.Count == 0)
        {
            return;
        }
        undoStack.Add(levelData.ToSnapshotBytes());
        byte[] snap = redoStack[redoStack.Count - 1];
        redoStack.RemoveAt(redoStack.Count - 1);
        levelData.FromSnapshotBytes(snap);
        EditorUtility.SetDirty(levelData);
        Repaint();
    }

    private void StartTest()
    {
        if (levelData == null)
        {
            statusMessage = "Load a level first.";
            return;
        }
        levelData.SyncDerivedFields();
        LevelValidationResult validation = LevelValidator.Validate(levelData);
        RebuildInvalidCells(validation);
        if (!validation.IsValid)
        {
            statusMessage = "Fix validation errors before Test.";
            Repaint();
            return;
        }
        EditorUtility.SetDirty(levelData);
        AssetDatabase.SaveAssets();
        TestSession.BeginTest(levelData);
        statusMessage = "Entered Test. Swipe/drag to move. Restart/Exit in Game view overlay.";
    }

    private void RunAnalyze()
    {
        if (levelData == null)
        {
            statusMessage = "Load a level first.";
            return;
        }
        levelData.SyncDerivedFields();
        LevelAnalyzer.MaxStoredSolutions = maxStoredSolutions;
        analysis = LevelAnalyzer.Analyze(levelData, maxStoredSolutions);
        RebuildInvalidCellsFromAnalysis();
        selectedSolutionIndex = 0;
        ApplySelectedSolution();
        int total = analysis.SolutionCountAtMinimum;
        statusMessage = analysis.Solvable
            ? ("Analyze complete: " + analysis.StoredOptimalSolutionCount + " stored / " + FormatMetric(total) + " solutions (<= MoveLimit).")
            : "Analyze complete: not solvable or incomplete.";
        Repaint();
    }

    private void ToggleShowSolution()
    {
        if (levelData == null)
        {
            statusMessage = "Load a level first.";
            return;
        }
        RunAnalyze();
        showSolution = shownSolution.Count > 0;
        if (showSolution)
        {
            statusMessage = "Solution recalculated and shown (" + (selectedSolutionIndex + 1) + "/" + analysis.OptimalSolutions.Count + ").";
        }
        else
        {
            statusMessage = analysis != null && !analysis.Solvable
                ? "No solution to show."
                : "Solution recalculated; nothing to display.";
        }
        Repaint();
    }

    private void PlaySolution()
    {
        if (levelData == null)
        {
            return;
        }
        if (shownSolution.Count == 0)
        {
            RunAnalyze();
        }
        if (shownSolution.Count == 0)
        {
            statusMessage = "No solution to play.";
            return;
        }
        levelData.SyncDerivedFields();
        LevelValidationResult validation = LevelValidator.Validate(levelData);
        RebuildInvalidCells(validation);
        if (!validation.IsValid)
        {
            statusMessage = "Fix validation errors before Play Solution.";
            Repaint();
            return;
        }
        EditorUtility.SetDirty(levelData);
        AssetDatabase.SaveAssets();
        TestSession.BeginTest(levelData, shownSolution);
        statusMessage = "Playing optimal solution in Test.";
    }

    private void PlaceAt(int x, int y, LevelObjectType type)
    {
        if (!CanPlace(type, x, y, -1))
        {
            invalidCells.Add(PackCell(x, y));
            statusMessage = "Invalid placement at " + x + "," + y;
            Repaint();
            return;
        }
        if (type == LevelObjectType.PlayerStart)
        {
            RemoveAllOfType(LevelObjectType.PlayerStart);
        }
        if (type == LevelObjectType.Goal)
        {
            RemoveAllOfType(LevelObjectType.Goal);
        }
        if (type == LevelObjectType.Floor)
        {
            if (HasTypeAt(x, y, LevelObjectType.Floor))
            {
                return;
            }
        }
        else
        {
            RemoveExclusiveAt(x, y, type);
        }
        LevelObjectData obj = new LevelObjectData(levelData.AllocateObjectId(), type, x, y);
        levelData.Objects.Add(obj);
        levelData.SyncDerivedFields();
        EditorUtility.SetDirty(levelData);
        selectedObjectId = obj.Id;
        invalidCells.Remove(PackCell(x, y));
        Repaint();
    }

    private void DeleteAt(int x, int y, bool preferTop)
    {
        for (int i = levelData.Objects.Count - 1; i >= 0; i--)
        {
            LevelObjectData obj = levelData.Objects[i];
            if (obj.X != x || obj.Y != y)
            {
                continue;
            }
            if (!preferTop && obj.Type == LevelObjectType.Floor)
            {
                continue;
            }
            levelData.Objects.RemoveAt(i);
            if (obj.Id == selectedObjectId)
            {
                selectedObjectId = -1;
            }
            if (preferTop && obj.Type != LevelObjectType.Floor)
            {
                break;
            }
            if (!preferTop)
            {
                break;
            }
        }
        levelData.SyncDerivedFields();
        EditorUtility.SetDirty(levelData);
        Repaint();
    }

    private void DuplicateSelected(int x, int y)
    {
        LevelObjectData src = FindById(selectedObjectId);
        if (src == null)
        {
            return;
        }
        if (src.Type == LevelObjectType.PlayerStart || src.Type == LevelObjectType.Goal)
        {
            statusMessage = "Cannot duplicate Player Start or Goal.";
            return;
        }
        if (!CanPlace(src.Type, x, y, -1))
        {
            invalidCells.Add(PackCell(x, y));
            statusMessage = "Duplicate target invalid.";
            return;
        }
        RemoveExclusiveAt(x, y, src.Type);
        LevelObjectData clone = new LevelObjectData(levelData.AllocateObjectId(), src.Type, x, y);
        levelData.Objects.Add(clone);
        selectedObjectId = clone.Id;
        levelData.SyncDerivedFields();
        EditorUtility.SetDirty(levelData);
        Repaint();
    }

    private bool CanPlace(LevelObjectType type, int x, int y, int ignoreId)
    {
        if (!InGrid(x, y))
        {
            return false;
        }
        if (type == LevelObjectType.Floor)
        {
            return true;
        }
        for (int i = 0; i < levelData.Objects.Count; i++)
        {
            LevelObjectData obj = levelData.Objects[i];
            if (obj.Id == ignoreId)
            {
                continue;
            }
            if (obj.X != x || obj.Y != y)
            {
                continue;
            }
            if (obj.Type == LevelObjectType.Floor)
            {
                continue;
            }
            if (LevelValidator.PlacementConflicts(type, obj.Type))
            {
                return false;
            }
        }
        return true;
    }

    private void RemoveExclusiveAt(int x, int y, LevelObjectType incoming)
    {
        for (int i = levelData.Objects.Count - 1; i >= 0; i--)
        {
            LevelObjectData obj = levelData.Objects[i];
            if (obj.X != x || obj.Y != y)
            {
                continue;
            }
            if (obj.Type == LevelObjectType.Floor)
            {
                continue;
            }
            if (LevelValidator.PlacementConflicts(incoming, obj.Type))
            {
                levelData.Objects.RemoveAt(i);
            }
        }
    }

    private void RemoveAllOfType(LevelObjectType type)
    {
        for (int i = levelData.Objects.Count - 1; i >= 0; i--)
        {
            if (levelData.Objects[i].Type == type)
            {
                levelData.Objects.RemoveAt(i);
            }
        }
    }

    private bool HasTypeAt(int x, int y, LevelObjectType type)
    {
        for (int i = 0; i < levelData.Objects.Count; i++)
        {
            LevelObjectData obj = levelData.Objects[i];
            if (obj.X == x && obj.Y == y && obj.Type == type)
            {
                return true;
            }
        }
        return false;
    }

    private LevelObjectData FindTopObject(int x, int y)
    {
        LevelObjectData best = null;
        for (int i = 0; i < levelData.Objects.Count; i++)
        {
            LevelObjectData obj = levelData.Objects[i];
            if (obj.X != x || obj.Y != y)
            {
                continue;
            }
            if (best == null || obj.Type != LevelObjectType.Floor)
            {
                best = obj;
            }
        }
        return best;
    }

    private LevelObjectData FindById(int id)
    {
        for (int i = 0; i < levelData.Objects.Count; i++)
        {
            if (levelData.Objects[i].Id == id)
            {
                return levelData.Objects[i];
            }
        }
        return null;
    }

    private bool InGrid(int x, int y)
    {
        return levelData != null && x >= 0 && y >= 0 && x < levelData.Width && y < levelData.Height;
    }

    private static long PackCell(int x, int y)
    {
        return ((long)y << 32) ^ (uint)x;
    }

    private void RebuildInvalidCells(LevelValidationResult validation)
    {
        invalidCells.Clear();
        for (int i = 0; i < validation.Issues.Count; i++)
        {
            LevelValidationIssue issue = validation.Issues[i];
            if (issue.Cell.x >= 0)
            {
                invalidCells.Add(PackCell(issue.Cell.x, issue.Cell.y));
            }
        }
    }

    private void RebuildInvalidCellsFromAnalysis()
    {
        invalidCells.Clear();
        if (analysis == null)
        {
            return;
        }
        for (int i = 0; i < analysis.ValidationIssues.Count; i++)
        {
            LevelValidationIssue issue = analysis.ValidationIssues[i];
            if (issue.Cell.x >= 0)
            {
                invalidCells.Add(PackCell(issue.Cell.x, issue.Cell.y));
            }
        }
        for (int i = 0; i < analysis.Deadlocks.Count; i++)
        {
            LevelDeadlockInfo d = analysis.Deadlocks[i];
            if (d.Cell.x >= 0)
            {
                invalidCells.Add(PackCell(d.Cell.x, d.Cell.y));
            }
        }
    }

    private static void DrawRectBorder(Rect rect, Color color)
    {
        EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 2f), color);
        EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 2f, rect.width, 2f), color);
        EditorGUI.DrawRect(new Rect(rect.x, rect.y, 2f, rect.height), color);
        EditorGUI.DrawRect(new Rect(rect.xMax - 2f, rect.y, 2f, rect.height), color);
    }
}
