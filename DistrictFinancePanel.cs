using System.Collections.Generic;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.UI;
using UnityEngine;

namespace DistrictFinanceManager
{
    /// <summary>
    /// 独立 OnGUI 面板，带缩放与拖动。
    ///
    /// 拖动：按住标题栏拖动。缩放：面板内滚轮 0.6x~2.0x。
    /// 显示：财务（自身 / 含下辖合计）、层级分配、层级树、未分配列表。
    /// 用原版区划工具选中区划；F9 开关面板。
    /// </summary>
    public class DistrictFinancePanel : MonoBehaviour
    {
        #region Constants

        private const float PW = 560f;
        private const float PH = 900f;
        private const float PAD = 12f;

        // 行高（比字号大，给中文/emoji 留足空间，避免溢出重叠）
        private const float TITLE_H = 26f;
        private const float VALUE_H = 22f;
        private const float TEXT_H = 20f;
        private const float NODE_H = 26f;
        private const float HEADER_H = 18f;
        private const float BTN_H = 26f;
        private const float GAP = 6f;

        // 统一配色（可调）：GDP 与人均GDP 共用这一套 16 档颜色，从低（深红）到高（紫）
        private static readonly Color[] TIER_COLORS =
        {
            new Color(0.80f, 0.10f, 0.15f), // 深红
            new Color(0.95f, 0.22f, 0.18f), // 红
            new Color(1.0f, 0.35f, 0.18f),  // 橙红
            new Color(1.0f, 0.45f, 0.12f),  // 橙
            new Color(1.0f, 0.58f, 0.08f),  // 橙黄
            new Color(1.0f, 0.72f, 0.10f),  // 黄
            new Color(1.0f, 0.85f, 0.20f),  // 金黄
            new Color(0.85f, 1.0f, 0.25f),  // 黄绿
            new Color(0.55f, 1.0f, 0.35f),  // 浅绿
            new Color(0.30f, 1.0f, 0.50f),  // 绿
            new Color(0.10f, 1.0f, 0.70f),  // 青
            new Color(0.0f, 0.85f, 1.0f),   // 亮蓝
            new Color(0.15f, 0.60f, 1.0f),  // 蓝
            new Color(0.30f, 0.42f, 1.0f),  // 深蓝
            new Color(0.50f, 0.30f, 1.0f),  // 蓝紫
            new Color(0.70f, 0.30f, 1.0f),  // 紫
        };

        // 人均GDP 分档阈值（15 个，对应 16 档颜色）
        private static readonly long[] PCAP_TIERS =
            { 5, 10, 15, 20, 25, 30, 40, 50, 65, 80, 100, 120, 150, 180, 220 };

        // GDP 分档阈值（15 个，对应 16 档颜色）
        // 每级 = 人均GDP 分档 × 人口分档对应值（逐级相乘），多取整一点
        private static readonly long[] GDP_TIERS =
            { 500, 2500, 7500, 20000, 50000, 120000, 300000, 750000, 1600000, 3000000, 6000000, 10000000, 20000000, 30000000, 45000000 };

        // 人口分档阈值（15 个，对应 16 档颜色）—— 最高档 20 万，低人口细分，中高稍粗
        private static readonly long[] POP_TIERS =
            { 100, 250, 500, 1000, 2000, 4000, 8000, 15000, 25000, 40000, 60000, 90000, 130000, 170000, 200000 };

        // 地价分档阈值（15 个，对应 16 档颜色）—— 每 8 一档（原版 kr/m² 基准）
        private static readonly long[] LAND_TIERS =
            { 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 96, 104, 112, 120 };

        #endregion

        #region Fields

        private DistrictFinanceHub _hub;
        private bool _vis;
        private KeyCode _key = KeyCode.F9;
        private float _keyReloadTimer;

        private Vector2 _panelPos = new Vector2(-1f, 80f);
        private float _scale = 1.2f; // 默认 120%（从设置读取）
        private bool _dragging;

        // 供镜头缩放补丁使用：面板屏幕矩形（Input.mousePosition 左下原点坐标）
        public static Rect PanelInputRect = new Rect(-1f, -1f, 0f, 0f);
        public static bool PanelVisible = false;

        public static bool IsMouseOverPanel()
        {
            return PanelVisible && PanelInputRect.Contains(Input.mousePosition);
        }

        private Vector2 _scroll;
        private Vector2 _sortScroll;
        private int _viewMode; // 0=层级 1=组合 2=所有区划 3=市 4=区县 5=乡镇 6=村社区
        private bool _filterSubtree;
        private int _activeGroupIdx = -1;
        /// <summary>当前“选中”并在顶部显示合计详情的组合索引（-1 = 无，与区划选择互斥）。</summary>
        private int _detailGroup = -1;
        /// <summary>组合列表（按存档保存，来自 Hub）。</summary>
        private List<GroupData> Groups
        {
            get { return _hub != null ? _hub.Groups : new List<GroupData>(); }
        }
        private bool _memberExpand = false; // 激活组合的成员选择是否展开
        private string _groupNameInput = ""; // 主面板内组合名输入
        private UIPanel _nameDlg;           // 原生命名弹窗
        private UITextField _nameTf;
        private UILabel _nameTitleLb;
        private UILabel _nameHintLb;
        private int _sortKey; // 0=GDP 1=人口 2=人均GDP
        private bool _modeDropOpen; // 现实化数据下拉展开状态
        private bool _helpVis; // 操作说明面板可见
        private Vector2 _helpPos = new Vector2(-1f, 100f);
        private bool _helpDragging;
        private Vector2 _helpScroll;
        private readonly Dictionary<ushort, bool> _ex = new Dictionary<ushort, bool>();
        private int _addLevel = DistLevel.REGION;

        private DistrictFinanceCalculator.FinanceResult _fin;
        private ushort _finDistrict;
        private float _finRefresh;
        private float _lastResWeight = -1f;
        private float _lastWorkWeight = -1f;
        private int _lastDisplayMode = -1;

        private GUIStyle _ti, _ts, _fl, _fv, _pcv, _btn, _bn2, _hdr, _nodeBtn, _diag, _legend, _rankBtn;
        private bool _styled;
        private Texture2D _bgTex;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _hub = GetComponent<DistrictFinanceHub>();
            LoadKey();
        }

        private void LoadKey()
        {
            ModSettings s = ModSettings.Load();
            _key = s.GetPanelKeyCode();
            Loc.Lang = s.Language;
            _scale = Mathf.Clamp(s.PanelScale, 0.6f, 2.0f); // 恢复保存的缩放
            if (_hub != null)
            {
                _hub.RefreshSettings();
                // 居民/工人权重变化 → 清空缓存即时重算
                if (Mathf.Abs(s.ResidentWeight - _lastResWeight) > 0.0001f ||
                    Mathf.Abs(s.WorkerWeight - _lastWorkWeight) > 0.0001f)
                {
                    _lastResWeight = s.ResidentWeight;
                    _lastWorkWeight = s.WorkerWeight;
                    _hub.Calculator.ClearCache();
                    _finDistrict = 0;
                }
                // 现实化数据切换 → 立即重算
                if (s.DisplayMode != _lastDisplayMode)
                {
                    _lastDisplayMode = s.DisplayMode;
                    _hub.Calculator.ClearCache();
                    _finDistrict = 0;
                }
            }
        }

        private void Update()
        {
            _keyReloadTimer -= Time.deltaTime;
            if (_keyReloadTimer <= 0f)
            {
                _keyReloadTimer = 0.5f; // 更快响应设置变化（权重/模式即时重算）
                LoadKey();
            }

            if (Input.GetKeyDown(_key))
            {
                _vis = !_vis;
                if (_vis) _finDistrict = 0;
            }

            if (_vis)
            {
                byte toolDistrict = GetToolDistrict();
                if (toolDistrict != 0)
                {
                    _hub.SelectedVanillaDistrict = toolDistrict;
                    if (_hub.SelectedID == 0 && _detailGroup < 0) // 组合选中时不被地图区划抢选择
                        SelectDistrict((ushort)toolDistrict);
                }
            }
        }

        private void OnGUI()
        {
            // 确保 IME 启用（支持中文输入法到 TextField）
            try { Input.imeCompositionMode = IMECompositionMode.On; }
            catch { }

            MakeStyles();
            PanelVisible = false;
            if (!_vis) return;

            ForwardNameInput(); // 弹窗输入框聚焦时转发键盘输入
            DrawNameCaret();    // 自绘闪烁光标（引擎光标在 IMGUI 下不可用）

            if (_hub == null || _hub.Hierarchy == null)
            {
                GUI.Box(new Rect(20, 60, 320, 40), Loc.T("[DFM] 未初始化", "[DFM] Not initialized"), _fl);
                return;
            }

            if (_panelPos.x < 0)
                _panelPos = new Vector2(Screen.width - PW - 20, 80);

            HandlePanelInput();

            // 更新静态面板矩形（Input.mousePosition 左下原点坐标），供镜头缩放补丁判断
            PanelVisible = true;
            PanelInputRect = new Rect(_panelPos.x,
                Screen.height - _panelPos.y - PH * _scale,
                PW * _scale, PH * _scale);

            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(
                new Vector3(_panelPos.x, _panelPos.y, 0),
                Quaternion.identity,
                new Vector3(_scale, _scale, 1f));

            DrawPanel();

            GUI.matrix = old;

            if (_helpVis)
                DrawHelpPanel();

            ConsumePanelMouse();
        }

        /// <summary>独立操作说明面板（可拖动、不随主面板移动/缩放）。</summary>
        private void DrawHelpPanel()
        {
            if (_helpPos.x < 0)
                _helpPos = new Vector2((Screen.width - 680) / 2f, 60f); // 初始居中可见

            Rect p = new Rect(_helpPos.x, _helpPos.y, 680, 700);
            Rect titleBar = new Rect(_helpPos.x, _helpPos.y, 680, 60);

            Event ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 0 && titleBar.Contains(ev.mousePosition))
                _helpDragging = true;
            if (ev.type == EventType.MouseUp && ev.button == 0)
                _helpDragging = false;
            if (_helpDragging && ev.type == EventType.MouseDrag)
                _helpPos += ev.delta;

            // 限制在屏幕内，避免拖出看不见
            _helpPos.x = Mathf.Clamp(_helpPos.x, 10f, Mathf.Max(10f, Screen.width - 680));
            _helpPos.y = Mathf.Clamp(_helpPos.y, 10f, Mathf.Max(10f, Screen.height - 300));

            if (_bgTex == null)
                _bgTex = Tex(new Color(0.07f, 0.07f, 0.13f, 1f));
            GUI.DrawTexture(p, _bgTex);

            if (GUI.Button(new Rect(p.x + p.width - 50, p.y + 4, 44, 30), "✕", _btn))
                _helpVis = false;

            float y = p.y + PAD + 4;
            GUIStyle helpTitle = new GUIStyle(_ti);
            helpTitle.fontSize = 45; // 标题放大 3 倍
            GUI.Label(new Rect(p.x + PAD, y, p.width - PAD * 2, 60), Loc.T("操作说明（拖动标题栏移动）", "Help (drag title to move)"), helpTitle);
            y += 60;

            string[] helpLines = Loc.IsEn
                ? new string[] {
                    "Drag the panel by its top-left; wheel over the top-right to zoom.",
                    "List below for viewing/sorting - switch views: Hierarchy / All districts / City / District / Town / Village.",
                    "[Assign levels]",
                    "First select the \"Hierarchy\" view.",
                    "1. (Optional) Click a district in the assigned list first, then follow the steps below to attach new districts under the selected one.",
                    "2. In \"Assign level\" choose the target level for new districts: City / District / Town / Village.",
                    "3. Click a district in the \"Unassigned\" list to add it.",
                    "Example: If district A is set as City level, A itself is the directly-administered area; districts B and C are attached under A. City-level ranking will include all data of A and its subordinates, while in district-level ranking, A participates separately as a directly-administered district (can be disabled in options).",
                    "[Remove hierarchy]",
                    "Select an assigned district and click the \"Remove\" button: it will be removed from the hierarchy along with all its subordinates (the district itself stays in the game).",
                    "[Groups]",
                    "The Group view lets you group any districts and name them. A group only sums its members' own values and never affects the hierarchy; created groups auto-sort."
                }
                : new string[] {
                    "左上角拖动面板；右上角滚轮缩放面板。",
                    "下方列表用于查看与排序——用 层级/所有区划/市/区县/乡镇/村社区 切换视图。",
                    "【层级分配】",
                    "请先选择「层级」视图。",
                    "1.（可选）已加入的区划列表中点击某个区划再执行下面步骤，即可挂到当前选中区划下。",
                    "2. 在「分配层级」处选择新加入目标级别：市 / 区县 / 乡镇 / 村社区。",
                    "3. 在「未分配区划」点选一个区划即可加入。",
                    "例：如a区划被定为市级，其自身定义为市直辖区域，b区c区被挂入a市，市级排名将会计入a与下辖区域的所有数据，而按照区级排名，a自身作为直辖区划会单独参与排名（直辖参与排名可在选项中关闭）。",
                    "【移除层级】",
                    "选中一个已分配的区划，点「移除」按钮，会将其连同所有下辖一起从层级树中移除（区划本身仍保留在游戏中）。",
                    "【组合】",
                    "组合视图可把任意区划组合成组并命名；组合只统计各成员自身值合计，不影响层级。创建后自动排序。"
                };

            GUIStyle helpStyle = new GUIStyle(_fl);
            helpStyle.fontSize = 22; // 字体 ×2
            helpStyle.wordWrap = true;

            // 滚动内容区
            float scrollW = p.width - 24;
            float totalH = 0;
            float[] lineH = new float[helpLines.Length];
            for (int i = 0; i < helpLines.Length; i++)
            {
                float lh = helpLines[i].Length > 60 ? 130f : 62f; // 长行给更高
                lineH[i] = lh;
                totalH += lh;
            }

            Rect contentRect = new Rect(p.x + 8, y, p.width - 16, p.height - (y - p.y) - 10);
            _helpScroll = GUI.BeginScrollView(contentRect, _helpScroll,
                new Rect(0, 0, scrollW, totalH + 20));

            float cy = 0;
            for (int i = 0; i < helpLines.Length; i++)
            {
                GUI.Label(new Rect(4, cy, scrollW - 16, lineH[i]), helpLines[i], helpStyle);
                cy += lineH[i];
            }
            GUI.EndScrollView();
        }

        /// <summary>
        /// 绘制结束后，把落在面板范围内、且未被按钮消费的鼠标按下/抬起事件补消费，
        /// 避免点击面板空白处同时穿透到游戏内 UI。按钮自身已消费的事件类型会变成 Used，此处不再重复处理。
        /// </summary>
        private void ConsumePanelMouse()
        {
            Event e = Event.current;
            Rect panelScreen = new Rect(_panelPos.x, _panelPos.y, PW * _scale, PH * _scale);

            // 右键：取消当前选中区划
            if (e.type == EventType.MouseDown && e.button == 1 && panelScreen.Contains(e.mousePosition))
            {
                ClearSelection();
                e.Use();
                return;
            }

            if ((e.type == EventType.MouseDown || e.type == EventType.MouseUp)
                && panelScreen.Contains(e.mousePosition))
            {
                e.Use();
            }
        }

        private void HandlePanelInput()
        {
            Event e = Event.current;
            Rect titleScreen = new Rect(_panelPos.x, _panelPos.y, PW * _scale, TITLE_H * _scale);

            // 说明按钮区域：不触发拖动，让按钮正常响应点击
            Rect helpBtnScreen = new Rect(_panelPos.x + (PW / 2f - 40) * _scale,
                _panelPos.y + (PAD + 1) * _scale, 80 * _scale, (TITLE_H - 2) * _scale);

            if (e.type == EventType.MouseDown && e.button == 0
                && titleScreen.Contains(e.mousePosition)
                && !helpBtnScreen.Contains(e.mousePosition))
            {
                _dragging = true;
                e.Use(); // 标题栏按下只用于拖动，不穿透到游戏
            }
            if (e.type == EventType.MouseUp && e.button == 0)
                _dragging = false;
            if (_dragging && e.type == EventType.MouseDrag)
            {
                _panelPos += e.delta;
                e.Use();
            }

            // 缩放只在标题栏触发，避免与下方列表滚动条重叠；缩放后保存
            if (e.type == EventType.ScrollWheel && titleScreen.Contains(e.mousePosition))
            {
                _scale = Mathf.Clamp(_scale - e.delta.y * 0.02f, 0.6f, 2.0f);
                e.Use();
                if (_hub != null && _hub.Settings != null)
                {
                    _hub.Settings.PanelScale = _scale;
                    _hub.Settings.Save();
                }
            }
        }

        #endregion

        #region Draw

        private void DrawPanel()
        {
            Rect p = new Rect(0, 0, PW, PH);

            // 背景用 DrawTexture（纯绘制，不吞鼠标事件，避免阻断面板下的点击）
            if (_bgTex == null)
                _bgTex = Tex(new Color(0.07f, 0.07f, 0.13f, 1f));
            GUI.DrawTexture(p, _bgTex);

            float y = PAD;

            // 标题（改为拖动提示）
            GUI.Label(new Rect(PAD, y, PW - PAD * 2 - 280, TITLE_H), Loc.T("点此拖动面板", "Drag to move panel"), _ti);
            // 右上角：在此处使用滚轮进行缩放 + 百分比（右对齐，靠右显示）
            GUIStyle zoomRight = new GUIStyle(_fl);
            zoomRight.alignment = TextAnchor.MiddleRight;
            GUI.Label(new Rect(PW - 290, y, 280, TEXT_H),
                Loc.T("在此处使用滚轮进行缩放 ", "Use mouse wheel to zoom here ") + string.Format("{0:P0}", _scale), zoomRight);

            // 顶部正中：操作说明按钮
            if (GUI.Button(new Rect(PW / 2f - 40, y + 1, 80, TITLE_H - 2), Loc.T("说明", "Help"), _helpVis ? _bn2 : _btn))
                _helpVis = !_helpVis;

            y += TITLE_H + GAP;

            if (_hub.SelectedID != _finDistrict || Time.time >= _finRefresh)
            {
                _finDistrict = _hub.SelectedID;
                _finRefresh = Time.time + 2f;
                if (_hub.SelectedID != 0)
                {
                    try { _fin = _hub.Calculator.Calculate(_hub.SelectedID); }
                    catch (System.Exception exx) { Debug.LogError("[DFM] Calculate err: " + exx); _fin = new DistrictFinanceCalculator.FinanceResult(); }
                }
            }

            if (_detailGroup >= 0 && _detailGroup < Groups.Count)
            {
                // 组合：只显示合计详情
                y = DrawGroupSummary(PAD, y, PW - PAD * 2);
            }
            else if (_hub.SelectedID == 0)
            {
                GUI.Label(new Rect(PAD, y, PW - PAD * 2, TEXT_H * 2),
                    Loc.T("点击下方『所有区划』可查看默认排序。",
                        "Click \"All districts\" below for default sorting."), _fl);
                y += TEXT_H * 2 + GAP;
            }
            else
            {
                string name = _hub.GetVanillaDistrictName(_hub.SelectedID);
                GUI.Label(new Rect(PAD, y, PW - PAD * 2, TITLE_H), "📊 " + name, _ti);
                y += TITLE_H;

                double pcap = _fin.Population > 0 ? (double)_fin.GDP / _fin.Population : 0.0;

                // 自身
                DrawGdp(new Rect(PAD, y, PW - PAD * 2, VALUE_H),
                    Loc.T("本区划 GDP " + CurrencySymbol(), "This district GDP " + CurrencySymbol()), _fin.GDP);
                y += VALUE_H;
                DrawGdpPerCapita(new Rect(PAD, y, PW - PAD * 2, VALUE_H),
                    Loc.T("本区划 人均GDP " + CurrencySymbol(), "This district GDP/cap " + CurrencySymbol()), pcap, _fin.Population);
                y += VALUE_H;
                // 支出/税收/净收入暂时注释掉
                // GUI.Label(new Rect(PAD, y, PW - PAD * 2, VALUE_H),
                //     "自身 支出 $" + F(_fin.Expense), _fv);
                // y += VALUE_H;
                // GUI.Label(new Rect(PAD, y, PW - PAD * 2, VALUE_H),
                //     "自身 税收 $" + F(_fin.Tax), _fv);
                // y += VALUE_H;
                // DrawNetIncome(new Rect(PAD, y, PW - PAD * 2, VALUE_H),
                //     "自身 净收入 $", _fin.NetIncome);
                // y += VALUE_H;
                DrawPopulationLine(new Rect(PAD, y, PW - PAD * 2, TEXT_H),
                    Loc.T("本区划 人口 ", "This district pop "), _fin.Population, _fin.Workers, _fin.BuildingCount);
                y += TEXT_H;
                GUI.Label(new Rect(PAD, y, PW - PAD * 2, TEXT_H),
                    Loc.T("本区划 面积 ", "This district area ") + AreaKm2(_fin.Area) + Loc.T(" km²", " km²"), _fl);
                y += TEXT_H;

                double aggPcap = _fin.AggPopulation > 0 ? (double)_fin.AggGDP / _fin.AggPopulation : 0.0;

                // 合计（含下辖）
                DrawGdp(new Rect(PAD, y, PW - PAD * 2, VALUE_H),
                    Loc.T("合计 GDP " + CurrencySymbol(), "Total GDP " + CurrencySymbol()), _fin.AggGDP);
                y += VALUE_H;
                DrawGdpPerCapita(new Rect(PAD, y, PW - PAD * 2, VALUE_H),
                    Loc.T("合计 人均GDP " + CurrencySymbol(), "Total GDP/cap " + CurrencySymbol()), aggPcap, _fin.AggPopulation);
                y += VALUE_H;
                // 支出/税收/净收入暂时注释掉
                // GUI.Label(new Rect(PAD, y, PW - PAD * 2, VALUE_H),
                //     "合计 支出 $" + F(_fin.AggExpense), _fv);
                // y += VALUE_H;
                // GUI.Label(new Rect(PAD, y, PW - PAD * 2, VALUE_H),
                //     "合计 税收 $" + F(_fin.AggTax), _fv);
                // y += VALUE_H;
                // DrawNetIncome(new Rect(PAD, y, PW - PAD * 2, VALUE_H),
                //     "合计 净收入 $", _fin.AggNetIncome);
                // y += VALUE_H;
                DrawPopulationLine(new Rect(PAD, y, PW - PAD * 2, TEXT_H),
                    Loc.T("合计 人口 ", "Total pop "), _fin.AggPopulation, _fin.AggWorkers, _fin.AggBuildings);
                y += TEXT_H;
                if (_fin.AggArea != _fin.Area)
                {
                    GUI.Label(new Rect(PAD, y, PW - PAD * 2, TEXT_H),
                        Loc.T("合计 面积 ", "Total area ") + AreaKm2(_fin.AggArea) + Loc.T(" km²", " km²"), _fl);
                    y += TEXT_H;
                }

                // 诊断（选项可开关，多行）
                bool showDebug = _hub != null && _hub.Settings != null && _hub.Settings.ShowDebug;
                if (showDebug)
                {
                    string[] lines = _fin.Diag.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        GUI.Label(new Rect(PAD, y, PW - PAD * 2, TEXT_H),
                            (i == 0 ? Loc.T("调试: ", "Debug: ") : "") + lines[i], _diag);
                        y += TEXT_H;
                    }
                }
                y += GAP;
            }

            // 状态
            string status = Loc.T("当前选中：", "Selected: ");
            if (_detailGroup >= 0 && _detailGroup < Groups.Count)
                status += Loc.T("组合 · ", "Group · ") + Groups[_detailGroup].Name;
            else if (_hub.SelectedID == 0)
                status += Loc.T("无", "None");
            else
                status += _hub.GetVanillaDistrictName(_hub.SelectedID) + " [" +
                  (_hub.Hierarchy.LevelOf.ContainsKey(_hub.SelectedID)
                      ? LevelName(_hub.Hierarchy.LevelOf[_hub.SelectedID])
                      : Loc.T("未分配", "Unassigned")) + "]";
            GUI.Label(new Rect(PAD, y, PW - PAD * 2, TEXT_H), status, _fl);
            y += TEXT_H + GAP;

            // ==== 分配层级 ====
            GUI.Label(new Rect(PAD, y, 70, BTN_H), Loc.T("分配层级：", "Assign: "), _fl);
            float bx = PAD + 72;
            float bw = 72f;
            for (int lv = DistLevel.REGION; lv <= DistLevel.VILLAGE; lv++)
            {
                bool on = _addLevel == lv;
                string lb = LevelName(lv);
                if (GUI.Button(new Rect(bx, y, bw, BTN_H), lb, on ? _bn2 : _btn))
                    _addLevel = lv;
                bx += bw + 5;
            }

            bool canRemove = _hub.SelectedID != 0 && _hub.Hierarchy.LevelOf.ContainsKey(_hub.SelectedID);
            GUI.enabled = canRemove;
            if (GUI.Button(new Rect(bx + 2, y, 92, BTN_H), Loc.T("移除 Remove", "Remove"), _btn))
                RemoveSelected();
            GUI.enabled = true;

            y += BTN_H + GAP;

            // ==== 颜色图例（按排序依据切换；默认 GDP）====
            if (_sortKey == 1)
            {
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · 人口（人）", "Legend · Population"), POP_TIERS);
            }
            else if (_sortKey == 2)
            {
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · 人均GDP（" + CurrencySymbol() + "/人）", "Legend · GDP/capita (" + CurrencySymbol() + "/person)"), GetDisplayPCapTiers());
            }
            else if (_sortKey == 3)
            {
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · 地价（" + LandUnit() + "）", "Legend · Land value (" + LandUnit() + ")"), GetLandDisplayTiers());
            }
            else
            {
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · GDP（" + CurrencySymbol() + "）", "Legend · GDP (" + CurrencySymbol() + ")"), GetDisplayGDPTiers());
            }
            y += GAP;

            // ==== 视图按钮 ====
            string[] viewLabels = Loc.IsEn
                ? new string[] { "Tree", "Group", "All dist", "City", "District", "Town", "Village" }
                : new string[] { "层级", "组合", "所有区划", "市", "区县", "乡镇", "村社区" };

            // 第 1 排：层级 / 组合 / 筛选（固定宽度）
            float r1 = PAD;
            if (GUI.Button(new Rect(r1, y, 70, BTN_H), viewLabels[0], _viewMode == 0 ? _bn2 : _btn)) _viewMode = 0;
            if (GUI.Button(new Rect(r1 + 74, y, 70, BTN_H), viewLabels[1], _viewMode == 1 ? _bn2 : _btn)) _viewMode = 1;
            bool groupSel = _detailGroup >= 0 && _detailGroup < Groups.Count;
            if (GUI.Button(new Rect(r1 + 148, y, 130, BTN_H),
                groupSel ? Loc.T("筛选:组合成员", "Filter: group members")
                         : Loc.T("筛选:选中下辖", "Filter: subtree"), _filterSubtree ? _bn2 : _btn))
                _filterSubtree = !_filterSubtree;
            y += BTN_H + GAP;

            // 第 2 排：所有区划 / 市 / 区县 / 乡镇 / 村社区
            float cw2 = (PW - PAD * 2 - 16) / 5f;
            float r2 = PAD;
            for (int i = 2; i < viewLabels.Length; i++)
            {
                bool on = _viewMode == i;
                if (GUI.Button(new Rect(r2, y, cw2, BTN_H), viewLabels[i], on ? _bn2 : _btn))
                    _viewMode = i;
                r2 += cw2 + 4;
            }
            y += BTN_H + GAP;

            // 第 3 排：排序依据
            string[] sortLabels = Loc.IsEn
                ? new string[] { "GDP", "Pop", "GDP/cap", "Land" }
                : new string[] { "GDP", "人口", "人均GDP", "地价" };
            GUI.Label(new Rect(PAD, y, 60, BTN_H), Loc.T("排序:", "Sort: "), _fl);
            float sx = PAD + 60;
            for (int i = 0; i < sortLabels.Length; i++)
            {
                bool on = _sortKey == i;
                if (GUI.Button(new Rect(sx, y, 64, BTN_H), sortLabels[i], on ? _bn2 : _btn))
                    _sortKey = i;
                sx += 68;
            }
            y += BTN_H + GAP;

            // ==== 列表 ====
            Rect list = new Rect(PAD, y, PW - PAD * 2, PH - PAD - y);
            if (_viewMode == 0)
                y = DrawCityTotals(y, PW - PAD * 2); // 层级模式：树上方显示全区合计
            list = new Rect(PAD, y, PW - PAD * 2, PH - PAD - y);
            switch (_viewMode)
            {
                case 1: DrawGroupView(list); break;                    // 组合
                case 2: DrawSortedList(list); break;                   // 所有区划
                case 3:
                case 4:
                case 5:
                case 6: DrawRankingList(list, _viewMode - 2); break;  // 市/区县/乡镇/村社区
                default: DrawTreeList(list); break;                    // 层级
            }

            // 最后绘制（浮在最上层），避免展开的下拉窗口被图例/列表遮挡
            DrawModeDropdown();
        }

        /// <summary>面板右上角现实化数据下拉（标题栏下方 20，与选项联动）。</summary>
        private void DrawModeDropdown()
        {
            string[] names = Loc.IsEn
                ? new string[] { "Vanilla wk", "Vanilla yr", "RMB yr", "USD yr" }
                : new string[] { "原版周化", "原版年化", "人民币年化", "美元年化" };
            int cur = _hub != null && _hub.Settings != null ? _hub.Settings.DisplayMode : 0;
            if (cur < 0 || cur >= names.Length) cur = 0;

            Rect btn = new Rect(PW - 146, PAD + TITLE_H + 20, 136, BTN_H);

            // 下拉框上方：统计模式选择（右移一点）
            GUI.Label(new Rect(btn.x + 12, btn.y - 18, 140, TEXT_H), Loc.T("统计模式选择：", "Statistics mode:"), _fl);

            if (GUI.Button(btn, "📊 " + names[cur], _btn))
                _modeDropOpen = !_modeDropOpen;

            if (_modeDropOpen)
            {
                Color oldBg = GUI.backgroundColor;
                GUI.backgroundColor = Color.black; // 仅展开的下拉窗口背景纯黑
                for (int i = 0; i < names.Length; i++)
                {
                    Rect item = new Rect(btn.x, btn.y + BTN_H * (i + 1) + 2, btn.width, BTN_H);
                    if (GUI.Button(item, names[i] + (i == cur ? " ✓" : ""), _btn))
                    {
                        _modeDropOpen = false;
                        if (i != cur)
                        {
                            _hub.Settings.DisplayMode = i;
                            _hub.Settings.Save();
                            _hub.Calculator.ClearCache();
                            _finDistrict = 0;
                            _lastDisplayMode = i;
                        }
                    }
                }
                GUI.backgroundColor = oldBg;
            }
        }

        private float DrawHeader(float y, float w, string text)
        {
            GUI.Label(new Rect(0, y, w, HEADER_H), text, _hdr);
            return y + HEADER_H;
        }

        /// <summary>层级模式下树上方显示全区合计 GDP / 人均 / 人口。返回新 y。</summary>
        private float DrawCityTotals(float y, float w)
        {
            double[] gdpArr = _hub.Calculator.GetDistrictGDP();
            long[] popArr = _hub.Calculator.GetDistrictPopulation();
            double totalGdp = 0;
            long totalPop = 0;
            ushort[] all = _hub.GetVanillaDistricts();
            foreach (ushort did in all)
            {
                totalGdp += gdpArr[did];
                totalPop += popArr[did];
            }
            double avg = totalPop > 0 ? totalGdp / totalPop : 0;
            GUI.Label(new Rect(PAD, y, w, VALUE_H),
                Loc.T("全区 GDP " + CurrencySymbol(), "City GDP " + CurrencySymbol()) + F(totalGdp) +
                Loc.T("  人均 " + CurrencySymbol(), "  /cap " + CurrencySymbol()) + F(avg) +
                Loc.T("  人口 ", "  pop ") + totalPop.ToString("N0"), _fv);
            return y + VALUE_H + GAP;
        }

        /// <summary>递归收集展开状态下可见的节点（防循环）。</summary>
        private void CollectNodes(ushort id, int depth, List<ushort> ids, List<int> depths, HashSet<ushort> visited)
        {
            if (!visited.Add(id)) return;
            ids.Add(id);
            depths.Add(depth);

            if (_ex.ContainsKey(id) && _ex[id])
                foreach (ushort c in _hub.Hierarchy.GetChildren(id))
                    CollectNodes(c, depth + 1, ids, depths, visited);
        }

        /// <summary>绘制单行树节点（y 由调用方绝对递增传入），非选中节点按聚合 GDP 着色。</summary>
        private void DrawNodeRow(ushort id, int depth, float y, float w, double[] agg)
        {
            float ind = depth * 14f;
            string name = _hub.GetVanillaDistrictName(id);
            int lv = _hub.Hierarchy.LevelOf.ContainsKey(id) ? _hub.Hierarchy.LevelOf[id] : 0;
            string tag = lv > 0 ? ("[" + LevelName(lv) + "] ") : "";

            List<ushort> children = _hub.Hierarchy.GetChildren(id);
            bool has = children.Count > 0;
            bool ex = _ex.ContainsKey(id) && _ex[id];
            string prefix = has ? (ex ? "▼ " : "▶ ") : "  ";

            bool selected = id == _hub.SelectedID;
            Color old = GUI.color;
            if (!selected)
                GUI.color = GdpColor(agg[id]);
            GUIStyle st = selected ? _ts : _rankBtn;
            Rect row = new Rect(ind, y, w - ind, NODE_H);
            if (GUI.Button(row, prefix + tag + name, st))
            {
                SelectDistrict(id);
                if (has) { _ex[id] = !ex; }
            }
            GUI.color = old;
        }

        private float DrawUnassigned(float y, float w, ushort did, string name)
        {
            Rect btn = new Rect(0, y, w, NODE_H);
            if (GUI.Button(btn, "  + " + name, _nodeBtn))
            {
                ushort parent = ResolveParent(_addLevel);
                _hub.Hierarchy.SetParent(did, parent, _addLevel);
                _hub.MarkDirty();
                // 保持当前选中（母区划）不变，方便连续点击「+」把多个区划挂到同一母区划下；
                // 只重置财务缓存让母区划的合计立即刷新，并展开母节点让新子区划立即可见
                _finDistrict = 0;
                if (parent != 0) _ex[parent] = true;
                Debug.Log(string.Format("[DFM] Assigned #{0} to level {1} under parent {2}",
                    did, _addLevel, parent));
            }
            return y + NODE_H;
        }

        /// <summary>层级树视图：已分配层级树 + 未分配列表。</summary>
        private void DrawTreeList(Rect list)
        {
            var ids = new List<ushort>();
            var depths = new List<int>();
            var visited = new HashSet<ushort>();
            foreach (ushort rid in _hub.Hierarchy.GetRootNodes())
                CollectNodes(rid, 0, ids, depths, visited);

            int unassignedCount = 0;
            ushort[] all = _hub.GetVanillaDistricts();
            foreach (ushort did in all)
            {
                if (_hub.Hierarchy.LevelOf.ContainsKey(did)) continue;
                if (string.IsNullOrEmpty(_hub.GetVanillaDistrictName(did))) continue;
                unassignedCount++;
            }

            float contentH = HEADER_H + ids.Count * NODE_H
                           + GAP + HEADER_H + unassignedCount * NODE_H + 24f;

            _scroll = GUI.BeginScrollView(list, _scroll, new Rect(0, 0, list.width - 20, contentH));
            float cy = 0;
            float lw = list.width - 20;

            double[] agg = _hub.Calculator.GetAggregateGDP();
            cy = DrawHeader(cy, lw, Loc.T("— 已分配层级 / Assigned —", "— Assigned hierarchy —"));
            for (int i = 0; i < ids.Count; i++)
                DrawNodeRow(ids[i], depths[i], cy + i * NODE_H, lw, agg);
            cy += ids.Count * NODE_H;

            cy += GAP;
            cy = DrawHeader(cy, lw, Loc.T("— 未分配区划 / Unassigned（点击分配）—", "— Unassigned (click to assign) —"));

            bool any = false;
            foreach (ushort did in all)
            {
                if (_hub.Hierarchy.LevelOf.ContainsKey(did)) continue;
                string nm = _hub.GetVanillaDistrictName(did);
                if (string.IsNullOrEmpty(nm)) continue;
                any = true;
                cy = DrawUnassigned(cy, lw, did, nm);
            }
            if (!any)
                cy = DrawHeader(cy, lw, Loc.T("（无未分配区划）", "(No unassigned districts)"));

            GUI.EndScrollView();
        }

        /// <summary>是否通过筛选：开启筛选时——若选中组合则只保留该组合成员；
        /// 若选中区划则保留其层级下辖及祖先链；两者都无则全通过。</summary>
        private bool PassFilter(ushort did)
        {
            if (!_filterSubtree) return true;
            if (_detailGroup >= 0 && _detailGroup < Groups.Count)
                return Groups[_detailGroup].Members.Contains(did); // 按组合成员过滤
            if (_hub.SelectedID == 0) return true;
            if (did == _hub.SelectedID) return true; // 包含父节点自身
            // 保留选中节点的祖先链（上级直辖条目，如按X市筛选后区县排名里保留X市直辖）
            if (_hub.Hierarchy.IsDescendantOf(_hub.SelectedID, did)) return true;
            return _hub.Hierarchy.IsDescendantOf(did, _hub.SelectedID);
        }

        private static double SortValue(int key, ushort did, double[] gdp, long[] pop, double[] land = null)
        {
            switch (key)
            {
                case 1: return pop[did];
                case 2: return pop[did] > 0 ? gdp[did] / pop[did] : 0.0;
                case 3: return land != null ? land[did] : 0.0;
                default: return gdp[did];
            }
        }

        private string FormatSortValue(double value)
        {
            string cur = CurrencySymbol();
            switch (_sortKey)
            {
                case 1: return value.ToString("N0") + Loc.T(" 人", " pop");
                case 2: return cur + F(value) + Loc.T("/人", "/cap");
                case 3: return value.ToString("0.00") + " " + LandUnit();
                default: return cur + F((long)value);
            }
        }

        private static Color SortColor(int key, double value)
        {
            if (key == 1) return PopColor((long)value);
            if (key == 2) return GdpPerCapitaColor(value);
            if (key == 3) return LandColor(value);
            return GdpColor(value);
        }

        private static string SortLabel(int key)
        {
            switch (key)
            {
                case 1: return Loc.T("人口", "Population");
                case 2: return Loc.T("人均GDP", "GDP/capita");
                case 3: return Loc.T("地价", "Land value");
                default: return "GDP";
            }
        }

        /// <summary>地价分档颜色（0~150，16 档）。</summary>
        private static Color LandColor(double v)
        {
            long[] tiers = GetLandDisplayTiers();
            for (int i = 0; i < tiers.Length; i++)
                if (v < tiers[i]) return TIER_COLORS[i];
            return TIER_COLORS[TIER_COLORS.Length - 1];
        }

        /// <summary>排序视图：所有区划按所选指标（GDP/人口/人均GDP）降序排名，点击查看财务。</summary>
        private void DrawSortedList(Rect list)
        {
            double[] gdp = _hub.Calculator.GetDistrictGDP();
            long[] pop = _hub.Calculator.GetDistrictPopulation();
            double[] landD = LandToDisplay(_hub.Calculator.GetDistrictLandValue());
            ushort[] all = _hub.GetVanillaDistricts();

            var items = new List<KeyValuePair<ushort, double>>();
            foreach (ushort did in all)
            {
                if (string.IsNullOrEmpty(_hub.GetVanillaDistrictName(did))) continue;
                if (!PassFilter(did)) continue;
                items.Add(new KeyValuePair<ushort, double>(did, SortValue(_sortKey, did, gdp, pop, landD)));
            }
            items.Sort((a, b) => b.Value.CompareTo(a.Value)); // 降序

            float contentH = HEADER_H + items.Count * NODE_H + 24f;
            _sortScroll = GUI.BeginScrollView(list, _sortScroll, new Rect(0, 0, list.width - 20, contentH));
            float lw = list.width - 20;
            float cy = DrawHeader(0, lw,
                Loc.T("— 各区划 " + SortLabel(_sortKey) + " 排名（降序，点击查看）—",
                      "— All districts by " + SortLabel(_sortKey) + " (desc, click to view) —"));

            for (int i = 0; i < items.Count; i++)
            {
                ushort did = items[i].Key;
                string name = _hub.GetVanillaDistrictName(did);
                bool selected = did == _hub.SelectedID;
                string line = (selected ? "▶ " : "  ")
                    + string.Format("{0}. {1}    {2}", i + 1, name, FormatSortValue(items[i].Value));
                Color old = GUI.color;
                GUI.color = SortColor(_sortKey, items[i].Value);
                Rect btn = new Rect(0, cy + i * NODE_H, lw, NODE_H);
                if (GUI.Button(btn, line, _rankBtn))
                {
                    SelectDistrict(did);
                }
                GUI.color = old;
            }
            GUI.EndScrollView();
        }

        private struct RankEntry
        {
            public ushort id;
            public double value;
            public bool parentLevel; // 是否为上一级节点（直辖/附加参考）
        }

        /// <summary>
        /// 单级排名视图：指定层级的区划按聚合指标（GDP/人口/人均GDP）降序排名。
        /// 额外加入上一级（N-1 级）节点作为「直辖」附加条目一起排序，但不占排名号。
        /// </summary>
        private void DrawRankingList(Rect list, int level)
        {
            double[] agg = _hub.Calculator.GetAggregateGDP();
            long[] aggPop = _hub.Calculator.GetAggregatePopulation();
            double[] selfGdp = _hub.Calculator.GetDistrictGDP();
            long[] selfPop = _hub.Calculator.GetDistrictPopulation();
            double[] aggLandD = LandToDisplay(_hub.Calculator.GetAggregateLandValue());
            double[] selfLandD = LandToDisplay(_hub.Calculator.GetDistrictLandValue());

            var items = new List<RankEntry>();
            foreach (ushort did in _hub.Hierarchy.GetDistrictsByLevel(level))
            {
                if (string.IsNullOrEmpty(_hub.GetVanillaDistrictName(did))) continue;
                if (!PassFilter(did)) continue;
                items.Add(new RankEntry { id = did, value = SortValue(_sortKey, did, agg, aggPop, aggLandD), parentLevel = false });
            }

            // 加入上一级节点（直辖）：数值用其自身，不聚合
            bool includeDirect = _hub != null && _hub.Settings != null && _hub.Settings.IncludeDirect;
            if (level > DistLevel.REGION && includeDirect)
            {
                int parentLevel = level - 1;
                foreach (ushort did in _hub.Hierarchy.GetDistrictsByLevel(parentLevel))
                {
                    if (string.IsNullOrEmpty(_hub.GetVanillaDistrictName(did))) continue;
                    if (!PassFilter(did)) continue;
                    items.Add(new RankEntry { id = did, value = SortValue(_sortKey, did, selfGdp, selfPop, selfLandD), parentLevel = true });
                }
            }

            items.Sort((a, b) => b.value.CompareTo(a.value)); // 降序

            float contentH = HEADER_H + items.Count * NODE_H + 24f;
            _sortScroll = GUI.BeginScrollView(list, _sortScroll, new Rect(0, 0, list.width - 20, contentH));
            float lw = list.width - 20;
            string head = Loc.T("— " + LevelName(level) + " 排名（聚合" + SortLabel(_sortKey) + "，点击查看）—",
                                "— " + LevelName(level) + " ranking (aggregate " + SortLabel(_sortKey) + ", click to view) —");
            float cy = DrawHeader(0, lw, head);

            int rank = 0;
            for (int i = 0; i < items.Count; i++)
            {
                ushort did = items[i].id;
                string name = _hub.GetVanillaDistrictName(did);
                bool selected = did == _hub.SelectedID;
                rank++;
                string label = items[i].parentLevel
                    ? (name + Loc.T("直辖", " Direct-admin"))
                    : name;
                string line = (selected ? "▶ " : "  ")
                    + string.Format("{0}. {1}    {2}", rank, label, FormatSortValue(items[i].value));
                Color old = GUI.color;
                GUI.color = SortColor(_sortKey, items[i].value);
                Rect btn = new Rect(0, cy + i * NODE_H, lw, NODE_H);
                if (GUI.Button(btn, line, _rankBtn))
                {
                    SelectDistrict(did);
                }
                GUI.color = old;
            }
            GUI.EndScrollView();
        }

        /// <summary>层级中文名。</summary>
        private static string LevelName(int level)
        {
            switch (level)
            {
                case DistLevel.REGION: return Loc.T("市", "City");
                case DistLevel.DISTRICT: return Loc.T("区县", "District");
                case DistLevel.NEIGHBOR: return Loc.T("乡镇", "Town");
                case DistLevel.VILLAGE: return Loc.T("村社区", "Village");
                default: return "?";
            }
        }

        /// <summary>组合视图：创建/命名组合、添加成员区划；组合按成员自身值合计排序；不影响层级。</summary>
        private void DrawGroupView(Rect list)
        {
            double[] gdp = _hub.Calculator.GetDistrictGDP();
            long[] pop = _hub.Calculator.GetDistrictPopulation();
            long[] landRaw = _hub.Calculator.GetDistrictLandValue(); // 组合地价按成员面积加权平均
            long[] areaRaw = _hub.Calculator.GetDistrictArea();
            ushort[] all = _hub.GetVanillaDistricts();
            float lw = list.width - 20;
            float x0 = list.x;
            float cy = list.y;

            // 标题
            GUI.Label(new Rect(x0, cy, lw, HEADER_H),
                Loc.T("— 组合（成员自身值合计，不影响层级）—",
                      "— Groups (sum of member own values, hierarchy unaffected) —"), _hdr);
            cy += HEADER_H + GAP;

            // 命名行：主面板内自绘输入框（中文输入）
            GUI.Label(new Rect(x0, cy, 66, BTN_H), Loc.T("组合名:", "Name:"), _fl);
            string nameStr = _groupNameInput;
            Rect nameFld = new Rect(x0 + 70, cy, lw - 156, BTN_H);
            GUI.SetNextControlName("DFMGroupNameField");
            nameStr = GUI.TextField(nameFld, nameStr, 40);
            _groupNameInput = nameStr;
            bool nameFocused = GUI.GetNameOfFocusedControl() == "DFMGroupNameField";
            if (nameFocused)
                TrackImeCaret(nameFld, nameStr); // IME 候选窗跟随面板/输入框
            bool create = GUI.Button(new Rect(x0 + lw - 82, cy, 78, BTN_H), Loc.T("新建", "Create"), _btn);
            // 回车也创建
            if (nameFocused && (Event.current.type == EventType.KeyDown)
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
                create = true;
            if (create)
            {
                string n = _groupNameInput.Trim();
                if (n.Length > 0)
                {
                    Groups.Add(new GroupData { Name = n });
                    _activeGroupIdx = Groups.Count - 1;
                    _memberExpand = true;
                    _sortScroll = Vector2.zero;
                    _finDistrict = 0;
                    _groupNameInput = "";
                    if (_hub != null) _hub.MarkDirty();
                    GUI.FocusControl("");
                }
            }
            cy += BTN_H + GAP;

            // 组合排序列表（固定；右键组合名收起/展开成员）
            var order = new List<int>();
            for (int i = 0; i < Groups.Count; i++) order.Add(i);
            order.Sort((a, b) => GroupValue(b, gdp, pop, landRaw, areaRaw).CompareTo(GroupValue(a, gdp, pop, landRaw, areaRaw)));

            int toDelete = -1;
            for (int r = 0; r < order.Count; r++)
            {
                int gi = order[r];
                GroupData g = Groups[gi];
                bool active = gi == _activeGroupIdx;
                double gval = GroupValue(gi, gdp, pop, landRaw, areaRaw);
                string line = (active ? "▶ " : "  ") + (r + 1) + ". " + g.Name
                    + "  " + FormatSortValue(gval)
                    + "  " + Loc.T("成员", "mem") + g.Members.Count;
                Rect rowRect = new Rect(x0, cy, lw - 146, NODE_H);
                Color old = GUI.color;
                if (!active) GUI.color = SortColor(_sortKey, gval);
                if (GUI.Button(rowRect, line, active ? _ts : _rankBtn))
                {
                    _activeGroupIdx = active ? -1 : gi; // 保持原“展开/收起成员”交互
                    if (gi != _detailGroup) SelectGroup(gi); // 点击组合 → 顶部显示该组合合计
                }
                GUI.color = old;
                if (GUI.Button(new Rect(x0 + lw - 142, cy, 66, NODE_H), Loc.T("删除", "Del"), _btn))
                    toDelete = gi;
                if (IsPanelRightClick(rowRect))
                {
                    _activeGroupIdx = gi;
                    _memberExpand = !_memberExpand;
                }
                cy += NODE_H;
            }
            if (toDelete >= 0)
            {
                Groups.RemoveAt(toDelete);
                if (_activeGroupIdx >= toDelete) _activeGroupIdx--;
                if (_activeGroupIdx >= Groups.Count) _activeGroupIdx = -1;
                if (_detailGroup == toDelete) _detailGroup = -1;       // 删除的是详情组合 → 清空
                else if (_detailGroup > toDelete) _detailGroup--;
                if (_hub != null) _hub.MarkDirty();
            }
            cy += GAP;

            // 成员选择滚动区
            float remainH = (list.y + list.height) - cy - 6f;
            if (remainH > 20f)
            {
                int memberRows = 0;
                if (_memberExpand && _activeGroupIdx >= 0 && _activeGroupIdx < Groups.Count)
                    foreach (ushort did in all)
                        if (!string.IsNullOrEmpty(_hub.GetVanillaDistrictName(did))) memberRows++;
                float contentH = HEADER_H + (memberRows > 0 ? memberRows * NODE_H : 1) + 20f;
                Rect memberRect = new Rect(x0, cy, lw, remainH);
                _sortScroll = GUI.BeginScrollView(memberRect, _sortScroll, new Rect(0, 0, lw, contentH));
                float mcy = 0;
                if (_memberExpand && _activeGroupIdx >= 0 && _activeGroupIdx < Groups.Count)
                {
                    GroupData ag = Groups[_activeGroupIdx];
                    mcy = DrawHeader(mcy, lw, "▼ " + ag.Name + " — " +
                        Loc.T("点击区划加入/移除（右键组合名收起）", "click districts to add/remove (right-click name to collapse)"));
                    foreach (ushort did in all)
                    {
                        string nm = _hub.GetVanillaDistrictName(did);
                        if (string.IsNullOrEmpty(nm)) continue;
                        bool inG = ag.Members.Contains(did);
                        string lb = (inG ? "☑ " : "☐ ") + nm;
                        if (GUI.Button(new Rect(0, mcy, lw, NODE_H), lb, inG ? _ts : _rankBtn))
                        {
                            if (inG) ag.Members.Remove(did); else ag.Members.Add(did);
                            if (_hub != null) _hub.MarkDirty();
                        }
                        mcy += NODE_H;
                    }
                }
                else
                {
                    mcy = DrawHeader(mcy, lw,
                        Loc.T("（未展开 —— 右键组合名展开/收起成员列表）",
                              "(collapsed - right-click a group name to expand/collapse)"));
                }
                GUI.EndScrollView();
            }
        }

        /// <summary>弹窗输入框聚焦时，手动转发键盘字符到 UITextField（绕过 IMGUI 键盘焦点限制）。</summary>
        private void ForwardNameInput()
        {
            if (_nameDlg == null || _nameTf == null || !_nameDlg.isVisible) return;
            if (!_nameTf.hasFocus) return;
            Event ev = Event.current;
            if (ev.type == EventType.KeyDown)
            {
                if (ev.keyCode == KeyCode.Backspace)
                {
                    if (_nameTf.text.Length > 0)
                        _nameTf.text = _nameTf.text.Substring(0, _nameTf.text.Length - 1);
                    SyncCaretToEnd();
                    ev.Use();
                }
                else if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
                {
                    ConfirmGroupName();
                    ev.Use();
                }
                else if (ev.keyCode == KeyCode.Escape)
                {
                    HideNameDialog();
                    ev.Use();
                }
                else if (ev.character != 0 && !char.IsControl(ev.character))
                {
                    // 字母/数字/空格/标点，以及 IME commit 的中文字符
                    _nameTf.text += ev.character;
                    SyncCaretToEnd();
                    ev.Use();
                }
            }
        }

        /// <summary>在主面板 OnGUI 自绘闪烁光标（画在命名输入框文本末尾，屏幕坐标）。</summary>
        private void DrawNameCaret()
        {
            if (_nameDlg == null || _nameTf == null || !_nameDlg.isVisible) return;
            if (!_nameTf.hasFocus) return;
            if ((int)(Time.time * 2f) % 2 != 0) return; // 半秒闪烁

            Vector3 baseP = _nameTf.absolutePosition; // 输入框自身屏幕位置
            string t = _nameTf.text;
            float tw = 0f;
            foreach (char c in t)
                tw += (c > 127) ? 22f : 12f; // 中文/英文粗略宽度
            float cx = baseP.x + 558f + tw; // 输入框文本起点校准
            float cy = baseP.y + 37f;       // 纵向校准
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(cx, cy, 2f, _nameTf.height - 6f), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        /// <summary>
        /// 把输入法（IME）组合光标位置同步到组合名输入框内，让中文输入法的
        /// 候选/拼音窗口跟随面板显示，而不是固定在屏幕某个角落。
        /// 面板可拖动/缩放，故每帧按当前面板位置重新计算。
        /// </summary>
        private void TrackImeCaret(Rect fldRect, string text)
        {
            try
            {
                if (_scale <= 0f) return;
                // 面板局部坐标 → 屏幕坐标（面板位移 + 缩放）
                float sx = _panelPos.x + fldRect.x * _scale;
                float sy = _panelPos.y + fldRect.y * _scale;

                // 文本左内边距 + 文本宽度（按当前 GUI 字体测量后乘缩放）
                float pad = 4f * _scale;
                float tw = 0f;
                GUIStyle ts = GUI.skin.textField;
                if (ts != null && !string.IsNullOrEmpty(text))
                    tw = ts.CalcSize(new GUIContent(text)).x * _scale;

                // compositionCursorPos 使用左下原点（屏幕底部 = 0）坐标
                float caretX = sx + pad + tw;
                float caretY = Screen.height - (sy + fldRect.height * _scale - 2f);
                Input.compositionCursorPos = new Vector2(caretX, caretY);
            }
            catch { }
        }

        /// <summary>手动输入后把光标同步到文本末尾（否则光标不跟随输入）。</summary>
        private void SyncCaretToEnd()
        {
            if (_nameTf == null) return;
            int len = _nameTf.text.Length;
            try
            {
                _nameTf.selectionStart = len;
                _nameTf.selectionEnd = len;
            }
            catch { }
        }

        /// <summary>检测右键点击了主面板内某面板坐标区域（组合行用，不随滚动）。</summary>
        private bool IsPanelRightClick(Rect panelRect)
        {
            Event ev = Event.current;
            if (ev.type != EventType.MouseDown || ev.button != 1) return false;
            Rect s = new Rect(_panelPos.x + panelRect.x * _scale,
                _panelPos.y + panelRect.y * _scale,
                panelRect.width * _scale, panelRect.height * _scale);
            if (s.Contains(ev.mousePosition))
            {
                ev.Use();
                return true;
            }
            return false;
        }

        /// <summary>创建原生命名弹窗（ColossalFramework UI，支持中文 IME）。</summary>
        private void EnsureNameDialog()
        {
            if (_nameDlg != null) return;
            UIView view = UIView.GetAView();
            _nameDlg = view.AddUIComponent(typeof(UIPanel)) as UIPanel; // UIView 用非泛型 AddUIComponent
            _nameDlg.name = "DFMGroupName";
            _nameDlg.width = 340f;
            _nameDlg.height = 130f;
            _nameDlg.backgroundSprite = "GenericPanel";
            _nameDlg.opacity = 0.96f;
            _nameDlg.isInteractive = true; // 关键：父面板必须 interactive 子控件才能接收点击
            _nameDlg.relativePosition = new Vector3(Screen.width / 2f - 170f, Screen.height / 2f - 75f, 0f);

            _nameTitleLb = _nameDlg.AddUIComponent<UILabel>();
            _nameTitleLb.text = Loc.T("组合名：", "Group name:");
            _nameTitleLb.textScale = 1.1f;
            _nameTitleLb.textColor = new Color32(255, 255, 255, 255);
            _nameTitleLb.relativePosition = new Vector3(12f, 10f, 0f);

            _nameTf = _nameDlg.AddUIComponent<UITextField>();
            _nameTf.width = 316f;
            _nameTf.height = 32f;
            _nameTf.relativePosition = new Vector3(12f, 40f, 0f);
            _nameTf.padding = new RectOffset(6, 6, 7, 4);
            _nameTf.textScale = 1f;
            // 背景用已确认存在的 GenericPanel，保证有渲染可命中点击
            _nameTf.normalBgSprite = "GenericPanel";
            _nameTf.hoveredBgSprite = "GenericPanel";
            _nameTf.focusedBgSprite = "GenericPanel";
            _nameTf.horizontalAlignment = UIHorizontalAlignment.Left;
            _nameTf.canFocus = true;
            _nameTf.isInteractive = true;
            _nameTf.enabled = true;
            _nameTf.readOnly = false;
            _nameTf.submitOnFocusLost = false;
            _nameTf.selectOnFocus = false; // 聚焦不全选，让光标正常显示
            _nameTf.eventMouseDown += (c, p) => { _nameTf.Focus(); }; // 点击强制聚焦
            // 输入光标提示（闪烁光标即聚焦提示）
            _nameTf.cursorBlinkTime = 0.4f;
            _nameTf.cursorWidth = 2;
            _nameTf.eventGotFocus += (comp, p) => { };
            _nameTf.eventTextSubmitted += (comp, txt) => { ConfirmGroupName(); };

            _nameHintLb = _nameDlg.AddUIComponent<UILabel>();
            _nameHintLb.text = Loc.T("输入组合名，按回车确认", "Type a group name, press Enter to confirm");
            _nameHintLb.textScale = 1f;
            _nameHintLb.textColor = new Color32(255, 255, 255, 255);
            _nameHintLb.relativePosition = new Vector3(12f, 92f, 0f);

            _nameDlg.Hide();
        }

        private void ShowNameDialog()
        {
            EnsureNameDialog();
            // 屏幕正上方（顶部居中）显示
            _nameDlg.relativePosition = new Vector3(Screen.width / 2f - 170f, 16f, 0f);
            _nameDlg.Show();
            _nameDlg.BringToFront();
            // 每次打开刷新语言文本（切换语言后仍正确）
            if (_nameTitleLb != null)
                _nameTitleLb.text = Loc.T("组合名：", "Group name:");
            if (_nameHintLb != null)
                _nameHintLb.text = Loc.T("输入组合名，按回车确认", "Type a group name, press Enter to confirm");
            if (_nameTf != null)
            {
                _nameTf.text = "";
                _nameTf.Focus();
            }
        }

        private void HideNameDialog()
        {
            if (_nameDlg != null) _nameDlg.Hide();
        }

        private void ConfirmGroupName()
        {
            if (_nameTf == null) return;
            string n = _nameTf.text.Trim();
            Debug.Log("[DFM] Confirm name: '" + n + "' len=" + n.Length + " groups=" + Groups.Count);
            HideNameDialog();
            if (n.Length > 0)
            {
                Groups.Add(new GroupData { Name = n });
                _activeGroupIdx = Groups.Count - 1;
                _memberExpand = true;
                _sortScroll = Vector2.zero; // 滚动复位，确保成员选择框可见
                _finDistrict = 0;
                if (_hub != null) _hub.MarkDirty();
            }
        }

        /// <summary>组合的统计值（按排序依据）：GDP/人口为成员求和，人均=和/和；
        /// 地价为成员面积加权平均地价再随模式换算显示（避免把地价当 GDP 求和导致异常高）。</summary>
        private double GroupValue(int idx, double[] gdp, long[] pop, long[] landRaw, long[] areaRaw)
        {
            GroupData g = Groups[idx];

            // 地价：面积加权平均（kr/m²），×LandMult 后与列表/图例同一口径
            if (_sortKey == 3)
            {
                double lsum = 0, asum = 0;
                foreach (ushort m in g.Members)
                {
                    if (m >= landRaw.Length || m >= areaRaw.Length) continue;
                    long a = areaRaw[m];
                    if (a <= 0) continue;
                    lsum += landRaw[m] * a;
                    asum += a;
                }
                return asum > 0 ? (lsum / asum) * LandMult() : 0;
            }

            double sg = 0, sp = 0;
            foreach (ushort m in g.Members)
            {
                sg += gdp[m];
                sp += pop[m];
            }
            switch (_sortKey)
            {
                case 1: return sp;
                case 2: return sp > 0 ? sg / sp : 0;
                default: return sg;
            }
        }

        /// <summary>选中一个区划：取消组合选择。</summary>
        private void SelectDistrict(ushort id)
        {
            _detailGroup = -1;
            _hub.SelectedID = id;
            _finDistrict = 0;
        }

        /// <summary>清除当前选择（区划 + 组合）。</summary>
        private void ClearSelection()
        {
            _detailGroup = -1;
            _hub.SelectedID = 0;
            _finDistrict = 0;
        }

        /// <summary>选中一个组合，顶部显示其合计详情（清除区划选择）。</summary>
        private void SelectGroup(int gi)
        {
            _hub.SelectedID = 0;
            _finDistrict = 0;
            _detailGroup = gi;
        }

        /// <summary>组合成员自身值的合计：GDP/人口/工人/建筑/面积（只显示合计用）。</summary>
        private bool ComputeGroupTotals(int gi, out double gdp, out long pop,
            out int workers, out int buildings, out long area)
        {
            gdp = 0; pop = 0; workers = 0; buildings = 0; area = 0;
            if (_hub == null || _hub.Calculator == null) return false;
            if (gi < 0 || gi >= Groups.Count) return false;
            GroupData g = Groups[gi];
            foreach (ushort m in g.Members)
            {
                DistrictFinanceCalculator.FinanceResult c = _hub.Calculator.Calculate(m);
                if (!c.IsValid) continue;
                gdp += c.GDP;
                pop += c.Population;
                workers += c.Workers;
                buildings += c.BuildingCount;
                area += c.Area;
            }
            return g.Members.Count > 0;
        }

        private ushort ResolveParent(int level)
        {
            if (level == DistLevel.REGION) return 0;

            ushort sel = _hub.SelectedID;
            if (sel == 0) return 0;
            if (!_hub.Hierarchy.LevelOf.ContainsKey(sel)) return 0;

            int selLevel = _hub.Hierarchy.LevelOf[sel];
            // 父节点层级需高于子节点，允许跨级（市可直挂区县/乡镇/村社区等）
            if (selLevel < level) return sel;
            return 0;
        }

        private void RemoveSelected()
        {
            ushort id = _hub.SelectedID;
            if (id == 0) return;
            _hub.Hierarchy.Remove(id);
            _hub.MarkDirty();
            ClearSelection();
            _fin = new DistrictFinanceCalculator.FinanceResult();
        }

        #endregion

        #region Tool district detection

        private static MemberInfo _districtMember;

        private static MemberInfo GetDistrictMember()
        {
            if ((object)_districtMember == null)
            {
                const BindingFlags flags = BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo f = typeof(DistrictTool).GetField("m_district", flags);
                if ((object)f != null) _districtMember = f;
                else _districtMember = typeof(DistrictTool).GetProperty("districtID", flags);
            }
            return _districtMember;
        }

        private static byte GetToolDistrict()
        {
            try
            {
                DistrictTool tool = ToolsModifierControl.toolController.CurrentTool as DistrictTool;
                if (tool == null) return 0;
                MemberInfo m = GetDistrictMember();
                if ((object)m == null) return 0;
                object v = m is FieldInfo
                    ? ((FieldInfo)m).GetValue(tool)
                    : ((PropertyInfo)m).GetValue(tool, null);
                if (v is byte) return (byte)v;
                if (v is ushort) return (byte)(ushort)v;
                if (v is int) return (byte)(int)v;
                if (v is uint) return (byte)(uint)v;
            }
            catch { }
            return 0;
        }

        #endregion

        #region Styles

        private static string F(double n)
        {
            if (n < 0) return "-" + F(-n);
            if (n >= 1000000000) return (n / 1000000000d).ToString("F2") + "B";
            if (n >= 1000000) return (n / 1000000d).ToString("F2") + "M";
            if (n >= 1000) return (n / 1000d).ToString("F2") + "K";
            return n.ToString("F2");
        }

        /// <summary>面积格数 → km² 文本（1 格 = 64 m²）。小于 0.1 km² 保留三位小数避免显示成 0.00。</summary>
        private static string AreaKm2(long cells)
        {
            if (cells <= 0) return "0";
            double km2 = cells * 64.0 / 1000000.0;
            if (km2 >= 100) return km2.ToString("0.0");
            if (km2 >= 0.1) return km2.ToString("0.00");
            return km2.ToString("0.000");
        }

        /// <summary>选中组合时在顶部绘制合计详情（只显示合计，不区分自身/下辖）。返回新 y。</summary>
        private float DrawGroupSummary(float x, float y, float w)
        {
            if (_detailGroup < 0 || _detailGroup >= Groups.Count) return y;
            GroupData g = Groups[_detailGroup];
            string gname = string.IsNullOrEmpty(g.Name) ? Loc.T("未命名", "unnamed") : g.Name;
            GUI.Label(new Rect(x, y, w, TITLE_H), "📊 " + Loc.T("组合 · ", "Group · ") + gname, _ti);
            y += TITLE_H;

            double gdp; long pop; int workers, buildings; long area;
            bool any = ComputeGroupTotals(_detailGroup, out gdp, out pop, out workers, out buildings, out area);
            if (!any)
            {
                GUI.Label(new Rect(x, y, w, TEXT_H), Loc.T("（空组合，点击下方区划加入）", "(empty — click districts below to add)"), _fl);
                y += TEXT_H + GAP;
                return y;
            }

            double pcap = pop > 0 ? gdp / pop : 0.0;
            DrawGdp(new Rect(x, y, w, VALUE_H),
                Loc.T("合计 GDP " + CurrencySymbol(), "Total GDP " + CurrencySymbol()), gdp);
            y += VALUE_H;
            DrawGdpPerCapita(new Rect(x, y, w, VALUE_H),
                Loc.T("合计 人均GDP " + CurrencySymbol(), "Total GDP/cap " + CurrencySymbol()), pcap, (int)pop);
            y += VALUE_H;
            DrawPopulationLine(new Rect(x, y, w, TEXT_H),
                Loc.T("合计 人口 ", "Total pop "), (int)pop, workers, buildings);
            y += TEXT_H;
            GUI.Label(new Rect(x, y, w, TEXT_H),
                Loc.T("合计 面积 ", "Total area ") + AreaKm2(area) + Loc.T(" km²", " km²"), _fl);
            y += TEXT_H + GAP;
            return y;
        }

        /// <summary>绘制人均GDP（K/M/G 简写，按数值分档着色；人口为 0 时显示灰色“—”）。</summary>
        private void DrawGdpPerCapita(Rect r, string label, double perCapita, int population)
        {
            Color old = GUI.color;
            GUI.color = population > 0
                ? GdpPerCapitaColor(perCapita)
                : new Color(0.55f, 0.55f, 0.6f);
            GUI.Label(r, label + (population > 0 ? F(perCapita) : "—"), _pcv);
            GUI.color = old;
        }

        /// <summary>绘制 GDP（浮点，按数值分档着色）。</summary>
        private void DrawGdp(Rect r, string label, double gdp)
        {
            Color old = GUI.color;
            GUI.color = GdpColor(gdp);
            GUI.Label(r, label + F(gdp), _pcv);
            GUI.color = old;
        }

        /// <summary>绘制人口行：人口数字按人口16色分级着色，工作/建筑为普通文字。</summary>
        private void DrawPopulationLine(Rect r, string prefix, int population, int workers, int buildings)
        {
            string popText = prefix + population.ToString("N0");
            Color old = GUI.color;
            GUI.color = PopColor(population);
            GUI.Label(new Rect(r.x, r.y, r.width, r.height), popText, _pcv);
            GUI.color = old;

            float w = _pcv.CalcSize(new GUIContent(popText)).x;
            GUI.Label(new Rect(r.x + w, r.y, r.width - w, r.height),
                Loc.T("  工作 ", "  Workers ") + workers.ToString("N0") +
                Loc.T("  建筑 ", "  Buildings ") + buildings.ToString("N0"), _fl);
        }

        /// <summary>绘制净收入（非负绿色，负红色）。</summary>
        /* 支出/收入/净收入暂时注释掉
        private void DrawNetIncome(Rect r, string label, long value)
        {
            Color old = GUI.color;
            GUI.color = value >= 0
                ? new Color(0.35f, 0.9f, 0.45f)
                : new Color(1f, 0.4f, 0.4f);
            GUI.Label(r, label + F(value), _pcv);
            GUI.color = old;
        }
        */

        /// <summary>人均GDP 分档颜色：深红→红→橙红→橙→金黄→黄绿→绿→青→亮蓝→蓝→紫，逐档递增。</summary>
        private static Color GdpPerCapitaColor(double perCapita)
        {
            long[] tiers = GetDisplayPCapTiers();
            for (int i = 0; i < tiers.Length; i++)
                if (perCapita < tiers[i]) return TIER_COLORS[i];
            return TIER_COLORS[TIER_COLORS.Length - 1];
        }

        /// <summary>地价换算系数：原版周化/年化=1（kr/m²）；人民币年化=×420、美元年化=×60（RMB/USD 每平米）。</summary>
        private static double LandMult()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Settings != null)
            {
                if (hub.Settings.DisplayMode == 2) return 420.0;
                if (hub.Settings.DisplayMode == 3) return 60.0;
            }
            return 1.0;
        }

        /// <summary>地价单位：原版周化/年化 kr/m²；人民币/美元每平米。</summary>
        private static string LandUnit()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Settings != null)
            {
                if (hub.Settings.DisplayMode == 2) return Loc.T("RMB/m²", "RMB/m²");
                if (hub.Settings.DisplayMode == 3) return Loc.T("USD/m²", "USD/m²");
            }
            return Loc.T("kr/m²", "kr/m²");
        }

        /// <summary>把 long[] 地价换算成显示值 double[]（随模式）。</summary>
        private double[] LandToDisplay(long[] land)
        {
            double f = LandMult();
            double[] r = new double[land.Length];
            for (int i = 0; i < land.Length; i++) r[i] = land[i] * f;
            return r;
        }
        /// <summary>把 double[]（加权）地价换算成显示值 double[]。</summary>
        private double[] LandToDisplay(double[] land)
        {
            double f = LandMult();
            if (f == 1.0) return land;
            double[] r = new double[land.Length];
            for (int i = 0; i < land.Length; i++) r[i] = land[i] * f;
            return r;
        }

        /// <summary>换算后的地价分档阈值（随模式）。</summary>
        private static long[] GetLandDisplayTiers()
        {
            double f = LandMult();
            if (f == 1.0) return LAND_TIERS;
            long[] t = new long[LAND_TIERS.Length];
            for (int i = 0; i < t.Length; i++) t[i] = (long)(LAND_TIERS[i] * f);
            return t;
        }

        /// <summary>货币符号：原版(kr 瑞典克朗)、人民币(¥)、美元($)。</summary>
        private static string CurrencySymbol()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Settings != null)
            {
                switch (hub.Settings.DisplayMode)
                {
                    case 0: return "kr";   // 原版周化（游戏货币瑞典克朗）
                    case 1: return "kr";   // 原版年化
                    case 2: return "¥";    // 人民币
                    default: return "$";   // 美元
                }
            }
            return "kr";
        }

        /// <summary>换算后的 GDP 分档阈值（按现实化系数缩放）。</summary>
        private static long[] GetDisplayGDPTiers()
        {
            double f = DistrictFinanceCalculator.GetDisplayFactor();
            if (f == 1.0) return GDP_TIERS;
            long[] t = new long[GDP_TIERS.Length];
            for (int i = 0; i < t.Length; i++) t[i] = (long)(GDP_TIERS[i] * f);
            return t;
        }

        /// <summary>换算后的人均GDP 分档阈值。</summary>
        private static long[] GetDisplayPCapTiers()
        {
            double f = DistrictFinanceCalculator.GetDisplayFactor();
            if (f == 1.0) return PCAP_TIERS;
            long[] t = new long[PCAP_TIERS.Length];
            for (int i = 0; i < t.Length; i++) t[i] = (long)(PCAP_TIERS[i] * f);
            return t;
        }

        /// <summary>GDP 分档颜色：与人均GDP 共用同一套 TIER_COLORS 渐变（用换算后阈值）。</summary>
        private static Color GdpColor(double gdp)
        {
            long[] tiers = GetDisplayGDPTiers();
            for (int i = 0; i < tiers.Length; i++)
                if (gdp < tiers[i]) return TIER_COLORS[i];
            return TIER_COLORS[TIER_COLORS.Length - 1];
        }

        /// <summary>人口分档颜色：复用同一套 TIER_COLORS 渐变。</summary>
        private static Color PopColor(long pop)
        {
            for (int i = 0; i < POP_TIERS.Length; i++)
                if (pop < POP_TIERS[i]) return TIER_COLORS[i];
            return TIER_COLORS[TIER_COLORS.Length - 1];
        }

        /// <summary>绘制颜色图例：16 档颜色色块 + 各档阈值下限。返回新的 y。</summary>
        private float DrawLegend(float x, float y, float w, string title, long[] tiers)
        {
            GUI.Label(new Rect(x, y, w, HEADER_H), title, _hdr);
            y += HEADER_H;

            const int COLS = 8;
            int total = tiers.Length + 1; // 16 档颜色
            float sw = w / COLS;
            float sh = 12f;
            float th = 13f;

            for (int i = 0; i < total; i++)
            {
                int row = i / COLS;
                int col = i % COLS;
                float xx = x + col * sw;
                float yy = y + row * (sh + th);

                Color old = GUI.color;
                GUI.color = TIER_COLORS[i];
                GUI.DrawTexture(new Rect(xx, yy, sw - 2f, sh), Texture2D.whiteTexture);
                GUI.color = old;

                GUI.Label(new Rect(xx, yy + sh + 1, sw, th), LegendLabel(tiers, i), _legend);
            }
            return y + 2 * (sh + th);
        }

        private static string LegendLabel(long[] tiers, int idx)
        {
            if (idx == 0) return "<" + F(tiers[0]);
            if (idx >= tiers.Length) return "≥" + F(tiers[tiers.Length - 1]);
            return F(tiers[idx - 1]);
        }

        private void MakeStyles()
        {
            if (_styled) return;

            _ti = MakeLabel(15, FontStyle.Bold, Color.white);
            _fl = MakeLabel(12, FontStyle.Normal, new Color(0.82f, 0.82f, 0.87f));
            _fv = MakeLabel(13, FontStyle.Bold, new Color(1f, 0.85f, 0.3f));
            _pcv = MakeLabel(13, FontStyle.Bold, Color.white); // 人均GDP，颜色由 GUI.color 分档
            _hdr = MakeLabel(12, FontStyle.Bold, new Color(0.75f, 0.75f, 0.85f));
            _diag = MakeLabel(10, FontStyle.Normal, new Color(0.65f, 0.65f, 0.72f));
            _legend = MakeLabel(9, FontStyle.Normal, new Color(0.72f, 0.72f, 0.78f));

            _nodeBtn = MakeRowStyle(13, new Color(0.82f, 0.82f, 0.88f));
            _ts = MakeRowStyle(13, new Color(0.7f, 0.85f, 1f));
            _rankBtn = MakeRowStyle(13, Color.white); // 白色文字，用于排名行按 GDP 着色

            _btn = MakeButtonStyle(12, Color.white);
            _bn2 = MakeButtonStyle(12, Color.yellow);
            _bn2.normal.background = Tex(new Color(0.25f, 0.5f, 0.25f));

            _styled = true;
        }

        private static GUIStyle MakeLabel(int fontSize, FontStyle style, Color color)
        {
            GUIStyle s = new GUIStyle(GUI.skin.label);
            s.fontSize = fontSize;
            s.fontStyle = style;
            s.normal.textColor = color;
            s.alignment = TextAnchor.MiddleLeft;
            s.wordWrap = false;
            s.clipping = TextClipping.Clip;
            s.padding = new RectOffset(0, 0, 0, 0);
            s.margin = new RectOffset(0, 0, 0, 0);
            s.overflow = new RectOffset(0, 0, 0, 0);
            return s;
        }

        private static GUIStyle MakeRowStyle(int fontSize, Color textColor)
        {
            GUIStyle s = new GUIStyle(GUI.skin.label);
            s.fontSize = fontSize;
            s.fontStyle = FontStyle.Normal;
            s.normal.textColor = textColor;
            s.hover.textColor = textColor;
            s.active.textColor = textColor;
            s.alignment = TextAnchor.MiddleLeft;
            s.wordWrap = false;
            s.clipping = TextClipping.Clip;
            s.padding = new RectOffset(4, 4, 0, 0);
            s.margin = new RectOffset(0, 0, 0, 0);
            s.overflow = new RectOffset(0, 0, 0, 0);
            s.border = new RectOffset(0, 0, 0, 0);
            return s;
        }

        private static GUIStyle MakeButtonStyle(int fontSize, Color textColor)
        {
            GUIStyle s = new GUIStyle(GUI.skin.button);
            s.fontSize = fontSize;
            s.normal.textColor = textColor;
            s.hover.textColor = textColor;
            s.active.textColor = textColor;
            s.stretchWidth = false;
            return s;
        }

        private static Texture2D Tex(Color c)
        {
            var t = new Texture2D(2, 2);
            var px = new Color[4];
            for (int i = 0; i < 4; i++) px[i] = c;
            t.SetPixels(px); t.Apply();
            return t;
        }

        #endregion
    }
}
