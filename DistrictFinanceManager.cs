using System;
using System.Reflection;
using ColossalFramework;
using UnityEngine;

namespace DistrictFinanceManager
{
    /// <summary>
    /// 核心枢纽 — 管理原版区划层级关系 + 财务计算。
    /// 不创建自定义区域，不修改游戏逻辑。
    /// </summary>
    public class DistrictFinanceHub : MonoBehaviour
    {
        private static DistrictFinanceHub _instance;
        public static DistrictFinanceHub Instance { get { return _instance; } }

        public static void Dispose()
        {
            if (_instance != null)
            {
                // 落盘只发生在「游戏存档」之后（见 FlushCache）：这里若发现**已经存过档但还没写**，
                // 补写一次（存档后立刻退出/切关卡的兜底）；否则**什么都不写** —— 内存里的只是缓存，
                // 回档/不存档退出时它就该被丢掉，不能覆盖数据库（用户 2026-09-28）。
                if (_saveRequested)
                {
                    _saveRequested = false;
                    _instance.FlushCache("退出前补写");
                }
                else if (_instance._dirty)
                {
                    // 明说一句：这些改动**没有**落盘（用户 2026-09-28 的规矩：不存档就回档 → 丢弃）
                    Debug.Log("[DFM] 缓存丢弃：本次会话有未存档的改动（层级/组合/投资额/周库），按设定不写入文件");
                }
                _instance.Hierarchy = null;
                Destroy(_instance.gameObject);
                _instance = null;
            }
        }

        /// <summary>
        /// 游戏存档了（由 <see cref="DistrictSaveHook"/> 从存档线程调用）→ 置标志，
        /// 真正的写盘在主线程的 Update 里做。**只碰一个 volatile 字段**，不调用任何 Unity API。
        /// </summary>
        internal static void NotifyGameSaved()
        {
            _saveRequested = true;
        }

        /// <summary>「游戏已存档，缓存待落盘」的标志（跨线程写、主线程读）。</summary>
        private static volatile bool _saveRequested;

        public DistrictHierarchy Hierarchy;
        public DistrictFinanceCalculator Calculator;
        public ModSettings Settings { get; private set; }
        public string SaveName { get; private set; }
        public System.Collections.Generic.List<GroupData> Groups = new System.Collections.Generic.List<GroupData>();

        /// <summary>「自定义政府投资额」视图：区划 ID → 该区划**按周分期**的投资记录（见 InvestInstallment）。
        /// 一笔录入按当前周期均摊成 N 份记在「当周起 N 周」上；统计时只累加最近 N 周内的分期，
        /// 与「建筑价值增量」同口径。显示时才乘显示系数（见 Panel.InvestToDisplay）。按存档存 .inv。</summary>
        public System.Collections.Generic.Dictionary<ushort,
            System.Collections.Generic.List<InvestInstallment>> Investments =
            new System.Collections.Generic.Dictionary<ushort,
                System.Collections.Generic.List<InvestInstallment>>();

        public ushort SelectedID;
        public int EditingLevel = 1;

        /// <summary>原版区划工具当前选中的区划 ID（byte，0 表示未选中）。</summary>
        public byte SelectedVanillaDistrict { get; set; }

        private const float PRUNE_GRACE = 30f;    // 读档后宽限这么久才开始判断失效区划
        private const float PRUNE_INTERVAL = 10f; // 之后每隔这么久检查一次；连续两次缺失才真删

        /// <summary>缓存里是否有「还没落盘」的改动（= 上次存档之后动过）。只用于面板显示与日志。</summary>
        private bool _dirty;
        /// <summary>周库需要**整表重写**（不是追加）：清掉某个区划的历史后，追加式文件里的旧行还在，
        /// 只清内存的话下次读档又会读回来。重写推到存档落盘时一起做（见 FlushCache）。</summary>
        private bool _seriesNeedsRewrite;
        private float _densityTick;
        private float _pruneGrace;                // 剩余宽限时间
        private float _pruneTick;                 // 距下次检查
        private System.Collections.Generic.HashSet<ushort> _missingPrev =
            new System.Collections.Generic.HashSet<ushort>();  // 上次检查缺失的 id（二次确认用）

        // 原版区划 ID 回收：删掉的 ID 会被复用给新画的区划，新主人不该继承旧主人的数据。
        private System.Collections.Generic.HashSet<ushort> _deadIds =
            new System.Collections.Generic.HashSet<ushort>();  // 墓碑：曾经存在、后来被删的 ID
        private System.Collections.Generic.HashSet<ushort> _knownDistricts =
            new System.Collections.Generic.HashSet<ushort>();  // 上次检查时已创建的 ID（对比出新增/消失）
        private bool _knownReady;                              // 是否已建立首次基线
        private System.Collections.Generic.HashSet<ushort> _gonePrev =
            new System.Collections.Generic.HashSet<ushort>();  // 上次检查时「已消失」的 ID（墓碑二次确认用）
        private uint _lastWeek = uint.MaxValue;   // 上次采样的原版游戏周
        private DistrictSeriesDB _series;         // 周度时间序列库
        private uint _pendingWeek;                // 待采样的周（等建成区数据就绪再采）
        private bool _hasPendingWeek;

        /// <summary>周度时间序列数据库（按原版游戏周记录各区划全字段，供增速/增量使用）。</summary>
        public DistrictSeriesDB Series { get { return _series; } }

        /// <summary>当前 Investments 里的周号是**按哪种刻度**记的（frame = 原版帧周 / cal = RealTime 日历周）。
        /// 从 .inv 文件头读入，写盘时写回去；它和设置里的当前刻度不一致时换锚（见 ReanchorInvestments）。</summary>
        private string _investScale = DistrictDataStore.InvestScaleFrame;

        /// <summary>缓存里是否有还没落盘的改动（面板显示「未保存」用）。</summary>
        public bool CacheDirty { get { return _dirty; } }

        /// <summary>
        /// **夜间是否暂停写入周库**（用户 2026-09-28：「如果有 realtime，且没有打开此开关，那么夜晚的数据
        /// 暂停写入……跳过夜晚」「如果使用 realtime 且未打开开关则只统计白天数据」）。
        ///
        /// 判据 = **装了 RealTime** 且 **公园全天运营的覆盖当前没生效**。
        /// 为什么这样对：装了 RealTime 而没开覆盖时，公园/广场夜里关门 → 吸引力与地价掉 → 夜里采到的
        /// 周全是"被压低"的数据，写进周库就会让增量/增速里混进一份假波动。与其事后修，不如**夜里不采**。
        /// 用 `RealTimeBridge.Active`（**实际生效**的覆盖状态）而不是勾选状态 `ParkAlwaysOpen`：
        /// 勾了但补丁没挂上（RealTime 版本不同之类）时公园照样关门，那就该跳夜晚。
        /// 场景是**动态判断**的（"中途打开开关之后数据正常计入"）—— 每帧都按当前状态走，不是读档时锁死。
        /// </summary>
        public bool NightSkipActive
        {
            get { return RealTimeBridge.Installed && !RealTimeBridge.Active; }
        }

        /// <summary>本次进入夜晚以来被跳过的周数（昼夜交替时打进日志）。</summary>
        private int _nightSkipped;
        private bool _lastNightState;

        /// <summary>本次夜晚已跳过的周数（面板状态行显示）。</summary>
        public int NightSkippedWeeks { get { return _nightSkipped; } }

        /// <summary>夜间跳过若干周后写一行日志（进入夜晚 / 回到白天时各一次，不刷屏）。</summary>
        private void TickNightState()
        {
            bool night = GameWeek.IsNight;
            if (night == _lastNightState) return;
            _lastNightState = night;
            if (!NightSkipActive)
            {
                if (night) Debug.Log("[DFM] 周库写入：进入夜晚（当前不跳夜晚：未装 RealTime 或公园覆盖已生效）");
                return;
            }
            if (night)
            {
                _nightSkipped = 0;
                Debug.Log("[DFM] 周库写入：进入夜晚 → 暂停采样（RealTime 在场且未开「公园全天运营」，只统计白天数据）");
            }
            else
                Debug.Log("[DFM] 周库写入：回到白天 → 恢复采样（本次夜晚跳过了 " + _nightSkipped + " 周）");
        }

        /// <summary>
        /// 读档后**延期做**投资分期的换锚 + 裁剪：日历周要等 SimulationManager.Update 把
        /// m_currentGameTime 刷新成本存档的值（OnLevelLoaded 那一刻可能还是上一张图/主菜单的日期）。
        /// </summary>
        private float _investInitTimer = -1f;
        private const float INVEST_INIT_DELAY = 1f;

        /// <summary>
        /// 把「自定义投资额」的全部分期**换锚**到当前刻度（用户 2026-09-28）。
        ///
        /// 换锚 = 保持每一期**相对当前时刻的位置**：第 w 期（旧刻度）离现在 (w − 旧刻度的当前周) 个刻度单位，
        /// 换到新刻度就是「新刻度的当前周 + 同一偏移」。于是「3 周前录的一笔」换完仍然是「3 周前」，
        /// 统计窗口与「待计入」判定立刻自洽。
        ///
        /// 不做换算的话：装了 RealTime 的档里两种刻度的周号能差几百，老分期要么全被当成"很久以前"
        /// （窗口外，显示 0）、要么全被当成"未来还没计入"，数字直接没法看。
        ///
        /// 只在**刻度真的变了**时动手（读档时、勾选设置时各调一次）；两种刻度当前值相同时（没装 RealTime）
        /// 只翻刻度名，数据一个字节都不动。整表按区划重写，量级是每档几百条，代价可忽略。
        /// </summary>
        public void ReanchorInvestments(string why)
        {
            string target = InvestWeek.ScaleName;
            if (_investScale == target) return;
            long curNew = (long)InvestWeek.Now;                                   // 新刻度的当前周
            long curOld = _investScale == DistrictDataStore.InvestScaleCal       // 旧刻度的当前周
                ? (long)GameWeek.CalendarWeek : (long)GameWeek.CurrentWeek;
            long delta = curNew - curOld;
            int moved = 0;
            if (delta != 0 && Investments != null)
            {
                foreach (System.Collections.Generic.KeyValuePair<ushort,
                         System.Collections.Generic.List<InvestInstallment>> kv in Investments)
                {
                    System.Collections.Generic.List<InvestInstallment> list = kv.Value;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        InvestInstallment e = list[i];
                        long w = (long)e.Week + delta;
                        if (w < 0) w = 0;
                        if ((long)e.Week == w) continue;
                        e.Week = (uint)w;
                        list[i] = e;
                        moved++;
                    }
                }
            }
            Debug.Log("[DFM] 投资分期换锚（" + why + "）：" + _investScale + " → " + target +
                      "，位移 " + delta + " 周，改了 " + moved + " 条分期" +
                      (delta == 0 ? "（两种刻度当前同值，只翻刻度名）" : ""));
            _investScale = target;
            if (Investments != null && Investments.Count > 0) MarkDirty();   // 文件头刻度也要跟着落盘
        }

        /// <summary>
        /// 「公园和广场全天开放」的读档自动重应用：勾过的档在关卡加载后**等几秒**（RealTime 也在这时才加载完）
        /// 再应用一次；只试一次，失败不反复刷日志。**默认不勾选 → 这里什么都不做**（用户 2026-09-27）。
        /// </summary>
        private float _parkReapplyTimer = PARK_REAPPLY_DELAY;
        private bool _parkReapplied;
        private const float PARK_REAPPLY_DELAY = 10f;

        private void Awake()
        {
            _instance = this;
            Settings = ModSettings.Load();
            Calculator = new DistrictFinanceCalculator();
            SaveName = MakeSaveName();
            Hierarchy = DistrictDataStore.Load(SaveName);
            Groups = DistrictDataStore.LoadGroups(SaveName);
            Investments = DistrictDataStore.LoadInvestments(SaveName, out _investScale);
            // ⚠️ 换锚与裁剪**不在这里做**：RealTime 日历周要读 SimulationManager.m_currentGameTime，而那个字段
            //    是 SimulationManager.Update 里算出来的 —— OnLevelLoaded 这一瞬间它可能还是**上一张图/主菜单**的值
            //    （帧号 m_currentFrameIndex 是存档里带过来的、当场就对，日历不是）。用一个短延时在 Update 里补做，
            //    见 _investInitTimer。（裁剪也用同一个周号，一起挪过去，免得拿脏周号把分期裁掉。）
            _investInitTimer = INVEST_INIT_DELAY;
            // 失效区划的清理不在这里做：读档瞬间区划可能还没建出来，立即判断会误删整个层级。
            // 改由 Update 里的 TickPruneMissingDistricts() 延迟+二次确认后再清（见该方法注释）。
            _pruneGrace = PRUNE_GRACE;
            _pruneTick = 0f;
            _missingPrev.Clear();
            _knownDistricts.Clear();
            _knownReady = false;
            _deadIds = DistrictDataStore.LoadDeadIds(SaveName);   // 墓碑：跨会话保留，用于识别 ID 复用
            Debug.Log("[DFM] Save key='" + SaveName + "' levels=" +
                (Hierarchy != null ? Hierarchy.LevelOf.Count : -1) +
                " metaId='" + (Singleton<SimulationManager>.instance != null &&
                    Singleton<SimulationManager>.instance.m_metaData != null
                    ? Singleton<SimulationManager>.instance.m_metaData.m_gameInstanceIdentifier : "") + "'");

            // 周度时间序列库：读档加载历史；当前周若尚未记录则先补记一条（避免读档当周漏记）
            _series = new DistrictSeriesDB();
            DistrictSeriesStore.Load(_series, SaveName);
            // 通勤样本（平均通勤时间）也跨存档保留（用户 2026-09-27：「样本保存后要留着」）
            if (Calculator != null) Calculator.LoadCommuteSamples(DistrictDataStore.LoadCommuteSamples(SaveName));
            _lastWeek = GameWeek.CurrentWeek;

            // 丢弃「还没被保存的时间线」：玩家没点保存就退出的话，那些周的记录仍然留在文件里，
            // 但重新读档后游戏周回到了存档点之前——这些 >当前周 的记录不该计入统计
            // （否则回档/读旧档时增量会拿另一条时间线的数据当基准，算出离谱的值）。
            // 文件是追加式的，这些周仍在文件里，但每次读档都会再丢一次，不会参与任何统计。
            int dropped = _series.DropWeeksAfter(_lastWeek);
            if (dropped > 0)
                Debug.Log("[DFM] Series: 丢弃 " + dropped + " 个晚于当前周(" + _lastWeek
                          + ")的记录（未保存/回档的时间线）");
            // 当前周若尚未记录 → 排队，等建成区数据就绪再采（避免把「建成区=0」的垃圾周写进库）
            if (!_series.HasWeek(_lastWeek)) { _pendingWeek = _lastWeek; _hasPendingWeek = true; }

            // 语言不再自动识别：旧的 Steam 语言探测在当前运行库上必然失败
            // （GetSteamLanguage 会抛 Method not found: System.Type.op_Inequality），
            // 结果是每次读档都被强行改成英文。改为面板顶部的「语言」按钮手动切换。
        }



        /// <summary>
        /// 原版区划的定期维护（每 PRUNE_INTERVAL 秒一次；读档后先宽限 PRUNE_GRACE 秒，游戏暂停时不计时）：
        ///   ① 清理「已被玩家删掉」的区划在层级/组合里留下的幽灵条目。
        ///      （旧代码只在 Awake 清一次，而那一刻区划可能还没建出来，有误删整个层级的风险。）
        ///   ② 识别 **ID 复用** —— 原版会回收被删区划的 ID，玩家新画的区划可能拿到同一个 ID。
        ///      被判过「删除」的 ID 一旦又出现，就认定换了主人：清掉它的层级/组合归属和**周库历史**，
        ///      否则它的增量/增速会拿完全无关的另一块地当基准。
        /// 三道保险：宽限期；连续两次检查都判定才动手；层级项若会被全部删光则不下手；
        /// 外加「一个已创建区划都没有」时本轮直接放弃判断。
        /// </summary>
        private void TickDistrictMaintenance()
        {
            try
            {
                if (Hierarchy == null) return;
                if (_pruneGrace > 0f) { _pruneGrace -= Time.deltaTime; return; }

                _pruneTick -= Time.deltaTime;
                if (_pruneTick > 0f) return;
                _pruneTick = PRUNE_INTERVAL;

                DistrictManager dm = Singleton<DistrictManager>.instance;
                if (dm == null) return;
                District[] buf = dm.m_districts.m_buffer;
                uint size = dm.m_districts.m_size;
                if (buf == null || size < 2) return;
                uint scan = size < (uint)buf.Length ? size : (uint)buf.Length;

                // 当前已创建的区划集合
                var created = new System.Collections.Generic.HashSet<ushort>();
                for (uint i = 1; i < scan; i++)
                    if ((buf[i].m_flags & District.Flags.Created) != 0) created.Add((ushort)i);

                // 保险：一个区划都没有 → 多半是管理器还没就绪，本轮不判断
                if (created.Count == 0) return;

                // 首次只建立基线，不做任何判断
                if (!_knownReady)
                {
                    _knownReady = true;
                    _knownDistricts = created;
                    _missingPrev.Clear();
                    _gonePrev.Clear();
                    return;
                }

                // ---- ② ID 复用：墓碑里的 ID 又出现了 → 换主人了，清掉旧数据 ----
                var reused = new System.Collections.Generic.List<ushort>();
                foreach (ushort id in _deadIds)
                    if (created.Contains(id)) reused.Add(id);
                if (reused.Count > 0)
                {
                    bool historyDropped = false;
                    for (int i = 0; i < reused.Count; i++)
                    {
                        ushort id = reused[i];
                        Hierarchy.Remove(id);
                        for (int gi = 0; gi < Groups.Count; gi++) Groups[gi].Members.Remove(id);
                        Investments.Remove(id);   // 自定义投资额同样按区划 ID 存，必须一起清
                        int n = _series != null ? _series.DropDistrict(id) : 0;
                        if (n > 0) historyDropped = true;
                        _deadIds.Remove(id);
                        Debug.Log("[DFM] 区划 ID " + id + " 被复用：已清除其层级/组合/投资额归属与 " + n + " 周历史");
                    }
                    // 追加式文件里的旧行必须靠整表重写才能清掉 —— 但**不立刻写**（写作只发生在游戏存档后），
                    // 先把"该重写"记下来，落盘时走 Rewrite 分支（见 FlushCache）。
                    if (historyDropped && _series != null) _seriesNeedsRewrite = true;
                    MarkDirty();
                    _knownDistricts = created;
                    _missingPrev.Clear();
                    _gonePrev.Clear();
                    return;
                }

                // ---- ① 幽灵条目：层级/组合里已不存在的区划 ----
                var missing = new System.Collections.Generic.HashSet<ushort>();
                foreach (var kv in Hierarchy.LevelOf)
                {
                    ushort id = kv.Key;
                    if (id >= buf.Length || (buf[id].m_flags & District.Flags.Created) == 0)
                        missing.Add(id);
                }
                for (int gi = 0; gi < Groups.Count; gi++)
                {
                    GroupData g = Groups[gi];
                    foreach (ushort m in g.Members)
                        if (m >= buf.Length || (buf[m].m_flags & District.Flags.Created) == 0)
                            missing.Add(m);
                }

                var confirmed = new System.Collections.Generic.List<ushort>();
                foreach (ushort id in missing)
                    if (_missingPrev.Contains(id)) confirmed.Add(id);

                // 保险：层级项会被全部删光 → 判断本身可疑，不下手
                bool allGone = Hierarchy.LevelOf.Count > 0 && confirmed.Count >= Hierarchy.LevelOf.Count;
                if (confirmed.Count > 0 && !allGone)
                {
                    foreach (ushort id in confirmed) Hierarchy.Remove(id);

                    int removedMembers = 0;
                    for (int gi = 0; gi < Groups.Count; gi++)
                    {
                        GroupData g = Groups[gi];
                        var dead = new System.Collections.Generic.List<ushort>();
                        foreach (ushort m in g.Members)
                            if (confirmed.Contains(m)) dead.Add(m);
                        for (int k = 0; k < dead.Count; k++) { g.Members.Remove(dead[k]); removedMembers++; }
                    }

                    int removedInvest = 0;
                    for (int i = 0; i < confirmed.Count; i++)
                        if (Investments.Remove(confirmed[i])) removedInvest++;

                    MarkDirty();
                    Debug.Log("[DFM] 清理失效区划: 层级 " + confirmed.Count + " 项, 组合成员 " +
                        removedMembers + " 项, 投资额 " + removedInvest + " 项");
                }

                // ---- 墓碑：连续两次确认「消失」的已创建区划（供下次识别 ID 复用）----
                var gone = new System.Collections.Generic.HashSet<ushort>();
                foreach (ushort id in _knownDistricts) if (!created.Contains(id)) gone.Add(id);
                var confirmedGone = new System.Collections.Generic.List<ushort>();
                foreach (ushort id in gone)
                    if (_gonePrev.Contains(id)) confirmedGone.Add(id);
                if (confirmedGone.Count > 0)
                {
                    for (int i = 0; i < confirmedGone.Count; i++) _deadIds.Add(confirmedGone[i]);
                    MarkDirty();   // 墓碑也只进内存，存档时随 .dead 一起落盘（见 FlushCache）
                    Debug.Log("[DFM] 记录 " + confirmedGone.Count + " 个已删除区划的墓碑（该 ID 若被复用会清掉旧数据）");
                }

                _missingPrev = missing;
                _gonePrev = gone;
                _knownDistricts = created;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[DFM] TickDistrictMaintenance failed: " + ex.Message);
            }
        }

        /// <summary>
        /// 裁掉**永远进不了统计窗口**的投资分期：早于「当前周 + 1 − 最长周期(260 周)」的那些，
        /// 在任何周期下都算不出来了（窗口下界最多回溯 260 周），留着只会让 .inv 越写越长。
        /// 某区划的分期被裁光 → 整条删掉（显示本来也是 0，与「清空」同效）。
        ///
        /// ⚠️ 只裁**过去**，绝不裁未来：月/季/年/5 年的分期本来就记在未来的周上，
        ///    照周库那样丢弃晚于当前周的记录，会让一读档分期就少掉一大半。
        /// ⚠️ 周刻度必须和录入/显示同源（<see cref="InvestWeek.Now"/>），否则勾了「跟随 RealTime 日历」
        ///    之后会按帧周去裁日历周的账，一笔也保不住。
        /// </summary>
        public void PruneInvestments()
        {
            if (Investments == null || Investments.Count == 0) return;
            long lo = (long)InvestWeek.Now + 1 - ModSettings.MaxPeriodWeeks;
            System.Collections.Generic.List<ushort> empty = null;
            foreach (System.Collections.Generic.KeyValuePair<ushort,
                     System.Collections.Generic.List<InvestInstallment>> kv in Investments)
            {
                System.Collections.Generic.List<InvestInstallment> list = kv.Value;
                if (list == null) { (empty ?? (empty = new System.Collections.Generic.List<ushort>())).Add(kv.Key); continue; }
                for (int i = list.Count - 1; i >= 0; i--)
                    if ((long)list[i].Week < lo) list.RemoveAt(i);
                if (list.Count == 0)
                    (empty ?? (empty = new System.Collections.Generic.List<ushort>())).Add(kv.Key);
            }
            if (empty == null) return;
            for (int i = 0; i < empty.Count; i++) Investments.Remove(empty[i]);
        }

        /// <summary>层级数据按存档区分：用存档唯一标识作为文件名 key，互不覆盖。</summary>
        private static string MakeSaveName()
        {
            try
            {
                SimulationManager sm = Singleton<SimulationManager>.instance;
                if (sm != null && sm.m_metaData != null)
                {
                    string id = sm.m_metaData.m_gameInstanceIdentifier;
                    if (!string.IsNullOrEmpty(id))
                        return "district_hierarchy_" + id;
                    if (!string.IsNullOrEmpty(sm.m_metaData.m_CityName))
                        return "district_hierarchy_" + sm.m_metaData.m_CityName;
                }
            }
            catch { }
            return "district_hierarchy_unsaved"; // 未保存新游戏用独立名，避免读到旧全局数据
        }

        private void Update()
        {
            // 昼夜状态：跨过昼夜界线时写一行日志（只在"该跳夜晚"时才写，见 NightSkipActive）。
            // 放在跨周检测之前 —— 采样要在同一帧就用到最新的昼夜判断。
            TickNightState();

            // 原版游戏周边界检测（帧数法，对 RealTime 等真实时间模组免疫）：跨周则排队采样
            if (GameWeek.Tick(ref _lastWeek))
            {
                if (Calculator != null) Calculator.ClearCache(); // 丢弃上一周缓存，按最新重算
                _pendingWeek = _lastWeek;
                _hasPendingWeek = true;
            }
            // 建成区数据就绪后才真正采样（否则会把「建成区=0」写成基准，污染增量）
            if (_hasPendingWeek && Calculator != null && Calculator.BuiltAreaReady)
            {
                SampleWeek(_pendingWeek);
                _hasPendingWeek = false;
                MarkDirty();
            }

            // 注：统计模式（货币/周期）变了**不需要**对周库做任何事 —— 库里一律是原始值，
            //     显示系数只在读数时现乘（见 DistrictSeriesDB 顶部的「存储口径」）。

            // 公园全天开放：勾过的档，读档约 10 秒后自动再应用一次（幂等；没勾过则不动 RealTime 的配置）
            if (Settings != null && Settings.ParkAlwaysOpen && !_parkReapplied)
            {
                _parkReapplyTimer -= Time.deltaTime;
                if (_parkReapplyTimer <= 0f)
                {
                    _parkReapplied = true;
                    string pdetail;
                    bool pok = RealTimeBridge.SetOverride(true, out pdetail);
                    Debug.Log("[DFM] 读档后自动应用「公园全天开放」：" + (pok ? "成功 " : "未生效 ") + pdetail);
                }
            }

            // 通勤时间跟踪：每帧调用，内部**每 COMMUTE_POLL_FRAMES 帧**才真轮询一次监视名单
            // （第三版：只看就业居民自己的 CurrentLocation，不再扫实例/车辆缓冲）。
            // 开销自己会打日志（ShowDebug 时每 5 秒一行，含「轮询耗时/次」）。
            if (Calculator != null) Calculator.TickCommuteTrack();

            // 区划定期维护：清理已删区划的幽灵条目 + 识别 ID 复用（延迟 + 二次确认，见方法注释）
            TickDistrictMaintenance();

            // 每秒遍历一部分建筑，自动保存间隔秒完成整体密度遍历（避免卡顿）
            _densityTick -= Time.deltaTime;
            if (_densityTick <= 0f)
            {
                _densityTick = 1f;
                if (Calculator != null) Calculator.TickDensityBuild();
            }

            // 读档后延迟做一次投资分期换锚 + 裁剪（日历周要等 m_currentGameTime 刷新，见 _investInitTimer）
            if (_investInitTimer > 0f)
            {
                _investInitTimer -= Time.deltaTime;
                if (_investInitTimer <= 0f)
                {
                    _investInitTimer = -1f;
                    ReanchorInvestments("读档");
                    PruneInvestments();
                }
            }

            // 落盘**只在游戏存档之后**（用户 2026-09-28：内存里的一律只是缓存，保存时才覆盖数据库）。
            // 存档线程只置标志位，真正的写文件在这里做（主线程，避免线程问题）。
            if (_saveRequested)
            {
                _saveRequested = false;
                FlushCache("游戏存档");
            }
        }

        /// <summary>
        /// 把内存缓存里的全部数据写入本模组的存档文件（.hier / .grp / .inv / .commute / .dead / .series）。
        /// **只有两种时候会调用**：游戏存档之后（Update）、以及"已存档但还没写就退出"的兜底（Dispose）。
        ///
        /// 刻意不做「定时自动写盘」：那样玩一段不存档就回档时，被丢弃的那条时间线会覆盖数据库
        /// （区划周库、投资分期、墓碑全脏）。现在数据库永远等于「上一次存档时的样子」。
        /// 周库优先追加（Flush 只写新周，支持无上限增长）；只有清过某区划历史时才整表重写（Rewrite）。
        /// </summary>
        public void FlushCache(string why)
        {
            try
            {
                DistrictDataStore.Save(Hierarchy, SaveName);
                DistrictDataStore.SaveGroups(Groups, SaveName);
                DistrictDataStore.SaveInvestments(Investments, SaveName, _investScale);
                if (Calculator != null) DistrictDataStore.SaveCommuteSamples(SaveName, Calculator.DumpCommuteSamples());
                DistrictDataStore.SaveDeadIds(_deadIds, SaveName);
                if (_series != null)
                {
                    if (_seriesNeedsRewrite) { DistrictSeriesStore.Rewrite(_series, SaveName); _seriesNeedsRewrite = false; }
                    else DistrictSeriesStore.Flush(_series, SaveName);
                }
                _dirty = false;
                Debug.Log("[DFM] 缓存已落盘（" + why + "）：层级 " + (Hierarchy != null ? Hierarchy.LevelOf.Count : 0) +
                          " 项 / 组合 " + Groups.Count + " 组 / 投资 " + Investments.Count + " 区划 / 周库 " +
                          (_series != null ? _series.WeekCount : 0) + " 周");
            }
            catch (System.Exception ex) { Debug.LogError("[DFM] FlushCache failed: " + ex.Message); }
        }

        /// <summary>
        /// 采样一周：对每个已创建的原版区划取一条 FinanceResult 全字段快照，追加进序列库。
        /// 每周仅调用一次（由 Update 的周边界检测触发 / Awake 补记）。
        /// </summary>
        private void SampleWeek(uint week)
        {
            try
            {
                if (_series == null || Calculator == null) return;

                // **夜间只统计白天数据**：装 RealTime 且公园覆盖没生效时，夜里这一周直接跳过（不写行）。
                // 跨周检测是按帧数走的（原版帧周，与昼夜解耦），所以夜里会连着跨过好几周 —— 那些周
                // 全部丢弃；回到白天后，跨周检测照常继续（周号仍按游戏帧周连续递增，只是夜里那几周
                // 在周库里是**空档**：`EnsureBasePrim` / `GetDistrictBuiltValueDelta` 取基准时本来就是
                // "取 ≤ 目标周的最近一个有效周"，空档天然被跳过，不需要为它改任何口径）。
                if (NightSkipActive && GameWeek.IsNight)
                {
                    _nightSkipped++;
                    if (Settings != null && Settings.ShowDebug)
                        Debug.Log("[DFM] 周库：夜间跳过第 " + week + " 周（只统计白天数据）");
                    return;
                }

                ushort[] ids = GetVanillaDistricts();
                // 三项通勤指标（按居住地归到区划）——采样时按区划取值，随 FinanceResult 一起入周库。
                // 都是与货币/周期无关的原始量；通勤时间在预热期内是 0（＝该周无数据，与既有约定一致）。
                double[] commuteKm = Calculator.GetCommuteDistance();
                double[] localEmpPct = Calculator.GetLocalEmploymentRate();
                double[] commuteMin = Calculator.GetCommuteTime();
                // 「区域工人数」（键 12 第二子模式）也要入周库（v8 第 50 列），否则这一项没有增速基准
                double[] panelWorkers = Calculator.GetDistrictPanelWorkers();
                var rows = new System.Collections.Generic.Dictionary<ushort, double[]>();
                for (int i = 0; i < ids.Length; i++)
                {
                    ushort id = ids[i];
                    if (id == 0) continue;
                    string nm = GetVanillaDistrictName(id);
                    if (!string.IsNullOrEmpty(nm)) _series.Names[id] = nm;
                    DistrictFinanceCalculator.FinanceResult r = Calculator.Calculate(id);
                    if (!r.IsValid) continue;
                    // 入库一律存原始值（GDP 在这里除回显示系数）；通勤三项按区划带上
                    rows[id] = DistrictSeriesDB.ToRow(r, commuteKm[id], localEmpPct[id], commuteMin[id],
                        panelWorkers[id]);
                }

                long ticks = 0L;
                try { ticks = GameWeek.VanillaDate.Ticks; } catch { }
                // 模组被卸下过一阵又装回来 → 中间那些周没有记录，用「卸下前最后一周」与「本周」逐列线性插值补上
                // （用户 2026-09-27）。不补的话增速/增量的「N 周前」窗口会跨过空档，测出来的是更长的区间。
                // ⚠️ 但**夜间跳过**时绝不能补：那些空档是我们自己故意跳掉的夜晚，补出来正好是"夜里被压低的
                //    地价"那条假数据 —— 等于白跳（见 NightSkipActive）。
                //    （已知取舍：这样"装了 RealTime 且没开公园覆盖"的档就**永不补空档**了，比注释原意宽 ——
                //      2026-09-29 核查文档 1.2 指出，用户 2026-09-29 决定**暂不改**，见交接文档 §8.36。）
                if (!NightSkipActive)
                {
                    uint lastRec = _series.LastWeek();   // 补之前先记下来，日志里才是「从哪补到哪」
                    int filled = _series.FillGapWeeks(week, rows);
                    if (filled > 0)
                        Debug.Log("[DFM] 周库空档补齐：在 " + lastRec + " 与 " + week +
                                  " 之间插值补了 " + filled + " 周（模组曾被卸下）");
                }
                // 本周只写一次（2026-09-29 核查文档 1.3：原来这个调用在 if/else 两个分支里各有一份、
                // 外面还有一份，一帧最多写 2 次 —— UpsertWeek 是覆盖式的，结果对，但纯属冗余）
                _series.UpsertWeek(week, ticks, rows); // 按真实游戏周存；同周重复记录则以最新为准（回档覆盖）
                // ⚠️ 这一行别删：它是「跨周采样」的心跳（§8.22 的 1 现实秒 = 60 模拟帧 就是靠它反推的）
                Debug.Log("[DFM] Series sampled week " + week + " (" + rows.Count + " districts)"
                          + (NightSkipActive ? "（白天）" : ""));
            }
            catch (System.Exception ex) { Debug.LogWarning("[DFM] SampleWeek failed: " + ex.Message); }
        }

        private void OnDestroy()
        {
            // 与 Dispose 同样的规矩：**不主动写盘**。只有"游戏已经存过档、但还没来得及落盘"
            // （存档后立刻退出/切换关卡）才补写一次，其余情况缓存直接丢掉。
            if (_saveRequested)
            {
                _saveRequested = false;
                FlushCache("退出前补写");
            }
            if (_instance == this) _instance = null;
        }

        public void RefreshSettings() { Settings = ModSettings.Load(); }

        /// <summary>
        /// 标记「缓存里有改动」—— **不再触发写盘**（用户 2026-09-28：只在游戏存档时落盘）。
        /// 现在只用于面板上的「未保存」提示与日志，落盘由 <see cref="FlushCache"/> 负责。
        /// </summary>
        public void MarkDirty()
        {
            _dirty = true;
        }

        /// <summary>获取原版区划名</summary>
        public string GetVanillaDistrictName(ushort id)
        {
            if (id == 0) return "(none)";
            try
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                return dm.GetDistrictName(id);
            }
            catch { return "District #" + id; }
        }

        /// <summary>获取原版区划列表</summary>
        public ushort[] GetVanillaDistricts()
        {
            try
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                District[] buf = dm.m_districts.m_buffer;
                uint size = dm.m_districts.m_size;
                var list = new System.Collections.Generic.List<ushort>();
                for (uint i = 1; i < size; i++)
                {
                    if ((buf[i].m_flags & District.Flags.Created) != 0)
                        list.Add((ushort)i);
                }
                return list.ToArray();
            }
            catch { return new ushort[0]; }
        }
    }


    /// <summary>
    /// 与 RealTime 模组（工坊 3059406297）的**可选**互操作：让公园和广场**全天都算"在运营"**。
    /// **覆盖模式**（用户 2026-09-27：「是覆盖状态，不要改变 realtime 的设置」）——
    /// 本模组**绝不修改 RealTime 的配置**，只在内存里把它的判定盖住：
    ///
    /// 病根（反编译 RealTime.dll 得到）：`RealTime.CustomAI.RealTimeBuildingAI.IsParkMaintenanceHours(int)`
    /// 决定这栋楼当前算不算"在运营"（按班次/工作日/当前小时），公园另有 `IsParkMaintenanceHours` 管维护时段，
/// 而公园的吸引力/维护度只在维护时段里产出
    /// （RealTime 用 `ParkAIPatch` 补了 `ParkAI.ProduceGoods / GetMaintenanceLevel / GetColor`）→
    /// 夜里不在这段里 ⇒ 等于关门 ⇒ 吸引力与地价下降 ⇒ 本模组 GDP / 地价 / 人均可支配跟着波动。
    ///
    /// 做法：用 Harmony（DFM 既有的 CitiesHarmony 依赖）给这个方法挂一个 **Prefix**：
    ///   · 只有当那栋楼是**公园/广场**（`ItemClass.Service.Beautification`）时才 `__result = true; return false;`
    ///     —— 即"永远在维护时段"，直接跳过原方法（原方法读的就是 RealTime 的配置，我们不看也不改）；
    ///   · 其它建筑（万一这个方法也被别处调用）**不介入**，交给原逻辑。
    /// 取消勾选 = 撤掉这个补丁（`Unpatch`），一切回到 RealTime 原样。**全程不改它的配置、不写存档。**
    ///
    /// **反射找目标 + try/catch 兜底**：没装 RealTime、或它的方法签名变了 → 打补丁失败 → 面板与日志写明原因，
    /// 不影响其它功能（DFM 不引用 RealTime.dll，编译期不依赖）。
    /// </summary>
    internal static class RealTimeBridge
    {
        private static HarmonyLib.Harmony _harmony;
        private static bool _active;
        /// <summary>覆盖当前是否生效（面板勾选框显示的就是它）。</summary>
        public static bool Active { get { return _active; } }

        private static bool _installed;
        // 初值给个「很久以前」（别用 0：Time.time 在启动头 5 秒内还小于 5，会误判成"刚查过"→ 永远不出结果）
        private static float _installedChecked = -999f;

        /// <summary>
        /// RealTime 是否已加载（**带 5 秒缓存** —— 设置面板每帧都要问它，不能每帧去扫程序集）。
        /// 用途：夜间跳过周库写入的判据之一（见 Hub.NightSkipActive）。
        /// </summary>
        public static bool Installed
        {
            get
            {
                if (Time.time - _installedChecked < 5f) return _installed;
                _installedChecked = Time.time;
                try { _installed = !object.ReferenceEquals(FindRealTime(), null); }
                catch { _installed = false; }
                return _installed;
            }
        }

        /// <summary>在已加载的程序集里找 RealTime（找不到返回 null）。</summary>
        private static System.Reflection.Assembly FindRealTime()
        {
            System.Reflection.Assembly[] asms = System.AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                if (asms[i].GetName().Name == "RealTime") return asms[i];
            }
            return null;
        }

        /// <summary>
        /// 找要覆盖的两个判定方法（都在 `RealTime.CustomAI.RealTimeBuildingAI` 上，参数都是建筑 ID）：
        ///   · **`IsBuildingWorking`（主）**：**运营时间**判定 —— 按班次/工作日/当前小时决定这栋楼"现在开不开门"；
        ///     RealTime 的补丁都通过它（如 `PlayerBuildingAIPatch.IsBuildingWorkingSafe`）判断建筑是否在运营。
        ///   · `IsParkMaintenanceHours`（辅）：**公园维护时段**判定 —— 公园吸引力/维护度只在这段里产出。
        /// 两个都盖成"恒真"，公园就是真正意义上的全天运营；找不到主目标就报错返回（附上到底找到了啥）。
        /// </summary>
        private static bool FindTargets(System.Reflection.Assembly asm, out System.Reflection.MethodInfo main,
            out System.Reflection.MethodInfo aux, out string how)
        {
            main = null; aux = null; how = null;
            System.Type ai = asm.GetType("RealTime.CustomAI.RealTimeBuildingAI");
            if (object.ReferenceEquals(ai, null)) { how = "没找到 RealTime.CustomAI.RealTimeBuildingAI"; return false; }
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Static;
            main = ai.GetMethod("IsBuildingWorking", F);
            aux = ai.GetMethod("IsParkMaintenanceHours", F);
            if (object.ReferenceEquals(main, null) && object.ReferenceEquals(aux, null))
            { how = "两个判定方法都没找到（RealTime 版本不同？）"; return false; }
            how = (object.ReferenceEquals(main, null) ? "运营时间判定缺失" : "运营时间=" + main.Name)
                + (object.ReferenceEquals(aux, null) ? "，维护时段判定缺失" : "，维护时段=" + aux.Name);
            return true;
        }

        /// <summary>开 / 关覆盖。detail 写明结果（面板与日志都会显示；失败也写日志）。</summary>
        public static bool SetOverride(bool on, out string detail)
        {
            detail = null;
            try
            {
                if (on == _active) { detail = on ? "覆盖已经是开启状态" : "覆盖本来就是关闭的"; return true; }
                System.Reflection.Assembly asm = FindRealTime();
                if (object.ReferenceEquals(asm, null)) { detail = "没找到 RealTime（未安装或尚未加载）"; Debug.LogWarning("[DFM] 公园全覆盖：" + detail); return false; }
                System.Reflection.MethodInfo main, aux; string how;
                if (!FindTargets(asm, out main, out aux, out how)) { detail = how; Debug.LogWarning("[DFM] 公园全覆盖：" + detail); return false; }

                if (on)
                {
                    if (object.ReferenceEquals(_harmony, null)) _harmony = new HarmonyLib.Harmony("DistrictFinanceManager.parkoverride");
                    System.Reflection.MethodInfo prefix = typeof(RealTimeBridge).GetMethod("ParkMaintenancePrefix",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (!object.ReferenceEquals(main, null))
                        _harmony.Patch(main, new HarmonyLib.HarmonyMethod(prefix), null, null);
                    if (!object.ReferenceEquals(aux, null))
                        _harmony.Patch(aux, new HarmonyLib.HarmonyMethod(prefix), null, null);
                    _active = true;
                    detail = "已覆盖 " + how + "（公园/广场恒为运营中/维护时段；未改动 RealTime 的任何设置）";
                }
                else
                {
                    if (!object.ReferenceEquals(_harmony, null))
                    {
                        if (!object.ReferenceEquals(main, null))
                            _harmony.Unpatch(main, HarmonyLib.HarmonyPatchType.All, _harmony.Id);
                        if (!object.ReferenceEquals(aux, null))
                            _harmony.Unpatch(aux, HarmonyLib.HarmonyPatchType.All, _harmony.Id);
                    }
                    _active = false;
                    detail = "已撤掉覆盖，恢复 RealTime 原逻辑（" + how + "）";
                }
                Debug.Log("[DFM] 公园全覆盖：" + detail);
                return true;
            }
            catch (System.Exception ex)
            {
                detail = "调用失败：" + ex.Message;
                Debug.LogWarning("[DFM] 公园全覆盖失败：" + ex);
                return false;
            }
        }

        /// <summary>
        /// Harmony 前缀：**只对公园/广场**把结果改成 true（＝恒在维护时段）并跳过原方法；其它建筑不介入。
        /// 用 `object[] __args` 拿参数（不必知道原参数是 int 还是 ushort，版本兼容性最好）。
        /// </summary>
        public static bool ParkMaintenancePrefix(object[] __args, ref bool __result)
        {
            try
            {
                if (__args != null && __args.Length > 0 && __args[0] != null)
                {
                    ushort id = 0;
                    try { id = System.Convert.ToUInt16(__args[0], System.Globalization.CultureInfo.InvariantCulture); }
                    catch (System.Exception) { id = 0; }
                    if (id != 0)
                    {
                        BuildingManager bm = Singleton<BuildingManager>.instance;
                        if (bm != null)
                        {
                            Building[] buf = bm.m_buildings.m_buffer;
                            if (id < buf.Length)
                            {
                                BuildingInfo info = buf[id].Info;
                                if (info != null && info.m_class.m_service != ItemClass.Service.Beautification)
                                    return true;      // 不是公园/广场 → 交给 RealTime 原逻辑
                            }
                        }
                    }
                }
                __result = true;   // 公园/广场（或读不到楼）→ 恒为维护时段
                return false;      // 跳过原方法
            }
            catch (System.Exception) { return true; }   // 自己出错就什么都不做，别影响别人的逻辑
        }
    }
}
