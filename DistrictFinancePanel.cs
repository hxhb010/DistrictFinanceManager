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

        // 人均GDP 分档阈值（15 个，对应 16 档颜色）—— 原档 ×1.5（四舍五入）
        private static readonly long[] PCAP_TIERS =
            { 8, 15, 23, 30, 38, 45, 60, 75, 98, 120, 150, 180, 225, 270, 330 };

        // GDP 分档阈值（15 个，对应 16 档颜色）
        // 每级 = 人均GDP 分档 × 人口分档对应值（逐级相乘），多取整一点
        private static readonly long[] GDP_TIERS =
            { 500, 2500, 7500, 20000, 50000, 120000, 300000, 750000, 1600000, 3000000, 6000000, 10000000, 20000000, 30000000, 45000000 };

        // 人口分档阈值（15 个，对应 16 档颜色）—— 最高档 20 万，低人口细分，中高稍粗
        private static readonly long[] POP_TIERS =
            { 100, 250, 500, 1000, 2000, 4000, 8000, 15000, 25000, 40000, 60000, 90000, 130000, 170000, 200000 };

        // 「区域工人数」分档阈值（15 个）—— 用户 2026-09-28：「图例按照人口图例的一半设置」，
        // 即 POP_TIERS ÷ 2（工人数本来就在人口的一半量级：一个区划的岗位数 ≈ 常住人口的一半）。
        // 与货币/周期无关（人口档位也不随模式变）。**必须在 POP_TIERS 之后声明**。
        private static readonly double[] WORKERS_TIERS = HalfTiersD(POP_TIERS);

        // 地价分档阈值（15 个，对应 16 档颜色）—— 每 8 一档（原版 kr/m² 基准）
        private static readonly long[] LAND_TIERS =
            { 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 96, 104, 112, 120 };

        // 地均GDP 分档阈值（15 个，对应 16 档颜色）—— 基准为「原版周化(×1)」下的地均GDP（货币/m²）。
        // = 真实 $/m² 档 ÷ 375（美元模式系数）再四舍五入到两位；乘 GetDisplayFactor 后：
        //   美元(×375) 还原真实 $/m²、人民币(×2625)=×7、原版(×52)=×52。
        private static readonly double[] GDP_AREA_TIERS =
            { 0, 0.01, 0.02, 0.05, 0.13, 0.32, 0.8, 2.13, 5.33, 13.33, 32, 66.67, 133.33, 266.67, 533.33 };

        // 人口密度分档阈值（15 个，对应 16 档颜色）—— 按现实人口密度（人/km²）划分，
        // 覆盖无人区 <5 → 超密城市核心 >6万，对数递增。
        private static readonly long[] POP_DENSITY_TIERS =
            { 5, 15, 40, 90, 200, 400, 800, 1500, 3000, 6000, 12000, 20000, 32000, 45000, 60000 };

        // 面积分档阈值（15 个，对应 16 档颜色）—— 单位 km²（从 0.01 小区划起，中位数≈2，50 km² 封顶）
        private static readonly double[] AREA_TIERS =
            { 0.01, 0.05, 0.1, 0.2, 0.4, 0.7, 1.2, 2, 3, 5, 8, 13, 20, 32, 50 };

        // 建成区面积分档阈值（15 个）—— = 区域面积档位 × 0.75（原为「÷2」，本版 ×1.5）。
        // 建成区已含建筑空隙（×2，见计算器 BUILT_AREA_GAP_FACTOR），阈值随之抬高。
        // **只换阈值，颜色阶梯仍是同一套 TIER_COLORS**。（必须在 AREA_TIERS 之后声明）
        private static readonly double[] BUILT_AREA_TIERS = ScaledTiers(AREA_TIERS, 0.75);

        // 平均通勤距离分档阈值（15 档，单位 km）—— 住址↔工作地的**直线**距离。
        // 用户 2026-09-27 定：主区间就压在 **1~10 km**（一格 8 m，跨区通勤多在这个量级），
        // 两端各留一点余量（<1 km 的近距离、>10 km 的远距离照样分得出来，再极端就顶到首尾档）。
        // ⚠️ 取色是**反向**的（越近越好，见 CommuteColor / 图例的 invert），档位表本身仍升序。
        // 与货币/统计周期**无关**，直接就用，不需要 GetXxxDisplayTiers 包装。
        private static readonly double[] COMMUTE_TIERS =
            { 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 5.0, 6.0, 7.0, 8.0, 9.5, 11.0, 13.0 };

        // 本地就业率分档阈值（15 档，单位 %）—— 本区划就业居民中工作地也在本单元的比例。
        // 用户 2026-09-27 定：**0~100 平均排**（16 档等分 100%，这里取整到整数档位）。
        private static readonly double[] LOCAL_EMP_TIERS =
            { 7, 13, 20, 27, 33, 40, 47, 53, 60, 67, 73, 80, 87, 93, 100 };

        // 平均通勤时间分档阈值（15 档，单位**分钟**）—— 换算见计算器的 REAL_SECONDS_PER_FRAME（60 模拟帧 = 10 秒）。
        // 按「10~20 分钟最常见、40 分钟以上算远」划，与通勤距离档位基本同量级（1 km ≈ 1.5 分钟）。
        // ⚠️ 用户 2026-09-27 明确要求**不要缩小这张表**（保持 3~85 分钟这套）。
        // ⚠️ 与通勤距离一样**越短越好 → 反向取色**（图例必须 invert:true）。
        /// <summary>通勤时间档位（分钟，15 档递增）。
        /// 2026-09-28 用户定稿：**8 12 16 20 24 28 32 36 40 45 50 60 80 110 150**
        /// （配合「单趟上限放到 5 小时」，原来那套 3…85 分钟的表留不住长尾）。
        /// 与 `CommuteTimeColor`（越短越好 → 反向取色）、图例 `invert: true` 是**同一张表**，改这里就行。</summary>
        private static readonly double[] COMMUTE_TIME_TIERS =
            { 8, 12, 16, 20, 24, 28, 32, 36, 40, 45, 50, 60, 80, 110, 150 };

        /// <summary>把一张分档表整体缩放（不能用 Linq，写显式循环）。</summary>
        private static double[] ScaledTiers(double[] src, double f)
        {
            double[] r = new double[src.Length];
            for (int i = 0; i < src.Length; i++) r[i] = src[i] * f;
            return r;
        }

        /// <summary>long[] 版减半（用于人均可支配 = 人均GDP 档位 ÷ 2）。</summary>
        private static long[] HalfTiers(long[] src)
        {
            long[] r = new long[src.Length];
            for (int i = 0; i < src.Length; i++) r[i] = (long)(src[i] * 0.5);
            return r;
        }

        /// <summary>double[] 版减半（区域工人数 = 人口档位 ÷ 2；要 double[] 才能与就业率的档位共用
        /// DrawLegend 的重载 —— long[] 与 double[] 在三元表达式里没有公共类型，会编译不过）。</summary>
        private static double[] HalfTiersD(long[] src)
        {
            double[] r = new double[src.Length];
            for (int i = 0; i < src.Length; i++) r[i] = src[i] * 0.5;
            return r;
        }

        // 建筑价值增量 / 自定义政府投资额 的**发散档位**（15 个，对应 16 档颜色；基准 = 原版 kr、周）。
        // 索引 0–5 为负、索引 6 = [-10k, +10k) 含 0 → **0 落在黄色档 TIER_COLORS[6]（金黄）**；
        // 负值→红端(0..5)、正值→绿紫端(7..15)。
        //
        // ⚠️ **最大值 15,000,000 = GDP 表最大值(45,000,000) 的 1/3**（用户 2026-09-28 要求：
        //    「自定义投资额和建筑价值增量的图例修一下，保证最大值为 gdp 图例的 1/3，其他值和负值你自己合理分配」）。
        //    注意：本图例的**换算系数仍是价格系数**（与地价同源，见 GetBuiltDeltaDisplayTiers），
        //    GDP 图例用的是流量系数，两者差 6.25~8.3 倍 → **只有"原版 + 周"这一档比值正好是 1/3**；
        //    人民币/美元下比值随系数走（用户 2026-09-28 明确选了这个口径：
        //    「地价视图的比例是不一样的」—— 增量由地价派生，必须留在地价那套系数里）。
        //
        // 档距（沿用用户原来那套手调思路，只把量级对齐到 15M）：
        //   · 正向 8 档 ~2.5 倍递增：10k → 15M（1万 / 2.5万 / 6万 / 15万 / 40万 / 100万 / 250万 / 600万 / 1500万）；
        //   · 负向 5 档更宽（~3 倍）：-10k → -300万 —— 缩水/拆除的幅度可以很大，多留几档位；
        //   · ±10k 以内算"没动"，落在金黄档（原来是 ±15k，跟着整体量级一起收）。
        private static readonly double[] BUILT_DELTA_TIERS =
            { -3000000, -1000000, -300000, -100000, -30000, -10000,
               10000, 25000, 60000, 150000, 400000, 1000000, 2500000, 6000000, 15000000 };

        // 「增速」分档阈值（15 个，对应 16 档颜色）—— 用户 2026-09-27 指定，**单位是「年」**（年图例）。
        // 档位 = 该值起（标签是各档**下限**）：<-10 / −10 / −5 / −2 / −1 / +0.5 / +1 / +2 / +4 / +6 /
        //   +9 / +12 / +16 / +20 / +25 / ≥+30。
        // 特点：**0 附近分得很细**（−1% ~ +0.5% 是一档），负值只有 4 档（缩水方向粗）；
        //   因此 "0" 落在 TIER_COLORS[4]（橙黄）而不是正中间的金黄 —— 这是刻意的档距取舍，不是错位。
        // ⚠️ 实际用的是 GetGrowthDisplayTiers()：把这**年**档位按当前周期换算（周 ÷52、月 ×4/52…）。
        // ⚠️ 这一张是**所有排序键共用**的：增速是比值，量纲上各键可比。
        //   代价是量级不同（面积增速常常是 0，GDP 增速动辄几十个百分点），若某个键看着「全挤在一档」，
        //   就是档位不合身 —— 到时候再按键拆成各自的表（目前先共用一张，保持跨键可比）。
        private static readonly double[] GROWTH_TIERS =
            { -10, -5, -2, -1, 0.5, 1, 2, 4, 6, 9, 12, 16, 20, 25, 30 };

        // 人均可支配收入分档阈值（15 个，对应 16 档颜色）—— = 人均GDP 档位 ÷ 2。
        // 两者都是「人均货币值」，可支配收入是本区划居民的实际到手收入，量级约为人均GDP 的一半。
        // （必须在 PCAP_TIERS 之后声明）
        private static readonly long[] INCOME_TIERS = HalfTiers(PCAP_TIERS);

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
        private int _viewMode; // 0=层级 1=组合 2=所有区划 3=市 4=区县 5=乡镇 6=村社区 7=自定义(政府投资额)
        /// <summary>「自定义政府投资额」视图的模式号。与组合等视图并列，但**自成一体**：
        /// 不接入全局排序系统（`_sortKey`），自带排序/取色/格式化。</summary>
        private const int VIEW_INVEST = 7;
        private bool _filterSubtree;

        // ---- 「自定义政府投资额」视图的状态 ----
        private string _investInput = "";      // 输入框内容（与 _groupNameInput 同款模式）
        private int _investUnit;               // 0=k(×1000) 1=m(×1e6) 2=b(×1e9)，默认 k
        private ushort _investEditTarget;      // 正在编辑哪个区划（0 = 未选）
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
        private int _sortKey; // 0=GDP 1=人口 2=人均GDP 3=地价 4=地均GDP 5=人口密度 6=面积 7=建筑价值增量 8=建成区面积 9=人均可支配
                              // 10=自定义政府投资额 11=平均通勤距离(km) 12=就业相关 13=平均通勤时间(分钟)
                              // ⚠️ 12 是**双子口径**的键（2026-09-28）：`_employWorkers` 决定显示
                              //    「本地就业率(%)」还是「区域工人数(人)」——取值/取色/格式化/图例/占比/增速
                              //    四处都要跟着切（统一走 SelfEmployValues / AggEmployValues / EmployModeName /
                              //    EmployGrowthSupported / GrowthActive）。键名（下拉/表头）统一叫「就业相关」。
                              // （6~13 在「更多▾」下拉里；11/13 配色**反向**：越短越好）
                              // ⚠️ 键号与计算器 GrowthSupported / 各 switch 一一对应，加键必须同步改（清单见交接文档 §0.2 B）
        private bool _moreSortOpen; // 更多排序下拉展开状态
        /// <summary>通勤时间「开始/停止」上次点的是哪个（-1 没点过 / 0 开始 / 1 停止）——只用来高亮（用户 2026-09-27）。</summary>
        private int _commuteBtn = -1;

        /// <summary>
        /// 键 12「就业相关」的子口径（用户 2026-09-28）：false = 本地就业率（%），true = 区域工人数（人）。
        /// **显示口径开关**，与「最长10%/增速」一样**不落盘**，读档由 Awake 复位成 false。
        /// </summary>
        private bool _employWorkers;
        /// <summary>
        /// 「最长10%」口径开关（用户 2026-09-27）：开着时，**平均通勤时间**这一列的数值与排名改用
        /// 「每区划样本里最大的 10%」的平均（`GetCommuteTopTime`），聚合/组合按趟数加权同样跟着切。
        /// ⚠️ 只是**显示口径**：不写进设置、不落盘，周库入库永远记均值（显示设置不许改历史数据）。
        /// 高亮 = **当前是否开启**（与「开始/停止」那种"上次点过"不同，这个是开关）。
        /// </summary>
        private bool _commuteTopMode;
        /// <summary>「让所有公园和广场全天开放」的执行结果（显示在设置面板里，用户 2026-09-27）。</summary>
        private string _parkOpenMsg;
        /// <summary>「自定义投资额跟随 RealTime 日历」切换后的结果提示（绿色一行，同 _parkOpenMsg）。</summary>
        private string _investScaleMsg;
        private float _moreBtnX, _moreBtnY; // 「更多 ▾」按钮位置（供最后绘制下拉用）
        private Vector2 _moreScroll; // 「更多 ▾」下拉的滚动位置（限高 5 行，超出滚动）
        private bool _moreDrag;      // 是否正在拖「更多 ▾」右侧那个滑块
        /// <summary>「增速」开关（「自定义」右边的按钮）。开启后，**能算增速**的排序键一律改用
        /// 「增速%」参与排序、热力配色与数值显示；算不了增速的键（建筑价值增量 / 自定义政府投资额）保持原样。
        /// 与 _viewMode / _sortKey 一样**只在本次会话内有效**，不写进设置（避免下次开局莫名其妙还是增速视图）。
        /// 口径见 DistrictFinanceCalculator.GetGrowth。</summary>
        private bool _growthMode;
        private bool _helpVis; // 操作说明面板可见
        private Vector2 _helpPos = new Vector2(-1f, 100f);
        private bool _helpDragging;

        // ---- 独立「设置」面板（标题栏「说明」左边的按钮打开）----
        private bool _settingsOpen;
        private Vector2 _setPos = new Vector2(-1f, 140f);
        private bool _setDragging;
        /// <summary>
        /// **构建标记**：每次重新部署 DLL 时手动改一次，显示在设置面板最下面。
        /// 用途（2026-09-27 血的教训）：改了 DLL 但玩家没重启游戏时，面板/日志看起来"功能没生效"，
        /// 有了这一行就能一眼确认"游戏里跑的到底是哪一版"，不用再靠日志反推。
        /// </summary>
        private const string BUILD_TAG = "2026-10-01 11:20 v3.1 发布版";

        private const float SET_W = 640f;   // 2026-09-27 用户要求：设置面板调宽、调高、字加大
        private const float SET_H = 700f;   // 用户 2026-09-28：+「跟随 RealTime 日历」+「只统计白天数据」说明 + 缓存/状态行
        private Vector2 _helpScroll;
        private readonly Dictionary<ushort, bool> _ex = new Dictionary<ushort, bool>();
        private int _addLevel = DistLevel.REGION;

        private DistrictFinanceCalculator.FinanceResult _fin;
        private ushort _finDistrict;
        private float _finRefresh;
        private int _lastDisplayMode = -1;
        private int _modeDropWhich;   // 统计模式下拉：0=收起 1=货币列表展开 2=周期列表展开

        private GUIStyle _ti, _fl, _fv, _pcv, _btn, _btnWrap, _hdr, _nodeBtn, _diag, _legend, _rankBtn, _shield, _bn2;
        private bool _styled;
        private Texture2D _bgTex;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _hub = GetComponent<DistrictFinanceHub>();
            LoadKey();
            // 构建标记：面板上不再常显（用户 2026-09-28 要求删掉那一行），改为**每次进图写一行日志** ——
            // 核对「游戏里跑的到底是哪一版」时去 output_log.txt 搜 [DFM] 构建 即可（排查手段不丢）。
            Debug.Log("[DFM] 构建：" + BUILD_TAG);
            // 「显示口径」类开关**一律默认关闭**（用户 2026-09-27：「还是默认关闭」）：
            //   · 「最长10%」→ 每次开新图/读档都从**平均值**口径开始；
            //   · 「增速」→ 同理，读档后回到关闭（说明面板里也是这么写的）；
            //   · 「开始/停止」按钮的**高亮复位**（没点过 → 两个都不亮，与「默认停止」一致）。
            // 这三个都不落盘。这里**显式复位**，不依赖「面板组件每张图都重建」这个巧合 ——
            // 哪天面板改成跨图复用（单例），这一行就是唯一能保证默认状态的地方。
            _commuteTopMode = false;
            _growthMode = false;
            _commuteBtn = -1;
            _employWorkers = false;   // 「就业相关」默认回到「本地就业率」（用户 2026-09-28 定）
            // 打开存档后是否自动显示面板（居中，见 OnGUI 里的位置初始化）。
            // 由选项「打开存档时自动显示面板」控制，默认开。快捷键（默认 F9）始终是开关。
            _vis = ModSettings.Load().ShowOnLoad;
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
                // 语言：以设置文件为准（在「选项」里改过语言时，面板要跟着切）
                string lang = (_hub.Settings != null) ? _hub.Settings.Language : s.Language;
                if (!string.IsNullOrEmpty(lang) && lang != Loc.Lang) Loc.Lang = lang;
                // 统计模式（货币 / 周期任一）切换 → 立即重算
                int stamp = ModeStamp(s.DisplayCurrency, s.DisplayPeriod);
                if (stamp != _lastDisplayMode)
                {
                    _lastDisplayMode = stamp;
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
            {
                // 首次显示（打开存档）时居中。按当前分辨率和缩放实时算，适配任意分辨率；
                // 面板比屏幕还大时退化为靠上/靠左 8px，保证标题栏（拖动/按钮）始终在屏内。
                float pw = PW * _scale;
                float ph = PH * _scale;
                _panelPos = new Vector2(
                    Mathf.Max(8f, (Screen.width - pw) * 0.5f),
                    Mathf.Max(8f, (Screen.height - ph) * 0.5f));
            }

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
            DrawSettingsPanel();

            ConsumePanelMouse();
        }

        /// <summary>
        /// 独立「设置」面板（可拖动、不随主面板移动/缩放），由标题栏「设置」按钮开关。
        /// **这里每一项改动都立即写入 settings.cfg**（`_hub.Settings.Save()`）——
        /// 与选项界面共用同一份 ModSettings，所以两边永远一致。
        /// 结构和 DrawHelpPanel 一样：自己的位置/自己的拖拽/在 GUI.matrix 还原之后绘制。
        /// </summary>
        private void DrawSettingsPanel()
        {
            if (!_settingsOpen) return;

            if (_setPos.x < 0)
                _setPos = new Vector2((Screen.width - SET_W) / 2f, 140f); // 初始居中

            Rect p = new Rect(_setPos.x, _setPos.y, SET_W, SET_H);
            Rect titleBar = new Rect(_setPos.x, _setPos.y, SET_W, 56);

            Event ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 0 && titleBar.Contains(ev.mousePosition))
                _setDragging = true;
            if (ev.type == EventType.MouseUp && ev.button == 0)
                _setDragging = false;
            if (_setDragging && ev.type == EventType.MouseDrag)
                _setPos += ev.delta;

            _setPos.x = Mathf.Clamp(_setPos.x, 10f, Mathf.Max(10f, Screen.width - SET_W));
            _setPos.y = Mathf.Clamp(_setPos.y, 10f, Mathf.Max(10f, Screen.height - 140f));

            if (_bgTex == null)
                _bgTex = Tex(new Color(0.07f, 0.07f, 0.13f, 1f));
            GUI.DrawTexture(p, _bgTex);

            if (GUI.Button(new Rect(p.x + p.width - 50, p.y + 4, 44, 30), "✕", _btn))
                _settingsOpen = false;

            // 面板内的字体统一放大（设置面板条目少、要给足可读性，用户 2026-09-27 要求）
            GUIStyle setBtn = new GUIStyle(_btn); setBtn.fontSize = 16;
            GUIStyle setTxt = new GUIStyle(_fl); setTxt.fontSize = 14;
            GUIStyle setDiag = new GUIStyle(_diag); setDiag.fontSize = 13;
            float setBtnH = BTN_H + 8f;
            float setTextH = TEXT_H + 6f;

            float y = p.y + 8;
            GUIStyle setTitle = new GUIStyle(_ti); setTitle.fontSize = 22;
            GUI.Label(new Rect(p.x + PAD, y, p.width - PAD * 2 - 60, 40),
                Loc.T("设置（拖动标题栏移动）", "Settings (drag title to move)"), setTitle);
            y += 44;

            if (_hub == null || _hub.Settings == null) return;
            ModSettings s = _hub.Settings;

            // ---- 排序中是否显示直辖区划（默认否）----
            bool direct = s.IncludeDirect;
            if (GUI.Button(new Rect(p.x + PAD, y, p.width - PAD * 2, setBtnH),
                (direct ? "☑ " : "☐ ") + Loc.T("排序中显示直辖区划",
                    "Show directly-administered entries in rankings"),
                setBtn))
            {
                s.IncludeDirect = !direct;
                s.Save();          // 面板里的改动同样立即落盘
                _finDistrict = 0;  // 榜单要重画
            }
            y += setBtnH + 4;

            GUI.Label(new Rect(p.x + PAD, y, p.width - PAD * 2, setTextH),
                Loc.T("开启后，单级排名里会额外列出上一级（直辖）区划作为参考条目，不占排名号。",
                      "When on, per-level rankings also list the parent-level entries as reference rows (not numbered)."),
                setDiag);
            y += setTextH + GAP;

            // ---- 公园/广场全天开放（**勾选框**，直接显示当前状态；用户 2026-09-27「是否开启用框表示」）----
            // **覆盖模式**（用户 2026-09-27）：「勾选框」直接就是 RealTimeBridge 的覆盖状态，
            // 不读、也不改 RealTime 的任何配置 —— 勾上 = 挂 Harmony 前缀盖住它的公园维护判定。
            bool rtOn = RealTimeBridge.Active;
            string parkTitle = Loc.T("公园和广场全天运营", "Keep all parks & plazas open 24h");
            string parkLabel = (rtOn ? "☑ " : "☐ ") + parkTitle
                + Loc.T("（覆盖模式，不改 RealTime 设置）", " (override; RealTime settings untouched)");
            if (GUI.Button(new Rect(p.x + PAD, y, p.width - PAD * 2, setBtnH), parkLabel, setBtn))
            {
                string detail;
                bool ok = RealTimeBridge.SetOverride(!rtOn, out detail);
                if (ok) { s.ParkAlwaysOpen = !rtOn; s.Save(); }   // 保存勾选状态（默认不勾选）
                _parkOpenMsg = (ok ? Loc.T("成功：", "OK: ") : Loc.T("未改动：", "Not changed: ")) + detail;
            }
            y += setBtnH + 4;

            // 说明**手动分段**（用户 2026-09-27：「注意分段」）：一行一个意思，别写成一大段。
            // 段落靠 \n 分隔；**同时开 wordWrap 并用 CalcHeight 量实际高度** —— 中文一行能放多少字取决于
            // 字体（本模组装了中文字体替换），写死 setTextH × N 的话长句会横着溢出面板、或者被下一块盖住
            // （说明面板当初就是这么踩的坑，见 DrawHelpPanel 的注释）。
            GUIStyle setNote = new GUIStyle(setDiag); setNote.wordWrap = true;
            float noteW = p.width - PAD * 2;
            string parkNote = Loc.T("若使用 RealTime，公园与广场会在夜间关闭，其吸引力与地价随之下降。\n"
                      + "地价是 GDP、地均 GDP、人均可支配收入等多项统计的重要输入之一，统计结果因此产生波动。\n"
                      + "建议勾选本项：本模组只会在内存里盖住公园与广场的运营时间判定，不会修改 RealTime 的任何设置；取消勾选即完全恢复。\n"
                      + "若不勾选（默认）：装了 RealTime 时只统计白天数据，夜晚暂停写入周库、跳过夜晚，白天照常按游戏周连续记录。\n"
                      + "未安装 RealTime 时本项无效果。",
                      "With RealTime installed, parks and plazas close at night; their attractiveness and land value drop.\n"
                      + "Land value is one of the important inputs to GDP, GDP per m2, disposable income and other stats, so the figures fluctuate.\n"
                      + "It is recommended to check this box: this mod only overrides the operating-hours check for parks and plazas in memory and never changes RealTime's settings; unchecking restores everything.\n"
                      + "If left unchecked (the default) while RealTime is installed, only daytime data is counted: writing to the weekly DB pauses at night and the night weeks are skipped, while daytime keeps being recorded week by week.\n"
                      + "No effect when RealTime is not installed.");
            float parkNoteH = setNote.CalcHeight(new GUIContent(parkNote), noteW);
            GUI.Label(new Rect(p.x + PAD, y, noteW, parkNoteH), parkNote, setNote);
            y += parkNoteH + 2;
            if (!string.IsNullOrEmpty(_parkOpenMsg))
            {
                // 结果提示同样按面板宽度折行（用户 2026-09-28：「设置的文字按面板宽度分行」）——
                // 之前是固定两行的矩形，长句（比如失败原因里带着方法名）会横着溢出面板
                GUIStyle res = new GUIStyle(setTxt); res.wordWrap = true;
                res.normal.textColor = new Color(0.65f, 1f, 0.65f);
                float msgH = res.CalcHeight(new GUIContent(_parkOpenMsg), noteW);
                GUI.Label(new Rect(p.x + PAD, y, noteW, msgH), _parkOpenMsg, res);
                y += msgH + GAP;
            }

            // ---- 当前状态行（活数据，不是说明文字）：让玩家一眼看出「现在到底写不写周库」 ----
            {
                string st;
                Color stc;
                if (_hub == null) { st = ""; stc = Color.gray; }
                else if (!RealTimeBridge.Installed)
                { st = Loc.T("当前：未检测到 RealTime → 全天都写入周库。", "Now: no RealTime detected - the weekly DB records the whole day."); stc = new Color(0.65f, 0.8f, 1f); }
                else if (_hub.NightSkipActive)
                {
                    st = GameWeek.IsNight
                        ? Loc.T("当前：RealTime 在场、未开覆盖，夜里 → 周库暂停写入（本次夜晚已跳过 " + _hub.NightSkippedWeeks + " 周），白天恢复。",
                                "Now: RealTime present, override off, night - DB writing paused (" + _hub.NightSkippedWeeks + " weeks skipped this night); resumes in daytime.")
                        : Loc.T("当前：RealTime 在场、未开覆盖，白天 → 正常写入（只统计白天数据）。",
                                "Now: RealTime present, override off, daytime - writing normally (daytime data only).");
                    stc = GameWeek.IsNight ? new Color(1f, 0.8f, 0.45f) : new Color(0.65f, 1f, 0.65f);
                }
                else
                { st = Loc.T("当前：公园覆盖已生效 → 全天都写入周库。", "Now: park override active - the weekly DB records the whole day."); stc = new Color(0.65f, 1f, 0.65f); }
                if (st.Length > 0)
                {
                    GUIStyle stS = new GUIStyle(setTxt); stS.wordWrap = true;
                    stS.normal.textColor = stc;
                    float h = stS.CalcHeight(new GUIContent(st), noteW);
                    GUI.Label(new Rect(p.x + PAD, y, noteW, h), st, stS);
                    y += h + 2;
                }
            }
            // ---- 自定义投资额是否跟随 RealTime 日历（用户 2026-09-28：「设置里加入自定义投资额是否跟随
            //      realtime 日历，勾选后把自定义投资额记入步长改为按 realtime 日历的周」）----
            bool cal = s.InvestFollowRealTime;
            string calLabel = (cal ? "☑ " : "☐ ") + Loc.T("自定义投资额跟随 RealTime 日历",
                "Custom investment follows the RealTime calendar");
            if (GUI.Button(new Rect(p.x + PAD, y, p.width - PAD * 2, setBtnH), calLabel, setBtn))
            {
                s.InvestFollowRealTime = !cal;
                s.Save();
                if (_hub != null) _hub.ReanchorInvestments("切换设置");   // 已有分期换锚，不丢
                _investScaleMsg = s.InvestFollowRealTime
                    ? Loc.T("已改为按 RealTime 日历周记账（已有分期已换锚，相对位置不变）。",
                            "Now recording by RealTime calendar weeks (existing instalments re-anchored, positions kept).")
                    : Loc.T("已改回按原版帧周记账（已有分期已换锚，相对位置不变）。",
                            "Back to vanilla frame-weeks (existing instalments re-anchored, positions kept).");
            }
            y += setBtnH + 4;

            // 说明**手动分段**（同公园那段的规矩）：一行一个意思；同样 wordWrap + CalcHeight 量高
            string calNote = Loc.T("原版「游戏周」按模拟帧数算（4096 帧 = 1 周），不受 RealTime 影响；RealTime 改写了游戏日历，游戏里看到的「一周」比它长得多。\n"
                      + "本项决定「自定义政府投资额」的分期按哪把尺子记账：不勾 = 原版帧周（与周库同尺）；勾上 = 游戏日历上的周（周 1 期 / 月 4 期 / 季 13 期 / 年 52 期 / 5 年 260 期 / 10 年 520 期）。\n"
                      + "切换勾选不会丢数据：已有分期会按「相对现在的位置」整体平移（3 周前录的仍是 3 周前）。未装 RealTime 时两种刻度几乎相同，勾不勾都一样。",
                      "Vanilla \"weeks\" count simulation frames (4096 frames = 1 week) and ignore RealTime; RealTime rewrites the game calendar, so a calendar week lasts far longer.\n"
                      + "This decides which ruler the custom-investment instalments use: off = vanilla frame-weeks (same ruler as the weekly DB); on = weeks on the in-game calendar (Week 1 / Month 4 / Quarter 13 / Year 52 / 5 years 260 / 10 years 520 instalments).\n"
                      + "Switching never loses data: existing instalments are shifted to keep their position relative to now (one entered 3 weeks ago is still 3 weeks ago). Without RealTime the two rulers are nearly identical.");
            float calNoteH = setNote.CalcHeight(new GUIContent(calNote), noteW);
            GUI.Label(new Rect(p.x + PAD, y, noteW, calNoteH), calNote, setNote);
            y += calNoteH + 2;
            if (!string.IsNullOrEmpty(_investScaleMsg))
            {
                GUIStyle res2 = new GUIStyle(setTxt); res2.wordWrap = true;
                res2.normal.textColor = new Color(0.65f, 1f, 0.65f);
                float h2 = res2.CalcHeight(new GUIContent(_investScaleMsg), noteW);
                GUI.Label(new Rect(p.x + PAD, y, noteW, h2), _investScaleMsg, res2);
                y += h2 + GAP;
            }

            // 构建标记：**面板上一个字都不画**（用户 2026-09-28 截图指着它说"这一段不要"；
            // 连"勾了显示调试信息才画"都不要 —— 用户档里 ShowDebug 就是开的）。
            // 需要核对版本时看日志：Awake 里每次进图写一行 `[DFM] 构建：…`（见 DistrictFinancePanel.Awake）。
            // 下面只留「缓存」状态行（文件只在游戏存档时写盘 → 有没有还没落盘的改动）。
            string cacheTxt = (_hub != null && _hub.CacheDirty)
                ? Loc.T("缓存：有改动未落盘（游戏存档时写入）", "Cache: changes not yet written (written when the game saves)")
                : Loc.T("缓存：与存档一致", "Cache: in sync with the save");
            GUI.Label(new Rect(p.x + PAD, y, p.width - PAD * 2, setTextH), cacheTxt, setDiag);
            y += setTextH + GAP;
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
                    "[Budget] For consistent statistics, the day budget and the night budget should be set to the same values. When the two differ, land value alternates between two sets of figures as day turns to night; land value is one of the important inputs to GDP, land value, disposable income and most other metrics in this mod, so the affected figures will fluctuate within a single game day. Budgets are set separately for day and night in the game's Budget panel.",
                    "Drag the top-left to move the panel; wheel over the top-right to zoom.",
                    "The list below is for viewing and sorting - switch views with Hierarchy / Group / All districts / City / District / Town / Village / Custom.",
                    "[Assign levels]",
                    "1. Switch to the Hierarchy view first.",
                    "2. (Optional) Click a district in the assigned list first; districts added afterwards will hang under it.",
                    "3. Under \"Assign level\" pick the target level: City / District / Town / Village.",
                    "4. Click a district in the \"Unassigned\" list to add it.",
                    "Example: with A set as City level, A itself is the directly-administered area and B, C hang under A. The city-level ranking covers A and everything below it; in the district-level ranking A takes part separately as directly-administered (can be turned off in Options).",
                    "[Remove hierarchy]",
                    "Select an assigned district and click Remove: it is removed from the hierarchy together with everything below it (the district itself stays in the game).",
                    "[Groups]",
                    "The Group view lets you group any districts and name them; a group only sums its members' own values and never affects the hierarchy. Groups sort themselves automatically.",
                    "[Custom government investment]",
                    "1. Switch to the Custom view and click a district in the list.",
                    "2. Type an amount above, pick a unit (k / m / b, default k) and click Apply; a negative amount deducts.",
                    "3. The money is not credited at once: it is spread over the current period as weekly instalments starting this week (Week 1 / Month 4 / Quarter 13 / Year 52 / 5 years 260 / 10 years 520).",
                    "4. The list ranks and colours by the part credited in the latest period, so a fresh entry shows only its first instalment, grows to the full amount week by week, then falls off again after one period.",
                    "5. Clear removes all instalments, including ones still pending in future weeks. Saved per save file.",
                    "6. Recording ruler: by default the vanilla game week (4096 simulation frames = 1 week, unaffected by RealTime). With \"Custom investment follows the RealTime calendar\" checked in Settings, instalments are recorded by weeks on the in-game calendar (the number of instalments does not change: Week 1 / Month 4 / Quarter 13 / Year 52 / 5 years 260 / 10 years 520). Switching the checkbox shifts existing instalments so they keep their position relative to now - nothing is lost.",
                    "[Saving] This mod's data (weekly DB, investment instalments, hierarchy, groups, commute samples, tombstones) is kept in memory as a cache and is written to files only when the game saves (under %LOCALAPPDATA%\\Colossal Order\\Cities_Skylines\\Addons\\Mods\\DistrictFinanceManager\\saves). So if you reload a save without saving first, everything produced since that save is discarded and the files stay as they were at that save. The Settings panel shows whether the cache still matches the save.",
                    "[Growth]",
                    "The button right of Custom is a toggle. When on, every sort key that has a growth rate (GDP / Pop / GDP per capita / Land value / GDP per m2 / Pop density / Area / Built-up area / Disposable per capita) switches to growth % for sorting, colours and display.",
                    "Growth = (current value - value N weeks ago) / value N weeks ago x 100%, N being the current period in weeks; 0 when there is less than one period of history. The legend tiers are annual and rescaled to the current period by compounding. Building value delta and Custom gov. investment are increments themselves and have no growth. The switch is not saved.",
                    "Note: in a small district (few residents or samples) growth can swing wildly from a single data point - treat it as indicative only.",
                    "[More] The dropdown extends to the bottom of the panel - scroll with the wheel or the slider on the right. Clicking an item only changes the sort key and keeps the list open.",
                    "[Commute] All three are counted by residence: avg commute = average straight-line home-to-work distance; local employment = share whose workplace is in the same district; avg commute time = door-to-door duration (waiting and transfers included). For an aggregate or a group, working in ANY member district counts as local. Distance and time are shorter-is-better (reversed colours, ascending order; 0 = no data, shown in white at the bottom). Avg commute time only counts after you press Start under that sort key (only trips heading to a workplace are counted); a district shows once it has enough samples (about a quarter of its employed residents), otherwise a white 0. Stop discards not-yet-finished trips but keeps the samples collected; pressing Start again just continues. It follows travelling citizens frame by frame and costs noticeable CPU, so press Stop when you do not need it.",
                    "[Employment] This key has two readings, and two buttons appear under it when it is the active sort key. Local employment = share of the district's employed residents whose workplace is also in that district (%). District workers = the worker count shown on the vanilla district panel (the sum of the alive counts of the commercial / industrial / office / player-industry areas; public-service employees are not included), read straight from the game data with no scanning. The worker count is a sum, so the list, the groups and the filtered view also show a share; and only the worker count supports Growth (local employment is a ratio, so it has none). The sub-mode always starts on Local employment after loading a save and is not written to the settings.",
                    "[Share] Sum-type metrics (GDP / Pop / Area / Building value delta / Built-up area / Custom investment / District workers) show a share; without a filter the denominator is the whole city, with a filter it becomes the aggregate of the filtered district (its own value plus everything under it).",
                    "[Data warm-up] Stats that scan every building (built-up area, building value delta, disposable income) need about 30 seconds before they have data; avg commute time needs Start to be pressed and enough samples for the district before it shows (it costs noticeable CPU - press Stop when unused). Showing 0 right after loading a save or enabling the mod is normal.",
                    "[Settings - Parks and daytime-only data] If you use RealTime it assigns operating hours to parks and plazas, so they close at night, their attractiveness and land value drop, and GDP / land value / disposable income here fluctuate within a day. Two ways to deal with it, and the Settings panel offers the first one: check \"Keep all parks & plazas open 24h\" and the mod overrides that operating-hours check in memory only (RealTime's own settings are never changed; unchecking restores everything). If you leave it unchecked (the default) while RealTime is installed, the mod instead counts daytime data only: writing to the weekly DB pauses at night and the night weeks are skipped, while daytime keeps being recorded week by week. Without RealTime neither has any effect.",
                }
                : new string[] {
                    "【预算设置】为保证统计口径一致，建议将白天预算与夜间预算设置为相同数值。两者不一致时，地价会随昼夜交替在两套数值之间变动；地价是本模组 GDP、地价、人均可支配收入等多项指标的重要输入之一，相关统计结果将在同一游戏日内出现波动。预算可在游戏内的「预算」面板中按昼夜分别设置。",
                    "【快捷键】按 F9 开关本面板（快捷键可在「选项」里修改；面板顶部也有「设置 / 说明 / 语言」按钮）。",
                    "左上角拖动面板；右上角滚轮缩放面板。",
                    "下方列表用于查看与排序 —— 用 层级 / 组合 / 所有区划 / 市 / 区县 / 乡镇 / 村社区 / 自定义 切换视图。",
                    "【层级分配】",
                    "1. 请先切到「层级」视图。",
                    "2.（可选）先在已分配列表里点一个区划，之后再加入的区划就会挂在它下面。",
                    "3. 在「分配层级」处选择新加入的目标级别：市 / 区县 / 乡镇 / 村社区。",
                    "4. 在「未分配区划」里点一个区划即可加入。",
                    "例：a 定为市级，则 a 自身是市直辖区域，b、c 挂入 a 市；市级排名算 a 与全部下辖，而按区级排名时 a 作为直辖区划单独参与（直辖参与排名可在选项中关闭）。",
                    "【移除层级】",
                    "选中一个已分配的区划，点「移除」按钮：它会连同所有下辖一起从层级树中移除（区划本身仍保留在游戏中）。",
                    "【组合】",
                    "组合视图可把任意区划组合成组并命名；组合只统计各成员自身值合计，不影响层级。创建后自动排序。",
                    "【自定义政府投资额】",
                    "1. 切到「自定义」视图，点列表里的区划。",
                    "2. 在上方输入金额、选单位（k / m / b，默认 k），点「确定」；负数表示冲减。",
                    "3. 这笔钱不是一次性计入：按当前周期均摊，从本周起每周计入一期（周 1 期 / 月 4 期 / 季 13 期 / 年 52 期 / 5 年 260 期 / 10 年 520 期）。",
                    "4. 列表按最近一个周期已计入的部分排序配色：刚录入只显示第一期，逐周涨到整笔，过一个周期后又逐周退出。",
                    "5.「清空」删掉全部分期（含未来还没计入的）。按存档保存。",
                    "6. 记账刻度：默认按原版游戏周（4096 模拟帧 = 1 周，不受 RealTime 影响）；在「设置」里勾选「自定义投资额跟随 RealTime 日历」后，改按游戏日历上的周记账（分期数量不变：周 1 期 / 月 4 期 / 季 13 期 / 年 52 期 / 5 年 260 期 / 10 年 520 期）。切换勾选会把已有分期按「相对现在的位置」整体平移，不会丢数据。",
                    "【保存】本模组的数据（周库、投资分期、层级、组合、通勤样本、墓碑）平时只放在内存里当缓存，只有游戏存档时才写入文件（位置：%LOCALAPPDATA%\\Colossal Order\\Cities_Skylines\\Addons\\Mods\\DistrictFinanceManager\\saves）。因此不存档就回档或读旧档时，那次游戏里产生的数据一律丢弃，文件仍是上次存档时的样子。「设置」面板最下面一行会显示缓存是否与存档一致。",
                    "【增速】",
                    "「自定义」右边的「增速」是开关。开启后，能算增速的排序键（GDP / 人口 / 人均GDP / 地价 / 地均GDP / 人口密度 / 面积 / 建成区面积 / 人均可支配）一律改用增速%排序、配色与显示。",
                    "增速 =（当前值 − N 周前的值）÷ N 周前的值 × 100%，N = 当前周期周数；历史不足一个周期时记 0。图例档位是年档位，按复利换算到当前周期。「建筑价值增量」「自定义政府投资额」本身是增量，没有增速。开关不保存，读档后回到关闭。",
                    "注意：区划较小时（人口、样本少）增速容易被个别数据带得大幅波动，仅供参考。",
                    "【更多 ▾】下拉展开到面板底部，条目超出可视范围时用右侧滑块或滚轮滚动；点条目只换排序键，不收起下拉。",
                    "【通勤】三项都按居住地统计：平均通勤距离＝住址到工作地的平均直线距离；本地就业率＝工作地也在本区划的比例；平均通勤时间＝门到门的时长（含候车与换乘）。聚合与组合里，工作地在范围内任一个成员内即算本地就业。距离与时间越短越好（反向配色、由小到大排；0＝无数据，白色排在最后）。平均通勤时间要在该排序下点「开始」才统计（只统计终点是工作地的那一趟）；本区划攒够样本（约就业居民数的八分之一）才显示，不足时显示白色 0，并在后面注明已统计的百分比（样本数占门槛的比例，攒到 100% 就会出数值）。「停止」会丢弃还没跟踪完的行程，已攒的样本保留；再点「开始」接着攒。这一项逐帧跟踪在途市民，性能消耗较大，不用时请点「停止」。最右边那个「最长10%」是显示口径开关：开启后改用各区划样本里最大的百分之十的平均值来显示与排名（聚合、组合也按样本数加权跟着切），用来看「最堵的那批通勤」；它只改显示，不影响周库与已攒样本，关掉就回到平均值。",
                    "【就业相关】这个键有两套口径：成为当前排序键时它下面会出现两个按钮。「本地就业率」＝本区划就业居民里、工作地也在本区划的比例（%）；「区域工人数」＝原版区划面板里那一格的工人数（商业 / 工业 / 办公 / 玩家产业四处「在岗人数」之和，不含公共服务职工），纯读原版数据、不做任何遍历。工人数是求和型，所以列表 / 组合 / 筛选后面都会写「占比」；也只有工人数能开「增速」（本地就业率是比值，没有增速）。子口径读档后一律回到「本地就业率」，不写进设置。",
                    "【占比】求和型指标（GDP / 人口 / 面积 / 建筑价值增量 / 建成区面积 / 自定义投资额 / 区域工人数）在列表里显示「占比」；没筛选时分母是全图合计，开启筛选后换成筛选区划的聚合值（自身 + 全部下辖）。",
                    "【统计耗时】建成区面积、建筑价值增量、人均可支配等需要遍历全城建筑的统计项，约 30 秒后才有数据；平均通勤时间需先点「开始」，并等本区划攒够样本后才显示（性能消耗较大，不用时点「停止」）。刚读取存档或刚启用模组时显示为 0 属正常。",
                    "【设置·公园与只统计白天】装了 RealTime 时，它会给公园和广场排营业班次、夜里关门，吸引力与地价随之下降，本模组的 GDP / 地价 / 人均可支配会在一天之内波动。两种应对，设置面板里可选：勾选「公园和广场全天运营」，本模组只在内存里覆盖那条运营时间判定（不改 RealTime 的设置，取消勾选即恢复）；不勾选（默认）而装了 RealTime 时，改为只统计白天数据 —— 夜晚暂停写入周库并跳过夜晚，白天照常按游戏周连续记录。没装 RealTime 时两种都不起作用。",
                };

            GUIStyle helpStyle = new GUIStyle(_fl);
            helpStyle.fontSize = 22; // 字体 ×2
            helpStyle.wordWrap = true;

            // ⚠️ 说明文字**整段一次绘制**，不再按行分块定位。
            // 原来是每条一个 GUI.Label、高度按字符数猜（`Length > 60 ? 130 : 62`），
            // 长句换行后超过预设高度就会被**下一块盖住**（2026-09-25 用户报「有些文字被挡住了」）。
            // 整段绘制 + CalcHeight 实测高度，从根上不可能再重叠。
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < helpLines.Length; i++)
            {
                if (i > 0) sb.Append('\n').Append('\n');
                sb.Append(helpLines[i]);
            }
            string helpText = sb.ToString();

            float scrollW = p.width - 24;
            float textW = scrollW - 16;
            float totalH = helpStyle.CalcHeight(new GUIContent(helpText), textW) + 20f;

            Rect contentRect = new Rect(p.x + 8, y, p.width - 16, p.height - (y - p.y) - 10);
            _helpScroll = GUI.BeginScrollView(contentRect, _helpScroll,
                new Rect(0, 0, scrollW, totalH));

            GUI.Label(new Rect(4, 0, textW, totalH), helpText, helpStyle);
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
            // 设置按钮区域：同上（**新加的标题栏按钮忘了加进排除列表，就会变成一按就拖动面板**）
            Rect setBtnScreen = new Rect(_panelPos.x + (PW / 2f - 126) * _scale,
                _panelPos.y + (PAD + 1) * _scale, 80 * _scale, (TITLE_H - 2) * _scale);
            // 语言按钮区域：同上，否则点它会变成拖动面板
            Rect langBtnScreen = new Rect(_panelPos.x + (PW / 2f + 44) * _scale,
                _panelPos.y + (PAD + 1) * _scale, 150 * _scale, (TITLE_H - 2) * _scale);

            if (e.type == EventType.MouseDown && e.button == 0
                && titleScreen.Contains(e.mousePosition)
                && !helpBtnScreen.Contains(e.mousePosition)
                && !setBtnScreen.Contains(e.mousePosition)
                && !langBtnScreen.Contains(e.mousePosition))
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
            // 文案缩短，给「说明」右侧的语言按钮让位（原来那句会顶到语言按钮上）
            GUI.Label(new Rect(PW - 290, y, 280, TEXT_H),
                Loc.T("缩放 ", "Zoom ") + string.Format("{0:P0}", _scale), zoomRight);

            // 顶部正中：设置 / 操作说明按钮（设置放在说明左边）
            if (GUI.Button(new Rect(PW / 2f - 126, y + 1, 80, TITLE_H - 2), Loc.T("设置", "Settings"), _settingsOpen ? _bn2 : _btn))
                _settingsOpen = !_settingsOpen;
            if (GUI.Button(new Rect(PW / 2f - 40, y + 1, 80, TITLE_H - 2), Loc.T("说明", "Help"), _helpVis ? _bn2 : _btn))
                _helpVis = !_helpVis;

            // 顶部：语言切换（在「说明」右边）。点一下即切换并写入设置，选项窗口里也会同步显示。
            // 标签**始终用英文**：这个按钮主要是给英文使用者找的，中文模式下也必须能认出 "Language"。
            if (GUI.Button(new Rect(PW / 2f + 44, y + 1, 150, TITLE_H - 2),
                Loc.IsEn ? "Language: English" : "Language: Chinese", _btn))
            {
                string next = Loc.IsEn ? "zh" : "en";
                if (_hub != null && _hub.Settings != null)
                {
                    _hub.Settings.Language = next;
                    _hub.Settings.AutoLanguage = false; // 手动切换优先，别再被自动识别覆盖
                    _hub.Settings.Save();
                }
                Loc.Lang = next;
                _finDistrict = 0; // 让详情区立即按新语言重绘
            }

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

            // ---- 新加入的区划会挂到哪里 ----
            // ⚠️ 父级由 ResolveParent() 决定，而它用的是**当前选中**节点；「选中」会被列表/排名里的
            //    一次点击改掉，所以很容易在不知情时挂到某个市下面。这里显式写出来，杜绝静默挂载。
            ushort pendParent = ResolveParent(_addLevel);
            string pendTo = pendParent == 0
                ? Loc.T("顶级（独立）", "top level (standalone)")
                : _hub.GetVanillaDistrictName(pendParent) + " [" +
                  LevelName(_hub.Hierarchy.LevelOf.ContainsKey(pendParent)
                      ? _hub.Hierarchy.LevelOf[pendParent] : 0) + "]";
            GUI.Label(new Rect(PAD, y, PW - PAD * 2 - 120, BTN_H),
                Loc.T("新加入将挂到：", "New additions attach to: ") + pendTo, _fl);

            // ---- 移到顶级：把挂错的节点提回根，不用删了重加 ----
            bool canPromote = _hub.SelectedID != 0
                && _hub.Hierarchy.LevelOf.ContainsKey(_hub.SelectedID)
                && _hub.Hierarchy.ParentOf.ContainsKey(_hub.SelectedID)
                && _hub.Hierarchy.ParentOf[_hub.SelectedID] != 0;
            GUI.enabled = canPromote;
            if (GUI.Button(new Rect(PW - PAD - 112, y, 112, BTN_H),
                Loc.T("移到顶级", "To top level"), _btn))
            {
                _hub.Hierarchy.SetParent(_hub.SelectedID, 0, _hub.Hierarchy.LevelOf[_hub.SelectedID]);
                _hub.MarkDirty();
                _finDistrict = 0;
                Debug.Log("[DFM] Promoted #" + _hub.SelectedID + " to top level");
            }
            GUI.enabled = true;
            y += BTN_H + GAP;

            // ==== 颜色图例（按排序依据切换；默认 GDP）====
            // 「增速」优先：能算增速的键在图例里换成「以 0 为中心」的百分数档位（SortLabel 已带「增速」后缀）。
            // ⚠️ 必须排除「自定义」视图：进这个视图**不会**自动把 _sortKey 改成 10（要手点那个排序按钮才会），
            //    否则从别的视图带着 _sortKey=0 进来时，图例会变成 GDP 增速、而列表显示的却是投资额。
            //    增量键 7 本来就「算不了增速」，自然落到下面的原分支。
            if (_viewMode != VIEW_INVEST && GrowthActive(_sortKey))
            {
                y = DrawLegendGrowth(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · " + SortLabel(_sortKey) + "（%/" + PeriodSuffix() + "）",
                          "Legend · " + SortLabel(_sortKey) + " (%/" + PeriodSuffix() + ")"));
            }
            // 「自定义」视图优先判断：它固定复用「建筑价值增量」的档位，与 _sortKey 无关
            else if (_viewMode == VIEW_INVEST)
            {
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · 自定义政府投资额（" + CurrencySymbol() + "/" + PeriodSuffix() + "）",
                          "Legend · Custom gov. investment (" + CurrencySymbol() + "/" + PeriodSuffix() + ")"),
                    GetBuiltDeltaDisplayTiers());
            }
            else if (_sortKey == 1)
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
            else if (_sortKey == 4)
            {
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · 地均GDP（" + CurrencySymbol() + "/m²）", "Legend · GDP/m² (" + CurrencySymbol() + "/m²)"), GetGdpAreaDisplayTiers());
            }
            else if (_sortKey == 5)
            {
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · 人口密度（人/km²）", "Legend · Pop density (pop/km²)"), POP_DENSITY_TIERS);
            }
            else if (_sortKey == 6)
            {
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · 面积（km²）", "Legend · Area (km²)"), AREA_TIERS);
            }
            else if (_sortKey == 7)
            {
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · 建筑价值增量（" + CurrencySymbol() + "/" + PeriodSuffix() + "）",
                          "Legend · Building value Δ (" + CurrencySymbol() + "/" + PeriodSuffix() + ")"),
                    GetBuiltDeltaDisplayTiers());
            }
            else if (_sortKey == 8)
            {
                // 与 SortColor 的 BuiltAreaColor 用同一张 BUILT_AREA_TIERS（区域面积档位 ÷ 2）；
                // 色块颜色仍是同一套 TIER_COLORS，只有数字变。
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · 建成区面积（km²）", "Legend · Built-up area (km²)"), BUILT_AREA_TIERS);
            }
            else if (_sortKey == 9)
            {
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · 人均可支配（" + CurrencySymbol() + "/人）", "Legend · Disposable income/cap (" + CurrencySymbol() + "/person)"), GetDisplayIncomeTiers());
            }
            else if (_sortKey == 10)
            {
                // 自定义政府投资额：与「建筑价值增量」复用同一张档位（_viewMode == VIEW_INVEST 分支上面已先拦）
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · 自定义政府投资额（" + CurrencySymbol() + "/" + PeriodSuffix() + "）",
                          "Legend · Custom gov. investment (" + CurrencySymbol() + "/" + PeriodSuffix() + ")"),
                    GetBuiltDeltaDisplayTiers());
            }
            else if (_sortKey == 11)
            {
                // 平均通勤距离：km 档位，与货币/周期无关（居住地口径，见计算器 GetCommuteDistance）。
                // **invert：越近越好** → 色阶与图例都反过来（短 = 紫、长 = 红），与 CommuteColor 同向。
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · 平均通勤距离（km，居住地口径，越近越好）",
                          "Legend · Avg commute (km, by residence, shorter is better)"),
                    COMMUTE_TIERS, true);
            }
            else if (_sortKey == 12)
            {
                // 键 12 两个子口径各自的图例（用户 2026-09-28：区域工人数按人口图例的一半设档）
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · " + EmployModeName()
                        + (_employWorkers ? "（人，原版区划面板口径）" : "（%）"),
                        _employWorkers
                            ? "Legend · District workers (count, as shown on the vanilla district panel)"
                            : "Legend · Local employment (%)"),
                    _employWorkers ? WORKERS_TIERS : LOCAL_EMP_TIERS);
            }
            else if (_sortKey == 13)
            {
                // 平均通勤时间：分钟（现实等效，换算见计算器）——**同样 invert：越短越好**
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · 平均通勤时间（分钟，越短越好）",
                          "Legend · Avg commute time (min, shorter is better)"),
                    COMMUTE_TIME_TIERS, true);
            }
            else
            {
                y = DrawLegend(PAD, y, PW - PAD * 2,
                    Loc.T("颜色图例 · GDP（" + CurrencySymbol() + "）", "Legend · GDP (" + CurrencySymbol() + ")"), GetDisplayGDPTiers());
            }
            y += GAP;

            // ==== 视图按钮 ====
            string[] viewLabels = Loc.IsEn
                ? new string[] { "Tree", "Group", "All dist", "City", "District", "Town", "Village", "Custom" }
                : new string[] { "层级", "组合", "所有区划", "市", "区县", "乡镇", "村社区", "自定义" };

            // 第 1 排：层级 / 组合 / 筛选 / 自定义（固定宽度）
            float r1 = PAD;
            if (GUI.Button(new Rect(r1, y, 70, BTN_H), viewLabels[0], _viewMode == 0 ? _bn2 : _btn)) _viewMode = 0;
            if (GUI.Button(new Rect(r1 + 74, y, 70, BTN_H), viewLabels[1], _viewMode == 1 ? _bn2 : _btn)) _viewMode = 1;
            bool groupSel = _detailGroup >= 0 && _detailGroup < Groups.Count;
            if (GUI.Button(new Rect(r1 + 148, y, 130, BTN_H),
                groupSel ? Loc.T("筛选:组合成员", "Filter: group members")
                         : Loc.T("筛选:选中下辖", "Filter: subtree"), _filterSubtree ? _bn2 : _btn))
                _filterSubtree = !_filterSubtree;
            // 「自定义」追加在筛选之后 —— 上面三个按钮的位置一个都没动
            if (GUI.Button(new Rect(r1 + 286, y, 70, BTN_H), viewLabels[7],
                _viewMode == VIEW_INVEST ? _bn2 : _btn))
                _viewMode = VIEW_INVEST;
            // 「增速」紧挨「自定义」右侧。它**不是视图**而是一个开关：不改 _viewMode，
            // 只是让下方所有能算增速的排序键改用增速口径（见 SortValue / SortColor / SortLabel）。
            if (GUI.Button(new Rect(r1 + 360, y, 70, BTN_H),
                Loc.T("增速", "Growth"), _growthMode ? _bn2 : _btn))
            {
                _growthMode = !_growthMode;
                // 排查用：日志里能证明「这个按钮确实被点了」（与「更多 ▾」同款开关，由「显示调试信息」控制）
                if (_hub != null && _hub.Settings != null && _hub.Settings.ShowDebug)
                    Debug.Log("[DFM] growth toggle = " + _growthMode + " key=" + _sortKey);
            }
            y += BTN_H + GAP;

            // 第 2 排：所有区划 / 市 / 区县 / 乡镇 / 村社区
            float cw2 = (PW - PAD * 2 - 16) / 5f;
            float r2 = PAD;
            for (int i = 2; i < 7; i++)
            {
                bool on = _viewMode == i;
                if (GUI.Button(new Rect(r2, y, cw2, BTN_H), viewLabels[i], on ? _bn2 : _btn))
                    _viewMode = i;
                r2 += cw2 + 4;
            }
            y += BTN_H + GAP;

            // 第 3 排：排序依据
            string[] sortLabels = Loc.IsEn
                ? new string[] { "GDP", "Pop", "GDP/cap", "Land", "GDP/m²", "Pop/m²" }
                : new string[] { "GDP", "人口", "人均GDP", "地价", "地均GDP", "人口密度" };
            if (_viewMode == VIEW_INVEST)
            {
                // 本视图固定按「自定义政府投资额」排序，这一排只画这一个键
                GUI.Label(new Rect(PAD, y, 60, BTN_H), Loc.T("排序:", "Sort: "), _fl);
                if (GUI.Button(new Rect(PAD + 60, y, 200, BTN_H),
                    Loc.T("自定义政府投资额", "Custom gov. investment"), _sortKey == 10 ? _bn2 : _btn))
                    _sortKey = 10;   // 点它即选中该排序键（与其他视图的排序键按钮同义）
                _moreBtnX = PAD + 264; _moreBtnY = y;
                _moreSortOpen = false;   // 本视图没有「更多 ▾」下拉
            }
            else
            {
                GUI.Label(new Rect(PAD, y, 60, BTN_H), Loc.T("排序:", "Sort: "), _fl);
                float sx = PAD + 60;
                for (int i = 0; i < sortLabels.Length; i++)
                {
                    bool on = _sortKey == i;
                    if (GUI.Button(new Rect(sx, y, 66, BTN_H), sortLabels[i], on ? _bn2 : _btn))
                        _sortKey = i;
                    sx += 70;
                }
                // 更多排序：下拉浮在列表上方（最后绘制），此处只放按钮、不占位
                _moreBtnX = sx;
                _moreBtnY = y;
                if (GUI.Button(new Rect(sx, y, 54, BTN_H),
                    Loc.T("更多 ▾", "More ▾"), _sortKey >= 6 ? _bn2 : _btn))
                {
                    _moreSortOpen = !_moreSortOpen;
                    _moreScroll = Vector2.zero; // 每次展开都从顶部开始，不沿用上次滚到的位置
                    if (_hub != null && _hub.Settings != null && _hub.Settings.ShowDebug)
                        Debug.Log("[DFM] moreSort toggle open=" + _moreSortOpen + " key=" + _sortKey);
                }
            }
            y += BTN_H + GAP;

            // ==== 「就业相关」的两个子口径按钮（用户 2026-09-28：「把本地就业率名字改为就业相关，
            //      点击后放两个按钮：一个还是本地就业率，第二个为区域工人数」）====
            // 与「最长10%」同一个性质：**显示口径开关**，只换这一列取值/配色/图例/占比，不动周库与设置。
            // 高亮 = 当前生效的那个；默认「本地就业率」（不落盘，读档复位，见 Awake）。
            if (_sortKey == 12 && _viewMode != VIEW_INVEST)
            {
                if (GUI.Button(new Rect(PAD, y, 110, BTN_H), Loc.T("本地就业率", "Local employment"),
                        _employWorkers ? _btn : _bn2))
                    _employWorkers = false;
                if (GUI.Button(new Rect(PAD + 116, y, 110, BTN_H), Loc.T("区域工人数", "District workers"),
                        _employWorkers ? _bn2 : _btn))
                    _employWorkers = true;
                y += BTN_H + 2f;
            }

            // ==== 平均通勤时间：开始 / 停止 ====（用户 2026-09-27）
            // 这一项是**逐帧跟踪在途市民**攒出来的，比较吃性能 —— 所以做成**手动开关**：
            //   只有点了「开始」才统计；「停止」会把**还没跟踪完**的在途行程丢掉（不完整的记录不进统计），
            //   **已留存的样本一律保留**（点「开始」也不清空，接着攒 —— 用户 2026-09-27 定）。
            // 两个按钮**不高亮**（用户要求）：它就是两个动作，不表示"当前项"。
            // 状态文字只写在按钮下面那一行（表头那几处提示已按用户要求删除）。
            if (_sortKey == 13 && _viewMode != VIEW_INVEST)
            {
                DistrictFinanceCalculator calc = _hub.Calculator;
                bool running = calc.CommuteTracking;
                // 高亮 = **上次点的那个按钮**（用户 2026-09-27：点「开始」就「开始」亮，点「停止」就「停止」亮；
                // 没点过之前两个都不亮）。
                if (GUI.Button(new Rect(PAD, y, 66, BTN_H), Loc.T("开始", "Start"),
                        _commuteBtn == 0 ? _bn2 : _btn))
                {
                    calc.StartCommuteTracking();
                    _commuteBtn = 0;
                }
                if (GUI.Button(new Rect(PAD + 70, y, 66, BTN_H), Loc.T("停止", "Stop"),
                        _commuteBtn == 1 ? _bn2 : _btn))
                {
                    calc.StopCommuteTracking();   // 无条件调用：停下所有在途跟踪并丢弃它们
                    _commuteBtn = 1;
                }
                // 「最长10%」：**显示口径开关**（跟统计开关无关，停止后照样能切）——
                // 开着时本列数值/排名改用「每区划样本里最大的 10%」的平均，聚合与组合按趟数加权一起跟着切。
                // 高亮 = **当前开启**（用户 2026-09-27：在「停止」右边再加一个）。
                if (GUI.Button(new Rect(PAD + 140, y, 88, BTN_H), Loc.T("最长10%", "Longest 10%"),
                        _commuteTopMode ? _bn2 : _btn))
                    _commuteTopMode = !_commuteTopMode;
                // 状态词**只有两个词**（用户 2026-09-27 定稿），跟按钮同一行、放在右边
                GUI.Label(new Rect(PAD + 232, y, 90, BTN_H),
                    running ? Loc.T("统计中", "Counting") : Loc.T("已停止", "Stopped"), _fl);
                y += BTN_H + 2f;

                // 提示**单独占一行并自动折行**（整段绘制 + 实测高度，绝不会被裁 / 压到列表）
                string hint = CommuteHint();
                if (hint.Length > 0)
                {
                    GUIStyle hintStyle = new GUIStyle(_fl);
                    hintStyle.wordWrap = true;
                    float hw = PW - PAD * 2;
                    float hh = hintStyle.CalcHeight(new GUIContent(hint), hw);
                    GUI.Label(new Rect(PAD, y, hw, hh), hint, hintStyle);
                    y += hh + GAP;
                }
                else
                {
                    y += GAP;
                }
            }

            // ==== 列表 ==== （右侧留出约 56px 给「更多 ▾」下拉那一列，避免下拉压到列表按钮而抢不到点击）
            const float SORT_DROP_COL = 56f;
            Rect list = new Rect(PAD, y, PW - PAD * 2 - SORT_DROP_COL, PH - PAD - y);
            // 层级 / 所有区划：列表上方显示全区合计（含 GDP 增速）
            if (_viewMode == 0 || _viewMode == 2)
                y = DrawCityTotals(y, PW - PAD * 2);
            list = new Rect(PAD, y, PW - PAD * 2 - SORT_DROP_COL, PH - PAD - y);
            switch (_viewMode)
            {
                case 1: DrawGroupView(list); break;                    // 组合
                case 2: DrawSortedList(list); break;                   // 所有区划
                case 3:
                case 4:
                case 5:
                case 6: DrawRankingList(list, _viewMode - 2); break;  // 市/区县/乡镇/村社区
                case VIEW_INVEST: DrawInvestView(list); break;         // 自定义政府投资额
                default: DrawTreeList(list); break;                    // 层级
            }

            // 最后绘制（浮在最上层），避免展开的下拉窗口被图例/列表遮挡
            DrawModeSelector();
            DrawMoreSortDropdown();
        }

        /// <summary>「更多 ▾」下拉的宽度（与「更多 ▾」按钮同宽）。高度不限项数、直接拉到面板底部，见 DrawMoreSortDropdown。</summary>
        private const float MORE_DROP_W = 54f;
        private const float MORE_DROP_SLIDER = 10f;   // 右侧滑块宽度
        private const float MORE_DROP_SLIDER_GAP = 2f;

        /// <summary>
        /// 「更多 ▾」排序下拉：最后绘制、向下展开。列表已在其右侧留出空列（见 DrawPanel 的 SORT_DROP_COL），
        /// 故下拉不再压到列表按钮，点击不会被列表抢走。
        /// 展开/收起只由「更多 ▾」按钮切换；**点条目只换排序键、不收起**（用户 2026-09-27 定）。
        /// 2026-09-27：高度**一直拉到面板底部** + 右侧滑块 + 滚轮（条目越加越多，直接向下铺开会溢出面板底部）。
        /// 滑块是**自绘**的：`GUI.BeginScrollView` 自带的滚动条在这套皮肤下看不见，而用户要的是"右边有滑块"。
        /// 滑块拖动与滚轮都只改 `_moreScroll.y`；条目用 BeginGroup 裁掉框外部分，整体按 −_moreScroll.y 上移。
        /// </summary>
        private void DrawMoreSortDropdown()
        {
            if (!_moreSortOpen) return;
            string[] more = Loc.IsEn ? new string[] { "Area", "Building value Δ", "Built-up area", "Disposable/cap", "Gov. investment", "Avg commute", "Employment", "Avg commute time" } : new string[] { "面积", "建筑价值增量", "建成区面积", "人均可支配", "自定义政府投资额", "平均通勤距离", "就业相关", "平均通勤时间" };
            int[] moreKeys = new int[] { 6, 7, 8, 9, 10, 11, 12, 13 }; // 追加更多排序项在此（两个数组必须等长同序）

            // 先量出内容总高（长条目换行占两行），再决定框高。
            // 高度上限 = **一直拉到面板底部**（内容区下沿 PH - PAD，与列表下边缘对齐）；
            // 条目再多就在框内用右侧滑块 / 滚轮滚（用户 2026-09-27 定：不限项数，直接顶到底）。
            float totalH = 0f;
            for (int i = 0; i < more.Length; i++)
                totalH += (_btn.CalcSize(new GUIContent(more[i])).x > 50f) ? BTN_H * 2f : BTN_H;
            float avail = PH - PAD - (_moreBtnY + BTN_H + GAP);
            if (avail < BTN_H) avail = BTN_H;   // 面板被缩得极矮时至少留一行，别算出负高度
            float boxH = totalH < avail ? totalH : avail;
            float maxScroll = totalH - boxH;
            bool needSlider = maxScroll > 0.5f;

            float dy = _moreBtnY + BTN_H + GAP;
            Rect box = new Rect(_moreBtnX, dy, MORE_DROP_W, boxH);              // 条目区（右缘与「更多 ▾」按钮对齐）
            Rect track = new Rect(box.xMax + MORE_DROP_SLIDER_GAP, dy, MORE_DROP_SLIDER, boxH);

            // 夹住滚动量：条目变少 / 换了排序键后 _moreScroll 可能停在越界位置
            if (needSlider) _moreScroll.y = Mathf.Clamp(_moreScroll.y, 0f, maxScroll);
            else { _moreScroll.y = 0f; _moreDrag = false; }

            float thumbH = needSlider ? Mathf.Max(24f, boxH * boxH / totalH) : boxH;
            float thumbT = needSlider ? (boxH - thumbH) * (_moreScroll.y / maxScroll) : 0f;
            Rect thumb = new Rect(track.x, track.y + thumbT, track.width, thumbH);

            // ---- 交互：滚轮 + 拖滑块 ----
            // 鼠标位置必须换算成**面板本地坐标**：Event.mousePosition 是屏幕坐标（面板还带 GUI.matrix 缩放），
            // 而 box/track 都是本地坐标，直接比会错位。
            Event e = Event.current;
            Vector2 mp = LocalMouse();
            if (needSlider && e.type == EventType.ScrollWheel && (box.Contains(mp) || track.Contains(mp)))
            {
                _moreScroll.y = Mathf.Clamp(_moreScroll.y + e.delta.y * (BTN_H * 1.5f), 0f, maxScroll);
                e.Use();
            }
            if (needSlider)
            {
                if (e.type == EventType.MouseDown && e.button == 0 && thumb.Contains(mp))
                {
                    _moreDrag = true;
                    e.Use();   // 吞掉这次按下，别让它顺带点到条目
                }
                else if (e.type == EventType.MouseUp && e.button == 0) _moreDrag = false;
                if (_moreDrag && e.type == EventType.MouseDrag && (boxH - thumbH) > 0.5f)
                {
                    _moreScroll.y = Mathf.Clamp(
                        _moreScroll.y + e.delta.y * (maxScroll / (boxH - thumbH)), 0f, maxScroll);
                    e.Use();
                }
            }

            // ---- 绘制 ----
            Color oldBg = GUI.backgroundColor;
            Color oldColor = GUI.color;
            GUI.backgroundColor = Color.black; // 下拉里的按钮底色保持纯黑（与之前一致）
            GUI.color = Color.black;
            GUI.DrawTexture(box, Texture2D.whiteTexture); // 下拉底
            GUI.color = oldColor;

            GUI.BeginGroup(box);   // BeginGroup 负责裁剪：条目超出框的部分不画
            float iy = -_moreScroll.y;
            for (int i = 0; i < more.Length; i++)
            {
                string lbl = more[i] + (moreKeys[i] == _sortKey ? " ✓" : "");
                bool twoLine = _btn.CalcSize(new GUIContent(more[i])).x > 50f;
                float ih = twoLine ? BTN_H * 2f : BTN_H;
                if (GUI.Button(new Rect(0, iy, MORE_DROP_W, ih), lbl, twoLine ? _btnWrap : _btn))
                {
                    // 点条目**只换排序键，不收起下拉**（用户 2026-09-27 定）：要连着试几个键时
                    // 不用每次重新展开；收起只由「更多 ▾」按钮自己切换。
                    _sortKey = moreKeys[i];
                    _moreScroll = Vector2.zero;   // 重画每行 ✓ 标记时从顶部开始，避免玩家看不到刚选的项
                }
                iy += ih;
            }
            GUI.EndGroup();

            if (needSlider)
            {
                GUI.color = new Color(0.22f, 0.22f, 0.28f, 1f);                 // 轨道
                GUI.DrawTexture(track, Texture2D.whiteTexture);
                GUI.color = _moreDrag ? new Color(1f, 0.85f, 0.3f, 1f)          // 滑块（拖动中变黄）
                                      : new Color(0.62f, 0.62f, 0.70f, 1f);
                GUI.DrawTexture(thumb, Texture2D.whiteTexture);
                GUI.color = oldColor;
            }
            GUI.backgroundColor = oldBg;
        }

        /// <summary>
        /// 鼠标在**面板本地 GUI 坐标**里的位置（面板左上为原点、未乘缩放）。
        /// 面板别处用的是 `Input.mousePosition`（屏幕坐标，见 PanelInputRect / HandlePanelInput），
        /// 两者不能混用 —— 这里统一转成本地坐标给下拉的命中判定用。
        /// </summary>
        private Vector2 LocalMouse()
        {
            Vector3 m = Input.mousePosition;
            return new Vector2((m.x - _panelPos.x) / _scale,
                               (Screen.height - m.y - _panelPos.y) / _scale);
        }

        /// <summary>
        /// 统计模式：**左右两个下拉**（标题栏下方 20）——左选货币（原版 kr / 人民币 ¥ / 美元 $），
        /// 右选周期（周 / 月 / 季 / 年 / 5年）。两者的组合决定全部倍率，
        /// 见 ModSettings.FlowFactor / PriceFactor / PeriodWeeks。
        /// </summary>
        private void DrawModeSelector()
        {
            int cur = _hub != null && _hub.Settings != null ? _hub.Settings.DisplayCurrency : 0;
            int per = _hub != null && _hub.Settings != null ? _hub.Settings.DisplayPeriod : 0;

            Rect btn = new Rect(PW - 146, PAD + TITLE_H + 20, 136, BTN_H);

            // 上方标题：右对齐到按钮行右边缘
            GUIStyle modeLbl = new GUIStyle(_fl);
            modeLbl.alignment = TextAnchor.MiddleRight;
            GUI.Label(new Rect(btn.x - 70, btn.y - 18, btn.width + 70, TEXT_H),
                Loc.T("统计模式选择：", "Statistics mode:"), modeLbl);

            Rect cBtn = new Rect(btn.x, btn.y, 66, BTN_H);        // 左：货币
            Rect pBtn = new Rect(btn.x + 70, btn.y, 66, BTN_H);   // 右：周期

            if (GUI.Button(cBtn, ModSettings.CurrencyName(cur) + " ▾", _btn))
                _modeDropWhich = (_modeDropWhich == 1) ? 0 : 1;
            if (GUI.Button(pBtn, ModSettings.PeriodName(per) + " ▾", _btn))
                _modeDropWhich = (_modeDropWhich == 2) ? 0 : 2;

            // 展开的列表：右对齐到各自按钮的右缘（宽度 100，保证「人民币 ✓」「5年 ✓」不截断）
            const float IW = 100f;
            Color oldBg = GUI.backgroundColor;
            GUI.backgroundColor = Color.black; // 展开的下拉窗口背景纯黑
            if (_modeDropWhich == 1)
            {
                float x = cBtn.xMax - IW;
                for (int i = 0; i < 3; i++)
                {
                    Rect item = new Rect(x, cBtn.y + BTN_H * (i + 1) + 2, IW, BTN_H);
                    if (GUI.Button(item, ModSettings.CurrencyName(i) + (i == cur ? " ✓" : ""), _btn))
                    {
                        _modeDropWhich = 0;
                        if (i != cur) { _hub.Settings.DisplayCurrency = i; ApplyModeChange(); }
                    }
                }
            }
            else if (_modeDropWhich == 2)
            {
                float x = pBtn.xMax - IW;
                for (int i = 0; i < 6; i++)   // 0周 1月 2季 3年 4=5年 5=10年（2026-09-28 加 10 年）
                {
                    Rect item = new Rect(x, pBtn.y + BTN_H * (i + 1) + 2, IW, BTN_H);
                    if (GUI.Button(item, ModSettings.PeriodName(i) + (i == per ? " ✓" : ""), _btn))
                    {
                        _modeDropWhich = 0;
                        if (i != per) { _hub.Settings.DisplayPeriod = i; ApplyModeChange(); }
                    }
                }
            }
            GUI.backgroundColor = oldBg;
        }

        /// <summary>统计模式改动后：存盘 + 清缓存让所有数值与图例立即重算。</summary>
        private void ApplyModeChange()
        {
            if (_hub == null || _hub.Settings == null) return;
            _hub.Settings.Save();
            if (_hub.Calculator != null) _hub.Calculator.ClearCache();
            _finDistrict = 0;
            _lastDisplayMode = ModeStamp(_hub.Settings.DisplayCurrency, _hub.Settings.DisplayPeriod);
        }

        /// <summary>把两个轴压成一个整数，用于「设置变了吗」的变化检测。</summary>
        private static int ModeStamp(int currency, int period) { return currency * 10 + period; }

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
            // 全图 GDP 增速（近 x 周，x = 当前周期周数）—— 数据不足时不显示
            string growthTxt = "";
            double gp;
            if (CityGdpGrowth(out gp))
                growthTxt = Loc.T("  GDP增速 ", "  GDP growth ") + Pct(gp) + "/" + PeriodSuffix();
            GUI.Label(new Rect(PAD, y, w, VALUE_H),
                Loc.T("全区 GDP " + CurrencySymbol(), "City GDP " + CurrencySymbol()) + F(totalGdp) +
                Loc.T("  人均 " + CurrencySymbol(), "  /cap " + CurrencySymbol()) + F(avg) +
                Loc.T("  人口 ", "  pop ") + totalPop.ToString("N0") + growthTxt, _fv);
            return y + VALUE_H + GAP;
        }

        /// <summary>
        /// 全图 GDP（**自身值合计**，与上面那行同一个口径）的增速%：Σ实时 ÷ Σ基准 − 1。
        /// 两端都是**原始值**（GDP 是唯一带显示系数的量，见 Calculator.GrowthRaw），所以与货币/周期无关。
        /// 基准合计为 0（没有历史）→ 返回 false，调用方不显示这一段。
        /// </summary>
        private bool CityGdpGrowth(out double pct)
        {
            pct = 0.0;
            double[] live, basev;
            if (!_hub.Calculator.GrowthRaw(0, false, out live, out basev)) return false;
            double l = 0, b = 0;
            ushort[] all = _hub.GetVanillaDistricts();
            for (int i = 0; i < all.Length; i++)
            {
                ushort did = all[i];
                if (did >= live.Length || did >= basev.Length) continue;
                l += live[did];
                b += basev[did];
            }
            if (b <= 0.0) return false;
            pct = (l - b) / b * 100.0;
            return true;
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

        /// <summary>绘制单行树节点（y 由调用方绝对递增传入），非选中节点按当前排序键着色。</summary>
        private void DrawNodeRow(ushort id, int depth, float y, float w, double[] aggVal)
        {
            float ind = depth * 14f;
            string name = _hub.GetVanillaDistrictName(id);
            int lv = _hub.Hierarchy.LevelOf.ContainsKey(id) ? _hub.Hierarchy.LevelOf[id] : 0;
            string tag = lv > 0 ? ("[" + LevelName(lv) + "] ") : "";

            List<ushort> children = _hub.Hierarchy.GetChildren(id);
            bool has = children.Count > 0;
            bool ex = _ex.ContainsKey(id) && _ex[id];
            string prefix = has ? (ex ? "▼ " : "▶ ") : "  ";

            Color old = GUI.color;
            // 选中行也照样热力着色 —— 选中只用 ▶ 前缀标识（2026-09-19 去掉选中态高亮）
            GUI.color = SortColor(_sortKey, aggVal[id]);
            Rect row = new Rect(ind, y, w - ind, NODE_H);
            if (GUI.Button(row, prefix + tag + name, _rankBtn))
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

            double[] aggVal = BuildAggregateSortValues(); // 每节点按当前排序键的聚合值（着色跟随 _sortKey）
            cy = DrawHeader(cy, lw, Loc.T("— 已分配层级 / Assigned —", "— Assigned hierarchy —"));
            for (int i = 0; i < ids.Count; i++)
                DrawNodeRow(ids[i], depths[i], cy + i * NODE_H, lw, aggVal);
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

        /// <summary>每区划在当前排序键下的「聚合」值（层级树节点着色用）。</summary>
        private double[] BuildAggregateSortValues()
        {
            double[] gdp = _hub.Calculator.GetAggregateGDP();
            long[] pop = _hub.Calculator.GetAggregatePopulation();
            double[] land = LandToDisplay(_hub.Calculator.GetAggregateLandValue());
            double[] m2 = AreaToM2(_hub.Calculator.GetAggregateArea());
            double[] delta = _hub.Calculator.GetAggregateBuiltValueDelta();
            double[] built = _hub.Calculator.GetAggregateBuiltArea();
            double[] income = IncomeToDisplay(_hub.Calculator.GetAggregateDisposableIncome());
            // 层级树是层级型视图 → 自定义投资额用**聚合**值（自身 + 全部下辖）
            double[] invest = InvestToDisplay(GetAggregateInvest());
            double[] v = new double[256];
            // 层级树是层级型视图 → 增速用**聚合**口径（不支持的键返回 null，SortValue 自动落回原口径）
            double[] growth = _growthMode ? GrowthForCurrentKey(true) : null;
            // 通勤距离 / 本地就业率（居住地口径）：层级树同样是**聚合**口径
            double[] commute = _hub.Calculator.GetAggregateCommuteDistance();
            double[] localEmp = AggEmployValues();   // 键 12 子口径：本地就业率 / 区域工人数
            double[] commuteTime = AggCommuteTimes();
            for (int i = 1; i < 256; i++)
                v[i] = SortValue(_sortKey, (ushort)i, gdp, pop, land, m2, delta, built, income, invest, growth, commute, localEmp, commuteTime);
            return v;
        }

        /// <summary>是否通过筛选：开启筛选时——若选中组合则只保留该组合成员；
        /// 若选中区划则保留**该区划自身 + 其层级下辖**；两者都无则全通过。
        /// ⚠️ 2026-09-19：**不再保留祖先链**（原先会把选中节点的父/祖父节点也带进来）。</summary>
        private bool PassFilter(ushort did)
        {
            if (!_filterSubtree) return true;
            if (_detailGroup >= 0 && _detailGroup < Groups.Count)
                return Groups[_detailGroup].Members.Contains(did); // 按组合成员过滤
            if (_hub.SelectedID == 0) return true;
            if (did == _hub.SelectedID) return true; // 保留选中节点自身
            // 只保留下辖。原先这里还有一条
            //   if (IsDescendantOf(SelectedID, did)) return true;   // ← 祖先链，已删
            // 会把「本身的父节点与祖父节点」也放进榜单。
            return _hub.Hierarchy.IsDescendantOf(did, _hub.SelectedID);
        }

        /// <summary>
        /// 「筛选:选中下辖」正在按**区划**筛选时，被筛选的那个区划 ID（0 = 没在按区划筛选：既可能是没开筛选，
        /// 也可能是筛选的是「组合成员」）。占比的分母要换成它的**聚合**值（自身 + 全部下辖），见 FilterAggValue。
        /// </summary>
        private ushort FilterDistrictId()
        {
            if (!_filterSubtree) return 0;
            if (_detailGroup >= 0 && _detailGroup < Groups.Count) return 0; // 按组合成员筛选，与某个区划的聚合值无关
            return _hub != null ? _hub.SelectedID : (ushort)0;
        }

        /// <summary>
        /// 该排序键能不能算「占比」——**只有求和型的键可以**：GDP / 人口 / 面积 / 建筑价值增量 /
        /// 建成区面积 / 自定义投资额。人均、地价、地均GDP、人口密度、人均可支配都是**比值型**
        /// （聚合后仍是平均值），「占多少比例」没有意义，故一律不显示。
        /// 排名视图与「所有区划」视图共用这一条判据。
        /// </summary>
        private bool ShareKey(int key)
        {
            // ⚠️ 键 12 只在「区域工人数」子模式下是求和型（能算占比）；「本地就业率」是比值，不能给占比。
            if (key == 12) return _employWorkers;
            return key == 0 || key == 1 || key == 6 || key == 7 || key == 8 || key == 10;
        }

        /// <summary>筛选区划在当前排序键下的**聚合**值（占比分母；比值型键 / 无筛选返回 0 → 不显示比例）。</summary>
        private double FilterAggDenom(ushort fid)
        {
            if (fid == 0 || !ShareKey(_sortKey)) return 0.0;
            switch (_sortKey)
            {
                case 0: return _hub.Calculator.GetAggregateGDP()[fid];
                case 1: return _hub.Calculator.GetAggregatePopulation()[fid];
                case 6: return AreaToM2(_hub.Calculator.GetAggregateArea())[fid];
                case 7: return _hub.Calculator.GetAggregateBuiltValueDelta()[fid];
                case 8: return _hub.Calculator.GetAggregateBuiltArea()[fid];
                case 10: return InvestToDisplay(GetAggregateInvest())[fid];
                case 12: return _hub.Calculator.GetAggregatePanelWorkers()[fid];   // 区域工人数
                default: return 0.0;
            }
        }

        /// <summary>
        /// 全图各原版区划「自身值」合计（当前排序键口径）——**没筛选**时的占比分母。
        /// 列表列的就是全部区划，于是各行占比之和 = 100%（用同一套 self 数组口径，不会与行值对不上）。
        /// </summary>
        private double TotalSelfValue(double[] gdp, long[] pop, double[] landD, double[] m2, double[] delta, double[] builtS, double[] incomeD, double[] investD,
            double[] employD = null)
        {
            double t = 0;
            ushort[] all = _hub.GetVanillaDistricts();
            for (int i = 0; i < all.Length; i++)
                // 通勤两列不用传（它们不是求和型，ShareKey 也不会让它们走到这里）；
                // 「就业相关」必须传：它的「区域工人数」子模式是求和型，要靠它算占比分母
                t += SortValue(_sortKey, all[i], gdp, pop, landD, m2, delta, builtS, incomeD, investD,
                    null, null, employD);
            return t;
        }

        /// <summary>地均GDP = GDP / 面积(m²)，用于排序（0 时返回 0）。</summary>
        private static double GdpPerArea(ushort did, double[] gdp, double[] m2)
        {
            if (m2 == null || m2[did] <= 0) return 0.0;
            return gdp[did] / m2[did];
        }

        /// <summary>面积为已转换好的 m²（GetDistrictArea 直接返回 m²），此处透传。</summary>
        private static double[] AreaToM2(double[] m2)
        {
            return m2;
        }

        private static double SortValue(int key, ushort did, double[] gdp, long[] pop, double[] land = null, double[] m2 = null, double[] delta = null, double[] built = null, double[] income = null, double[] invest = null, double[] growth = null, double[] commute = null, double[] localEmp = null, double[] commuteTime = null)
        {
            // 增速模式：调用方把该键的增速数组传进来（自身 / 聚合由调用方按视图决定），整行直接用增速%。
            // 算不了增速的键（7=建筑价值增量、10=自定义投资额、11=通勤距离、12=本地就业率、13=通勤时间）
            // 调用方传 null → 原样落到下面的原口径。
            if (growth != null) return growth[did];
            switch (key)
            {
                case 1: return pop[did];
                case 2: return pop[did] > 0 ? gdp[did] / pop[did] : 0.0;
                case 3: return land != null ? land[did] : 0.0;
                case 4: return GdpPerArea(did, gdp, m2);
                case 5: // 人口密度 = 人口/面积(km²)；m2 为平方米，km² = m2/1e6，故 = pop*1e6/m2
                    return (m2 != null && m2[did] > 0) ? pop[did] * 1000000.0 / m2[did] : 0.0;
                case 6: return m2 != null ? m2[did] : 0.0; // 面积（m²）
                case 7: return delta != null ? delta[did] : 0.0; // 建筑价值增量
                case 8: return built != null ? built[did] : 0.0; // 建成区面积（m²）
                case 9: return income != null ? income[did] : 0.0; // 人均可支配收入
                // 自定义政府投资额：调用方决定传**自身**还是**聚合**
                // （约定：平铺「所有区划」列表传自身，层级树/单级排名/组合传聚合）
                case 10: return invest != null ? invest[did] : 0.0;
                // 平均通勤距离（km）/ 本地就业率（%）：居住地口径，平铺列表传自身、层级树/排名传聚合
                case 11: return commute != null ? commute[did] : 0.0;
                case 12: return localEmp != null ? localEmp[did] : 0.0;
                case 13: return commuteTime != null ? commuteTime[did] : 0.0;   // 平均通勤时间（分钟）
                default: return gdp[did];
            }
        }

        /// <summary>
        /// 排序方向 + 「0 排最后」的统一比较器（2026-09-27 用户定）：
        ///   · 键 11（平均通勤距离）/ 13（平均通勤时间）**越短越好 → 升序**；
        ///   · 值 **0 = 没有数据**（区划没有就业居民 / 这一轮没采到通勤趟数），**无论什么方向一律沉底**，
        ///     并且显示为**白色**（见 SortColor）—— 否则它们会挤在「最近 / 最快」的前几名里，看着像好区划。
        ///   · 其余键维持原来的**降序**（值越大越靠前）。
        /// ⚠️ 「0 沉底」只对 11/13 生效：键 7（建筑价值增量）/ 10（投资额）可以为负，0 是有意义的值。
        /// </summary>
        private int CompareRows(double a, double b)
        {
            if (_sortKey == 11 || _sortKey == 13)
            {
                bool az = a <= 0.0, bz = b <= 0.0;
                if (az != bz) return az ? 1 : -1;   // 无数据的沉到最后
                return a.CompareTo(b);              // 升序：越短越好
            }
            return b.CompareTo(a);                  // 其余键：降序
        }

        /// <summary>
        /// 当前口径下的**自身**平均通勤时间数组（分钟）：开了「最长10%」→ 每区划留存样本里最大的 10%
        /// 的平均（`GetCommuteTopTime`），否则→ 全部样本的均值。**列表 / 排名 / 组合都要走这两个助手**，
        /// 别直接调 `GetCommuteTime()` —— 直接调会让四处口径不一致（用户 2026-09-27 加「最长10%」时定的）。
        /// </summary>
        private double[] SelfCommuteTimes()
        {
            DistrictFinanceCalculator c = _hub.Calculator;
            return _commuteTopMode ? c.GetCommuteTopTime() : c.GetCommuteTime();
        }

        /// <summary>当前口径下的**聚合**（自身 + 全部下辖）平均通勤时间数组（分钟）。</summary>
        private double[] AggCommuteTimes()
        {
            DistrictFinanceCalculator c = _hub.Calculator;
            return _commuteTopMode ? c.GetAggregateCommuteTopTime() : c.GetAggregateCommuteTime();
        }

        // ================== 键 12「就业相关」的两个子口径（用户 2026-09-28）==================
        // ⚠️ 与「最长10%」同规矩：**取这一列的四个地方（所有区划 / 排名 / 聚合 / 组合）一律走下面两个助手**，
        //    别再直接调 GetLocalEmploymentRate / GetAggregateLocalEmploymentRate —— 那会让子口径不一致
        //    （有的地方显示就业率、有的地方显示工人数）。

        /// <summary>当前子口径下的**自身**值数组（本地就业率 % / 区域工人数 人）。</summary>
        private double[] SelfEmployValues()
        {
            DistrictFinanceCalculator c = _hub.Calculator;
            return _employWorkers ? c.GetDistrictPanelWorkers() : c.GetLocalEmploymentRate();
        }

        /// <summary>当前子口径下的**聚合**（自身 + 全部下辖）值数组。</summary>
        private double[] AggEmployValues()
        {
            DistrictFinanceCalculator c = _hub.Calculator;
            return _employWorkers ? c.GetAggregatePanelWorkers() : c.GetAggregateLocalEmploymentRate();
        }

        /// <summary>键 12 当前子口径下**能否算增速**：只有「区域工人数」能（走周库 v8 的 PanelWorkers 列）；
        /// 「本地就业率」是比值，没有增速（与 11/13 一样保持原口径）。</summary>
        private bool EmployGrowthSupported()
        {
            return _sortKey != 12 || _employWorkers;
        }

        /// <summary>增速数组（当前键 + 当前子口径）；算不了返回 null（调用方据此退回原口径）。</summary>
        private double[] GrowthForCurrentKey(bool aggregate)
        {
            if (!EmployGrowthSupported()) return null;
            return _hub.Calculator.GetGrowth(_sortKey, aggregate);
        }

        /// <summary>增速的「实时值 / 基准值」两列（组合视图要按成员先求和再算增速）；算不了返回 false。</summary>
        private bool GrowthRawForCurrentKey(bool aggregate, out double[] live, out double[] basev)
        {
            live = null; basev = null;
            if (!EmployGrowthSupported()) return false;
            return _hub.Calculator.GrowthRaw(_sortKey, aggregate, out live, out basev);
        }

        /// <summary>键 12 当前子口径在**图例/标题**里用的名字（键名本身统一叫「就业相关」）。</summary>
        private string EmployModeName()
        {
            return _employWorkers ? Loc.T("区域工人数", "District workers")
                                  : Loc.T("本地就业率", "Local employment");
        }

        /// <summary>
        /// 「平均通勤时间」的提示文字（其它键返回空串）。用户 2026-09-27：这段提示**保留**，但要
        /// **单独占一行并自动折行** —— 不能挂到列表标题后面（右边被「更多 ▾」占掉 54px，会溢出/被裁）。
        /// 由 `DrawCommuteControls()` 在按钮那一行下面绘制。
        /// </summary>
        private string CommuteHint()
        {
            if (_sortKey != 13) return "";
            // ⚠️ **手动断成三行**（用户 2026-09-27：「还是长了…文字要分行」）：一行一个意思，
            //    每行都短到能整行放下（右边还要给「更多 ▾」那 54px 让位），别写成一大句。
            //    第 3 行是**换算口径**（用户 2026-09-27 要求写明）：时间按 realtime 设定 60 模拟帧 = 10 秒。
            return Loc.T("⚠ 逐帧跟踪在途市民，性能消耗较大。\n攒够样本（≥ 就业居民数÷8）才显示，不足＝白色 0。\n时间按 realtime 换算：60 模拟帧 = 10 秒（贴近现实）。",
                         "[!] Follows travelling citizens (CPU-heavy).\nShows once it has enough samples (>= 1/8 of its employed residents), below that: white 0.\nTime follows the realtime setting: 60 sim frames = 10 s (closer to real life).");
        }

        /// <summary>列表标题里的方向词（键 11/13 是升序，其余降序）。</summary>
        private string SortDirWord()
        {
            bool asc = (_sortKey == 11 || _sortKey == 13);
            return Loc.IsEn ? (asc ? "asc" : "desc") : (asc ? "升序" : "降序");
        }

        private string FormatSortValue(double value, double progress = -1.0)
        {
            // 增速模式：值是百分数（SortValue 已换成增速）。带符号、**两位小数**（图例也是两位，两边一致）；
            // ±0.005% 以内归零，避免显示成「-0.00%」
            if (GrowthActive(_sortKey))
            {
                double g = System.Math.Abs(value) < 0.005 ? 0.0 : value;
                string gs = g > 0 ? "+" : (g < 0 ? "-" : "");
                return gs + System.Math.Abs(g).ToString("0.00") + "%";
            }
            string cur = CurrencySymbol();
            switch (_sortKey)
            {
                case 1: return value.ToString("N0") + Loc.T(" 人", " pop");
                case 2: return cur + F(value) + Loc.T("/人", "/cap");
                case 3: return value.ToString("0.00") + " " + LandUnit();
                case 4: return cur + F(value) + Loc.T("/m²", "/m²");
                case 5: return value.ToString("0.0") + Loc.T(" 人/km²", " pop/km²");
                case 6: return AreaKm2(value) + Loc.T(" km²", " km²");
                case 7: // 建筑价值增量（带符号；微小值归零，避免显示成 -0.00）
                    {
                        double v = System.Math.Abs(value) < 0.5 ? 0.0 : value;
                        string sign = v > 0 ? "+" : (v < 0 ? "-" : "");
                        return sign + cur + F(System.Math.Abs(v));
                    }
                case 8: return AreaKm2(value) + Loc.T(" km²", " km²"); // 建成区面积
                case 9: return cur + F(value) + Loc.T("/人", "/cap"); // 人均可支配收入
                case 10: // 自定义政府投资额（带符号，可能是负数——冲减后；微小值归零避免 -0.00）
                    {
                        double v = System.Math.Abs(value) < 0.5 ? 0.0 : value;
                        string sign = v > 0 ? "+" : (v < 0 ? "-" : "");
                        return sign + cur + F(System.Math.Abs(v));
                    }
                case 11: return value.ToString("0.00") + Loc.T(" km", " km");   // 平均通勤距离
                case 12: return _employWorkers
                    ? value.ToString("N0") + Loc.T(" 人", " workers")           // 区域工人数（原版面板口径）
                    : value.ToString("0.0") + "%";                              // 本地就业率
                case 13: // 平均通勤时间（现实等效）
                    {
                        string s13 = value.ToString("0.0") + Loc.T(" 分钟", " min");
                        // **白色 0＝还没攒够门槛**的行，后面注明已统计的百分比（用户 2026-09-27：
                        // 「在白色状态的后面注明已统计的百分比」）—— 让人知道还要等多久。
                        // progress < 0 = 不适用（该区划没有就业居民），不注。
                        if (value <= 0.0 && progress >= 0.0)
                            s13 += Loc.T(" 已统计 ", " counted ") + progress.ToString("0") + "%";
                        return s13;
                    }
                default: return cur + F((long)value);
            }
        }

        /// <summary>
        /// 「增速」当前对某个键**是否真的生效**：键 12（就业相关）只有「区域工人数」子模式能算增速，
        /// 「本地就业率」是比值 → 不算（用户 2026-09-28 选定）。**取色 / 标签 / 图例 / 格式化一律用它**，
        /// 只要有一处漏判，就会出现"值是原值、颜色却按增速%上色"这种数字与颜色对不上的情况。
        /// </summary>
        private bool GrowthActive(int key)
        {
            if (!_growthMode) return false;
            if (!DistrictFinanceCalculator.GrowthSupported(key)) return false;
            return key != 12 || _employWorkers;
        }

        /// <summary>按排序键取热力色。**非 static**：增速模式下要按 _growthMode 换成百分数档位。</summary>
        private Color SortColor(int key, double value)
        {
            // 增速模式：能算增速的键一律走「以 0 为中心」的百分数分档（传进来的 value 已是增速%）
            if (GrowthActive(key)) return GrowthColor(value);
            if (key == 1) return PopColor((long)value);
            if (key == 2) return GdpPerCapitaColor(value);
            if (key == 3) return LandColor(value);
            if (key == 4) return GdpAreaColor(value);      // 地均GDP：现实货币/m² 分档
            if (key == 5) return PopDensityColor(value);   // 人口密度：人/km² 分档
            if (key == 6) return AreaColor(value);         // 面积：km² 分档
            if (key == 7) return BuiltDeltaColor(value);   // 建筑价值增量：以 0 为中心的对称分档
            if (key == 8) return BuiltAreaColor(value);    // 建成区面积：km² 分级，阈值=区域面积档位减半
            if (key == 9) return IncomePerCapitaColor(value); // 人均可支配收入
            if (key == 10) return BuiltDeltaColor(value);     // 自定义政府投资额：复用建筑价值增量的配色
            // 通勤距离 / 时间：**0 = 无数据**（没有就业居民 / 没采到趟数）→ 白字 + 排最后（见 CompareRows）
            if (key == 11) return value > 0.0 ? CommuteColor(value) : Color.white;
            if (key == 12) return _employWorkers ? WorkersColor(value) : LocalEmpColor(value);
                                                               // 12：「区域工人数」（人口档位÷2，人） 或 「本地就业率」（%）
            if (key == 13) return value > 0.0 ? CommuteTimeColor(value) : Color.white;
            return GdpColor(value);
        }

        /// <summary>排序键名。增速模式下给「能算增速」的键统一加后缀（表头/图例标题都走这里）。</summary>
        private string SortLabel(int key)
        {
            string n = SortLabelCore(key);
            if (GrowthActive(key))
                return n + Loc.T("增速", " Δ%");
            return n;
        }

        private static string SortLabelCore(int key)
        {
            switch (key)
            {
                case 1: return Loc.T("人口", "Population");
                case 2: return Loc.T("人均GDP", "GDP/capita");
                case 3: return Loc.T("地价", "Land value");
                case 4: return Loc.T("地均GDP", "GDP/m²");
                case 5: return Loc.T("人口密度", "Population density");
                case 6: return Loc.T("面积", "Area");
                case 7: return Loc.T("建筑价值增量", "Building value Δ");
                case 8: return Loc.T("建成区面积", "Built-up area");
                case 9: return Loc.T("人均可支配", "Disposable/capita");
                case 10: return Loc.T("自定义政府投资额", "Custom gov. investment");
                case 11: return Loc.T("平均通勤距离", "Avg commute");
                case 12: return Loc.T("就业相关", "Employment");   // 键名统一叫「就业相关」（用户 2026-09-28）；
                                                                   // 子口径（本地就业率/区域工人数）由图例标题与两个按钮体现
                case 13: return Loc.T("平均通勤时间", "Avg commute time");
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
            double[] m2 = AreaToM2(_hub.Calculator.GetDistrictArea());
            double[] delta = _hub.Calculator.GetDistrictBuiltValueDelta();
            double[] builtS = _hub.Calculator.GetDistrictBuiltArea();
            double[] incomeD = IncomeToDisplay(_hub.Calculator.GetDistrictDisposableIncome());
            // 「所有区划」是平铺列表 → 自定义投资额用**自身**值（层级型视图才用聚合）
            double[] investD = InvestToDisplay(GetInvestSelf());
            ushort[] all = _hub.GetVanillaDistricts();
            // 「所有区划」是平铺列表 → 增速用**自身**口径
            double[] growth = _growthMode ? GrowthForCurrentKey(false) : null;
            // 通勤距离 / 本地就业率（居住地口径）：平铺列表用**自身**值
            double[] commuteD = _hub.Calculator.GetCommuteDistance();
            double[] localEmpD = SelfEmployValues();   // 键 12 子口径：本地就业率 / 区域工人数
            double[] commuteTimeD = SelfCommuteTimes();
            double[] commuteProg = _hub.Calculator.GetCommuteProgress();   // 白色 0 的行要注明「已统计 x%」

            var items = new List<KeyValuePair<ushort, double>>();
            foreach (ushort did in all)
            {
                if (string.IsNullOrEmpty(_hub.GetVanillaDistrictName(did))) continue;
                if (!PassFilter(did)) continue;
                items.Add(new KeyValuePair<ushort, double>(did, SortValue(_sortKey, did, gdp, pop, landD, m2, delta, builtS, incomeD, investD, growth, commuteD, localEmpD, commuteTimeD)));
            }
            items.Sort((a, b) => CompareRows(a.Value, b.Value)); // 方向见 CompareRows（11/13 升序、0 沉底）

            float contentH = HEADER_H + items.Count * NODE_H + 24f;
            _sortScroll = GUI.BeginScrollView(list, _sortScroll, new Rect(0, 0, list.width - 20, contentH));
            float lw = list.width - 20;
            float cy = DrawHeader(0, lw,
                Loc.T("— 各区划 " + SortLabel(_sortKey) + " 排名（" + SortDirWord() + "，点击查看）—",
                      "— All districts by " + SortLabel(_sortKey) + " (" + SortDirWord() + ", click to view) —"));

            // 占比分母：**筛选了区划时**用该区划的**聚合值**（自身 + 全部下辖），否则用全图自身值合计。
            // 分母 > 0 才显示（增量 / 投资额这类可以为负，负的分母算占比没意义）。
            // 增速模式下这一行显示的是增速%，占比不适用。
            double shareTotal = 0.0;
            if (growth == null && ShareKey(_sortKey))
            {
                ushort fid = FilterDistrictId();
                shareTotal = fid != 0
                    ? FilterAggDenom(fid)
                    : TotalSelfValue(gdp, pop, landD, m2, delta, builtS, incomeD, investD, localEmpD);
            }
            bool showShare = shareTotal > 0.0;
            for (int i = 0; i < items.Count; i++)
            {
                ushort did = items[i].Key;
                string name = _hub.GetVanillaDistrictName(did);
                bool selected = did == _hub.SelectedID;
                // 分子就是这一行**显示的那个自身值**，与分母同一口径，不会出现「显示 A、占比按 B 算」
                string share = showShare
                    ? Loc.T("  占比 ", "  share ") + Share(items[i].Value, shareTotal)
                    : "";
                string line = (selected ? "▶ " : "  ")
                    + string.Format("{0}. {1}    {2}", i + 1, name,
                        FormatSortValue(items[i].Value, _sortKey == 13 ? commuteProg[did] : -1.0)) + share;
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
            double[] aggM2 = AreaToM2(_hub.Calculator.GetAggregateArea());
            double[] selfM2 = AreaToM2(_hub.Calculator.GetDistrictArea());
            double[] aggDelta = _hub.Calculator.GetAggregateBuiltValueDelta();
            double[] selfDelta = _hub.Calculator.GetDistrictBuiltValueDelta();
            double[] aggBuiltS = _hub.Calculator.GetAggregateBuiltArea();
            double[] selfBuiltS = _hub.Calculator.GetDistrictBuiltArea();
            double[] aggIncomeD = IncomeToDisplay(_hub.Calculator.GetAggregateDisposableIncome());
            double[] selfIncomeD = IncomeToDisplay(_hub.Calculator.GetDistrictDisposableIncome());
            // 单级排名是层级型视图 → 本列表用**聚合**；「直辖」附加条目与其它指标一样用**自身**
            double[] aggInvestD = InvestToDisplay(GetAggregateInvest());
            double[] selfInvestD = InvestToDisplay(GetInvestSelf());
            // 增速两套：本列表的区划用**聚合**口径，「直辖」附加条目与其它指标一样用**自身**口径
            double[] growthAgg = _growthMode ? GrowthForCurrentKey(true) : null;
            double[] growthSelf = _growthMode ? GrowthForCurrentKey(false) : null;
            // 通勤距离 / 本地就业率（居住地口径）：本列表用**聚合**（子树内 OD 对和 ÷ 子树就业居民），直辖行用**自身**
            double[] aggCommuteD = _hub.Calculator.GetAggregateCommuteDistance();
            double[] aggLocalEmpD = AggEmployValues();   // 键 12 子口径
            double[] selfCommuteD = _hub.Calculator.GetCommuteDistance();
            double[] selfLocalEmpD = SelfEmployValues();   // 键 12 子口径
            double[] aggCommuteTime = AggCommuteTimes();
            double[] selfCommuteTime = SelfCommuteTimes();
            double[] aggCommuteProg = _hub.Calculator.GetAggregateCommuteProgress();
            double[] selfCommuteProg = _hub.Calculator.GetCommuteProgress();

            var items = new List<RankEntry>();
            foreach (ushort did in _hub.Hierarchy.GetDistrictsByLevel(level))
            {
                if (string.IsNullOrEmpty(_hub.GetVanillaDistrictName(did))) continue;
                if (!PassFilter(did)) continue;
                items.Add(new RankEntry { id = did, value = SortValue(_sortKey, did, agg, aggPop, aggLandD, aggM2, aggDelta, aggBuiltS, aggIncomeD, aggInvestD, growthAgg, aggCommuteD, aggLocalEmpD, aggCommuteTime), parentLevel = false });
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
                    items.Add(new RankEntry { id = did, value = SortValue(_sortKey, did, selfGdp, selfPop, selfLandD, selfM2, selfDelta, selfBuiltS, selfIncomeD, selfInvestD, growthSelf, selfCommuteD, selfLocalEmpD, selfCommuteTime), parentLevel = true });
                }
            }

            items.Sort((a, b) => CompareRows(a.value, b.value)); // 方向见 CompareRows（11/13 升序、0 沉底）

            float contentH = HEADER_H + items.Count * NODE_H + 24f;
            _sortScroll = GUI.BeginScrollView(list, _sortScroll, new Rect(0, 0, list.width - 20, contentH));
            float lw = list.width - 20;
            string head = Loc.T("— " + LevelName(level) + " 排名（聚合" + SortLabel(_sortKey) + "，点击查看）—",
                                "— " + LevelName(level) + " ranking (aggregate " + SortLabel(_sortKey) + ", click to view) —");
            float cy = DrawHeader(0, lw, head);

            // 有**区划**筛选时，每行的聚合值后面跟一个「占筛选区划聚合值的比例」
            // （例：筛选「a 区」后看乡镇视图 → 每个乡镇后面显示占 a 区聚合值的比例）。
            // 只在求和型指标上显示。
            // 「直辖」附加条目**也显示**：它的值是**自身值**，也就是筛选区划「直辖的那部分」，
            // 所以 a 的直辖行占比 = a 自身 ÷ a 聚合（自身+全部下辖）—— 反映 a 有多少是自己直管、
            // 有多少交给了下级。筛选 a 时上一级里只有 a 能通过 PassFilter，所以那行就是 a 本人。
            ushort fid = FilterDistrictId();
            double denom = FilterAggDenom(fid);
            bool showShare = denom > 0.0 && growthAgg == null;

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
                // 分母是筛选区划的聚合值，分子就是这一行显示的那个值（直辖行是自身值）—— 同一口径，不另算
                string share = showShare
                    ? Loc.T("  占比 ", "  share ") + Share(items[i].value, denom)
                    : "";
                string line = (selected ? "▶ " : "  ")
                    + string.Format("{0}. {1}    {2}", rank, label,
                        FormatSortValue(items[i].value, _sortKey == 13
                            ? (items[i].parentLevel ? selfCommuteProg[did] : aggCommuteProg[did]) : -1.0)) + share;
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
            double[] areaRaw = _hub.Calculator.GetDistrictArea();
            double[] deltaRaw = _hub.Calculator.GetDistrictBuiltValueDelta();
            double[] builtRaw = _hub.Calculator.GetDistrictBuiltArea();
            double[] incomeRaw = IncomeToDisplay(_hub.Calculator.GetDistrictDisposableIncome());
            // 自定义政府投资额：与组合里**其它所有指标**同口径 —— 都是「成员**自身**值合计」，
            // 不做层级聚合（本视图的标题与说明也是这么写的，且避免父子同组时重复计入）
            double[] investRaw = InvestToDisplay(GetInvestSelf());
            ushort[] all = _hub.GetVanillaDistricts();
            // 增速：组合用**成员自身值**口径的当前值/基准值（求和后现算，见 GroupValue）
            double[] gLive, gBase;
            if (!_growthMode || !GrowthRawForCurrentKey(false, out gLive, out gBase))
            { gLive = null; gBase = null; }
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
            double totalGdp = TotalGdp(gdp);
            long totalPop = TotalPop(pop);
            // 「区域工人数」的占比分母（全图自身值合计）——只在工人子模式下用到
            double[] workSelf = _hub.Calculator.GetDistrictPanelWorkers();
            double totalWork = (_sortKey == 12 && _employWorkers) ? TotalSumD(workSelf) : 0.0;
            // 通勤距离 / 本地就业率：**每帧按成员预算一次**，排序比较器与行渲染都只读（见 GroupValue 注释）
            double[] gCommute, gLocalEmp, gCommuteTime, gCommuteProg;
            BuildGroupCommuteValues(out gCommute, out gLocalEmp, out gCommuteTime, out gCommuteProg);
            var order = new List<int>();
            for (int i = 0; i < Groups.Count; i++) order.Add(i);
            order.Sort((a, b) => CompareRows(
                GroupValue(a, gdp, pop, landRaw, areaRaw, deltaRaw, builtRaw, incomeRaw, investRaw, gLive, gBase, gCommute, gLocalEmp, gCommuteTime),
                GroupValue(b, gdp, pop, landRaw, areaRaw, deltaRaw, builtRaw, incomeRaw, investRaw, gLive, gBase, gCommute, gLocalEmp, gCommuteTime)));

            int toDelete = -1;
            for (int r = 0; r < order.Count; r++)
            {
                int gi = order[r];
                GroupData g = Groups[gi];
                bool active = gi == _activeGroupIdx;
                double gval = GroupValue(gi, gdp, pop, landRaw, areaRaw, deltaRaw, builtRaw, incomeRaw, investRaw, gLive, gBase, gCommute, gLocalEmp, gCommuteTime);
                string share = "";
                // 增速模式下这一行显示的是增速%，占比（哪来的份额）不适用
                if (gLive == null)
                {
                    if (_sortKey == 0) share = Loc.T("  占比 ", "  share ") + Share(GroupGdp(gi, gdp), totalGdp);
                    else if (_sortKey == 1) share = Loc.T("  占比 ", "  share ") + Share(GroupPop(gi, pop), totalPop);
                    // 「就业相关 · 区域工人数」（用户 2026-09-28：组合后面也要写占比）——
                    // 成员自身值合计 ÷ 全图合计（与「所有区划」的占比同一套口径）
                    else if (_sortKey == 12 && _employWorkers && totalWork > 0.0)
                        share = Loc.T("  占比 ", "  share ") + Share(GroupSumD(gi, workSelf), totalWork);
                }
                string line = (active ? "▶ " : "  ") + (r + 1) + ". " + g.Name
                    + "  " + FormatSortValue(gval, _sortKey == 13 ? gCommuteProg[gi] : -1.0) + share
                    + "  " + Loc.T("成员", "mem") + g.Members.Count;
                Rect rowRect = new Rect(x0, cy, lw - 146, NODE_H);
                Color old = GUI.color;
                // 选中行也照样热力着色 —— 选中只用 ▶ 前缀标识（2026-09-19 去掉选中态高亮）
                GUI.color = SortColor(_sortKey, gval);
                if (GUI.Button(rowRect, line, _rankBtn))
                {
                    // 激活该组并切换成员展开状态（可逆）；同时顶部显示该组合合计
                    _activeGroupIdx = gi;
                    _memberExpand = !_memberExpand;
                    SelectGroup(gi);
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
                        if (GUI.Button(new Rect(0, mcy, lw, NODE_H), lb, _rankBtn))
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

        // ================== 「自定义政府投资额」视图 ==================
        // 本视图**自成一体**：不接入 _sortKey / SortValue / SortColor / GroupValue 那套全局排序系统，
        // 自带排序、取色与格式化，只调用现有的只读工具（BuiltDeltaColor / F / CurrencySymbol / TrackImeCaret）。

        /// <summary>
        /// **录入**换算系数：把输入框里那个「本周期的总额」换回**每期的原始值**（kr/周）——
        /// `每期原始值 = 输入额 ÷ InvestMult()`。所以它必须带上周期周数 N。
        ///
        /// ⚠️ 注意它**只用于录入**，不用于显示。**显示**（`InvestToDisplay`）只乘 `LandMult()` ——
        /// 「乘 N」这件事在分期模型里是自然发生的：窗口里有 N 期，和就是 N 倍。
        /// 若显示也乘 N，则「月视图里录一笔、切到周视图看」会变成 1/N² 而不是 1/N（实测差 4 倍），
        /// 与「建筑价值增量」切周期时按 1/N 缩放的规律对不上。
        ///
        /// ⚠️ 与「建筑价值增量」共用**价格系数** `LandMult()`（420 / 60）：图例、取值、录入三处同一套，
        /// 都是地价那一族的口径（增量 = 面积 × 显示地价）。⚠️ 别擅自换到 GDP 的流量系数上
        /// （2026-09-28 试过一次，被用户否掉：「地价视图的比例是不一样的，你给改了干啥」）。
        /// </summary>
        private static double InvestMult()
        {
            return LandMult() * CurrentPeriodWeeks();
        }

        /// <summary>当前统计周期的周数（周 1 / 月 4 / 季 13 / 年 52 / 5年 260）。
        /// 三个地方用它：录入时**分几期**、统计时**取最近几周**的窗口、`InvestMult()` 的分母 —— 必须是同一个数。</summary>
        private static int CurrentPeriodWeeks()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            int period = (hub != null && hub.Settings != null) ? hub.Settings.DisplayPeriod : 0;
            int n = ModSettings.PeriodWeeks(period);
            return n < 1 ? 1 : n;
        }

        /// <summary>单位键倍率：0 = k(×1000，默认) / 1 = m(×1e6) / 2 = b(×1e9)。</summary>
        private static double InvestUnitMult(int unit)
        {
            if (unit == 1) return 1000000.0;
            if (unit == 2) return 1000000000.0;
            return 1000.0;
        }

        /// <summary>
        /// 把输入串解析成数值（全项目唯一的 string→数值解析器；F() 是反方向）。
        /// 忽略空格与千分位逗号；允许前导负号（用于**冲减**已录入的额）；
        /// 可选后缀 k/m/b（大小写不敏感）会**覆盖**当前单位档，兼容直接打 "1.5k"。
        /// 遇到其它字符或不含数字则返回 false。
        /// </summary>
        private static bool ParseKmb(string s, double unitMult, out double value)
        {
            value = 0.0;
            if (string.IsNullOrEmpty(s)) return false;
            double mult = unitMult;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == ',') continue;
                if ((c >= '0' && c <= '9') || c == '.' || c == '-') { sb.Append(c); continue; }
                if (c == 'k' || c == 'K') { mult = 1000.0; continue; }
                if (c == 'm' || c == 'M') { mult = 1000000.0; continue; }
                if (c == 'b' || c == 'B') { mult = 1000000000.0; continue; }
                return false;
            }
            if (sb.Length == 0) return false;
            double v;
            if (!double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v)) return false;
            value = v * mult;
            return true;
        }

        /// <summary>
        /// 各区划**自身**的投资额原始值（未乘显示系数）—— 只累加**最近 x 周**窗口内已计入的分期，
        /// x = 当前统计周期的周数（周 1 / 月 4 / 季 13 / 年 52 / 5年 260）。
        ///
        /// 口径与「建筑价值增量」完全对齐：增量看「最近 x 周里多盖了多少」，
        /// 这里看「最近 x 周里计入了多少」；两者都是滚动窗口，都随周期切换而变。
        /// 于是：周视图录入即整笔显示；月视图录入先显示 1/4，之后逐周涨到整笔，
        /// 再过一个周期又逐周退出窗口（滚动窗口的固有行为，**不是 bug**）。
        /// </summary>
        private double[] GetInvestSelf()
        {
            double[] r = new double[256];
            if (_hub == null || _hub.Investments == null || _hub.Investments.Count == 0) return r;
            long curW = (long)InvestWeek.Now;   // 记账刻度（帧周 / RealTime 日历周），与录入同源
            long lo = curW + 1 - CurrentPeriodWeeks();   // 窗口下界（含）：最近 x 周
            foreach (KeyValuePair<ushort, List<InvestInstallment>> kv in _hub.Investments)
            {
                List<InvestInstallment> list = kv.Value;
                if (kv.Key >= 256 || list == null) continue;
                double s = 0.0;
                for (int i = 0; i < list.Count; i++)
                {
                    long w = (long)list[i].Week;
                    if (w <= curW && w >= lo) s += list[i].Amount;
                }
                r[kv.Key] = s;
            }
            return r;
        }

        /// <summary>某区划**待计入**的分期（记账周 &gt; 当前周）：原始值合计与份数。用于目标行提示。</summary>
        private void GetPendingInvest(ushort id, out double rawSum, out int count)
        {
            rawSum = 0.0; count = 0;
            if (_hub == null || _hub.Investments == null || id == 0) return;
            List<InvestInstallment> list;
            if (!_hub.Investments.TryGetValue(id, out list) || list == null) return;
            long curW = (long)InvestWeek.Now;   // 记账刻度（帧周 / RealTime 日历周），与录入同源
            for (int i = 0; i < list.Count; i++)
                if ((long)list[i].Week > curW) { rawSum += list[i].Amount; count++; }
        }

        /// <summary>
        /// 把一份金额并入某一周。**同一周的多笔合并成一条** —— 否则同一周连续追加几次，
        /// .inv 里就会堆出一串「周号相同、金额不同」的行（内容等价、只是白占地方）。
        /// 注意 InvestInstallment 是 struct，改完必须回写列表。
        /// </summary>
        private static void AddInstallment(List<InvestInstallment> list, uint week, double amount)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Week != week) continue;
                InvestInstallment e = list[i];
                e.Amount += amount;
                list[i] = e;
                return;
            }
            list.Add(new InvestInstallment(week, amount));
        }

        /// <summary>
        /// 原始值 → 显示值（仿 IncomeToDisplay 的 f==1 免分配写法）。
        /// **只乘 `LandMult()`，不乘周期周数** —— 窗口里已经装着 N 期分期，和自然就是 N 倍
        /// （与「建筑价值增量」的缩放规律一致，详见 InvestMult 的注释）。
        /// </summary>
        private double[] InvestToDisplay(double[] raw)
        {
            double f = LandMult();
            if (f == 1.0) return raw;
            double[] r = new double[raw.Length];
            for (int i = 0; i < raw.Length; i++) r[i] = raw[i] * f;
            return r;
        }

        /// <summary>
        /// 各区划的**聚合**投资额（自身 + 全部下辖，递归）。照抄
        /// Calculator.AccumArea(DistrictFinanceCalculator.cs:314) 的后序累加 + visited 防环。
        /// 注：按约定「平铺所有区划的列表用自身值，层级型视图用聚合值」——
        /// 本视图是平铺列表，所以走 GetInvestSelf()；此函数备用（将来层级树/单级排名要用它就得改现有排序系统）。
        /// </summary>
        private double[] GetAggregateInvest()
        {
            double[] self = GetInvestSelf();
            double[] agg = new double[256];
            for (int i = 0; i < 256; i++) agg[i] = self[i];
            if (_hub == null || _hub.Hierarchy == null) return agg;
            double[] sum = new double[256];
            var visited = new HashSet<ushort>();
            foreach (ushort root in _hub.Hierarchy.GetRootNodes())
                AccumInvest(root, self, sum, _hub.Hierarchy, visited);
            foreach (ushort id in new List<ushort>(visited)) agg[id] = sum[id];
            return agg;
        }

        private static void AccumInvest(ushort d, double[] self, double[] sum,
            DistrictHierarchy h, HashSet<ushort> visited)
        {
            if (!visited.Add(d)) return;   // 防环（层级成环会递归死循环）
            double s = self[d];
            foreach (ushort child in h.GetChildren(d))
            {
                AccumInvest(child, self, sum, h, visited);
                s += sum[child];
            }
            sum[d] = s;
        }

        /// <summary>「自定义政府投资额」视图：上方输入行 + 下方所有区划列表（按**本周期已计入额**降序）。</summary>
        private void DrawInvestView(Rect list)
        {
            ushort[] all = _hub.GetVanillaDistricts();
            double[] self = GetInvestSelf();
            double[] disp = InvestToDisplay(self);
            int weeks = CurrentPeriodWeeks();   // 分期数 = 统计窗口周数 = 当前周期周数

            float lw = list.width - 20;
            float x0 = list.x;
            float cy = list.y;

            // 编辑目标若已被删掉 → 自动清空
            if (_investEditTarget != 0 && string.IsNullOrEmpty(_hub.GetVanillaDistrictName(_investEditTarget)))
                _investEditTarget = 0;

            // 记账刻度提示：勾了「跟随 RealTime 日历」时把「周」的含义写明
            // （否则「1 周」到底是 4096 模拟帧还是游戏日历上的一周，差得很远）。
            // 单开一行（_diag 小字），不挤进标题行 —— 标题本来就快占满宽度了。
            GUI.Label(new Rect(x0, cy, lw, HEADER_H),
                Loc.T("— 自定义政府投资额（点击下方区划输入；按最近 " + weeks + " 周已计入额降序，可输入负数）—",
                      "— Custom government investment (click a district to enter; sorted by the amount credited in the last " + weeks + " weeks; negatives allowed) —"), _hdr);
            cy += HEADER_H + GAP;
            if (InvestWeek.FollowRealTime)
            {
                GUI.Label(new Rect(x0, cy, lw, HEADER_H),
                    Loc.T("※ 记账刻度：RealTime 日历周（游戏里显示的日期；可在「设置」里改回原版帧周）",
                          "Note: recording ruler = RealTime calendar weeks (the in-game date; change it in Settings)"), _diag);
                cy += HEADER_H;
            }

            // ---- 输入行：左 Label + 输入框 + [k][m][b] + [确定]（仿组合命名行的横排）----
            GUI.Label(new Rect(x0, cy, 66, BTN_H), Loc.T("投资额:", "Amount:"), _fl);
            float bx = x0 + lw;
            Rect okBtn = new Rect(bx - 78, cy, 78, BTN_H); bx -= 78 + 6;
            Rect clrBtn = new Rect(bx - 56, cy, 56, BTN_H); bx -= 56 + 6;
            Rect bBtn = new Rect(bx - 32, cy, 32, BTN_H); bx -= 32 + 3;
            Rect mBtn = new Rect(bx - 32, cy, 32, BTN_H); bx -= 32 + 3;
            Rect kBtn = new Rect(bx - 32, cy, 32, BTN_H); bx -= 32 + 8;
            Rect fld = new Rect(x0 + 70, cy, System.Math.Max(60f, bx - (x0 + 70)), BTN_H);

            GUI.SetNextControlName("DFMInvestField");
            _investInput = GUI.TextField(fld, _investInput, 20);
            bool focused = GUI.GetNameOfFocusedControl() == "DFMInvestField";
            if (focused) TrackImeCaret(fld, _investInput);   // 中文输入法候选窗跟随

            if (GUI.Button(kBtn, "k", _investUnit == 0 ? _bn2 : _btn)) _investUnit = 0;
            if (GUI.Button(mBtn, "m", _investUnit == 1 ? _bn2 : _btn)) _investUnit = 1;
            if (GUI.Button(bBtn, "b", _investUnit == 2 ? _bn2 : _btn)) _investUnit = 2;

            // 清空：删掉当前目标区划的**全部分期** —— 包括已计入的和记在未来周上还没计入的。
            // 这是「取消一笔投资」唯一干净的手段：分期模型下要一笔笔冲减回去是不可能的。
            GUI.enabled = _investEditTarget != 0;
            if (GUI.Button(clrBtn, Loc.T("清空", "Clear"), _btn))
            {
                if (_hub.Investments != null && _hub.Investments.Remove(_investEditTarget))
                {
                    _hub.MarkDirty();
                    Debug.Log("[DFM] Investment #" + _investEditTarget + " cleared（含全部待计入分期）");
                }
            }
            GUI.enabled = true;

            bool confirmByButton = GUI.Button(okBtn, Loc.T("确定", "Apply"), _btn);
            bool confirmByKey = focused && Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            bool confirm = confirmByButton || confirmByKey;

            if (confirm && _investEditTarget != 0 && _hub.Investments != null)
            {
                double amt;
                if (ParseKmb(_investInput, InvestUnitMult(_investUnit), out amt))
                {
                    // 输入额是本周期总额（显示口径）→ ÷InvestMult() 得到**每一期**的原始值（kr/周），
                    // 周视图(1 期)整笔进本周，月视图(4 期)本周起 4 周各一份。
                    // 窗口装满分期时 4 份的和 = 输入额 / LandMult()，再 ×LandMult() 正好还原成输入额。
                    double f = InvestMult();
                    double part = (f != 0.0) ? amt / f : amt;
                    uint curW = InvestWeek.Now;   // 记账刻度：默认原版帧周；勾选后 = RealTime 日历周

                    List<InvestInstallment> insts;   // 不能叫 list：本方法的入参也叫 list(Rect)
                    if (!_hub.Investments.TryGetValue(_investEditTarget, out insts) || insts == null)
                    {
                        insts = new List<InvestInstallment>();
                        _hub.Investments[_investEditTarget] = insts;
                    }
                    // 第一期落在**本周**（而不是下周），所以录入后立刻就能看到 1/N，不会出现「输了却是 0」。
                    for (int i = 0; i < weeks; i++) AddInstallment(insts, curW + (uint)i, part);

                    _hub.PruneInvestments();
                    _hub.MarkDirty();
                    // 诊断：每一次写入都留痕，便于回头查「这笔是谁什么时候加的」
                    Debug.Log("[DFM] Investment #" + _investEditTarget + " += " + amt.ToString("0.###") +
                        "（本周期口径；分 " + weeks + " 期 × " + part.ToString("0.###") + " 原始值，第 " + curW + " 周起）" +
                        "  [输入='" + _investInput + "' 单位=" + _investUnit + " 换算÷" + f.ToString("0.###") +
                        " 触发=" + (confirmByButton ? "按钮" : "回车") + "]");
                    _investInput = "";   // 清空，便于连续追加
                    GUI.FocusControl("");
                }
            }
            cy += BTN_H + 2;

            // 目标行：当前编辑哪个区划 + **本周期的已计入额**（排序/配色用的就是它）+ 还没轮到的分期。
            // **不预填到输入框**：确认是「再录入一笔」，预填再确认会翻倍。
            string curTxt = "";
            double pendRaw = 0.0; int pendN = 0;
            if (_investEditTarget != 0 && _investEditTarget < 256)
            {
                curTxt = CurrencySymbol() + F(disp[_investEditTarget]);
                GetPendingInvest(_investEditTarget, out pendRaw, out pendN);
            }
            // 只显示待计入的**金额**，不显示期数（2026-09-26 用户要求：「待计入期数删了，只留金额」）。
            // 期数仍由 GetPendingInvest 返回，这里只当「有没有待计入」的开关用。
            string pendTxt = pendN > 0
                ? Loc.T("    待计入：", "    pending: ") + CurrencySymbol() + F(pendRaw * LandMult())
                : "";
            GUI.Label(new Rect(x0, cy, lw, TEXT_H),
                _investEditTarget == 0
                    ? Loc.T("目标：未选择 —— 点下方任一区划开始输入（负数表示冲减；按本周期分期计入）",
                            "Target: none - click a district below (negative = deduct; credited in weekly instalments)")
                    : Loc.T("目标：", "Target: ") + _hub.GetVanillaDistrictName(_investEditTarget)
                      + Loc.T("    本期计入：", "    in window: ") + curTxt
                      + pendTxt
                      + Loc.T("    单位：", "    unit: ") + (_investUnit == 0 ? "k" : (_investUnit == 1 ? "m" : "b")),
                _fl);
            cy += TEXT_H + GAP;

            // ---- 列表：所有区划，按**本周期（最近 x 周）已计入额**降序 ----
            var items = new List<KeyValuePair<ushort, double>>();
            for (int i = 0; i < all.Length; i++)
            {
                ushort did = all[i];
                if (string.IsNullOrEmpty(_hub.GetVanillaDistrictName(did))) continue;
                if (!PassFilter(did)) continue;
                items.Add(new KeyValuePair<ushort, double>(did, disp[did]));
            }
            items.Sort((a, b) => b.Value.CompareTo(a.Value));   // 降序（Comparison 委托可用，非 Func）

            Rect listRect = new Rect(x0, cy, lw, System.Math.Max(60f, list.yMax - cy));
            float contentH = HEADER_H + items.Count * NODE_H + 24f;
            _sortScroll = GUI.BeginScrollView(listRect, _sortScroll, new Rect(0, 0, lw, contentH));
            float scy = DrawHeader(0, lw,
                Loc.T("— 各区划 自定义政府投资额 排名（本周期计入额降序，点击输入）—",
                      "— Districts by custom investment (current period, desc; click to edit) —"));
            for (int i = 0; i < items.Count; i++)
            {
                ushort did = items[i].Key;
                bool editing = did == _investEditTarget;
                string line = (editing ? "▶ " : "  ") + string.Format("{0}. {1}    {2}",
                    i + 1, _hub.GetVanillaDistrictName(did), CurrencySymbol() + F(items[i].Value));
                Color old = GUI.color;
                GUI.color = BuiltDeltaColor(items[i].Value);   // 复用「建筑价值增量」的配色
                if (GUI.Button(new Rect(0, scy + i * NODE_H, lw, NODE_H), line, _rankBtn))
                {
                    _investEditTarget = did;
                    SelectDistrict(did);
                }
                GUI.color = old;
            }
            GUI.EndScrollView();
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

        /// <summary>
        /// 组合的通勤类指标（**居住地口径**），每帧按成员预算一次：
        ///   · 平均通勤距离（km） = Σ成员(距离×就业居民数) ÷ Σ成员就业居民数 —— 按就业居民加权，
        ///     不能对成员各自的平均值再求平均（会被小成员带偏，与人均可支配同一道理）；
        ///   · 本地就业率（%） = 成员集**内部**的 OD 对和 ÷ Σ成员就业居民数 ——
        ///     成员之间跨区上班**也算本地**（用户 2026-09-27 定的规则，与层级聚合一致）；
        ///   · 平均通勤时间（分钟）= Σ成员(时间×趟数) ÷ Σ成员趟数（与距离同一套加权）。
        /// </summary>
        private void BuildGroupCommuteValues(out double[] commute, out double[] localEmp, out double[] commuteTime,
            out double[] commuteProg)
        {
            commute = new double[Groups.Count];
            localEmp = new double[Groups.Count];
            commuteTime = new double[Groups.Count];
            commuteProg = new double[Groups.Count];   // 「统计进度」%：白色 0 的行注明「已统计 x%」
            DistrictFinanceCalculator calc = _hub.Calculator;
            double[] dist = calc.GetCommuteDistance();
            long[] cnt = calc.GetCommuteCount();
            double[] time = _commuteTopMode ? calc.GetCommuteTopTime() : calc.GetCommuteTime();
            long[] timeCnt = calc.GetCommuteTimeCount();
            // 「区域工人数」子模式要用它（成员自身值求和；非工人模式用不到，白拿一次缓存数组，代价可忽略）
            double[] workSelf = calc.GetDistrictPanelWorkers();
            for (int gi = 0; gi < Groups.Count; gi++)
            {
                GroupData g = Groups[gi];
                if (g.Members == null || g.Members.Count == 0) continue;
                // Members 是 HashSet<ushort>（不能下标）→ 先摊平成 List 再做成员×成员的 OD 双重求和
                List<ushort> ms = new List<ushort>();
                foreach (ushort m in g.Members) if (m < 256) ms.Add(m);
                if (ms.Count == 0) continue;
                double wsum = 0.0;   // Σ 距离 × 就业居民数
                long total = 0;      // Σ 就业居民数
                double tsum = 0.0;   // Σ 通勤时间 × 趟数
                long ttotal = 0;     // Σ 趟数
                for (int m = 0; m < ms.Count; m++)
                {
                    wsum += dist[ms[m]] * cnt[ms[m]];
                    total += cnt[ms[m]];
                    tsum += time[ms[m]] * timeCnt[ms[m]];
                    ttotal += timeCnt[ms[m]];
                }
                // 时间×趟数 ÷ 趟数 → 分钟；门槛与计算器一致：样本数 ≥ 就业居民数÷8（and 条件）。
                // time[] 已经按当前口径取好（均值 / 最长10%，见上面那一行三元），这里只负责按趟数加权。
                if (ttotal > 0
                    && DistrictFinanceCalculator.CommuteSamplesEnough(ttotal, total))
                    commuteTime[gi] = tsum / ttotal;
                // 进度 = 成员趟数 ÷ 成员门槛（Σ就业居民 ÷ 8）：没到门槛时面板注明「已统计 x%」。
                // 与计算器那边（自身/聚合）同一套算法，只是把「一个区划」换成「成员集」。
                if (total > 0)
                {
                    long needT = DistrictFinanceCalculator.CommuteNeed(total);
                    commuteProg[gi] = ttotal >= needT ? 100.0 : (double)ttotal * 100.0 / needT;
                }
                else commuteProg[gi] = -1.0;   // 成员没有就业居民 → 这一项不适用
                if (total <= 0) continue;
                commute[gi] = wsum / total;   // 距离×人数 ÷ 人数 → km
                // 键 12「就业相关」的第二个子口径：**区域工人数** = 成员**自身**值之和
                // （原版区划面板口径；组合一律用成员自身值合计，见 GroupValue 的约定）。
                if (_employWorkers)
                {
                    double wsumSelf = 0.0;
                    for (int m = 0; m < ms.Count; m++) wsumSelf += workSelf[ms[m]];
                    localEmp[gi] = wsumSelf;
                    continue;
                }
                long inside = 0;              // 成员集内部的 OD 对和
                for (int a = 0; a < ms.Count; a++)
                    for (int b = 0; b < ms.Count; b++)
                        inside += calc.GetOdCount(ms[a], ms[b]);
                localEmp[gi] = (double)inside / total * 100.0;
            }
        }

        /// <summary>组合的统计值（按排序依据）：GDP/人口为成员求和，人均=和/和；
        /// 地价为成员面积加权平均地价再随模式换算显示（避免把地价当 GDP 求和导致异常高）。</summary>
        private double GroupValue(int idx, double[] gdp, long[] pop, long[] landRaw, double[] areaRaw, double[] delta, double[] built, double[] income, double[] invest, double[] gLive = null, double[] gBase = null, double[] gCommute = null, double[] gLocalEmp = null, double[] gCommuteTime = null)
        {
            GroupData g = Groups[idx];

            // 通勤距离 / 本地就业率（居住地口径）：调用方已按**成员集**预算好（见 BuildGroupCommuteValues）——
            // 这两项都要「先按成员加权/求和再取比值」，不能像 GDP 那样逐成员求和；也绝不能放到排序比较器里
            // 现算（成员数² × 比较次数）。所以直接取预算好的值。
            if (_sortKey == 11 && gCommute != null && idx < gCommute.Length) return gCommute[idx];
            if (_sortKey == 12 && gLocalEmp != null && idx < gLocalEmp.Length) return gLocalEmp[idx];
            if (_sortKey == 13 && gCommuteTime != null && idx < gCommuteTime.Length) return gCommuteTime[idx];

            // 增速模式：组合的增速 = (Σ成员当前值 − Σ成员基准值) ÷ Σ成员基准值 ——
            // **不能对成员各自的增速求平均**（会被小基数成员带偏），必须按总量算
            // （与人均可支配的「Σ分子 ÷ Σ人口」同一思路）。gLive/gBase 为 null = 该键算不了增速 → 落回原口径。
            if (gLive != null && gBase != null)
            {
                double gl = 0, gb = 0;
                foreach (ushort m in g.Members)
                {
                    if (m >= gLive.Length || m >= gBase.Length) continue;
                    gl += gLive[m];
                    gb += gBase[m];
                }
                return gb > 0 ? (gl - gb) / gb * 100.0 : 0.0;
            }

            // 地价：面积加权平均（kr/m²），×LandMult 后与列表/图例同一口径
            if (_sortKey == 3)
            {
                double lsum = 0, asum = 0;
                foreach (ushort m in g.Members)
                {
                    if (m >= landRaw.Length || m >= areaRaw.Length) continue;
                    double a = areaRaw[m];
                    if (a <= 0) continue;
                    lsum += landRaw[m] * a;
                    asum += a;
                }
                return asum > 0 ? (lsum / asum) * LandMult() : 0;
            }

            double sg = 0, sp = 0, sa = 0, sd = 0, sb = 0, si = 0, sv = 0;
            foreach (ushort m in g.Members)
            {
                sg += gdp[m];
                sp += pop[m];
                if (m < areaRaw.Length) sa += areaRaw[m]; // 面积 m² 合计
                if (delta != null && m < delta.Length) sd += delta[m]; // 建筑价值增量合计
                if (built != null && m < built.Length) sb += built[m]; // 建成区面积合计
                // 人均可支配：si 累加的是**分子**（人口×人均），最后再除总人口 —— 与
                // 计算器的「聚合分子 ÷ 聚合人口」口径一致，不能对成员人均值求平均
                if (income != null && m < income.Length) si += pop[m] * income[m];
                // 自定义政府投资额：与其它指标同口径 = 成员**自身**值合计（调用方传的是自身数组），
                // 不做层级聚合，所以父子同组也不会重复计入
                if (invest != null && m < invest.Length) sv += invest[m];
            }
            switch (_sortKey)
            {
                case 1: return sp;
                case 2: return sp > 0 ? sg / sp : 0;
                case 4: return sa > 0 ? sg / sa : 0; // 地均GDP = GDP/面积 m²
                case 5: return sa > 0 ? sp * 1000000.0 / sa : 0; // 人口密度 = 人口/面积 km²（sa 位 m²）
                case 6: return sa; // 面积 m² 合计
                case 7: return sd; // 建筑价值增量合计
                case 8: return sb; // 建成区面积合计
                case 9: return sp > 0 ? si / sp : 0; // 人均可支配 = Σ分子 / Σ人口
                case 10: return sv; // 自定义政府投资额合计
                default: return sg;
            }
        }

        /// <summary>全区（所有原版区划）自身 GDP 合计，作为占全图比率的分母。</summary>
        private double TotalGdp(double[] gdp)
        {
            double t = 0;
            foreach (ushort did in _hub.GetVanillaDistricts()) t += gdp[did];
            return t;
        }

        /// <summary>全区（所有原版区划）人口合计，作为占全图比率的分母。</summary>
        private long TotalPop(long[] pop)
        {
            long t = 0;
            foreach (ushort did in _hub.GetVanillaDistricts()) t += pop[did];
            return t;
        }

        /// <summary>组合成员 GDP 自身值合计（用于占全图比率）。</summary>
        private double GroupGdp(int gi, double[] gdp)
        {
            double s = 0;
            foreach (ushort m in Groups[gi].Members) s += gdp[m];
            return s;
        }

        /// <summary>组合成员人口合计（用于占全图比率）。</summary>
        private long GroupPop(int gi, long[] pop)
        {
            long s = 0;
            foreach (ushort m in Groups[gi].Members) s += pop[m];
            return s;
        }

        /// <summary>组合的成员**自身值**求和（double 版，给「区域工人数」用；与 GroupPop 同规矩）。</summary>
        private double GroupSumD(int gi, double[] self)
        {
            double s = 0.0;
            foreach (ushort m in Groups[gi].Members)
                if (m < self.Length) s += self[m];
            return s;
        }

        /// <summary>全图各原版区划的自身值合计（double 版，占比分母）。</summary>
        private double TotalSumD(double[] self)
        {
            double t = 0.0;
            ushort[] all = _hub.GetVanillaDistricts();
            for (int i = 0; i < all.Length; i++)
                if (all[i] < self.Length) t += self[all[i]];
            return t;
        }

        /// <summary>数值对总量占比 × 100，格式如 "12.3%"。</summary>
        private static string Share(double val, double total)
        {
            if (total <= 0) return "0.0%";
            return (val / total * 100.0).ToString("0.0") + "%";
        }

        /// <summary>选中一个区划：取消组合选择。</summary>
        private void SelectDistrict(ushort id)
        {
            _detailGroup = -1;
            _hub.SelectedID = id;
            _finDistrict = 0;
        }

        /// <summary>清除当前选择（区划 + 组合详情 + 组合行高亮）。</summary>
        private void ClearSelection()
        {
            _detailGroup = -1;
            _hub.SelectedID = 0;
            _finDistrict = 0;
            _activeGroupIdx = -1; // 右键取消详情时同时取消组合行高亮
            _memberExpand = false; // 并收起成员列表
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
            out int workers, out int buildings, out double area)
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

        /// <summary>面积 m² → km² 文本。小于 0.1 km² 保留三位小数避免显示成 0.00。</summary>
        private static string AreaKm2(double m2)
        {
            if (m2 <= 0) return "0";
            double km2 = m2 / 1000000.0;
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

            double gdp; long pop; int workers, buildings; double area;
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

        /// <summary>地价换算系数（价格，**与周期无关**）：原版 1 / 人民币 420 / 美元 60。
        /// ⚠️ 真源是 ModSettings.PriceFactor —— 计算器侧的 LandMultForCalc 调的是同一个函数，只有一份。</summary>
        private static double LandMult()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Settings != null)
                return ModSettings.PriceFactor(hub.Settings.DisplayCurrency);
            return 1.0;
        }

        /// <summary>地价单位：原版 kr/m²；人民币 ¥/m²；美元 $/m²。</summary>
        private static string LandUnit()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            int cur = (hub != null && hub.Settings != null) ? hub.Settings.DisplayCurrency : 0;
            if (cur == 1) return Loc.T("¥/m²", "¥/m²");
            if (cur == 2) return Loc.T("$/m²", "$/m²");
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

        /// <summary>
        /// 把人均可支配收入的原始值（克朗/周）换算成当前显示模式的数值。
        /// 与 GDP 不同，收入在采集侧保持原值（写进 .series 的也是原值），系数只在显示层乘。
        /// </summary>
        private double[] IncomeToDisplay(double[] income)
        {
            double f = DistrictFinanceCalculator.GetDisplayFactor();
            if (f == 1.0) return income;
            double[] r = new double[income.Length];
            for (int i = 0; i < income.Length; i++) r[i] = income[i] * f;
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
                return ModSettings.CurrencySymbolOf(hub.Settings.DisplayCurrency);
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

        /// <summary>换算后的人均可支配收入分档阈值（与 GDP/人均GDP 同一套显示系数）。</summary>
        private static long[] GetDisplayIncomeTiers()
        {
            double f = DistrictFinanceCalculator.GetDisplayFactor();
            if (f == 1.0) return INCOME_TIERS;
            long[] t = new long[INCOME_TIERS.Length];
            for (int i = 0; i < t.Length; i++) t[i] = (long)(INCOME_TIERS[i] * f);
            return t;
        }

        /// <summary>地均GDP 分档阈值（随货币换算：美元基准 ×1，人民币 ×7）。</summary>
        /// <summary>地均GDP 分档阈值（随统计模式缩放，与 GDP/人均GDP 图例同口径）。</summary>
        private static double[] GetGdpAreaDisplayTiers()
        {
            double f = DistrictFinanceCalculator.GetDisplayFactor();
            if (f == 1.0) return GDP_AREA_TIERS;
            double[] t = new double[GDP_AREA_TIERS.Length];
            for (int i = 0; i < t.Length; i++) t[i] = GDP_AREA_TIERS[i] * f;
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

        /// <summary>地均GDP 分档颜色：复用 TIER_COLORS 渐变（随统计模式缩放）。</summary>
        private static Color GdpAreaColor(double gdpArea)
        {
            double[] tiers = GetGdpAreaDisplayTiers();
            for (int i = 0; i < tiers.Length; i++)
                if (gdpArea < tiers[i]) return TIER_COLORS[i];
            return TIER_COLORS[TIER_COLORS.Length - 1];
        }

        /// <summary>人口密度分档颜色：复用 TIER_COLORS 渐变（人/km²）。</summary>
        private static Color PopDensityColor(double density)
        {
            for (int i = 0; i < POP_DENSITY_TIERS.Length; i++)
                if (density < POP_DENSITY_TIERS[i]) return TIER_COLORS[i];
            return TIER_COLORS[TIER_COLORS.Length - 1];
        }

        /// <summary>面积分档颜色：复用 TIER_COLORS 渐变（入参为 m²，按 km² 分档比对）。</summary>
        private static Color AreaColor(double areaM2)
        {
            double km2 = areaM2 / 1000000.0;
            for (int i = 0; i < AREA_TIERS.Length; i++)
                if (km2 < AREA_TIERS[i]) return TIER_COLORS[i];
            return TIER_COLORS[TIER_COLORS.Length - 1];
        }

        /// <summary>
        /// 建成区面积分档颜色：与 AreaColor 同一套 TIER_COLORS 渐变，只是阈值换成
        /// BUILT_AREA_TIERS（= 区域面积档位 ÷ 2）。图例必须用同一张表，否则颜色与数字对不上。
        /// </summary>
        private static Color BuiltAreaColor(double areaM2)
        {
            double km2 = areaM2 / 1000000.0;
            for (int i = 0; i < BUILT_AREA_TIERS.Length; i++)
                if (km2 < BUILT_AREA_TIERS[i]) return TIER_COLORS[i];
            return TIER_COLORS[TIER_COLORS.Length - 1];
        }

        /// <summary>
        /// 平均通勤距离分档颜色（入参 = km，居住地口径）。
        /// **色阶是反的**：通勤越近越好（用户 2026-09-27 定），所以距离越小取越靠后的颜色（紫端）、
        /// 距离越大取越靠前的（红端）—— 与其它键「值越大越靠后」相反。
        /// ⚠️ 图例必须用 `DrawLegend(..., invert: true)`，否则图例与列表颜色对不上。
        /// </summary>
        private static Color CommuteColor(double km)
        {
            int last = TIER_COLORS.Length - 1;
            for (int i = 0; i < COMMUTE_TIERS.Length; i++)
                if (km < COMMUTE_TIERS[i]) return TIER_COLORS[last - i];
            return TIER_COLORS[0];
        }

        /// <summary>
        /// 平均通勤时间分档颜色（入参 = 分钟）——**同样反向**：通勤越短越好（与 CommuteColor 一致），
        /// 所以时间越短取越靠后的紫端。图例必须一起反（`DrawLegend(..., invert: true)`）。
        /// </summary>
        private static Color CommuteTimeColor(double minutes)
        {
            int last = TIER_COLORS.Length - 1;
            for (int i = 0; i < COMMUTE_TIME_TIERS.Length; i++)
                if (minutes < COMMUTE_TIME_TIERS[i]) return TIER_COLORS[last - i];
            return TIER_COLORS[0];
        }

        /// <summary>本地就业率分档颜色（入参 = %）。</summary>
        private static Color LocalEmpColor(double pct)
        {
            for (int i = 0; i < LOCAL_EMP_TIERS.Length; i++)
                if (pct < LOCAL_EMP_TIERS[i]) return TIER_COLORS[i];
            return TIER_COLORS[TIER_COLORS.Length - 1];
        }

        /// <summary>区域工人数分档颜色（入参 = 人）—— 与人口同一套取色方向，档位是人口的一半。</summary>
        private static Color WorkersColor(double n)
        {
            for (int i = 0; i < WORKERS_TIERS.Length; i++)
                if (n < WORKERS_TIERS[i]) return TIER_COLORS[i];
            return TIER_COLORS[TIER_COLORS.Length - 1];
        }

        /// <summary>人均可支配收入分档颜色：复用 TIER_COLORS 渐变（克朗/周·人，随显示模式缩放阈值）。</summary>
        private static Color IncomePerCapitaColor(double perCapita)
        {
            long[] tiers = GetDisplayIncomeTiers();
            for (int i = 0; i < tiers.Length; i++)
                if (perCapita < tiers[i]) return TIER_COLORS[i];
            return TIER_COLORS[TIER_COLORS.Length - 1];
        }

        /// <summary>当前周期的名字（周/月/季/年/5年），用于图例标题后缀。</summary>
        private static string PeriodSuffix()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            int p = (hub != null && hub.Settings != null) ? hub.Settings.DisplayPeriod : 0;
            return ModSettings.PeriodName(p);
        }

        /// <summary>建筑价值增量（键 7）/ 自定义投资额（键 10）的分档阈值：kr 基准 × **流量系数**（与 GDP 同源，
        /// 见 GetDisplayFactor）× 周期周数（1/4/13/52/260/520）。最大值 = GDP 图例的 1/3（用户 2026-09-28 要求）。</summary>
        private static double[] GetBuiltDeltaDisplayTiers()
        {
            // 价格 × 周期周数（周/月/季/年/5年/10年 = 1/4/13/52/260/520）—— **与地价同一套系数**。
            //
            // ⚠️ 2026-09-28 我一度把它改成与 GDP 同源的流量系数（想让"本图例最大值 = GDP 图例的 1/3"
            //    在三种货币下都成立），**用户否掉了**：「地价视图的比例是不一样的，你给改了干啥」——
            //    增量是「面积 × 地价」派生的量，必须留在地价那套系数里（420/60），否则与地价图的
            //    数值比例对不上。保持本函数的 LandMult() 不动，只调档位表（见 BUILT_DELTA_TIERS 的注释：
            //    表最大值 15,000,000，在**原版/周**下正好是 GDP 表的 1/3；人民币/美元下比值随系数走，
            //    用户明确接受"不是 1/3"）。
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            int period = (hub != null && hub.Settings != null) ? hub.Settings.DisplayPeriod : 0;
            double f = LandMult() * ModSettings.PeriodWeeks(period);
            if (f == 1.0) return BUILT_DELTA_TIERS;
            double[] t = new double[BUILT_DELTA_TIERS.Length];
            for (int i = 0; i < t.Length; i++) t[i] = BUILT_DELTA_TIERS[i] * f;
            return t;
        }

        /// <summary>建成区价值增量分档颜色：以 0 为中心的发散色（负→红端、0→中段、正→紫端）。</summary>
        private static Color BuiltDeltaColor(double v)
        {
            double[] tiers = GetBuiltDeltaDisplayTiers();
            for (int i = 0; i < tiers.Length; i++)
                if (v < tiers[i]) return TIER_COLORS[i];
            return TIER_COLORS[TIER_COLORS.Length - 1];
        }

        /// <summary>增速分档颜色（%/周期，负值走红端、正值走绿紫端；正负分界在 +0.5%/+1% 之间）。
        /// 阈值随周期缩放（见 GetGrowthDisplayTiers），所以颜色含义在月/季/年下自动跟着变。</summary>
        private static Color GrowthColor(double v)
        {
            double[] tiers = GetGrowthDisplayTiers();
            for (int i = 0; i < tiers.Length; i++)
                if (v < tiers[i]) return TIER_COLORS[i];
            return TIER_COLORS[TIER_COLORS.Length - 1];
        }

        /// <summary>绘制颜色图例：16 档颜色色块 + 各档阈值下限。long[]/double[] 共用一套模板（返回新 y）。</summary>
        private float DrawLegend(float x, float y, float w, string title, long[] tiers, bool invert = false)
        {
            string[] labels = new string[tiers.Length + 1];
            for (int i = 0; i < labels.Length; i++) labels[i] = LegendLabel(tiers, i);
            return DrawLegendCore(x, y, w, title, labels, invert);
        }

        private float DrawLegend(float x, float y, float w, string title, double[] tiers, bool invert = false)
        {
            string[] labels = new string[tiers.Length + 1];
            for (int i = 0; i < labels.Length; i++) labels[i] = LegendLabel(tiers, i);
            return DrawLegendCore(x, y, w, title, labels, invert);
        }

        /// <summary>增速图例：档位以 % 显示（通用的 LegendNum 会把 −100 印成「-100.00」，不能用）。</summary>
        private float DrawLegendGrowth(float x, float y, float w, string title)
        {
            double[] tiers = GetGrowthDisplayTiers();
            string[] labels = new string[tiers.Length + 1];
            for (int i = 0; i < labels.Length; i++)
            {
                if (i == 0) labels[i] = "<" + Pct(tiers[0]);
                else if (i >= tiers.Length) labels[i] = "≥" + Pct(tiers[tiers.Length - 1]);
                else labels[i] = Pct(tiers[i - 1]);
            }
            return DrawLegendCore(x, y, w, title, labels);
        }

        /// <summary>
        /// 增速分档阈值（**当前周期口径**）：基准表是**年**档位（用户 2026-09-27 指定），
        /// 按**复利**换算到当前周期：`(1 + 年档位)^(N/52) − 1`，N = 周期周数（1/4/13/52/260）。
        /// 5 年 = 年档位的 **5 次方**、周 = 52 次方根 —— 这正是"年增速连乘 N 年"的定义。
        /// 于是档位的含义很直观：5 年视图里 +10.41% = 「连续 5 年每年 +2%」，年视图 ±2% 就是「没动」。
        ///
        /// ⚠️ 三个别踩的：① 不能线性缩放（周/月差别不大，5 年差一倍多）；
        ///    ② 别照搬「增量」图例的 ×PeriodWeeks（5 年负档位会到 −2600%，而增速下限只有 −100%）；
        ///    ③ `Math.Pow` 在 r&gt;0 时单调，所以换算后仍**严格升序** —— 档位查表（`v &lt; tiers[i]`）靠这个。
        /// </summary>
        private static double[] GetGrowthDisplayTiers()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            int period = (hub != null && hub.Settings != null) ? hub.Settings.DisplayPeriod : 0;
            int n = ModSettings.PeriodWeeks(period);
            if (n == 52) return GROWTH_TIERS;   // 年视图就是原表
            double exp = n / 52.0;
            double[] t = new double[GROWTH_TIERS.Length];
            for (int i = 0; i < t.Length; i++)
            {
                double r = 1.0 + GROWTH_TIERS[i] / 100.0;   // 年档位 → 年倍率
                t[i] = r > 0.0 ? (System.Math.Pow(r, exp) - 1.0) * 100.0 : -100.0;
            }
            return t;
        }

        /// <summary>档位/数值标签：带符号的百分数，**两位小数**（+2.00% / −130.00%；×13 这类缩放会出现 .5，故留两位）。</summary>
        private static string Pct(double v)
        {
            return (v > 0 ? "+" : "") + v.ToString("0.00") + "%";
        }

        private float DrawLegendCore(float x, float y, float w, string title, string[] labels, bool invert = false)
        {
            GUI.Label(new Rect(x, y, w, HEADER_H), title, _hdr);
            y += HEADER_H;

            const int COLS = 8;
            int total = labels.Length; // 16 档颜色
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
                // invert = 值越小越「好」的指标（目前只有平均通勤距离）：色阶反过来，短距离取紫端、长距离取红端。
                // ⚠️ 必须和 XxxColor() 里的取色方向一致，否则图例与列表颜色对不上（用户 2026-09-27 要求：通勤越近越好）。
                GUI.color = invert ? TIER_COLORS[TIER_COLORS.Length - 1 - i] : TIER_COLORS[i];
                GUI.DrawTexture(new Rect(xx, yy, sw - 2f, sh), Texture2D.whiteTexture);
                GUI.color = old;

                GUI.Label(new Rect(xx, yy + sh + 1, sw, th), labels[i], _legend);
            }
            return y + 2 * (sh + th);
        }

        private static string LegendLabel(long[] tiers, int idx)
        {
            if (idx == 0) return "<" + F(tiers[0]);
            if (idx >= tiers.Length) return "≥" + F(tiers[tiers.Length - 1]);
            return F(tiers[idx - 1]);
        }

        /// <summary>double 档标签：数值大(≥1000)用 K/M/B，小(面积 km²)用小数。面积图例 / 建成区增值图例共用。</summary>
        private static string LegendLabel(double[] tiers, int idx)
        {
            if (idx == 0) return "<" + LegendNum(tiers[0]);
            if (idx >= tiers.Length) return "≥" + LegendNum(tiers[tiers.Length - 1]);
            return LegendNum(tiers[idx - 1]);
        }
        private static string LegendNum(double v)
        {
            double a = System.Math.Abs(v);
            if (a >= 1000.0) return (v < 0 ? "-" : "") + F(a); // K/M/B
            return Kml2S(v);
        }
        private static string Kml2S(double km2)
        {
            if (km2 >= 1) return km2.ToString("0.##");
            if (km2 >= 0.1) return km2.ToString("0.0");
            return km2.ToString("0.00");
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
            _rankBtn = MakeRowStyle(13, Color.white); // 白色文字，用于排名行按 GDP 着色

            _btn = MakeButtonStyle(12, Color.white);
            _btnWrap = MakeButtonStyle(11, Color.white); // 可换行（「更多 ▾」下拉里的长条目用）
            _btnWrap.wordWrap = true;
            _btnWrap.alignment = TextAnchor.MiddleCenter;
            _shield = new GUIStyle(); // 透明遮罩：吞掉落不到下拉条目上的点击，避免点穿到列表行
            // 按钮的「当前项」高亮：当前视图 / 当前排序键 / 展开中的说明 等
            _bn2 = MakeButtonStyle(12, Color.yellow);
            _bn2.normal.background = Tex(new Color(0.25f, 0.5f, 0.25f));
            // 注：2026-09-19 去掉的是**列表行**的选中高亮（原 `_ts` 浅蓝字 + 选中行跳过热力着色），
            //     列表行现在一律用 `_rankBtn`、一律参与热力着色，选中只靠 `▶` 前缀辨认。
            //     **按钮的高亮（`_bn2`）保留**（2026-09-20 按用户要求恢复）。

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
