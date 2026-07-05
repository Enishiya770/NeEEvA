using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// White-box reconstruction of NeEEvA's bedroom.
// Run via menu: NeEEvA > Build Room (White-box)
// Generates Assets/Scenes/NeEEvARoom.unity from primitives + Standard-shader materials.
public static class NeEEvARoomBuilder
{
    const string SCENE_PATH = "Assets/Scenes/NeEEvARoom.unity";
    const string MAT_FOLDER = "Assets/Materials/Room";

    // Room interior bounds. Origin at room center, floor at Y=0.
    const float RX = 4f;   // half-width  (X: -4 .. +4)
    const float RZ = 3f;   // half-depth  (Z: -3 .. +3)
    const float RY = 3f;   // height
    const float WALL_T = 0.1f;

    // Raised platform (right side, with the bay window).
    const float PLAT_X_MIN = 1.5f;
    const float PLAT_Y = 0.4f;

    // ---- Palette (sampled from screenshots) -----------------------------------
    static readonly Color C_WallPink       = new Color(0.97f, 0.84f, 0.86f);
    static readonly Color C_TrimWhite      = new Color(1.00f, 0.97f, 0.95f);
    static readonly Color C_Ceiling        = new Color(0.98f, 0.93f, 0.92f);
    static readonly Color C_FloorWood      = new Color(0.55f, 0.36f, 0.27f);
    static readonly Color C_Carpet         = new Color(0.92f, 0.80f, 0.80f);
    static readonly Color C_BedFrameDark   = new Color(0.30f, 0.22f, 0.22f);
    static readonly Color C_Headboard      = new Color(0.88f, 0.78f, 0.74f);
    static readonly Color C_Bedding        = new Color(0.95f, 0.92f, 0.92f);
    static readonly Color C_PillowPink     = new Color(0.90f, 0.65f, 0.65f);
    static readonly Color C_PillowDeco     = new Color(0.55f, 0.40f, 0.30f);
    static readonly Color C_BookshelfWhite = new Color(0.96f, 0.94f, 0.92f);
    static readonly Color C_BookA          = new Color(0.55f, 0.20f, 0.20f);
    static readonly Color C_BookB          = new Color(0.18f, 0.28f, 0.50f);
    static readonly Color C_BookC          = new Color(0.42f, 0.30f, 0.20f);
    static readonly Color C_BookD          = new Color(0.85f, 0.75f, 0.60f);
    static readonly Color C_AC             = new Color(0.97f, 0.97f, 0.97f);
    static readonly Color C_FrameDark      = new Color(0.20f, 0.15f, 0.12f);
    static readonly Color C_PaintingBg     = new Color(0.85f, 0.88f, 0.92f);
    static readonly Color C_PaintingBlue   = new Color(0.30f, 0.50f, 0.85f);
    static readonly Color C_PaintingGeoR   = new Color(0.85f, 0.30f, 0.20f);
    static readonly Color C_PaintingGeoY   = new Color(0.95f, 0.80f, 0.30f);
    static readonly Color C_CurtainPink    = new Color(0.85f, 0.65f, 0.78f);
    static readonly Color C_WindowSky      = new Color(1.00f, 0.78f, 0.55f); // emissive
    static readonly Color C_TableDark      = new Color(0.25f, 0.18f, 0.15f);
    static readonly Color C_TeaSet         = new Color(0.95f, 0.92f, 0.90f);
    static readonly Color C_PotTerra       = new Color(0.85f, 0.55f, 0.40f);
    static readonly Color C_PotWhite       = new Color(0.95f, 0.93f, 0.90f);
    static readonly Color C_Leaves         = new Color(0.30f, 0.55f, 0.30f);
    static readonly Color C_ChandelierMet  = new Color(0.30f, 0.25f, 0.20f);
    static readonly Color C_LampShade      = new Color(1.00f, 0.85f, 0.65f);
    static readonly Color C_CushionPink    = new Color(0.95f, 0.75f, 0.80f);

    static Dictionary<string, Material> _mats;

    [MenuItem("NeEEvA/Build Room (White-box)")]
    public static void BuildRoom()
    {
        EnsureFolder("Assets/Scenes");
        EnsureFolder("Assets/Materials");
        EnsureFolder(MAT_FOLDER);

        BuildMaterials();

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var root = new GameObject("NeEEvARoom").transform;

        BuildShell(root);
        BuildPlatform(root);
        BuildBed(root);
        BuildBench(root);
        BuildNightstand(root);
        BuildBookshelf(root);
        BuildCabinet(root);
        BuildAC(root);
        BuildPaintings(root);
        BuildWindow(root);
        BuildCurtains(root);
        BuildSteps(root);
        BuildCoffeeTable(root);
        BuildPlants(root);
        BuildCushion(root);
        BuildPlantStand(root);
        BuildChandelier(root);
        SetupLighting(root);
        SetupSpawnPoint(root);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, SCENE_PATH);
        AssetDatabase.SaveAssets();

        Debug.Log("[NeEEvARoom] Built and saved to " + SCENE_PATH);
        EditorUtility.DisplayDialog("NeEEvA Room",
            "白模房间已生成：\n" + SCENE_PATH +
            "\n\n下一步：双击场景文件打开，然后按 Play 在 Game 视图查看。", "OK");
    }

    // ---------------------------------------------------------------- Shell ---
    static void BuildShell(Transform root)
    {
        var shell = NewParent("Shell", root);

        // Floor (main wood area, X: -RX..PLAT_X_MIN)
        var floorMain = Box("Floor_Wood", shell,
            new Vector3((-RX + PLAT_X_MIN) * 0.5f, -0.05f, 0),
            new Vector3(PLAT_X_MIN + RX, 0.1f, RZ * 2),
            Mat("FloorWood"));
        AddCollider(floorMain);

        // Ceiling
        Box("Ceiling", shell,
            new Vector3(0, RY + 0.05f, 0),
            new Vector3(RX * 2, 0.1f, RZ * 2),
            Mat("Ceiling"));

        // Walls (pink interior). North/South/East/West.
        // West wall (behind bed)
        Box("Wall_West", shell,
            new Vector3(-RX - WALL_T * 0.5f, RY * 0.5f, 0),
            new Vector3(WALL_T, RY, RZ * 2),
            Mat("WallPink"));
        // North wall (behind bookshelf)
        Box("Wall_North", shell,
            new Vector3(0, RY * 0.5f, RZ + WALL_T * 0.5f),
            new Vector3(RX * 2, RY, WALL_T),
            Mat("WallPink"));
        // South wall (entrance side)
        Box("Wall_South", shell,
            new Vector3(0, RY * 0.5f, -RZ - WALL_T * 0.5f),
            new Vector3(RX * 2, RY, WALL_T),
            Mat("WallPink"));

        // East wall is split into segments to leave a window opening.
        // Window opening: Y 0.6..2.4 (relative to floor of platform, i.e. 1.0..2.8 absolute), Z -1..+2
        // We'll cut a window 1.0m..2.8m Y, Z -1..+2.
        const float winYMin = 1.0f;
        const float winYMax = 2.8f;
        const float winZMin = -1.0f;
        const float winZMax = 2.0f;

        // East wall: bottom strip
        Box("Wall_East_Bottom", shell,
            new Vector3(RX + WALL_T * 0.5f, winYMin * 0.5f, 0),
            new Vector3(WALL_T, winYMin, RZ * 2),
            Mat("WallPink"));
        // East wall: top strip
        Box("Wall_East_Top", shell,
            new Vector3(RX + WALL_T * 0.5f, (winYMax + RY) * 0.5f, 0),
            new Vector3(WALL_T, RY - winYMax, RZ * 2),
            Mat("WallPink"));
        // East wall: south of window
        Box("Wall_East_S", shell,
            new Vector3(RX + WALL_T * 0.5f, (winYMin + winYMax) * 0.5f, (-RZ + winZMin) * 0.5f),
            new Vector3(WALL_T, winYMax - winYMin, RZ + winZMin),
            Mat("WallPink"));
        // East wall: north of window
        Box("Wall_East_N", shell,
            new Vector3(RX + WALL_T * 0.5f, (winYMin + winYMax) * 0.5f, (RZ + winZMax) * 0.5f),
            new Vector3(WALL_T, winYMax - winYMin, RZ - winZMax),
            Mat("WallPink"));

        // White crown molding (top of walls)
        Box("Trim_W", shell, new Vector3(-RX + 0.05f, RY - 0.1f, 0), new Vector3(0.1f, 0.15f, RZ * 2 + 0.2f), Mat("TrimWhite"));
        Box("Trim_E", shell, new Vector3(RX - 0.05f, RY - 0.1f, 0), new Vector3(0.1f, 0.15f, RZ * 2 + 0.2f), Mat("TrimWhite"));
        Box("Trim_N", shell, new Vector3(0, RY - 0.1f, RZ - 0.05f), new Vector3(RX * 2 + 0.2f, 0.15f, 0.1f), Mat("TrimWhite"));
        Box("Trim_S", shell, new Vector3(0, RY - 0.1f, -RZ + 0.05f), new Vector3(RX * 2 + 0.2f, 0.15f, 0.1f), Mat("TrimWhite"));

        // Baseboards
        Box("Base_W", shell, new Vector3(-RX + 0.04f, 0.06f, 0), new Vector3(0.08f, 0.12f, RZ * 2), Mat("TrimWhite"));
        Box("Base_E", shell, new Vector3(RX - 0.04f, 0.06f, 0), new Vector3(0.08f, 0.12f, RZ * 2), Mat("TrimWhite"));
        Box("Base_N", shell, new Vector3(0, 0.06f, RZ - 0.04f), new Vector3(RX * 2, 0.12f, 0.08f), Mat("TrimWhite"));
        Box("Base_S", shell, new Vector3(0, 0.06f, -RZ + 0.04f), new Vector3(RX * 2, 0.12f, 0.08f), Mat("TrimWhite"));
    }

    // ---------------------------------------------------------- Raised Platform
    static void BuildPlatform(Transform root)
    {
        var plat = NewParent("Platform", root);

        // Platform top (carpet)
        var top = Box("Platform_Top", plat,
            new Vector3((PLAT_X_MIN + RX) * 0.5f, PLAT_Y - 0.05f, 0),
            new Vector3(RX - PLAT_X_MIN, 0.1f, RZ * 2),
            Mat("Carpet"));
        AddCollider(top);

        // Platform front face (visible riser, white trim)
        Box("Platform_Riser", plat,
            new Vector3(PLAT_X_MIN, PLAT_Y * 0.5f, 0),
            new Vector3(0.05f, PLAT_Y, RZ * 2),
            Mat("TrimWhite"));
    }

    // ---------------------------------------------------------------- Bed -----
    // Headboard against west wall. Long axis along X (foot points east).
    // Bed occupies: X in [-3.85, -1.85], Z in [-0.9, +0.9].
    static void BuildBed(Transform root)
    {
        var bed = NewParent("Bed", root);
        const float bedXMin = -3.85f, bedXMax = -1.85f;
        const float bedZHalf = 0.9f;

        // Box-spring / frame
        Box("Frame", bed,
            new Vector3((bedXMin + bedXMax) * 0.5f, 0.20f, 0),
            new Vector3(bedXMax - bedXMin, 0.40f, bedZHalf * 2),
            Mat("BedFrameDark"));

        // Mattress
        Box("Mattress", bed,
            new Vector3((bedXMin + bedXMax) * 0.5f, 0.55f, 0),
            new Vector3(bedXMax - bedXMin - 0.1f, 0.30f, bedZHalf * 2 - 0.1f),
            Mat("Bedding"));

        // Comforter (a slightly raised top blanket)
        Box("Comforter", bed,
            new Vector3((bedXMin + bedXMax) * 0.5f + 0.2f, 0.72f, 0),
            new Vector3(bedXMax - bedXMin - 0.5f, 0.05f, bedZHalf * 2 - 0.05f),
            Mat("Bedding"));

        // Headboard (tall, against west wall)
        Box("Headboard", bed,
            new Vector3(bedXMin - 0.05f, 1.1f, 0),
            new Vector3(0.1f, 1.6f, bedZHalf * 2 + 0.2f),
            Mat("Headboard"));
        // Headboard frame (darker outer)
        Box("Headboard_Frame_Top", bed,
            new Vector3(bedXMin - 0.07f, 1.95f, 0),
            new Vector3(0.14f, 0.1f, bedZHalf * 2 + 0.4f),
            Mat("BedFrameDark"));
        Box("Headboard_Frame_L", bed,
            new Vector3(bedXMin - 0.07f, 1.1f, -bedZHalf - 0.1f),
            new Vector3(0.14f, 1.7f, 0.1f),
            Mat("BedFrameDark"));
        Box("Headboard_Frame_R", bed,
            new Vector3(bedXMin - 0.07f, 1.1f, bedZHalf + 0.1f),
            new Vector3(0.14f, 1.7f, 0.1f),
            Mat("BedFrameDark"));

        // Pillows (two stacked horizontally near headboard)
        Box("Pillow_L", bed,
            new Vector3(bedXMin + 0.45f, 0.78f, -0.4f),
            new Vector3(0.7f, 0.18f, 0.55f),
            Mat("Bedding"));
        Box("Pillow_R", bed,
            new Vector3(bedXMin + 0.45f, 0.78f, 0.4f),
            new Vector3(0.7f, 0.18f, 0.55f),
            Mat("Bedding"));
        // Decorative pillow with bow
        Box("Pillow_Deco", bed,
            new Vector3(bedXMin + 0.95f, 0.80f, 0),
            new Vector3(0.45f, 0.22f, 0.45f),
            Mat("PillowDeco"));
    }

    // -------------------------------------------------------- Bench at foot ---
    static void BuildBench(Transform root)
    {
        var bench = NewParent("Bench", root);
        // Bench in front of bed (east of bed foot), same orientation as bed (long axis Z)
        Box("Bench_Top", bench,
            new Vector3(-1.4f, 0.42f, 0),
            new Vector3(0.45f, 0.08f, 1.6f),
            Mat("Bedding"));
        // Legs (4)
        for (int i = 0; i < 4; i++)
        {
            float xs = (i % 2 == 0) ? -0.18f : 0.18f;
            float zs = (i / 2 == 0) ? -0.7f : 0.7f;
            Box("Leg_" + i, bench,
                new Vector3(-1.4f + xs, 0.18f, zs),
                new Vector3(0.06f, 0.36f, 0.06f),
                Mat("BedFrameDark"));
        }
    }

    // ----------------------------------------------------- Nightstand + Lamp --
    static void BuildNightstand(Transform root)
    {
        var ns = NewParent("Nightstand", root);
        // South side of bed (foot side wall area), small table with lamp
        Box("Cabinet", ns,
            new Vector3(-3.5f, 0.30f, -1.4f),
            new Vector3(0.5f, 0.6f, 0.5f),
            Mat("BookshelfWhite"));
        // Lamp base
        var baseG = Cyl("Lamp_Base", ns,
            new Vector3(-3.5f, 0.65f, -1.4f),
            new Vector3(0.12f, 0.05f, 0.12f),
            Mat("BedFrameDark"));
        // Lamp post
        Cyl("Lamp_Post", ns,
            new Vector3(-3.5f, 0.85f, -1.4f),
            new Vector3(0.04f, 0.20f, 0.04f),
            Mat("BedFrameDark"));
        // Lamp shade
        Cyl("Lamp_Shade", ns,
            new Vector3(-3.5f, 1.05f, -1.4f),
            new Vector3(0.25f, 0.10f, 0.25f),
            Mat("LampShade_E"));
        // Light source inside
        var lampLight = new GameObject("Lamp_Light");
        lampLight.transform.SetParent(ns);
        lampLight.transform.localPosition = new Vector3(-3.5f, 1.05f, -1.4f);
        var l = lampLight.AddComponent<Light>();
        l.type = LightType.Point;
        l.range = 3f;
        l.intensity = 0.8f;
        l.color = new Color(1f, 0.85f, 0.7f);
    }

    // ----------------------------------------------------------- Bookshelf ----
    // White bookshelf against north wall, central. Multi-cell with books.
    static void BuildBookshelf(Transform root)
    {
        var bs = NewParent("Bookshelf", root);
        const float bsXc = -0.3f;     // center X
        const float bsW  = 2.4f;      // width
        const float bsD  = 0.4f;      // depth
        const float bsH  = 1.95f;     // height
        const float bsZc = RZ - bsD * 0.5f - 0.02f;

        // Outer carcass (back panel + top + sides + bottom). We'll build as a hollow shell.
        // For simplicity, a solid white box with an inset darker "interior" + book rows.
        Box("Carcass", bs,
            new Vector3(bsXc, bsH * 0.5f, bsZc),
            new Vector3(bsW, bsH, bsD),
            Mat("BookshelfWhite"));
        // Inset back (darker) — pushes interior look without true cavities
        Box("InteriorBack", bs,
            new Vector3(bsXc, bsH * 0.5f, bsZc + bsD * 0.4f),
            new Vector3(bsW - 0.1f, bsH - 0.1f, 0.02f),
            Mat("BedFrameDark"));

        // Internal vertical dividers (3 of them = 4 columns)
        for (int i = 1; i <= 3; i++)
        {
            float fx = bsXc - bsW * 0.5f + (bsW / 4f) * i;
            Box("Divider_V" + i, bs,
                new Vector3(fx, bsH * 0.5f, bsZc + 0.05f),
                new Vector3(0.04f, bsH - 0.1f, bsD - 0.05f),
                Mat("BookshelfWhite"));
        }
        // Internal horizontal shelves (3 of them = 4 rows)
        for (int j = 1; j <= 3; j++)
        {
            float fy = (bsH / 4f) * j;
            Box("Shelf_H" + j, bs,
                new Vector3(bsXc, fy, bsZc + 0.05f),
                new Vector3(bsW - 0.05f, 0.04f, bsD - 0.05f),
                Mat("BookshelfWhite"));
        }

        // Books — fill some cells with grouped book rows.
        var bookMats = new[] { Mat("BookA"), Mat("BookB"), Mat("BookC"), Mat("BookD") };
        int seed = 0;
        for (int col = 0; col < 4; col++)
        {
            for (int row = 0; row < 4; row++)
            {
                // Skip a few cells (decorative items instead)
                if ((col == 1 && row == 3) || (col == 3 && row == 0)) continue;

                float cellX0 = bsXc - bsW * 0.5f + (bsW / 4f) * col;
                float cellX1 = bsXc - bsW * 0.5f + (bsW / 4f) * (col + 1);
                float cellY0 = (bsH / 4f) * row;
                float cellCx = (cellX0 + cellX1) * 0.5f;
                float cellW = cellX1 - cellX0 - 0.06f;

                // 4-6 books per cell
                int books = 4 + (seed % 3);
                float bx0 = cellX0 + 0.04f;
                float gap = (cellW - 0.02f) / books;
                for (int b = 0; b < books; b++)
                {
                    float h = 0.30f + (((seed + b) * 13 % 7) / 70f);
                    float w = gap * 0.85f;
                    Box($"Book_{col}_{row}_{b}", bs,
                        new Vector3(bx0 + b * gap + gap * 0.5f, cellY0 + h * 0.5f + 0.04f, bsZc + 0.05f),
                        new Vector3(w, h, bsD - 0.12f),
                        bookMats[(seed + b) % bookMats.Length]);
                    seed++;
                }
            }
        }

        // Decorative items in skipped cells: a small "vase" and a "pyramid"
        Cyl("Deco_Vase", bs,
            new Vector3(bsXc - bsW * 0.5f + bsW / 8f, (bsH / 4f) * 3 + 0.15f, bsZc + 0.05f),
            new Vector3(0.10f, 0.15f, 0.10f),
            Mat("TrimWhite"));
        Box("Deco_Pyramid", bs,
            new Vector3(bsXc + bsW * 0.5f - bsW / 8f, (bsH / 4f) * 0 + 0.12f, bsZc + 0.05f),
            new Vector3(0.18f, 0.24f, 0.18f),
            Mat("TrimWhite"));

        // Lamp on top of bookshelf (small)
        Cyl("BS_LampBase", bs,
            new Vector3(bsXc + 0.7f, bsH + 0.04f, bsZc),
            new Vector3(0.10f, 0.04f, 0.10f),
            Mat("BedFrameDark"));
        Cyl("BS_LampShade", bs,
            new Vector3(bsXc + 0.7f, bsH + 0.20f, bsZc),
            new Vector3(0.20f, 0.10f, 0.20f),
            Mat("LampShade_E"));
    }

    // -------------------------------------------------- White cabinet (right) -
    static void BuildCabinet(Transform root)
    {
        var cab = NewParent("Cabinet_RightOfBookshelf", root);
        // East of bookshelf, smaller white cabinet/storage
        Box("Cabinet", cab,
            new Vector3(1.3f, 0.40f, RZ - 0.3f),
            new Vector3(1.4f, 0.8f, 0.5f),
            Mat("BookshelfWhite"));
        // Two drawer lines
        Box("Drawer1", cab,
            new Vector3(1.3f, 0.55f, RZ - 0.05f),
            new Vector3(1.36f, 0.02f, 0.02f),
            Mat("BedFrameDark"));
        Box("Drawer2", cab,
            new Vector3(1.3f, 0.25f, RZ - 0.05f),
            new Vector3(1.36f, 0.02f, 0.02f),
            Mat("BedFrameDark"));
    }

    // ------------------------------------------------------ Air conditioner ---
    static void BuildAC(Transform root)
    {
        var ac = NewParent("AirConditioner", root);
        Box("AC_Body", ac,
            new Vector3(1.3f, 2.55f, RZ - 0.12f),
            new Vector3(0.95f, 0.30f, 0.22f),
            Mat("AC"));
        Box("AC_Vent", ac,
            new Vector3(1.3f, 2.42f, RZ - 0.05f),
            new Vector3(0.85f, 0.04f, 0.05f),
            Mat("BedFrameDark"));
    }

    // ------------------------------------------------------------ Paintings --
    // Two on the west wall above the bed.
    static void BuildPaintings(Transform root)
    {
        var p = NewParent("Paintings", root);

        // Painting 1: blue flower vase (larger, square)
        const float p1Z = 0.5f;
        Box("P1_Frame", p, new Vector3(-RX + 0.05f, 1.85f, p1Z), new Vector3(0.06f, 0.7f, 0.55f), Mat("FrameDark"));
        Box("P1_Canvas", p, new Vector3(-RX + 0.08f, 1.85f, p1Z), new Vector3(0.02f, 0.6f, 0.45f), Mat("PaintingBg"));
        Box("P1_Vase", p, new Vector3(-RX + 0.10f, 1.7f, p1Z), new Vector3(0.01f, 0.18f, 0.10f), Mat("TrimWhite"));
        Box("P1_FlowerStem", p, new Vector3(-RX + 0.10f, 1.95f, p1Z), new Vector3(0.01f, 0.18f, 0.02f), Mat("Leaves"));
        Box("P1_FlowerHead", p, new Vector3(-RX + 0.10f, 2.08f, p1Z), new Vector3(0.01f, 0.10f, 0.18f), Mat("PaintingBlue"));

        // Painting 2: smaller geometric (right of P1)
        const float p2Z = -0.7f;
        Box("P2_Frame", p, new Vector3(-RX + 0.05f, 1.75f, p2Z), new Vector3(0.06f, 0.4f, 0.4f), Mat("FrameDark"));
        Box("P2_Canvas", p, new Vector3(-RX + 0.08f, 1.75f, p2Z), new Vector3(0.02f, 0.32f, 0.32f), Mat("Bedding"));
        Box("P2_BlockA", p, new Vector3(-RX + 0.10f, 1.85f, p2Z - 0.07f), new Vector3(0.01f, 0.10f, 0.10f), Mat("PaintingGeoR"));
        Box("P2_BlockB", p, new Vector3(-RX + 0.10f, 1.65f, p2Z + 0.07f), new Vector3(0.01f, 0.12f, 0.12f), Mat("PaintingGeoY"));
        Box("P2_BlockC", p, new Vector3(-RX + 0.10f, 1.85f, p2Z + 0.05f), new Vector3(0.01f, 0.06f, 0.06f), Mat("PaintingBlue"));
    }

    // ---------------------------------------------------------------- Window --
    // Sunset glow visible through east-wall opening.
    static void BuildWindow(Transform root)
    {
        var win = NewParent("Window", root);

        const float winYMin = 1.0f, winYMax = 2.8f;
        const float winZMin = -1.0f, winZMax = 2.0f;

        // Window glass (slight bluish tint)
        Box("Glass", win,
            new Vector3(RX, (winYMin + winYMax) * 0.5f, (winZMin + winZMax) * 0.5f),
            new Vector3(0.04f, winYMax - winYMin, winZMax - winZMin),
            Mat("Bedding"));

        // Vertical mullions (split window into 3 panels)
        for (int i = 1; i <= 2; i++)
        {
            float fz = winZMin + (winZMax - winZMin) * i / 3f;
            Box("Mullion_" + i, win,
                new Vector3(RX, (winYMin + winYMax) * 0.5f, fz),
                new Vector3(0.06f, winYMax - winYMin, 0.04f),
                Mat("TrimWhite"));
        }

        // Outside backdrop: large emissive plane east of the wall, radiating sunset light
        Box("SkyBackdrop", win,
            new Vector3(RX + 0.5f, 2.0f, 0.5f),
            new Vector3(0.05f, 4.0f, 6.0f),
            Mat("WindowSky"));

        // Window sill
        Box("Sill", win,
            new Vector3(RX - 0.05f, winYMin - 0.05f, (winZMin + winZMax) * 0.5f),
            new Vector3(0.30f, 0.10f, winZMax - winZMin + 0.4f),
            Mat("TrimWhite"));
    }

    // -------------------------------------------------------------- Curtains --
    static void BuildCurtains(Transform root)
    {
        var c = NewParent("Curtains", root);
        const float curtY = 1.4f;
        const float curtH = 2.4f;
        // Left (south) curtain
        Box("Curtain_S", c,
            new Vector3(RX - 0.15f, curtY, -1.4f),
            new Vector3(0.12f, curtH, 0.7f),
            Mat("CurtainPink"));
        // Right (north) curtain
        Box("Curtain_N", c,
            new Vector3(RX - 0.15f, curtY, 2.4f),
            new Vector3(0.12f, curtH, 0.7f),
            Mat("CurtainPink"));
        // Curtain rod
        Cyl("CurtainRod", c,
            new Vector3(RX - 0.15f, 2.65f, 0.5f),
            new Vector3(0.04f, 1.85f, 0.04f),
            Mat("FrameDark"));
        var rod = c.Find("CurtainRod");
        if (rod != null) rod.localRotation = Quaternion.Euler(90, 0, 0);
    }

    // ----------------------------------------------- Steps up to platform -----
    static void BuildSteps(Transform root)
    {
        var s = NewParent("Steps", root);
        // Two steps centered around X = PLAT_X_MIN, going down toward south
        Box("Step_1", s,
            new Vector3(PLAT_X_MIN - 0.15f, 0.10f, -1.0f),
            new Vector3(0.30f, 0.20f, 0.5f),
            Mat("BookshelfWhite"));
        Box("Step_Rail_L", s,
            new Vector3(PLAT_X_MIN - 0.30f, 0.30f, -1.25f),
            new Vector3(0.04f, 0.4f, 0.04f),
            Mat("BedFrameDark"));
        Box("Step_Rail_R", s,
            new Vector3(PLAT_X_MIN - 0.30f, 0.30f, -0.75f),
            new Vector3(0.04f, 0.4f, 0.04f),
            Mat("BedFrameDark"));
    }

    // ------------------------------------------------------- Coffee Table -----
    static void BuildCoffeeTable(Transform root)
    {
        var t = NewParent("CoffeeTable", root);
        const float tx = 2.7f, tz = 0.5f, ty = PLAT_Y;
        Box("Top", t,
            new Vector3(tx, ty + 0.35f, tz),
            new Vector3(0.8f, 0.05f, 0.5f),
            Mat("TableDark"));
        // Legs
        for (int i = 0; i < 4; i++)
        {
            float xs = (i % 2 == 0) ? -0.35f : 0.35f;
            float zs = (i / 2 == 0) ? -0.20f : 0.20f;
            Box("Leg_" + i, t,
                new Vector3(tx + xs, ty + 0.18f, tz + zs),
                new Vector3(0.05f, 0.32f, 0.05f),
                Mat("TableDark"));
        }
        // Tea pot (cylinder + small lid)
        Cyl("TeaPot", t, new Vector3(tx, ty + 0.43f, tz - 0.1f), new Vector3(0.10f, 0.06f, 0.10f), Mat("TeaSet"));
        Cyl("TeaPot_Lid", t, new Vector3(tx, ty + 0.50f, tz - 0.1f), new Vector3(0.04f, 0.02f, 0.04f), Mat("TeaSet"));
        // Two cups
        Cyl("Cup_1", t, new Vector3(tx + 0.20f, ty + 0.40f, tz + 0.10f), new Vector3(0.05f, 0.04f, 0.05f), Mat("TeaSet"));
        Cyl("Cup_2", t, new Vector3(tx - 0.20f, ty + 0.40f, tz + 0.15f), new Vector3(0.05f, 0.04f, 0.05f), Mat("TeaSet"));
    }

    // ------------------------------------------------------------ Plants ------
    static void BuildPlants(Transform root)
    {
        var pl = NewParent("Plants", root);
        // Plant on platform near window
        MakePlant(pl, "Plant_Window", new Vector3(3.6f, PLAT_Y, 1.5f), Mat("PotTerra"));
        // Plant at floor in front of platform
        MakePlant(pl, "Plant_Floor", new Vector3(1.0f, 0f, -1.8f), Mat("PotWhite"));
        // Plant on cabinet
        MakePlant(pl, "Plant_Cabinet", new Vector3(1.9f, 0.80f, RZ - 0.30f), Mat("PotWhite"), 0.6f);
    }

    static void MakePlant(Transform parent, string name, Vector3 pos, Material potMat, float scale = 1f)
    {
        var p = NewParent(name, parent);
        Cyl("Pot", p,
            pos + new Vector3(0, 0.12f * scale, 0),
            new Vector3(0.18f * scale, 0.12f * scale, 0.18f * scale),
            potMat);
        // Foliage = a few overlapping spheres
        Sph("Leaf_A", p, pos + new Vector3(0, 0.40f * scale, 0), Vector3.one * 0.30f * scale, Mat("Leaves"));
        Sph("Leaf_B", p, pos + new Vector3(0.10f * scale, 0.55f * scale, 0.05f * scale), Vector3.one * 0.22f * scale, Mat("Leaves"));
        Sph("Leaf_C", p, pos + new Vector3(-0.08f * scale, 0.50f * scale, -0.06f * scale), Vector3.one * 0.20f * scale, Mat("Leaves"));
    }

    // ------------------------------------------------------------- Cushion ----
    static void BuildCushion(Transform root)
    {
        var c = NewParent("Cushion", root);
        Cyl("Cushion", c,
            new Vector3(2.2f, PLAT_Y + 0.10f, -0.6f),
            new Vector3(0.45f, 0.10f, 0.45f),
            Mat("CushionPink"));
    }

    // -------------------------------------------------------- Plant stand -----
    static void BuildPlantStand(Transform root)
    {
        var s = NewParent("PlantStand", root);
        // Tall white stand near east wall, south
        Box("Stand", s, new Vector3(3.6f, 0.50f, -2.6f), new Vector3(0.4f, 1.0f, 0.4f), Mat("BookshelfWhite"));
        Sph("Top_Plant", s, new Vector3(3.6f, 1.20f, -2.6f), Vector3.one * 0.45f, Mat("Leaves"));
    }

    // ------------------------------------------------------- Chandelier -------
    static void BuildChandelier(Transform root)
    {
        var ch = NewParent("Chandelier", root);
        // Cord
        Cyl("Cord", ch, new Vector3(0, RY - 0.25f, 0), new Vector3(0.02f, 0.25f, 0.02f), Mat("ChandelierMet"));
        // Central column
        Cyl("Column", ch, new Vector3(0, RY - 0.55f, 0), new Vector3(0.06f, 0.20f, 0.06f), Mat("ChandelierMet"));
        // 5 arms with shades
        for (int i = 0; i < 5; i++)
        {
            float ang = i * Mathf.PI * 2f / 5f;
            float rArm = 0.35f;
            Vector3 armEnd = new Vector3(Mathf.Cos(ang) * rArm, RY - 0.55f, Mathf.Sin(ang) * rArm);
            // Arm
            Box("Arm_" + i, ch, armEnd * 0.5f + new Vector3(0, RY - 0.55f, 0) - new Vector3(0, (RY - 0.55f) * 0.5f, 0),
                new Vector3(rArm, 0.03f, 0.03f),
                Mat("ChandelierMet"));
            // Just place a cylinder shade pointing down
            Cyl("Shade_" + i, ch,
                armEnd + new Vector3(0, -0.10f, 0),
                new Vector3(0.10f, 0.10f, 0.10f),
                Mat("LampShade_E"));
        }
        // Main chandelier light
        var light = new GameObject("Chandelier_Light");
        light.transform.SetParent(ch);
        light.transform.localPosition = new Vector3(0, RY - 0.6f, 0);
        var lc = light.AddComponent<Light>();
        lc.type = LightType.Point;
        lc.range = 8f;
        lc.intensity = 1.4f;
        lc.color = new Color(1f, 0.88f, 0.75f);
        lc.shadows = LightShadows.Soft;
    }

    // ------------------------------------------------------------- Lighting --
    static void SetupLighting(Transform root)
    {
        // Directional light = sunset coming through window (from east, slightly south)
        var sun = new GameObject("SunsetLight");
        sun.transform.SetParent(root);
        sun.transform.position = new Vector3(RX + 1f, 2.5f, 0.5f);
        sun.transform.rotation = Quaternion.Euler(15f, -110f, 0f);
        var sl = sun.AddComponent<Light>();
        sl.type = LightType.Directional;
        sl.color = new Color(1f, 0.78f, 0.55f);
        sl.intensity = 1.3f;
        sl.shadows = LightShadows.Soft;

        // Ambient (warm pink tint)
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.85f, 0.78f, 0.85f);
        RenderSettings.ambientEquatorColor = new Color(0.95f, 0.80f, 0.78f);
        RenderSettings.ambientGroundColor = new Color(0.55f, 0.40f, 0.40f);
        RenderSettings.fog = false;
    }

    // ----------------------------------------------------------- Spawn --------
    static void SetupSpawnPoint(Transform root)
    {
        // A marker GameObject where the VR rig / NeEEvA character should be placed.
        var spawn = new GameObject("PlayerSpawn (place VR rig here)");
        spawn.transform.SetParent(root);
        // Standing in the middle of the room, facing the bed/character
        spawn.transform.position = new Vector3(0.5f, 0f, -1.5f);
        spawn.transform.rotation = Quaternion.Euler(0, -135f, 0);

        var nps = new GameObject("NeEEvASpawn (drop NeEEvA.vrm here)");
        nps.transform.SetParent(root);
        // Sitting on bed edge
        nps.transform.position = new Vector3(-1.6f, 0.6f, 0.0f);
        nps.transform.rotation = Quaternion.Euler(0, 135f, 0);

        // Add a Camera for non-VR preview. VR rig will replace this later.
        var camGO = new GameObject("Main Camera");
        camGO.tag = "MainCamera";
        camGO.transform.position = new Vector3(0.5f, 1.65f, -1.5f);
        camGO.transform.rotation = Quaternion.Euler(0, -135f, 0);
        var cam = camGO.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.9f, 0.7f, 0.65f);
        camGO.AddComponent<AudioListener>();
    }

    // ============================================================ Helpers ====
    static GameObject Box(string name, Transform parent, Vector3 center, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = center;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }

    static GameObject Cyl(string name, Transform parent, Vector3 center, Vector3 size, Material mat)
    {
        // Unity cylinder is 2 units tall, 1 unit wide. Scale appropriately:
        // size.x = diameter scale on X, size.y = height scale (Unity height=2 -> use size.y/1 for unit height already? actually scale.y of 1 gives 2m tall)
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = center;
        // Cylinder height in unity primitive = 2 * scale.y. We want full height = size.y.
        go.transform.localScale = new Vector3(size.x, size.y * 0.5f, size.z);
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }

    static GameObject Sph(string name, Transform parent, Vector3 center, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = center;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }

    static Transform NewParent(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    static void AddCollider(GameObject go)
    {
        if (go.GetComponent<Collider>() == null)
            go.AddComponent<BoxCollider>();
    }

    // ----------------------------------------------------------- Materials ---
    static Material Mat(string key) => _mats[key];

    static void BuildMaterials()
    {
        _mats = new Dictionary<string, Material>();
        AddMat("WallPink",       C_WallPink);
        AddMat("TrimWhite",      C_TrimWhite);
        AddMat("Ceiling",        C_Ceiling);
        AddMat("FloorWood",      C_FloorWood);
        AddMat("Carpet",         C_Carpet);
        AddMat("BedFrameDark",   C_BedFrameDark);
        AddMat("Headboard",      C_Headboard);
        AddMat("Bedding",        C_Bedding);
        AddMat("PillowPink",     C_PillowPink);
        AddMat("PillowDeco",     C_PillowDeco);
        AddMat("BookshelfWhite", C_BookshelfWhite);
        AddMat("BookA",          C_BookA);
        AddMat("BookB",          C_BookB);
        AddMat("BookC",          C_BookC);
        AddMat("BookD",          C_BookD);
        AddMat("AC",             C_AC);
        AddMat("FrameDark",      C_FrameDark);
        AddMat("PaintingBg",     C_PaintingBg);
        AddMat("PaintingBlue",   C_PaintingBlue);
        AddMat("PaintingGeoR",   C_PaintingGeoR);
        AddMat("PaintingGeoY",   C_PaintingGeoY);
        AddMat("CurtainPink",    C_CurtainPink);
        AddMat("WindowSky",      C_WindowSky, emission: C_WindowSky * 1.5f);
        AddMat("TableDark",      C_TableDark);
        AddMat("TeaSet",         C_TeaSet);
        AddMat("PotTerra",       C_PotTerra);
        AddMat("PotWhite",       C_PotWhite);
        AddMat("Leaves",         C_Leaves);
        AddMat("ChandelierMet",  C_ChandelierMet);
        AddMat("LampShade_E",    C_LampShade, emission: C_LampShade * 0.8f);
        AddMat("CushionPink",    C_CushionPink);
    }

    static void AddMat(string key, Color c, Color? emission = null)
    {
        string path = MAT_FOLDER + "/" + key + ".mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        Material m;
        if (existing != null)
        {
            m = existing;
        }
        else
        {
            m = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.color = c;
        if (emission.HasValue)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emission.Value);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        else
        {
            m.DisableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", Color.black);
        }
        _mats[key] = m;
    }

    static void EnsureFolder(string path)
    {
        if (!AssetDatabase.IsValidFolder(path))
        {
            string parent = Path.GetDirectoryName(path).Replace("\\", "/");
            string leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
