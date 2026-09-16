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
                if (_instance._series != null)
                    DistrictSeriesStore.Flush(_instance._series, _instance.SaveName);
                if (_instance.Hierarchy != null)
                    DistrictDataStore.Save(_instance.Hierarchy, _instance.SaveName);
                _instance.Hierarchy = null;
                Destroy(_instance.gameObject);
                _instance = null;
            }
        }

        public DistrictHierarchy Hierarchy;
        public DistrictFinanceCalculator Calculator;
        public ModSettings Settings { get; private set; }
        public string SaveName { get; private set; }
        public System.Collections.Generic.List<GroupData> Groups = new System.Collections.Generic.List<GroupData>();

        public ushort SelectedID;
        public int EditingLevel = 1;

        /// <summary>当前存档的有效居民/工人权重（per-save，未保存时回退全局设置）。</summary>
        public float CurrentResWeight = 0.5f;
        public float CurrentWorkWeight = 3f;

        /// <summary>原版区划工具当前选中的区划 ID（byte，0 表示未选中）。</summary>
        public byte SelectedVanillaDistrict { get; set; }

        private const float PRUNE_GRACE = 30f;    // 读档后宽限这么久才开始判断失效区划
        private const float PRUNE_INTERVAL = 10f; // 之后每隔这么久检查一次；连续两次缺失才真删

        private float _saveTimer;
        private bool _dirty;
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

        private void Awake()
        {
            _instance = this;
            Settings = ModSettings.Load();
            Calculator = new DistrictFinanceCalculator();
            SaveName = MakeSaveName();
            Hierarchy = DistrictDataStore.Load(SaveName);
            Groups = DistrictDataStore.LoadGroups(SaveName);
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
            float savedRes, savedWor;
            if (DistrictDataStore.TryLoadWeights(SaveName, out savedRes, out savedWor))
            {
                CurrentResWeight = savedRes;
                CurrentWorkWeight = savedWor;
            }
            else
            {
                CurrentResWeight = Settings.ResidentWeight;
                CurrentWorkWeight = Settings.WorkerWeight;
            }

            // 周度时间序列库：读档加载历史；当前周若尚未记录则先补记一条（避免读档当周漏记）
            _series = new DistrictSeriesDB();
            DistrictSeriesStore.Load(_series, SaveName);
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
                        int n = _series != null ? _series.DropDistrict(id) : 0;
                        if (n > 0) historyDropped = true;
                        _deadIds.Remove(id);
                        Debug.Log("[DFM] 区划 ID " + id + " 被复用：已清除其层级/组合归属与 " + n + " 周历史");
                    }
                    // 追加式文件里的旧行必须靠整表重写才能清掉
                    if (historyDropped && _series != null) DistrictSeriesStore.Rewrite(_series, SaveName);
                    DistrictDataStore.SaveDeadIds(_deadIds, SaveName);
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

                    MarkDirty();
                    Debug.Log("[DFM] 清理失效区划: 层级 " + confirmed.Count + " 项, 组合成员 " + removedMembers + " 项");
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
                    DistrictDataStore.SaveDeadIds(_deadIds, SaveName);
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

        /// <summary>当前存档的居民权重（保存过用存档值，否则用全局设置）。</summary>
        public float GetEffectiveResWeight()
        {
            return CurrentResWeight;
        }

        /// <summary>当前存档的工人权重（保存过用存档值，否则用全局设置）。</summary>
        public float GetEffectiveWorkWeight()
        {
            return CurrentWorkWeight;
        }

        /// <summary>把居民/工人权重保存到当前存档，并清缓存让下一帧重算。</summary>
        public void SaveCurrentWeights(float resWeight, float worWeight)
        {
            CurrentResWeight = resWeight;
            CurrentWorkWeight = worWeight;
            DistrictDataStore.SaveWeights(resWeight, worWeight, SaveName);
            if (Calculator != null) Calculator.ClearCache();
            Debug.Log("[DFM] Weights saved for " + SaveName + ": res=" + resWeight + " wor=" + worWeight);
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

            // 区划定期维护：清理已删区划的幽灵条目 + 识别 ID 复用（延迟 + 二次确认，见方法注释）
            TickDistrictMaintenance();

            // 每秒遍历一部分建筑，自动保存间隔秒完成整体密度遍历（避免卡顿）
            _densityTick -= Time.deltaTime;
            if (_densityTick <= 0f)
            {
                _densityTick = 1f;
                if (Calculator != null) Calculator.TickDensityBuild();
            }

            if (_dirty)
            {
                _saveTimer -= Time.deltaTime;
                if (_saveTimer <= 0f)
                {
                    DistrictDataStore.Save(Hierarchy, SaveName);
                    DistrictDataStore.SaveGroups(Groups, SaveName);
                    if (_series != null) DistrictSeriesStore.Flush(_series, SaveName);
                    _dirty = false;
                }
            }
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

                ushort[] ids = GetVanillaDistricts();
                var rows = new System.Collections.Generic.Dictionary<ushort, double[]>();
                for (int i = 0; i < ids.Length; i++)
                {
                    ushort id = ids[i];
                    if (id == 0) continue;
                    string nm = GetVanillaDistrictName(id);
                    if (!string.IsNullOrEmpty(nm)) _series.Names[id] = nm;
                    DistrictFinanceCalculator.FinanceResult r = Calculator.Calculate(id);
                    if (!r.IsValid) continue;
                    rows[id] = DistrictSeriesDB.ToRow(r);
                }

                long ticks = 0L;
                try { ticks = GameWeek.VanillaDate.Ticks; } catch { }
                _series.UpsertWeek(week, ticks, rows); // 按真实游戏周存；同周重复记录则以最新为准（回档覆盖）
                Debug.Log("[DFM] Series sampled week " + week + " (" + rows.Count + " districts)");
            }
            catch (System.Exception ex) { Debug.LogWarning("[DFM] SampleWeek failed: " + ex.Message); }
        }

        private void OnDestroy()
        {
            if (_series != null) DistrictSeriesStore.Flush(_series, SaveName);
            if (_dirty && Hierarchy != null)
            {
                DistrictDataStore.Save(Hierarchy, SaveName);
                DistrictDataStore.SaveGroups(Groups, SaveName);
            }
            if (_instance == this) _instance = null;
        }

        public void RefreshSettings() { Settings = ModSettings.Load(); }

        public void MarkDirty()
        {
            _dirty = true;
            _saveTimer = Settings.UpdateInterval;
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
}
