using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIChat.Memory;
using UnityEditor;
using UnityEngine;

// NeEEvA 记忆网络的编辑器可视化 / 编辑窗口。
// 菜单: NeEEvA > Memory Graph
//
// 三种数据源:
//   1. 运行时文件  Application.persistentDataPath/memory.json (她真实的长期记忆)
//   2. 种子文件    Assets/AIChatTookit/MemoryData/seed_memory.json (设计期模板)
//   3. Play 模式下直连场景里的 MemoryHub——实时看到 LLM 写入的节点
//
// 文件模式支持全量编辑(增删改节点/边/重命名); 直连模式为保护 MemoryStore 的
// 内部名字索引,禁用重命名与删除节点,其余(描述/权重/激活/边)可改。
// 无 Undo——用"保存"显式落盘,标题栏 * 表示有未保存改动。
public class MemoryGraphWindow : EditorWindow
{
    // ---------- 数据 ----------
    [Serializable]
    private class GraphFile
    {
        public int version = 1;
        public string last_decayed;
        public List<MemoryNode> nodes = new List<MemoryNode>();
        public List<MemoryEdge> edges = new List<MemoryEdge>();
    }

    private const string SeedPath = "Assets/AIChatTookit/MemoryData/seed_memory.json";
    private static string RuntimePath => Path.Combine(Application.persistentDataPath, "memory.json");

    private GraphFile m_File = new GraphFile();
    private MemoryHub m_LiveHub;                 // 非空 = 直连运行中实例
    private string m_LoadedPath = "";
    private bool m_Dirty;

    private List<MemoryNode> Nodes => m_LiveHub != null ? m_LiveHub.Store.Nodes : m_File.nodes;
    private List<MemoryEdge> Edges => m_LiveHub != null ? m_LiveHub.Store.Edges : m_File.edges;
    private bool IsLive => m_LiveHub != null;

    // ---------- 布局模拟 ----------
    private class Body { public float x, y, vx, vy; public bool pinned; }
    private readonly Dictionary<MemoryNode, Body> m_Bodies = new Dictionary<MemoryNode, Body>();
    private float m_Alpha = 1f;

    // ---------- 视图 / 交互 ----------
    private Vector2 m_Pan = Vector2.zero;
    private float m_Zoom = 1f;
    private MemoryNode m_Selected, m_DragNode;
    private bool m_Panning;
    private Vector2 m_MouseDownPos;
    private string m_Search = "";
    private Vector2 m_PanelScroll;
    private int m_AddEdgeTarget;
    private string m_PendingRename;
    private MemoryNode m_RenameNode;

    private const float PanelW = 320f;
    private static readonly Color ColBg = new Color(0.090f, 0.082f, 0.149f);
    private static readonly Color ColNode = new Color(0.789f, 0.498f, 0.553f);
    private static readonly Color ColNodeHi = new Color(0.910f, 0.627f, 0.675f);
    private static readonly Color ColCore = new Color(0.788f, 0.663f, 0.431f);
    private static readonly Color ColEdge = new Color(0.365f, 0.329f, 0.502f);
    private static readonly Color ColInk = new Color(0.937f, 0.918f, 0.949f);

    private GUIStyle m_LabelStyle;

    [MenuItem("NeEEvA/Memory Graph")]
    public static void Open()
    {
        var w = GetWindow<MemoryGraphWindow>("Memory Graph");
        w.minSize = new Vector2(760, 480);
    }

    private void OnEnable()
    {
        EditorApplication.update += OnEditorTick;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        if (Nodes.Count == 0) LoadRuntimeFile(false);
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorTick;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
    }

    private void OnPlayModeChanged(PlayModeStateChange s)
    {
        //退出 Play 后直连对象已销毁,且游戏在退出时刚保存过运行时文件——自动切回读文件
        if (s == PlayModeStateChange.EnteredEditMode && m_LoadedPath == "运行中的 MemoryHub 实例")
        {
            m_LiveHub = null;
            m_Dirty = false;
            LoadRuntimeFile(false);
        }
    }

    private void OnEditorTick()
    {
        if (m_Alpha > 0.021f || m_DragNode != null) { Tick(); Repaint(); }
    }

    // ==================== 数据源 ====================

    private void LoadRuntimeFile(bool interactive = true)
    {
        if (!ConfirmDiscard()) return;
        m_LiveHub = null;
        if (!File.Exists(RuntimePath))
        {
            if (interactive)
                EditorUtility.DisplayDialog("Memory Graph",
                    "运行时记忆文件不存在:\n" + RuntimePath + "\n\n先运行一次游戏,或改为载入种子文件。", "好");
            return;
        }
        LoadJson(File.ReadAllText(RuntimePath), RuntimePath);
    }

    private void LoadSeedFile()
    {
        if (!ConfirmDiscard()) return;
        m_LiveHub = null;
        if (!File.Exists(SeedPath))
        {
            EditorUtility.DisplayDialog("Memory Graph", "种子文件不存在: " + SeedPath, "好");
            return;
        }
        LoadJson(File.ReadAllText(SeedPath), SeedPath);
    }

    private void LoadJson(string json, string path)
    {
        try
        {
            m_File = JsonUtility.FromJson<GraphFile>(json) ?? new GraphFile();
            if (m_File.nodes == null) m_File.nodes = new List<MemoryNode>();
            if (m_File.edges == null) m_File.edges = new List<MemoryEdge>();
            m_LoadedPath = path;
            m_Dirty = false;
            m_Selected = null;
            ResetLayout();
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Memory Graph", "解析失败: " + e.Message, "好");
        }
    }

    private void ConnectLive()
    {
        if (!Application.isPlaying)
        {
            EditorUtility.DisplayDialog("Memory Graph", "直连模式只在 Play 模式下可用。", "好");
            return;
        }
        var hub = FindObjectOfType<MemoryHub>();
        if (hub == null || hub.Store == null)
        {
            EditorUtility.DisplayDialog("Memory Graph", "场景里没有已初始化的 MemoryHub。", "好");
            return;
        }
        if (!ConfirmDiscard()) return;
        m_LiveHub = hub;
        m_LoadedPath = "运行中的 MemoryHub 实例";
        m_Dirty = false;
        m_Selected = null;
        ResetLayout();
    }

    private bool ConfirmDiscard()
    {
        if (!m_Dirty) return true;
        return EditorUtility.DisplayDialog("Memory Graph", "有未保存的改动,载入其他数据源会丢弃它们。继续?", "丢弃并载入", "取消");
    }

    private void Save()
    {
        if (IsLive)
        {
            m_LiveHub.Store.Save();          // 直连模式走 MemoryStore 自己的原子保存
            m_Dirty = false;
            ShowNotification(new GUIContent("已保存到运行时文件"));
            return;
        }
        if (string.IsNullOrEmpty(m_LoadedPath)) return;
        try
        {
            string json = JsonUtility.ToJson(m_File, true);
            string tmp = m_LoadedPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(m_LoadedPath)) File.Replace(tmp, m_LoadedPath, null);
            else File.Move(tmp, m_LoadedPath);
            m_Dirty = false;
            if (m_LoadedPath == SeedPath) AssetDatabase.Refresh();
            ShowNotification(new GUIContent("已保存"));
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Memory Graph", "保存失败: " + e.Message, "好");
        }
    }

    // ==================== 排序(与 MemoryRanking 一致) ====================

    private static float Importance(MemoryNode n)
    {
        DateTime t = n.GetLastActivatedUtc();
        float days = (t == DateTime.MinValue) ? 365f : (float)(DateTime.UtcNow - t).TotalDays;
        if (days < 0f) days = 0f;
        float recency = 1f + 0.5f * Mathf.Exp(-days / 7f);
        return Mathf.Clamp01(n.weight) * recency;
    }

    private int RankOf(MemoryNode n)
    {
        float s = Importance(n);
        int rank = 1;
        foreach (var m in Nodes) if (m != n && Importance(m) > s) rank++;
        return rank;
    }

    // ==================== 力导向 ====================

    private Body BodyOf(MemoryNode n)
    {
        Body b;
        if (!m_Bodies.TryGetValue(n, out b))
        {
            //新节点落在已有节点质心附近,带一点随机散开
            b = new Body { x = UnityEngine.Random.Range(-40f, 40f), y = UnityEngine.Random.Range(-40f, 40f) };
            m_Bodies[n] = b;
        }
        return b;
    }

    private void ResetLayout()
    {
        m_Bodies.Clear();
        var list = Nodes;
        for (int i = 0; i < list.Count; i++)
        {
            float a = i / (float)Mathf.Max(1, list.Count) * Mathf.PI * 2f;
            m_Bodies[list[i]] = new Body { x = Mathf.Cos(a) * 220f, y = Mathf.Sin(a) * 220f };
        }
        m_Alpha = 1f;
        m_Pan = Vector2.zero;
        m_Zoom = 1f;
    }

    private void Tick()
    {
        var nodes = Nodes;
        var edges = Edges;
        for (int i = 0; i < nodes.Count; i++)
        {
            var a = BodyOf(nodes[i]);
            a.vx += -a.x * 0.0035f * m_Alpha;
            a.vy += -a.y * 0.0035f * m_Alpha;
            for (int j = i + 1; j < nodes.Count; j++)
            {
                var b = BodyOf(nodes[j]);
                float dx = a.x - b.x, dy = a.y - b.y;
                float d2 = dx * dx + dy * dy;
                if (d2 < 1f) { dx = UnityEngine.Random.value - .5f; dy = UnityEngine.Random.value - .5f; d2 = 1f; }
                float d = Mathf.Sqrt(d2);
                float f = Mathf.Min(12f, 5200f / d2) * m_Alpha;
                float fx = dx / d * f, fy = dy / d * f;
                a.vx += fx; a.vy += fy; b.vx -= fx; b.vy -= fy;
            }
        }
        foreach (var e in edges)
        {
            var na = FindNode(e.from); var nb = FindNode(e.to);
            if (na == null || nb == null) continue;
            var a = BodyOf(na); var b = BodyOf(nb);
            float rest = 105f + (1f - e.strength) * 120f + (Radius(na) + Radius(nb)) * .6f;
            float dx = b.x - a.x, dy = b.y - a.y;
            float d = Mathf.Max(1f, Mathf.Sqrt(dx * dx + dy * dy));
            float f = (d - rest) * 0.02f * e.strength * m_Alpha;
            float fx = dx / d * f, fy = dy / d * f;
            a.vx += fx; a.vy += fy; b.vx -= fx; b.vy -= fy;
        }
        foreach (var n in nodes)
        {
            var b = BodyOf(n);
            if (b.pinned) { b.vx = b.vy = 0; continue; }
            b.vx *= .86f; b.vy *= .86f;
            b.x += b.vx; b.y += b.vy;
        }
        m_Alpha = Mathf.Max(0.02f, m_Alpha * 0.995f);
    }

    private MemoryNode FindNode(string name)
    {
        var list = Nodes;
        for (int i = 0; i < list.Count; i++) if (list[i].name == name) return list[i];
        return null;
    }

    private float Radius(MemoryNode n)
    {
        //权重按数据范围归一化到 13..30
        float mn = 1f, mx = 0f;
        foreach (var m in Nodes) { if (m.weight < mn) mn = m.weight; if (m.weight > mx) mx = m.weight; }
        float t = (mx > mn) ? (n.weight - mn) / (mx - mn) : .5f;
        return 13f + 17f * t;
    }

    // ==================== GUI ====================

    private void OnGUI()
    {
        if (m_LabelStyle == null)
        {
            m_LabelStyle = new GUIStyle(EditorStyles.miniLabel)
            { alignment = TextAnchor.UpperCenter, fontSize = 11 };
            m_LabelStyle.normal.textColor = ColInk;
        }

        DrawToolbar();

        float top = EditorStyles.toolbar.fixedHeight;
        if (Application.isPlaying && !IsLive) top += 40f;   //警告条占用的高度
        var graphRect = new Rect(0, top, position.width - PanelW, position.height - top);
        var panelRect = new Rect(position.width - PanelW, top, PanelW, position.height - top);

        HandleGraphEvents(graphRect);
        DrawGraph(graphRect);
        DrawPanel(panelRect);

        titleContent.text = m_Dirty ? "Memory Graph *" : "Memory Graph";
    }

    private void DrawToolbar()
    {
        GUILayout.BeginHorizontal(EditorStyles.toolbar);
        if (GUILayout.Button("载入运行时", EditorStyles.toolbarButton, GUILayout.Width(74))) LoadRuntimeFile();
        if (GUILayout.Button("载入种子", EditorStyles.toolbarButton, GUILayout.Width(62))) LoadSeedFile();
        using (new EditorGUI.DisabledScope(!Application.isPlaying))
            if (GUILayout.Button("直连运行实例", EditorStyles.toolbarButton, GUILayout.Width(86))) ConnectLive();
        GUILayout.Space(8);
        using (new EditorGUI.DisabledScope(!m_Dirty && !IsLive))
            if (GUILayout.Button("保存", EditorStyles.toolbarButton, GUILayout.Width(44))) Save();
        if (GUILayout.Button("重新布局", EditorStyles.toolbarButton, GUILayout.Width(62))) ResetLayout();
        if (GUILayout.Button("添加节点", EditorStyles.toolbarButton, GUILayout.Width(62))) AddNode();
        GUILayout.Space(8);
        GUILayout.Label("搜索", GUILayout.Width(28));
        m_Search = GUILayout.TextField(m_Search, EditorStyles.toolbarTextField, GUILayout.Width(160));
        GUILayout.FlexibleSpace();
        GUILayout.Label($"节点 {Nodes.Count} · 边 {Edges.Count} · {(IsLive ? "直连 Play 实例" : Path.GetFileName(m_LoadedPath))}",
            EditorStyles.miniLabel);
        GUILayout.EndHorizontal();

        if (Application.isPlaying && !IsLive)
            EditorGUILayout.HelpBox("正在 Play:磁盘文件不是最新状态,退出时会被游戏覆盖。建议用「直连运行实例」查看/编辑。", MessageType.Warning);
    }

    private void AddNode()
    {
        string name = "新记忆";
        int k = 2;
        while (FindNode(name) != null) name = "新记忆 " + k++;
        var n = new MemoryNode(name, "", 0.6f);
        if (IsLive) m_LiveHub.Store.AddNode(n);   //走 API 维护内部索引
        else m_File.nodes.Add(n);
        var b = BodyOf(n);
        b.x = -m_Pan.x / m_Zoom; b.y = -m_Pan.y / m_Zoom;   //视野中心
        m_Selected = n;
        m_Dirty = true;
        m_Alpha = Mathf.Max(m_Alpha, .3f);
    }

    // ---------- 图区事件 ----------

    private Vector2 WorldToScreen(Body b, Rect r) =>
        new Vector2(b.x * m_Zoom + m_Pan.x + r.width * .5f, b.y * m_Zoom + m_Pan.y + r.height * .5f);

    private Vector2 ScreenToWorld(Vector2 p, Rect r) =>
        new Vector2((p.x - r.width * .5f - m_Pan.x) / m_Zoom, (p.y - r.height * .5f - m_Pan.y) / m_Zoom);

    private MemoryNode Pick(Vector2 local, Rect r)
    {
        MemoryNode best = null; float bd = float.MaxValue;
        foreach (var n in Nodes)
        {
            var s = WorldToScreen(BodyOf(n), r);
            float d = Vector2.Distance(s, local);
            if (d < Radius(n) * m_Zoom + 8f && d < bd) { best = n; bd = d; }
        }
        return best;
    }

    private void HandleGraphEvents(Rect r)
    {
        var e = Event.current;
        if (!r.Contains(e.mousePosition) && m_DragNode == null && !m_Panning) return;
        Vector2 local = e.mousePosition - r.position;

        switch (e.type)
        {
            case EventType.MouseDown when e.button == 0 || e.button == 2:
                m_MouseDownPos = local;
                var hit = Pick(local, r);
                if (hit != null && e.button == 0) { m_DragNode = hit; BodyOf(hit).pinned = true; }
                else m_Panning = true;
                m_Alpha = Mathf.Max(m_Alpha, .3f);
                e.Use();
                break;

            case EventType.MouseDrag:
                if (m_DragNode != null)
                {
                    var w = ScreenToWorld(local, r);
                    var b = BodyOf(m_DragNode);
                    b.x = w.x; b.y = w.y;
                    m_Alpha = Mathf.Max(m_Alpha, .25f);
                    e.Use(); Repaint();
                }
                else if (m_Panning) { m_Pan += e.delta; e.Use(); Repaint(); }
                break;

            case EventType.MouseUp:
                if (m_DragNode != null)
                {
                    BodyOf(m_DragNode).pinned = false;
                    if (Vector2.Distance(local, m_MouseDownPos) < 4f)
                        m_Selected = (m_Selected == m_DragNode) ? null : m_DragNode;
                    m_DragNode = null;
                    e.Use(); Repaint();
                }
                else if (m_Panning)
                {
                    if (Vector2.Distance(local, m_MouseDownPos) < 4f) m_Selected = null;   //点空白取消选中
                    m_Panning = false;
                    e.Use(); Repaint();
                }
                break;

            case EventType.ScrollWheel:
                float old = m_Zoom;
                m_Zoom = Mathf.Clamp(m_Zoom * (1f - e.delta.y * 0.04f), 0.25f, 3f);
                //以鼠标为锚点缩放
                Vector2 c = local - new Vector2(r.width, r.height) * .5f;
                m_Pan = c - (c - m_Pan) * (m_Zoom / old);
                e.Use(); Repaint();
                break;
        }
    }

    // ---------- 绘制 ----------

    private void DrawGraph(Rect r)
    {
        EditorGUI.DrawRect(r, ColBg);
        if (Event.current.type != EventType.Repaint) return;

        GUI.BeginClip(r);
        var focusSet = (m_Selected != null) ? NeighborSet(m_Selected) : null;
        string q = m_Search.Trim().ToLowerInvariant();
        var rc = new Rect(0, 0, r.width, r.height);

        foreach (var e in Edges)
        {
            var na = FindNode(e.from); var nb = FindNode(e.to);
            if (na == null || nb == null) continue;
            bool dim = focusSet != null && !(focusSet.Contains(na) && focusSet.Contains(nb));
            var c = ColEdge; c.a = dim ? 0.10f : 0.55f;
            Handles.color = c;
            var pa = WorldToScreen(BodyOf(na), rc); var pb = WorldToScreen(BodyOf(nb), rc);
            Handles.DrawAAPolyLine(0.8f + (e.strength - 0.5f) * 5f,
                new Vector3(pa.x, pa.y, 0), new Vector3(pb.x, pb.y, 0));
        }

        foreach (var n in Nodes)
        {
            bool dimF = focusSet != null && !focusSet.Contains(n);
            bool dimQ = q.Length > 0 &&
                !(n.name.ToLowerInvariant().Contains(q) ||
                  (n.description ?? "").ToLowerInvariant().Contains(q));
            float alpha = (dimF || dimQ) ? 0.15f : 1f;

            var p = WorldToScreen(BodyOf(n), rc);
            float rad = Radius(n) * m_Zoom;
            var pos3 = new Vector3(p.x, p.y, 0);

            var fill = (n == m_Selected) ? ColNodeHi : ColNode; fill.a = alpha;
            Handles.color = fill;
            Handles.DrawSolidDisc(pos3, Vector3.forward, rad);

            if (n.weight >= 0.945f)
            {
                var g = ColCore; g.a = alpha;
                Handles.color = g;
                Handles.DrawWireDisc(pos3, Vector3.forward, rad + 2.5f, 2.2f);
            }
            if (n == m_Selected)
            {
                var w = ColInk; w.a = 0.9f;
                Handles.color = w;
                Handles.DrawWireDisc(pos3, Vector3.forward, rad + 6f, 1.6f);
            }

            string label = n.name.Length > 14 ? n.name.Substring(0, 13) + "…" : n.name;
            var old = GUI.color;
            GUI.color = new Color(1, 1, 1, alpha);
            GUI.Label(new Rect(p.x - 80, p.y + rad + 3, 160, 18), label, m_LabelStyle);
            GUI.color = old;
        }
        GUI.EndClip();
    }

    private HashSet<MemoryNode> NeighborSet(MemoryNode n)
    {
        var s = new HashSet<MemoryNode> { n };
        foreach (var e in Edges)
        {
            if (e.from == n.name) { var m = FindNode(e.to); if (m != null) s.Add(m); }
            if (e.to == n.name) { var m = FindNode(e.from); if (m != null) s.Add(m); }
        }
        return s;
    }

    // ---------- 右侧面板 ----------

    private void DrawPanel(Rect r)
    {
        GUILayout.BeginArea(r, EditorStyles.helpBox);
        m_PanelScroll = GUILayout.BeginScrollView(m_PanelScroll);

        if (m_Selected == null) DrawOverviewPanel();
        else DrawNodePanel(m_Selected);

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawOverviewPanel()
    {
        GUILayout.Label("网络总览", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("数据源", string.IsNullOrEmpty(m_LoadedPath) ? "(未载入)" : m_LoadedPath, EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.Space(4);
        GUILayout.Label("重要性 Top 10(会注入感知帧)", EditorStyles.miniBoldLabel);
        foreach (var n in Nodes.OrderByDescending(Importance).Take(10))
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(n.name, EditorStyles.linkLabel)) { m_Selected = n; }
            GUILayout.FlexibleSpace();
            GUILayout.Label(Importance(n).ToString("F3"), EditorStyles.miniLabel);
            GUILayout.EndHorizontal();
        }
        EditorGUILayout.Space(6);
        EditorGUILayout.HelpBox(
            "左键拖动节点 / 点击选中\n空白处拖动平移,滚轮缩放\n" +
            (IsLive ? "直连模式:改动实时作用于游戏内记忆,「保存」立即落盘"
                    : "文件模式:所有改动需点「保存」写回 json"),
            MessageType.None);
    }

    private void DrawNodePanel(MemoryNode n)
    {
        bool core = n.weight >= 0.945f;
        GUILayout.Label(core ? "核心记忆" : "记忆节点", EditorStyles.miniBoldLabel);

        // --- 名称(重命名会同步所有边;直连模式禁用以保护索引) ---
        using (new EditorGUI.DisabledScope(IsLive))
        {
            if (m_RenameNode != n) { m_PendingRename = n.name; m_RenameNode = n; }
            m_PendingRename = EditorGUILayout.DelayedTextField("名称", m_PendingRename);
            if (m_PendingRename != n.name && !string.IsNullOrWhiteSpace(m_PendingRename))
            {
                if (FindNode(m_PendingRename) != null)
                {
                    EditorUtility.DisplayDialog("Memory Graph", "已存在同名节点: " + m_PendingRename, "好");
                    m_PendingRename = n.name;
                }
                else
                {
                    foreach (var e in Edges)
                    {
                        if (e.from == n.name) e.from = m_PendingRename;
                        if (e.to == n.name) e.to = m_PendingRename;
                    }
                    var body = BodyOf(n);          //改名前后是同一对象,布局信息不丢
                    n.name = m_PendingRename;
                    m_Bodies[n] = body;
                    m_Dirty = true;
                }
            }
        }
        if (IsLive) EditorGUILayout.LabelField("(直连模式下不可重命名/删除节点)", EditorStyles.miniLabel);

        // --- 描述 / 权重 ---
        EditorGUILayout.LabelField("描述");
        EditorGUI.BeginChangeCheck();
        string desc = EditorGUILayout.TextArea(n.description ?? "", GUILayout.MinHeight(56));
        float w = EditorGUILayout.Slider("权重 weight", n.weight, 0f, 1f);
        if (EditorGUI.EndChangeCheck())
        {
            n.description = desc;
            n.weight = w;
            m_Dirty = true;
        }

        // --- 状态 ---
        int rank = RankOf(n);
        EditorGUILayout.LabelField("重要性得分", Importance(n).ToString("F3") + $"(第 {rank} 位{(rank <= 30 ? ",会注入感知帧" : "")})");
        DateTime t = n.GetLastActivatedUtc();
        EditorGUILayout.LabelField("最近激活", t == DateTime.MinValue ? "未知" :
            t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + $"({(DateTime.UtcNow - t).TotalDays:F0} 天前)");
        if (GUILayout.Button("刷新激活时间(TouchActivated)")) { n.TouchActivated(); m_Dirty = true; }

        // --- 边 ---
        EditorGUILayout.Space(6);
        GUILayout.Label("关联到 →", EditorStyles.miniBoldLabel);
        DrawEdgeList(Edges.Where(e => e.from == n.name).ToList(), true, n);
        GUILayout.Label("← 被关联", EditorStyles.miniBoldLabel);
        DrawEdgeList(Edges.Where(e => e.to == n.name).ToList(), false, n);

        // --- 添加边 ---
        var candidates = Nodes.Where(m => m != n && !Edges.Any(e => e.from == n.name && e.to == m.name))
                              .Select(m => m.name).ToArray();
        if (candidates.Length > 0)
        {
            GUILayout.BeginHorizontal();
            m_AddEdgeTarget = Mathf.Clamp(m_AddEdgeTarget, 0, candidates.Length - 1);
            m_AddEdgeTarget = EditorGUILayout.Popup(m_AddEdgeTarget, candidates);
            if (GUILayout.Button("添加关联 →", GUILayout.Width(84)))
            {
                Edges.Add(new MemoryEdge(n.name, candidates[m_AddEdgeTarget], 0.8f));
                m_Dirty = true;
                m_Alpha = Mathf.Max(m_Alpha, .3f);
            }
            GUILayout.EndHorizontal();
        }

        // --- 删除节点 ---
        EditorGUILayout.Space(8);
        using (new EditorGUI.DisabledScope(IsLive))
        {
            var old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, .55f, .55f);
            if (GUILayout.Button("删除该节点(连带删除相关边)"))
            {
                if (EditorUtility.DisplayDialog("Memory Graph", $"删除节点「{n.name}」及其全部关联?", "删除", "取消"))
                {
                    m_File.edges.RemoveAll(e => e.from == n.name || e.to == n.name);
                    m_File.nodes.Remove(n);
                    m_Bodies.Remove(n);
                    m_Selected = null;
                    m_Dirty = true;
                }
            }
            GUI.backgroundColor = old;
        }
    }

    private void DrawEdgeList(List<MemoryEdge> list, bool outgoing, MemoryNode n)
    {
        if (list.Count == 0) { EditorGUILayout.LabelField("(无)", EditorStyles.miniLabel); return; }
        MemoryEdge toDelete = null;
        foreach (var e in list)
        {
            GUILayout.BeginHorizontal();
            string other = outgoing ? e.to : e.from;
            if (GUILayout.Button(other, EditorStyles.linkLabel, GUILayout.MaxWidth(150)))
            {
                var m = FindNode(other);
                if (m != null) m_Selected = m;
            }
            EditorGUI.BeginChangeCheck();
            float s = EditorGUILayout.Slider(e.strength, 0f, 1f);
            if (EditorGUI.EndChangeCheck()) { e.strength = s; m_Dirty = true; }
            if (GUILayout.Button("×", GUILayout.Width(20))) toDelete = e;
            GUILayout.EndHorizontal();
        }
        if (toDelete != null) { Edges.Remove(toDelete); m_Dirty = true; }
    }
}
