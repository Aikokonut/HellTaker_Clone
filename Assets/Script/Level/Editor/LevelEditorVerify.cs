using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class LevelEditorVerify
{
    [MenuItem("Tools/Level Editor/Verify Grid And Solutions")]
    public static void Verify()
    {
        int failures = 0;
        failures += VerifyGridResize();
        failures += VerifyMultipleSolutions();
        if (failures == 0)
        {
            Debug.Log("Level Editor Verify: PASS");
            EditorUtility.DisplayDialog("Level Editor Verify", "PASS", "OK");
        }
        else
        {
            Debug.LogError("Level Editor Verify: FAIL (" + failures + ")");
            EditorUtility.DisplayDialog("Level Editor Verify", "FAIL count=" + failures + " (see Console)", "OK");
        }
    }

    private static int VerifyGridResize()
    {
        int failures = 0;
        LevelData data = ScriptableObject.CreateInstance<LevelData>();
        data.Width = 3;
        data.Height = 3;
        data.Objects.Add(new LevelObjectData(1, LevelObjectType.Floor, 1, 1));
        data.Objects.Add(new LevelObjectData(2, LevelObjectType.PlayerStart, 1, 1));

        if (!data.AddRowTop())
        {
            failures++;
        }
        if (data.Height != 4 || data.Objects[0].X != 1 || data.Objects[0].Y != 2)
        {
            Debug.LogError("AddRowTop should offset object to (1,2), height 4.");
            failures++;
        }

        if (!data.AddColumnLeft())
        {
            failures++;
        }
        if (data.Width != 4 || data.Objects[0].X != 2 || data.Objects[0].Y != 2)
        {
            Debug.LogError("AddColumnLeft should move object to (2,2).");
            failures++;
        }

        byte[] snap = data.ToSnapshotBytes();
        LevelData loaded = ScriptableObject.CreateInstance<LevelData>();
        loaded.FromSnapshotBytes(snap);
        if (loaded.Width != data.Width || loaded.Height != data.Height || loaded.Objects.Count != data.Objects.Count)
        {
            Debug.LogError("Snapshot save/load mismatch.");
            failures++;
        }
        if (loaded.Objects[0].X != 2 || loaded.Objects[0].Y != 2)
        {
            Debug.LogError("Snapshot object position mismatch.");
            failures++;
        }

        Object.DestroyImmediate(data);
        Object.DestroyImmediate(loaded);
        return failures;
    }

    private static int VerifyMultipleSolutions()
    {
        int failures = 0;
        LevelData data = ScriptableObject.CreateInstance<LevelData>();
        data.Width = 2;
        data.Height = 2;
        data.MoveLimit = 2;
        data.Objects.Add(new LevelObjectData(1, LevelObjectType.Floor, 0, 0));
        data.Objects.Add(new LevelObjectData(2, LevelObjectType.Floor, 1, 0));
        data.Objects.Add(new LevelObjectData(3, LevelObjectType.Floor, 0, 1));
        data.Objects.Add(new LevelObjectData(4, LevelObjectType.Floor, 1, 1));
        data.Objects.Add(new LevelObjectData(5, LevelObjectType.PlayerStart, 0, 0));
        data.Objects.Add(new LevelObjectData(6, LevelObjectType.Goal, 1, 1));
        data.SyncDerivedFields();

        LevelSolveResult solve = LevelSolver.Solve(data, 500000, false, 16);
        if (!solve.Success || solve.MinimumMoves != 2)
        {
            Debug.LogError("Expected minimum moves 2, got " + solve.MinimumMoves + " success=" + solve.Success);
            failures++;
        }
        if (solve.SolutionCountAtMinimum < 2)
        {
            Debug.LogError("Expected total solution count >= 2, got " + solve.SolutionCountAtMinimum);
            failures++;
        }
        if (solve.StoredSolutions.Count < 2)
        {
            Debug.LogError("Expected at least 2 stored distinct sequences, stored=" + solve.StoredSolutions.Count);
            failures++;
        }
        if (solve.StoredSolutions.Count >= 2 && PathsEqual(solve.StoredSolutions[0], solve.StoredSolutions[1]))
        {
            Debug.LogError("Stored solutions were identical.");
            failures++;
        }
        if (failures == 0 && solve.StoredSolutions.Count >= 2)
        {
            Debug.Log("MultiSol POC: PASS MinMoves=" + solve.MinimumMoves
                + " Stored=" + solve.StoredSolutions.Count
                + " → Show Solution 1/" + solve.StoredSolutions.Count
                + " and 2/" + solve.StoredSolutions.Count);
        }

        for (int i = 0; i < solve.StoredSolutions.Count; i++)
        {
            if (!SimulateWinsWithinLimit(data, solve.StoredSolutions[i], data.MoveLimit))
            {
                Debug.LogError("Solution " + i + " does not match solver rules.");
                failures++;
            }
        }

        failures += VerifySpikeExtraMove();
        failures += VerifySpikeAttackCostsTwo();
        failures += VerifyWallBumpCostsNoTurn();
        failures += VerifyAdjacentEnemyPushBlocked();
        failures += VerifyRockNotDestroyed();
        failures += VerifyEnemyOnKeySurvives();

        Object.DestroyImmediate(data);
        return failures;
    }

    private static int VerifySpikeExtraMove()
    {
        LevelData data = ScriptableObject.CreateInstance<LevelData>();
        data.Width = 3;
        data.Height = 1;
        data.MoveLimit = 10;
        data.Objects.Add(new LevelObjectData(1, LevelObjectType.Floor, 0, 0));
        data.Objects.Add(new LevelObjectData(2, LevelObjectType.Floor, 1, 0));
        data.Objects.Add(new LevelObjectData(3, LevelObjectType.Floor, 2, 0));
        data.Objects.Add(new LevelObjectData(4, LevelObjectType.Spike, 1, 0));
        data.Objects.Add(new LevelObjectData(5, LevelObjectType.PlayerStart, 0, 0));
        data.Objects.Add(new LevelObjectData(6, LevelObjectType.Goal, 2, 0));
        data.SyncDerivedFields();

        LevelLogic logic = new LevelLogic(data);
        LevelSimState state = logic.CreateInitialState(data);
        logic.TryMove(ref state, LevelDir.Right, true);
        int failures = 0;
        if (state.Dead || state.MovesUsed != 2 || state.PlayerIndex != logic.ToIndex(1, 0))
        {
            Debug.LogError("Spike should cost +1 move and not kill. moves=" + state.MovesUsed + " dead=" + state.Dead);
            failures++;
        }
        Object.DestroyImmediate(data);
        return failures;
    }

    private static int VerifyAdjacentEnemyPushBlocked()
    {
        LevelData data = ScriptableObject.CreateInstance<LevelData>();
        data.Width = 4;
        data.Height = 1;
        data.MoveLimit = 10;
        data.Objects.Add(new LevelObjectData(1, LevelObjectType.Floor, 0, 0));
        data.Objects.Add(new LevelObjectData(2, LevelObjectType.Floor, 1, 0));
        data.Objects.Add(new LevelObjectData(3, LevelObjectType.Floor, 2, 0));
        data.Objects.Add(new LevelObjectData(4, LevelObjectType.Floor, 3, 0));
        data.Objects.Add(new LevelObjectData(5, LevelObjectType.PlayerStart, 0, 0));
        data.Objects.Add(new LevelObjectData(6, LevelObjectType.Enemy, 1, 0));
        data.Objects.Add(new LevelObjectData(7, LevelObjectType.Enemy, 2, 0));
        data.Objects.Add(new LevelObjectData(8, LevelObjectType.Goal, 3, 0));
        data.SyncDerivedFields();

        LevelLogic logic = new LevelLogic(data);
        LevelSimState state = logic.CreateInitialState(data);
        int playerBefore = state.PlayerIndex;
        logic.TryMove(ref state, LevelDir.Right, true);
        int failures = 0;
        if (state.PlayerIndex != playerBefore)
        {
            Debug.LogError("Push into adjacent enemy should be blocked; player should not move.");
            failures++;
        }
        if (!LevelLogic.HasEnemy(ref state, logic.ToIndex(1, 0)) || !LevelLogic.HasEnemy(ref state, logic.ToIndex(2, 0)))
        {
            Debug.LogError("Adjacent enemies must remain; no chain/kick into enemy.");
            failures++;
        }
        if (state.MovesUsed != 0)
        {
            Debug.LogError("Blocked attack must not spend a turn. moves=" + state.MovesUsed);
            failures++;
        }
        Object.DestroyImmediate(data);
        return failures;
    }

    private static int VerifySpikeAttackCostsTwo()
    {
        LevelData data = ScriptableObject.CreateInstance<LevelData>();
        data.Width = 3;
        data.Height = 1;
        data.MoveLimit = 10;
        data.Objects.Add(new LevelObjectData(1, LevelObjectType.Floor, 0, 0));
        data.Objects.Add(new LevelObjectData(2, LevelObjectType.Floor, 1, 0));
        data.Objects.Add(new LevelObjectData(3, LevelObjectType.Floor, 2, 0));
        data.Objects.Add(new LevelObjectData(4, LevelObjectType.Spike, 0, 0));
        data.Objects.Add(new LevelObjectData(5, LevelObjectType.PlayerStart, 0, 0));
        data.Objects.Add(new LevelObjectData(6, LevelObjectType.Enemy, 1, 0));
        data.Objects.Add(new LevelObjectData(7, LevelObjectType.Wall, 2, 0));
        data.Objects.Add(new LevelObjectData(8, LevelObjectType.Goal, 0, 0));
        data.SyncDerivedFields();

        LevelLogic logic = new LevelLogic(data);
        LevelSimState state = logic.CreateInitialState(data);
        // Stand on spike already (spawn). Kick enemy into wall.
        logic.TryMove(ref state, LevelDir.Right, true);
        int failures = 0;
        if (state.PlayerIndex != logic.ToIndex(0, 0))
        {
            Debug.LogError("Kick on spike: player must stay.");
            failures++;
        }
        if (state.MovesUsed != 2)
        {
            Debug.LogError("Kick while on spike must cost 2 (action+spike). moves=" + state.MovesUsed);
            failures++;
        }
        if (LevelLogic.HasEnemy(ref state, logic.ToIndex(1, 0)))
        {
            Debug.LogError("Enemy should be destroyed when kicked into wall.");
            failures++;
        }
        Object.DestroyImmediate(data);
        return failures;
    }

    private static int VerifyWallBumpCostsNoTurn()
    {
        LevelData data = ScriptableObject.CreateInstance<LevelData>();
        data.Width = 2;
        data.Height = 1;
        data.MoveLimit = 10;
        data.Objects.Add(new LevelObjectData(1, LevelObjectType.Floor, 0, 0));
        data.Objects.Add(new LevelObjectData(2, LevelObjectType.Wall, 1, 0));
        data.Objects.Add(new LevelObjectData(3, LevelObjectType.PlayerStart, 0, 0));
        data.Objects.Add(new LevelObjectData(4, LevelObjectType.Goal, 0, 0));
        data.SyncDerivedFields();

        LevelLogic logic = new LevelLogic(data);
        LevelSimState state = logic.CreateInitialState(data);
        LevelActionResult result = logic.TryMove(ref state, LevelDir.Right, true);
        int failures = 0;
        if (result != LevelActionResult.Bumped || state.MovesUsed != 0 || state.PlayerIndex != logic.ToIndex(0, 0))
        {
            Debug.LogError("Wall bump must not move or spend a turn. result=" + result + " moves=" + state.MovesUsed);
            failures++;
        }
        Object.DestroyImmediate(data);
        return failures;
    }

    private static int VerifyRockNotDestroyed()
    {
        LevelData data = ScriptableObject.CreateInstance<LevelData>();
        data.Width = 3;
        data.Height = 1;
        data.MoveLimit = 10;
        data.Objects.Add(new LevelObjectData(1, LevelObjectType.Floor, 0, 0));
        data.Objects.Add(new LevelObjectData(2, LevelObjectType.Floor, 1, 0));
        data.Objects.Add(new LevelObjectData(3, LevelObjectType.Wall, 2, 0));
        data.Objects.Add(new LevelObjectData(4, LevelObjectType.PlayerStart, 0, 0));
        data.Objects.Add(new LevelObjectData(5, LevelObjectType.Rock, 1, 0));
        data.Objects.Add(new LevelObjectData(6, LevelObjectType.Goal, 0, 0));
        data.SyncDerivedFields();

        LevelLogic logic = new LevelLogic(data);
        LevelSimState state = logic.CreateInitialState(data);
        int playerBefore = state.PlayerIndex;
        logic.TryMove(ref state, LevelDir.Right, true);
        int failures = 0;
        if (state.PlayerIndex != playerBefore)
        {
            Debug.LogError("Rock against wall should block; player must not move.");
            failures++;
        }
        if (!LevelLogic.HasRock(ref state, logic.ToIndex(1, 0)))
        {
            Debug.LogError("Rock must not be destroyed when push is blocked.");
            failures++;
        }
        if (state.MovesUsed != 0)
        {
            Debug.LogError("Blocked rock push must not spend a turn. moves=" + state.MovesUsed);
            failures++;
        }
        Object.DestroyImmediate(data);
        return failures;
    }

    private static int VerifyEnemyOnKeySurvives()
    {
        LevelData data = ScriptableObject.CreateInstance<LevelData>();
        data.Width = 3;
        data.Height = 1;
        data.MoveLimit = 10;
        data.Objects.Add(new LevelObjectData(1, LevelObjectType.Floor, 0, 0));
        data.Objects.Add(new LevelObjectData(2, LevelObjectType.Floor, 1, 0));
        data.Objects.Add(new LevelObjectData(3, LevelObjectType.Floor, 2, 0));
        data.Objects.Add(new LevelObjectData(4, LevelObjectType.PlayerStart, 0, 0));
        data.Objects.Add(new LevelObjectData(5, LevelObjectType.Enemy, 1, 0));
        data.Objects.Add(new LevelObjectData(6, LevelObjectType.Key, 2, 0));
        data.Objects.Add(new LevelObjectData(7, LevelObjectType.Goal, 0, 0));
        data.SyncDerivedFields();

        LevelLogic logic = new LevelLogic(data);
        LevelSimState state = logic.CreateInitialState(data);
        logic.TryMove(ref state, LevelDir.Right, true);
        int failures = 0;
        int keyCell = logic.ToIndex(2, 0);
        if (state.PlayerIndex != logic.ToIndex(0, 0))
        {
            Debug.LogError("Helltaker kick/push: player must stay in place.");
            failures++;
        }
        if (!LevelLogic.HasEnemy(ref state, keyCell))
        {
            Debug.LogError("Enemy pushed onto Key must survive and occupy the key cell.");
            failures++;
        }
        int keyIndex = logic.GetKeyIndex(keyCell);
        if (keyIndex < 0 || ((state.KeyPresentMask & (1UL << keyIndex)) == 0UL))
        {
            Debug.LogError("Key must remain until the player collects it.");
            failures++;
        }
        if (!LevelValidator.PlacementConflicts(LevelObjectType.Key, LevelObjectType.Wall))
        {
            failures++;
        }
        if (LevelValidator.PlacementConflicts(LevelObjectType.Key, LevelObjectType.Enemy))
        {
            Debug.LogError("Key must be allowed to coexist with Enemy.");
            failures++;
        }
        if (LevelValidator.PlacementConflicts(LevelObjectType.Rock, LevelObjectType.Spike))
        {
            Debug.LogError("Rock must be allowed to coexist with Spike.");
            failures++;
        }
        Object.DestroyImmediate(data);
        return failures;
    }

    private static bool PathsEqual(List<LevelDir> a, List<LevelDir> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    private static bool SimulateWinsWithinLimit(LevelData data, List<LevelDir> path, int moveLimit)
    {
        if (path == null || path.Count == 0)
        {
            return false;
        }
        LevelLogic logic = new LevelLogic(data);
        LevelSimState state = logic.CreateInitialState(data);
        for (int i = 0; i < path.Count; i++)
        {
            logic.TryMove(ref state, path[i], true);
        }
        return state.Won && state.MovesUsed <= moveLimit;
    }

    private static bool SimulateWinsInExactMoves(LevelData data, List<LevelDir> path, int expectedMoves)
    {
        if (path.Count == 0)
        {
            return false;
        }
        LevelLogic logic = new LevelLogic(data);
        LevelSimState state = logic.CreateInitialState(data);
        for (int i = 0; i < path.Count; i++)
        {
            logic.TryMove(ref state, path[i], false);
        }
        return state.Won && state.MovesUsed == expectedMoves;
    }
}
