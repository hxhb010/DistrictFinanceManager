using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace DistrictFinanceManager
{
    /// <summary>
    /// GDP 数据提取 — 只读，不修改游戏逻辑。
    ///
    /// GDP = 地价 ×（居住人数 + 工作人数）。
    ///   地价：District.m_groundData.m_finalLandvalue（0~255）。
    ///   居住人数：District.m_populationData.m_finalCount。
    ///   工作人数：遍历本区划内商业/工业/办公建筑，累加其 CitizenUnit 中的市民数
    ///            （工作场所建筑内登记的市民即该处就业人口）。
    /// 人均 GDP = GDP ÷ 居住人数（在面板显示层计算，规避除零）。
    ///
    /// 支出 = 区划内公共服务建筑（消防/警察/医疗/教育/垃圾/灾害）
    ///        与公园建筑（Beautification）的维护费之和；
    ///        公园区划（Parklife）内的建筑不计入。
    ///        电力/自来水/公共交通因按网络覆盖而非建筑位置供给，不计入。
    ///
    /// 聚合：Calculate 返回自身值 + 递归累加所有下辖子区划的合计（Agg*）。
    /// </summary>
    public class DistrictFinanceCalculator
    {
        public struct FinanceResult
        {
            // 自身
            public double GDP;            // 地价 ×（居住人数 + 工作人数），浮点
            public int Population;        // 居住人数
            public int Workers;           // 工作人数
            public int LandValue;         // 地价（0~255）
            public int BuildingCount;
            public double Area;           // 本区划面积（m²，区划网格 alpha×368.64 加权）
            public double BuiltArea;      // 建成区面积（m² = Σ建筑占地格数 × 64 × 空隙系数2，上限=区划面积）
            public double BuiltValueArea; // 「建筑价值增量」用的加权占地面积（原始面积 × 用途权重，无空隙系数）
            public double DisposableIncome; // 人均可支配周收入（克朗/周，原版口径，未乘显示系数）
            public double IncomeNum;      // 可支配收入分子（Σ工资 + Σ财产，克朗/周）——聚合要按分子求和
            // 各类型区域人口（调试用）
            public int ResPop;            // 住宅居住
            public int ComWorkers;        // 商业工人
            public int IndWorkers;        // 工业工人
            public int OffWorkers;        // 办公工人
            public int PlayerWorkers;     // 玩家工人
            public int ResLow;            // 低密度住宅居住
            public int ResHigh;           // 高密度住宅居住
            public int ComLow;            // 低密度商业工人
            public int ComHigh;           // 高密度商业工人
            // 各类型人口×地价（调试用）
            public long ResLowGDP, ResHighGDP, ComLowGDP, ComHighGDP, IndGDP, OffGDP, PlayerGDP;
            public long Expense;          // 支出（公共服务 + 公园建筑维护费，不含公园区划）
            public long Tax;              // 税收（全市总收入 × 本区划GDP占比）
            public long NetIncome;        // 净收入 = 税收 - 支出

            // 合计（含下辖所有子区划，递归）
            public double AggGDP;
            public int AggPopulation;
            public int AggWorkers;
            public int AggBuildings;
            public double AggArea;        // 聚合面积（含下辖所有子区划，m²）
            public double AggBuiltArea;   // 聚合建成区面积（m²）
            public double AggDisposableIncome; // 聚合人均可支配周收入（分子求和 ÷ 人口求和，克朗/周）
            public double AggIncomeNum;   // 聚合可支配收入分子（子区划分子求和）
            // 聚合各类型人口（调试用）
            public int AggResPop;
            public int AggComWorkers;
            public int AggIndWorkers;
            public int AggOffWorkers;
            public int AggPlayerWorkers;
            public int AggResLow;
            public int AggResHigh;
            public int AggComLow;
            public int AggComHigh;
            // 聚合各类型人口×地价
            public long AggResLowGDP, AggResHighGDP, AggComLowGDP, AggComHighGDP, AggIndGDP, AggOffGDP, AggPlayerGDP;
            public long AggExpense;       // 合计支出
            public long AggTax;           // 合计税收（按GDP比例分配）
            public long AggNetIncome;     // 合计净收入 = 合计税收 - 合计支出

            public bool IsValid;
            public string Diag;
        }

        private Dictionary<ushort, FinanceResult> _cache = new Dictionary<ushort, FinanceResult>();
        private Dictionary<ushort, float> _cacheTime = new Dictionary<ushort, float>();
        private const float CACHE_LIFE_FALLBACK = 10f;

        /// <summary>动态计算间隔 = 更新间隔（1~30 秒）。</summary>
        private static float CacheLife()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Settings != null)
                return Mathf.Clamp(hub.Settings.UpdateInterval, 1f, 30f);
            return CACHE_LIFE_FALLBACK;
        }

        /// <summary>清空所有缓存（设置变化后调用，让统计立即按新参数重算）。</summary>
        public void ClearCache()
        {
            _cache.Clear();
            _cacheTime.Clear();
            _districtGDP = null;
            _districtPop = null;
            _panelWorkers = null;      _panelWorkersTime = 0f;      // 区域工人数（键 12 第二子模式）
            _aggPanelWorkers = null;   _aggPanelWorkersTime = 0f;
            _districtArea = null;
            _districtAreaTime = 0f;
            _builtDelta = null;
            _builtDeltaTime = 0f;
            _districtBuiltArea = null;
            _districtBuiltAreaTime = 0f;
            _districtBuiltWeightArea = null;
            _districtBuiltWeightAreaTime = 0f;
            _basePrim = null;            // 增速基准（跨周 / 换周期 / 改设置都必须重算）
            _basePrimTime = 0f;
            // ⚠️ 这两个是「清零 + 记时间戳」型缓存，时间戳必须用 -∞ 而不是 0f：
            // 判据是 `Time.time - 时间戳 < CacheLife()`，开局 10 秒内 Time.time < 10，
            // 写成 0f 会让刚清零的值被当成有效缓存返回 0（工业/玩家/服务的 GDP、平均地价一起读 0）。
            _totalCityGDP = 0;
            _totalCityGDPTime = float.NegativeInfinity;
            _avgLandValue = 0;
            _avgLandValueTime = float.NegativeInfinity;
            _districtIncomeNum = null;
            _districtIncomeNumTime = 0f;
            _commuteCount = null;      _commuteCountTime = 0f;
            _commuteDist = null;       _commuteDistTime = 0f;
            _localEmpRate = null;      _localEmpRateTime = 0f;
            _aggCommuteDist = null;    _aggCommuteDistTime = 0f;
            _aggLocalEmpRate = null;   _aggLocalEmpRateTime = 0f;
            _commuteTime = null;       _commuteTimeTime = 0f;
            _commuteTimeCnt = null;    _commuteTimeCntTime = 0f;
            _aggCommuteTime = null;    _aggCommuteTimeTime = 0f;
            _commuteTop = null;        _commuteTopTime = 0f;
            _aggCommuteTop = null;     _aggCommuteTopTime = 0f;
            _commuteProg = null;       _commuteProgTime = 0f;
            _aggCommuteProg = null;    _aggCommuteProgTime = 0f;
            // ⚠️ 以下都是**建筑派生统计**，与设置/权重无关，绝不能在 ClearCache 里清：
            //   _allDensity（密度分桶 + GDP 分子） / _allBuiltCells（建成区） /
            //   _allBuiltWeightCells（加权建成区） / _allIncome（收入分子） /
            //   _allOd + _allCommuteSum（通勤/本地就业的源数据）
            // 原因：**每次跨游戏周** Hub.Update 都会先 ClearCache 再采样，清掉的话要等
            // 下一轮建筑遍历跑完（UpdateInterval×3 秒）才有值 ——
            //   · 清 _allIncome    → 人均可支配一整轮读作 0
            //   · 清 _allDensity   → **GDP / 人口密度在 0 和真值之间来回跳**（2026-09-18 踩到）
            //   · 清 _allBuiltCells → 把「建成区=0」的垃圾周写进周库，污染增量基准
            // 显示模式等设置只影响显示层（GetDisplayFactor / 地价倍率），不需要靠清这些来生效。
        }

        private static ushort _logDistrict;
        private static string _logDiag;

        private double _totalCityGDP;
        private float _totalCityGDPTime;
        private double[] _districtGDP;
        private float _districtGDPTime;
        private long[] _districtPop;
        private float _districtPopTime;
        // 「区域工人数」（键 12 的第二个子模式，2026-09-28）：自身 / 聚合两份，缓存口径与 _districtPop 一致。
        private double[] _panelWorkers;
        private float _panelWorkersTime;
        private double[] _aggPanelWorkers;
        private float _aggPanelWorkersTime;
        private double[] _districtArea;
        private float _districtAreaTime;
        private double[] _builtDelta;         // 建成区价值增量缓存（每区划）
        private float _builtDeltaTime;
        private double[] _districtBuiltArea;  // 建成区面积缓存
        private float _districtBuiltAreaTime;
        private double[] _districtBuiltWeightArea;  // 加权建成区面积缓存（增量用）
        private float _districtBuiltWeightAreaTime;
        private static System.Reflection.FieldInfo _incomeField;
        private static System.Reflection.FieldInfo _totalIncomeField;

        public FinanceResult Calculate(ushort districtId)
        {
            if (_cache.ContainsKey(districtId) && Time.time - _cacheTime[districtId] < CacheLife())
                return _cache[districtId];

            FinanceResult r = CalcSelf(districtId);
            if (r.IsValid)
            {
                AggregateChildren(districtId, ref r, new HashSet<ushort>());
                // 聚合人均 = 聚合分子 ÷ 聚合人口（不能对子节点人均值求平均，会被小人口节点带偏）
                r.AggDisposableIncome = r.AggPopulation > 0 ? r.AggIncomeNum / r.AggPopulation : 0.0;
                // 调试文本在 CalcSelf 已设：当前区划各类型人数 + 平均地价
                // ComputeTax(ref r); // 收入/净收入暂时注释掉
                if (districtId != _logDistrict || r.Diag != _logDiag)
                {
                    _logDistrict = districtId;
                    _logDiag = r.Diag;
                    Debug.Log("[DFM] GDP #" + districtId + " " + r.Diag);
                }
            }

            _cache[districtId] = r;
            _cacheTime[districtId] = Time.time;
            return r;
        }

        /// <summary>
        /// 所有原版区划的自身 GDP（按区划ID索引的数组），单次遍历建筑统计各区的就业人数，
        /// 用于排序视图。结果缓存 CACHE_LIFE 秒。
        /// </summary>
        public double[] GetDistrictGDP()
        {
            if (_districtGDP != null && Time.time - _districtGDPTime < CacheLife())
                return _districtGDP;

            DistrictManager dm = Singleton<DistrictManager>.instance;
            if (dm == null) return new double[256];

            double[] gdp = new double[256];
            District[] dbuf = dm.m_districts.m_buffer;
            uint dsize = dm.m_districts.m_size;
            for (uint d = 1; d < dsize; d++)
            {
                if ((dbuf[d].m_flags & District.Flags.Created) == 0) continue;
                gdp[d] = CalcGDP(GetDensity((ushort)d));
            }
            _districtGDP = gdp;
            _districtGDPTime = Time.time;
            return gdp;
        }

        /// <summary>所有原版区划的地价（按区划ID索引），用于地价排序。</summary>
        public long[] GetDistrictLandValue()
        {
            DistrictManager dm = Singleton<DistrictManager>.instance;
            long[] lv = new long[256];
            if (dm == null) return lv;
            District[] dbuf = dm.m_districts.m_buffer;
            uint dsize = dm.m_districts.m_size;
            for (uint d = 1; d < dsize; d++)
            {
                if ((dbuf[d].m_flags & District.Flags.Created) == 0) continue;
                lv[d] = dbuf[d].m_groundData.m_finalLandvalue;
            }
            return lv;
        }

        /// <summary>
        /// 聚合面积加权地价：层级树内每个节点 = 自身 + 全部下辖（含递归孙辈）的面积加权平均地价，
        /// 保证各级别（市/区县/乡镇/村社区）排名时都按聚合值排序。
        /// 未入树的已创建区划 = 自身地价。
        /// </summary>
        public double[] GetAggregateLandValue()
        {
            long[] selfLong = GetDistrictLandValue();
            double[] self = new double[256];
            for (int i = 0; i < 256; i++) self[i] = selfLong[i];
            double[] area = GetDistrictArea();
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            double[] agg = new double[256];
            for (int i = 0; i < 256; i++) agg[i] = self[i];
            if (hub == null || hub.Hierarchy == null) return agg;

            double[] lsum = new double[256]; // 子树 地价×面积 累加
            double[] wsum = new double[256]; // 子树 面积 累加
            var visited = new HashSet<ushort>();
            foreach (ushort root in hub.Hierarchy.GetRootNodes())
                AccumLand(root, self, area, lsum, wsum, hub.Hierarchy, visited);
            foreach (ushort id in new List<ushort>(visited))
                agg[id] = wsum[id] > 0 ? lsum[id] / wsum[id] : self[id];
            return agg;
        }

        /// <summary>后序累加：先算完子节点子树，再把 子节点子树 累进父节点，得到每个节点自身的子树合计。</summary>
        private static void AccumLand(ushort d, double[] self, double[] area, double[] lsum, double[] wsum, DistrictHierarchy h, HashSet<ushort> visited)
        {
            if (!visited.Add(d)) return;
            double l = self[d] * System.Math.Max(1, area[d]);
            double w = System.Math.Max(1, area[d]);
            foreach (ushort child in h.GetChildren(d))
            {
                AccumLand(child, self, area, lsum, wsum, h, visited);
                l += lsum[child];
                w += wsum[child];
            }
            lsum[d] = l;
            wsum[d] = w;
        }

        /// <summary>
        /// 区划面积（平方米）：遍历区划网格逐格，按每格的覆盖权重累加。
        /// 每格 19.2 m × 19.2 m = 368.64 m²；m_alpha(0~255) 表示该格被区划覆盖的比例，
        /// 故贡献 = (m_alpha/255) × 368.64。边缘半格/多区重叠由 alpha 精确计（缓存 CACHE_LIFE）。
        /// </summary>
        public double[] GetDistrictArea()
        {
            if (_districtArea != null && Time.time - _districtAreaTime < CacheLife())
                return _districtArea;
            double[] cnt = new double[256];
            try
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                if (dm == null) return cnt;
                DistrictManager.Cell[] grid = dm.m_districtGrid;
                if (grid == null) return cnt;
                double cellArea = (double)DistrictManager.DISTRICTGRID_CELL_SIZE
                                * (double)DistrictManager.DISTRICTGRID_CELL_SIZE; // 19.2×19.2 = 368.64 m²
                for (int i = 0; i < grid.Length; i++)
                {
                    DistrictManager.Cell c = grid[i];
                    if (c.m_district1 != 0 && c.m_alpha1 != 0) cnt[c.m_district1] += (c.m_alpha1 / 255.0) * cellArea;
                    if (c.m_district2 != 0 && c.m_alpha2 != 0) cnt[c.m_district2] += (c.m_alpha2 / 255.0) * cellArea;
                    if (c.m_district3 != 0 && c.m_alpha3 != 0) cnt[c.m_district3] += (c.m_alpha3 / 255.0) * cellArea;
                    if (c.m_district4 != 0 && c.m_alpha4 != 0) cnt[c.m_district4] += (c.m_alpha4 / 255.0) * cellArea;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[DFM] GetDistrictArea failed: " + ex.Message);
            }
            _districtArea = cnt;
            _districtAreaTime = Time.time;
            return cnt;
        }

        /// <summary>聚合面积（m²）：层级树内每个节点 = 自身 + 全部下辖面积，供「地均GDP」等按面积指标使用；未入树的已创建区划 = 自身面积。</summary>
        public double[] GetAggregateArea()
        {
            double[] self = GetDistrictArea();
            double[] agg = new double[256];
            for (int i = 0; i < 256; i++) agg[i] = self[i];
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub == null || hub.Hierarchy == null) return agg;

            double[] aSum = new double[256];
            var visited = new HashSet<ushort>();
            foreach (ushort root in hub.Hierarchy.GetRootNodes())
                AccumArea(root, self, aSum, hub.Hierarchy, visited);
            foreach (ushort id in new List<ushort>(visited)) agg[id] = aSum[id];
            return agg;
        }

        private static void AccumArea(ushort d, double[] self, double[] aSum, DistrictHierarchy h, HashSet<ushort> visited)
        {
            if (!visited.Add(d)) return;
            double s = System.Math.Max(0, self[d]);
            foreach (ushort child in h.GetChildren(d))
            {
                AccumArea(child, self, aSum, h, visited);
                s += aSum[child];
            }
            aSum[d] = s;
        }

        /// <summary>建成区数据是否已就绪（首次建筑遍历完成后为 true）。采样前用它把关。</summary>
        public bool BuiltAreaReady { get { return _allBuiltCells != null; } }

        /// <summary>
        /// 建筑之间的空隙系数：只按建筑占地（m_width×m_length）统计会漏掉建筑之间的道路、
        /// 人行道、院落等实际已建成的部分，故 ×2 作为近似。
        /// </summary>
        private const double BUILT_AREA_GAP_FACTOR = 2.0;

        /// <summary>
        /// 每区划建成区面积（m²）= Σ本区划内建筑占地格数 × 64 × 空隙系数(2.0)。
        /// **上限截断为该区划的总面积**（空隙系数是估计值，不能算出比区划本身还大的数）。
        /// 由密度分片遍历顺带统计（结果缓存）。
        /// </summary>
        public double[] GetDistrictBuiltArea()
        {
            if (_districtBuiltArea != null && Time.time - _districtBuiltAreaTime < CacheLife())
                return _districtBuiltArea;
            double[] r = new double[256];
            double[] area = GetDistrictArea();
            Dictionary<ushort, long> cells = _allBuiltCells;
            if (cells != null)
            {
                foreach (KeyValuePair<ushort, long> kv in cells)
                {
                    if (kv.Key >= 256) continue;
                    double v = kv.Value * 64.0 * BUILT_AREA_GAP_FACTOR;
                    double a = area[kv.Key];
                    if (a > 0.0 && v > a) v = a;   // 合理性上限：不得超过区划总面积
                    r[kv.Key] = v;
                }
            }
            _districtBuiltArea = r;
            _districtBuiltAreaTime = Time.time;
            return r;
        }

        /// <summary>
        /// 「建筑价值增量」用的加权占地面积（m²）= Σ 建筑占地格数 × 64 × 用途权重。
        /// **不含空隙系数**（增量用原始面积），也不做上限截断 —— 与「建成区面积」是两个口径。
        /// 结果缓存（同 GetDistrictBuiltArea，缓存键为空则重算，不会像 _allDensity 那样读到空）。
        /// </summary>
        public double[] GetDistrictBuiltWeightArea()
        {
            if (_districtBuiltWeightArea != null && Time.time - _districtBuiltWeightAreaTime < CacheLife())
                return _districtBuiltWeightArea;
            double[] r = new double[256];
            Dictionary<ushort, double> cells = _allBuiltWeightCells;
            if (cells != null)
            {
                foreach (KeyValuePair<ushort, double> kv in cells)
                    if (kv.Key < 256) r[kv.Key] = kv.Value * 64.0;
            }
            _districtBuiltWeightArea = r;
            _districtBuiltWeightAreaTime = Time.time;
            return r;
        }

        /// <summary>聚合建成区面积（m²，含下辖所有子区划，递归；复用 AccumArea 后序累加）。</summary>
        public double[] GetAggregateBuiltArea()
        {
            double[] self = GetDistrictBuiltArea();
            double[] agg = new double[256];
            for (int i = 0; i < 256; i++) agg[i] = self[i];
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub == null || hub.Hierarchy == null) return agg;
            double[] aSum = new double[256];
            var visited = new HashSet<ushort>();
            foreach (ushort root in hub.Hierarchy.GetRootNodes())
                AccumArea(root, self, aSum, hub.Hierarchy, visited);
            foreach (ushort id in new List<ushort>(visited)) agg[id] = aSum[id];
            return agg;
        }

        /// <summary>
        /// 每区划的可支配收入**分子**（Σ在岗工资 + Σ居民财产收入，克朗/周，原版口径）。
        /// 由密度分片遍历顺带统计（结果缓存）。人均要再除以该区划常住人口。
        /// </summary>
        public double[] GetDistrictIncomeNumerator()
        {
            if (_districtIncomeNum != null && Time.time - _districtIncomeNumTime < CacheLife())
                return _districtIncomeNum;
            double[] r = new double[256];
            Dictionary<ushort, IncomeData> inc = _allIncome;
            if (inc != null)
            {
                foreach (KeyValuePair<ushort, IncomeData> kv in inc)
                    if (kv.Key < 256) r[kv.Key] = kv.Value.WageNum + kv.Value.PropNum;
            }
            _districtIncomeNum = r;
            _districtIncomeNumTime = Time.time;
            return r;
        }

        /// <summary>每区划人均可支配周收入（克朗/周）= 分子 ÷ 常住人口；人口为 0 返回 0。</summary>
        public double[] GetDistrictDisposableIncome()
        {
            double[] num = GetDistrictIncomeNumerator();
            long[] pop = GetDistrictPopulation();
            double[] r = new double[256];
            for (int i = 0; i < 256; i++)
                r[i] = pop[i] > 0 ? num[i] / pop[i] : 0.0;
            return r;
        }

        /// <summary>
        /// 聚合人均可支配周收入 = 聚合分子 ÷ 聚合人口。**不能对子节点人均值求平均**
        /// （会被小人口节点带偏）。结构与 GetAggregatePopulation 保持一致（未入层级的区划为 0）。
        /// </summary>
        public double[] GetAggregateDisposableIncome()
        {
            double[] self = GetDistrictIncomeNumerator();
            long[] aggPop = GetAggregatePopulation();
            double[] r = new double[256];
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            double[] aggNum = new double[256];
            if (hub != null && hub.Hierarchy != null)
            {
                var visited = new HashSet<ushort>();
                foreach (ushort root in hub.Hierarchy.GetRootNodes())
                    ComputeAggregate(root, self, aggNum, hub.Hierarchy, visited);
            }
            for (int i = 0; i < 256; i++)
                r[i] = aggPop[i] > 0 ? aggNum[i] / aggPop[i] : 0.0;
            return r;
        }

        /// <summary>
        /// 建筑价值增量（每区划）：**当前值用实时数据**（实时建成区面积 × 当前地价 × LandMult），
        /// 基准值取周库里的"目标周"（= 最新历史周 − N）。N = 年化(1/2/3)? 52 : 1 周。
        /// 年化时若库里**有一年前的数据**就用一年前那周；**不足一个周期才用最早的有效周（初值）**。
        /// 结果按 CacheLife 缓存（约 10 秒），建成区面积随建筑分片遍历刷新（约 30 秒）。
        /// </summary>
        public double[] GetDistrictBuiltValueDelta()
        {
            if (_builtDelta != null && Time.time - _builtDeltaTime < CacheLife())
                return _builtDelta;
            double[] r = new double[256];
            try
            {
                DistrictFinanceHub hub = DistrictFinanceHub.Instance;
                DistrictSeriesDB series = hub != null ? hub.Series : null;
                if (series != null && series.HasAny)
                {
                    double mult = LandMultForCalc();
                    int n = PeriodWeeksForCalc();
                    // 基准面积用 v4 新增的 BuiltValueArea 列：老档没有这列 → 读出来是 0 →
                    // 那些周被当作无效基准自动跳过（老数据冷处理，不做迁移）。
                    int bi = SeriesFieldIndex("BuiltValueArea");
                    int li = SeriesFieldIndex("LandValue");
                    // 用【加权原始面积】而不是面板显示的建成区面积：增量按用途加权、不含空隙系数
                    double[] liveBuilt = GetDistrictBuiltWeightArea();
                    long[] liveLand = GetDistrictLandValue();    // 实时地价
                    for (ushort id = 1; id < 256; id++)
                    {
                        List<uint> ws = series.SeriesWeeks(id);
                        if (ws.Count == 0) continue;

                        // ⚠️ 锚点用【当前游戏周】，**不能**用 ws[ws.Count-1]（周库里最新的周）：
                        //   回档/读旧档后，周库里会残留"未来时间线"的周（那些周还没被重播覆盖），
                        //   用 ws.Last() 会把基准取到未来周上 —— 等于拿另一条时间线的数据当基准，
                        //   算出的增量会离谱（实测：赵县建成区"减少" 178240→149632 就是重播覆盖的痕迹）。
                        //   锚在当前游戏周后，所有 >target 的周（含未来周）都被自动排除。
                        uint curW = GameWeek.CurrentWeek;
                        // 当前值是实时的（处在当前周的下一周），故目标周 = 当前周 + 1 − N：
                        //   周化(N=1) → 当前周（最近已记录那周）；年化(N=52) → 约一年前那周。
                        long target = (long)curW + 1 - n;

                        // 基准：取 ≤目标周 的最近"有效"周（建成区>0）。年化且有一年前数据时，这就是一年前那周。
                        uint pastW = 0; bool havePast = false;
                        for (int i = 0; i < ws.Count; i++)
                        {
                            // 只排除「初始还没读到任何数据」的垃圾周（建成区 = 0）。
                            // 地价 = 0 是合法状态，对应的周照样可以当基准。
                            if (series.GetValue(id, ws[i], bi) <= 0.0) continue;
                            if ((long)ws[i] <= target) { pastW = ws[i]; havePast = true; }
                            else break;
                        }
                        if (!havePast) // 数据不足一个周期 → 才用最早的"有效"周（初值）
                        {
                            for (int i = 0; i < ws.Count; i++)
                                if (series.GetValue(id, ws[i], bi) > 0.0) { pastW = ws[i]; havePast = true; break; }
                        }
                        if (!havePast) continue;

                        // **两端同价**：基准周只取建成区面积，价格统一用【当前地价】。
                        // 若基准端用它自己那周的地价，delta 就变成 建成区 ×(当前地价 − 基准周地价)：
                        // 地价本身会在 19~23 这种区间来回摆（实测相邻周最多差 ±9），建成区一动不动
                        // 也会算出几百千的假增量。同价后 delta = (建成区_now − 建成区_base) × 当前地价，
                        // 只反映真的多盖了多少；面板上还能用「增量 ÷ 地价列 = 新增建筑面积」自行核对。
                        double pastBuilt = series.GetValue(id, pastW, bi);
                        double price = (double)liveLand[id] * mult;
                        r[id] = (liveBuilt[id] - pastBuilt) * price;
                    }
                }
            }
            catch (System.Exception ex) { Debug.LogWarning("[DFM] BuiltValueDelta failed: " + ex.Message); }
            _builtDelta = r;
            _builtDeltaTime = Time.time;
            return r;
        }

        /// <summary>聚合建成区价值增量（含下辖所有子区划，递归；供分级排名视图用）。</summary>
        public double[] GetAggregateBuiltValueDelta()
        {
            double[] self = GetDistrictBuiltValueDelta();
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub == null || hub.Hierarchy == null) return self;
            double[] agg = new double[256];
            var visited = new HashSet<ushort>();
            foreach (ushort root in hub.Hierarchy.GetRootNodes())
                ComputeAggregate(root, self, agg, hub.Hierarchy, visited);
            return agg;
        }

        private static int SeriesFieldIndex(string name)
        {
            for (int i = 0; i < DistrictSeriesDB.FIELDS.Length; i++)
                if (DistrictSeriesDB.FIELDS[i] == name) return i;
            return -1;
        }

        /// <summary>地价显示倍率（RMB ×420 / USD ×60），与面板 LandMult() 口径一致。</summary>
        /// <summary>价格系数（地价 / 增量里的地价）：原版 1 / 人民币 420 / 美元 60。
        /// ⚠️ 真源在 ModSettings.PriceFactor —— 面板侧的 LandMult() 必须调同一个，别再各写一份。</summary>
        private static double LandMultForCalc()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Settings != null)
                return ModSettings.PriceFactor(hub.Settings.DisplayCurrency);
            return 1.0;
        }

        /// <summary>周期周数（增量基准回退几周）：周/月/季/年/5年 = 1/4/13/52/260。</summary>
        private static int PeriodWeeksForCalc()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Settings != null)
                return ModSettings.PeriodWeeks(hub.Settings.DisplayPeriod);
            return 1;
        }

        #region 增速（与「建筑价值增量」同一个滚动窗口）

        // 增速的**原始量**（自身值口径）。周库里只有这 6 个量，其余指标全部由它们相除得到：
        //   人均GDP = GDP/人口、地均GDP = GDP/面积、人口密度 = 人口/面积。
        private const int GP_GDP = 0, GP_POP = 1, GP_LAND = 2, GP_AREA = 3, GP_BUILT = 4, GP_INC = 5;
        // GP_WORKERS（2026-09-28）：键 12 第二个子模式「区域工人数」的增速基准列（周库 PanelWorkers 列）。
        private const int GP_WORKERS = 6;
        private const int GP_COUNT = 7;

        private double[][] _basePrim;   // [原始量][区划ID] 基准周的值（只存自身值，聚合用时现算）
        private float _basePrimTime;

        /// <summary>
        /// 该排序键有没有「增速」口径。**键值与面板 DistrictFinancePanel 的 _sortKey 一一对应**，改一边必须改另一边：
        ///   0=GDP 1=人口 2=人均GDP 3=地价 4=地均GDP 5=人口密度 6=面积 8=建成区面积 9=人均可支配 → 有
        ///   7=建筑价值增量 10=自定义政府投资额 → **没有**：这两个本身就是「一段时间内的增量 / 累计量」，
        ///   再算增速等于对增量再求一次比值，没有意义（面板侧遇到它们就保持原口径，见 SortValue）。
        /// </summary>
        public static bool GrowthSupported(int key)
        {
            // ⚠️ 键 12 是**子模式感知**的：只有「区域工人数」能算增速（走周库 v8 的 PanelWorkers 列）；
            //    「本地就业率」是个比值，没有增速。所以面板侧还必须再判一次子模式
            //    （见面板的 EmployGrowthSupported / GrowthForCurrentKey），这里只表达"键 12 有这个能力"。
            return key == 0 || key == 1 || key == 2 || key == 3
                || key == 4 || key == 5 || key == 6 || key == 8 || key == 9
                || key == 12;
        }

        /// <summary>
        /// 增速（%，按周期）。`aggregate=false` 用自身值，`true` 用「自身 + 全部下辖」的聚合值。
        /// **不支持的键返回 null**（调用方据此退回原口径）。
        ///
        /// 口径与「建筑价值增量」逐字一致：当前值用**实时数据**（处在当前周的下一周），
        /// 基准值取周库里 ≤(当前周 + 1 − N) 的最近**有效**周，N = 当前周期周数（周 1 / 月 4 / 季 13 / 年 52 / 5年 260）；
        /// 不足一个周期才退回「最早的**有效**周」（初值）。有效 = 该值 &gt; 0（0 表示那周还没读到数据）。
        /// 基准 ≤ 0 → 该区划记 0（除不出增速）。
        /// ⚠️ 这条兜底对**比值**很敏感（新区划会算出 +1000%；窗口跨过 GDP 公式改动的老周会假性 −100%），
        ///    但用户 2026-09-27 明确要求**保留**（与增量口径一致）—— 排查手法见 EnsureBasePrim 的注释。
        ///
        /// ⚠️ 增速是**比值**，货币系数与周期周数在分子分母里约掉了 —— 「人民币/年」与「原版/周」下
        /// 同一个区划的增速**完全相同**，只有**窗口 N** 随周期变。这是它与「增量」最大的区别
        /// （增量随货币和周期一起缩放，增速只随周期变窗口）。
        /// </summary>
        public double[] GetGrowth(int key, bool aggregate)
        {
            double[] live, basev;
            if (!GrowthRaw(key, aggregate, out live, out basev)) return null;
            // 排查用：把「实时值 / 基准值 / 结果」按区划打几行（「显示调试信息」打开时每秒最多一次）。
            // 增速的换算全在 GrowthRaw 里，出问题只可能是这三个数之一不对 —— 打出来就能直接对账。
            DistrictFinanceHub logHub = DistrictFinanceHub.Instance;
            if (logHub != null && logHub.Settings != null && logHub.Settings.ShowDebug)
                LogGrowthSample(key, aggregate, live, basev);
            double[] r = new double[256];
            for (int i = 1; i < 256; i++)
            {
                if (basev[i] <= 0.0) continue;   // 基准无效 → 0（不能除）
                r[i] = (live[i] - basev[i]) / basev[i] * 100.0;
            }
            return r;
        }

        /// <summary>
        /// 增速用的「实时值 / 基准值」两个**自身值**数组（都是排序键口径的同一个量）。
        /// 组合视图要按成员先求和再算增速（不能对成员的增速求平均），所以除了比值还要拿到这两列，故单独开放。
        /// 不支持的键 → 返回 false，两个 out 均为 null。
        /// </summary>
        public bool GrowthRaw(int key, bool aggregate, out double[] live, out double[] basev)
        {
            live = null; basev = null;
            if (!GrowthSupported(key)) return false;

            // ---- 实时值：聚合口径直接调 GetAggregateXxx（未入层级的区划 = 自身值）----
            // ⚠️ GDP 是**唯一带显示系数**的量：实时端 `CalcGDP` 乘了 `GetDisplayFactor()`（货币 × 周期），
            //    而周库里一律存**原始值**（入库时除回了系数，见 DistrictSeriesDB 顶部的「存储口径」）。
            //    所以基准那一路要**现乘**当前系数，两端才同口径（下面 bGdp 那一行）。
            //    这个系数在比值里会约掉 → 增速与货币、周期都无关，只有窗口 N 随周期变。
            double[] lGdp = aggregate ? GetAggregateGDP() : GetDistrictGDP();
            double[] lPop = ToD(aggregate ? GetAggregatePopulation() : GetDistrictPopulation());
            double[] lLand = aggregate ? GetAggregateLandValue() : ToD(GetDistrictLandValue());
            double[] lArea = aggregate ? GetAggregateArea() : GetDistrictArea();
            double[] lBuilt = aggregate ? GetAggregateBuiltArea() : GetDistrictBuiltArea();
            double[] lInc = aggregate ? GetAggregateDisposableIncome() : GetDistrictDisposableIncome();
            // 键 12 第二子模式：区域工人数（原版区划面板口径，纯读游戏数据；基准 = 周库 v8 的 PanelWorkers 列）
            double[] lWorkers = aggregate ? GetAggregatePanelWorkers() : GetDistrictPanelWorkers();

            // ---- 基准值：周库里的原始量（自身），聚合口径再按子树累加 / 面积加权 ----
            // GDP 那一列要乘回当前显示系数（库是原始值、实时端带系数，见上面的说明）。
            // 先聚合再乘：两个操作都是线性的，谁先谁后一样，但放在聚合后只需乘一次。
            double[] bGdp = Mul(aggregate ? AggPrim(GP_GDP) : BasePrim(GP_GDP), GetDisplayFactor());
            double[] bPop = aggregate ? AggPrim(GP_POP) : BasePrim(GP_POP);
            double[] bArea = aggregate ? AggPrim(GP_AREA) : BasePrim(GP_AREA);
            double[] bBuilt = aggregate ? AggPrim(GP_BUILT) : BasePrim(GP_BUILT);
            // 地价：聚合是**面积加权平均**而不是求和，必须用基准周的地价+面积重新加权（见 AggLandWeighted）
            double[] bLand = aggregate
                ? AggLandWeighted(BasePrim(GP_LAND), BasePrim(GP_AREA))
                : BasePrim(GP_LAND);
            // 人均可支配：周库存的是**人均**，聚合却必须是 Σ分子 ÷ Σ人口 ——
            // 分子 = 人均 × 人口（两列周库里都有），先还原成分子再聚合（与 GetAggregateDisposableIncome 同口径）
            double[] bInc = Div(aggregate ? AggPrim(GP_INC) : BasePrim(GP_INC), bPop);
            double[] bWorkers = aggregate ? AggPrim(GP_WORKERS) : BasePrim(GP_WORKERS);

            switch (key)
            {
                case 0: live = lGdp; basev = bGdp; break;
                case 1: live = lPop; basev = bPop; break;
                case 2: live = Div(lGdp, lPop); basev = Div(bGdp, bPop); break;        // 人均GDP
                case 3: live = lLand; basev = bLand; break;                            // 地价
                case 4: live = Div(lGdp, lArea); basev = Div(bGdp, bArea); break;      // 地均GDP
                case 5: live = Div(Mul(lPop, 1000000.0), lArea);                       // 人口密度（人/km²）
                        basev = Div(Mul(bPop, 1000000.0), bArea); break;
                case 6: live = lArea; basev = bArea; break;
                case 8: live = lBuilt; basev = bBuilt; break;
                case 9: live = lInc; basev = bInc; break;
                case 12: live = lWorkers; basev = bWorkers; break;   // 区域工人数（面板侧只在工人子模式下调用）
                default: return false;
            }
            return live != null && basev != null;
        }

        /// <summary>
        /// 基准原始量的聚合（自身 + 全部下辖，递归 —— 与 GetAggregateXxx 同一套子树求和）。
        /// 用 ComputeAggregate 而不是 AccumArea：两者都是后序求和，但前者与 GDP/人口 的聚合口径一致。
        /// </summary>
        private double[] AggPrim(int p)
        {
            double[] self = BasePrim(p);
            // 与对应的 GetAggregateXxx **逐一对齐**（现有 getter 的口径本来就不统一，照抄各自的约定）：
            //   GDP / 人口 / 收入分子 → 未入层级的区划记 0（GetAggregateGDP/Population 就是全 0 起步）；
            //   面积 / 建成区面积     → 未入层级保留**自身值**（GetAggregateArea/BuiltArea 是全量拷贝起步）。
            // 弄反了会给「没入层级」的区划算出假 −100%（实时 0 ÷ 基准 非 0）。
            bool zeroIfUnassigned = (p == GP_GDP || p == GP_POP || p == GP_INC || p == GP_WORKERS);
            double[] agg = new double[256];
            if (!zeroIfUnassigned)
                for (int i = 0; i < 256; i++) agg[i] = self[i];
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub == null || hub.Hierarchy == null) return agg;
            double[] sum = new double[256];
            var visited = new HashSet<ushort>();
            foreach (ushort root in hub.Hierarchy.GetRootNodes())
                ComputeAggregate(root, self, sum, hub.Hierarchy, visited);
            foreach (ushort id in new List<ushort>(visited)) agg[id] = sum[id];
            return agg;
        }

        /// <summary>
        /// 面积加权平均地价的聚合，但输入可以换成**基准周**的地价与面积
        /// （＝ GetAggregateLandValue 的口径，只是两端都用基准值 —— 否则会算成「聚合地价 vs 基准周自身地价」）。
        /// </summary>
        private double[] AggLandWeighted(double[] landSelf, double[] areaSelf)
        {
            double[] agg = new double[256];
            if (landSelf == null) return agg;
            for (int i = 0; i < 256; i++) agg[i] = landSelf[i];
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub == null || hub.Hierarchy == null || areaSelf == null) return agg;
            double[] lsum = new double[256], wsum = new double[256];
            var visited = new HashSet<ushort>();
            foreach (ushort root in hub.Hierarchy.GetRootNodes())
                AccumLand(root, landSelf, areaSelf, lsum, wsum, hub.Hierarchy, visited);
            foreach (ushort id in new List<ushort>(visited))
                agg[id] = wsum[id] > 0 ? lsum[id] / wsum[id] : landSelf[id];
            return agg;
        }

        /// <summary>基准周上某原始量的**自身**值数组（无数据 = 0）。</summary>
        private double[] BasePrim(int p)
        {
            EnsureBasePrim();
            if (_basePrim == null || p < 0 || p >= _basePrim.Length) return new double[256];
            return _basePrim[p];
        }

        /// <summary>
        /// 算出「基准原始量」：每个区划、每个原始量各自取周库里 ≤(当前周 + 1 − N) 的最近**有效**周的值；
        /// 不足一个周期才退回最早的**有效**周（**兜底**，与 GetDistrictBuiltValueDelta 同一套规则，用户要求保留）。
        /// **逐列各自回退**：某列的 0 只是「那周这一列还没读到数据」，不该拖累其它列。
        /// GDP 那一列额外只认「与最新一周同一统计模式段」的周（见下面的段说明）。
        /// 结果按 CacheLife 缓存（只有开「增速」时才会被调用）。
        /// </summary>
        private void EnsureBasePrim()
        {
            if (_basePrim != null && Time.time - _basePrimTime < CacheLife()) return;
            double[][] r = new double[GP_COUNT][];
            for (int p = 0; p < GP_COUNT; p++) r[p] = new double[256];
            try
            {
                DistrictFinanceHub hub = DistrictFinanceHub.Instance;
                DistrictSeriesDB series = hub != null ? hub.Series : null;
                if (series != null && series.HasAny)
                {
                    int n = PeriodWeeksForCalc();
                    // 锚点必须是【当前游戏周】而不是周库最新周：回档后周库里会残留未来时间线的周，
                    // 用最新周会把基准取到未来去（与增量踩过的坑同一个，见 GetDistrictBuiltValueDelta）。
                    long target = (long)GameWeek.CurrentWeek + 1 - n;
                    int iGdp = SeriesFieldIndex("GDP");
                    int iPop = SeriesFieldIndex("Population");
                    int iLand = SeriesFieldIndex("LandValue");
                    int iArea = SeriesFieldIndex("Area");
                    int iBuilt = SeriesFieldIndex("BuiltArea");
                    int iInc = SeriesFieldIndex("DisposableIncome");
                    int iWorkers = SeriesFieldIndex("PanelWorkers");   // v8 追加列（区域工人数）

                    for (ushort id = 1; id < 256; id++)
                    {
                        List<uint> ws = series.SeriesWeeks(id);
                        if (ws.Count == 0) continue;

                        // 注：GDP 那一列的「老数据带统计模式系数」问题不在这里兜 ——
                        // 读周库时已经做过一次性迁移（v4 及以前清零 / v5 除回原始值，见
                        // DistrictSeriesStore 的 GdpWipeVersion、ConvertGdpToRaw），
                        // 之后库里一直是原始值，显示系数由 GrowthRaw 现乘，所以这里按普通列处理即可，
                        // 不需要「模式段」判定。
                        double[] lastV = new double[GP_COUNT];   // ≤target 的最近有效值
                        double[] firstV = new double[GP_COUNT];  // 最早的有效值（不足一个周期时兜底）
                        bool[] haveLast = new bool[GP_COUNT];
                        bool[] haveFirst = new bool[GP_COUNT];

                        for (int wi = 0; wi < ws.Count; wi++)    // ws 升序
                        {
                            uint w = ws[wi];
                            double[] row;
                            if (!series.TryGetRow(id, w, out row)) continue;
                            double pop = RowVal(row, iPop);
                            for (int p = 0; p < GP_COUNT; p++)
                            {
                                double v;
                                switch (p)
                                {
                                    case GP_GDP: v = RowVal(row, iGdp); break;
                                    case GP_POP: v = pop; break;
                                    case GP_LAND: v = RowVal(row, iLand); break;
                                    case GP_AREA: v = RowVal(row, iArea); break;
                                    case GP_BUILT: v = RowVal(row, iBuilt); break;
                                    case GP_WORKERS: v = RowVal(row, iWorkers); break;
                                    default: v = pop * RowVal(row, iInc); break; // 收入**分子** = 人均 × 人口
                                }
                                if (v <= 0.0) continue;              // 0 = 那周还没读到数据，跳过
                                if (!haveFirst[p]) { firstV[p] = v; haveFirst[p] = true; }
                                if ((long)w <= target) { lastV[p] = v; haveLast[p] = true; }
                            }
                        }
                        // 兜底：历史不足一个周期时用「最早的**有效**周」（与增量同一套规则）。
                        // ⚠️ 用户 2026-09-27 明确要求**保留**这个兜底，所以别再"顺手删掉"。
                        //    代价记在这里备查：增速是**比值**，拿很早的周当基数会放大得很厉害 ——
                        //      · 新建区划只有几周历史 → 分母是"刚建出来"那点值 → 动辄 +1000%；
                        //      · 窗口跨过 2026-09-18 的 GDP 公式改动 → 老周存的是旧公式的值（大几十~上千倍），
                        //        比值会假性接近 −100%（或反向爆表）。
                        //    真出现「异常大值」时先用 `[DFM] 增速 N=… 实时=… 基准=… → …%` 那几行对账（见 LogGrowthSample）。
                        for (int p = 0; p < GP_COUNT; p++)
                            r[p][id] = haveLast[p] ? lastV[p] : (haveFirst[p] ? firstV[p] : 0.0);
                    }
                }
            }
            catch (System.Exception ex) { Debug.LogWarning("[DFM] Growth base failed: " + ex.Message); }
            _basePrim = r;
            _basePrimTime = Time.time;
        }

        /// <summary>取周库行里的第 idx 列（越界/缺列返回 0 —— 老档没有的列自动当 0，即被当成无效基准跳过）。</summary>
        private static double RowVal(double[] row, int idx)
        {
            if (row == null || idx < 0 || idx >= row.Length) return 0.0;
            return row[idx];
        }

        private static double[] ToD(long[] a)
        {
            double[] r = new double[256];
            if (a != null) for (int i = 0; i < 256 && i < a.Length; i++) r[i] = a[i];
            return r;
        }

        private static double[] Mul(double[] a, double f)
        {
            double[] r = new double[256];
            for (int i = 0; i < 256; i++) r[i] = a[i] * f;
            return r;
        }

        /// <summary>
        /// 调试用：把增速的「实时值 / 基准值 → 结果%」打几行日志（每秒最多一次，最多 5 个有基准的区划）。
        /// 只在「显示调试信息」打开时调用。排查口径问题时看这三列一眼就知道是哪一端不对。
        /// </summary>
        private static float _growthLogTime;
        private static void LogGrowthSample(int key, bool aggregate, double[] live, double[] basev)
        {
            if (Time.time - _growthLogTime < 1f) return;
            _growthLogTime = Time.time;
            int n = PeriodWeeksForCalc();
            int shown = 0;
            for (int id = 1; id < 256 && shown < 5; id++)
            {
                if (basev[id] <= 0.0) continue;
                shown++;
                double pct = (live[id] - basev[id]) / basev[id] * 100.0;
                Debug.Log("[DFM] 增速 N=" + n + "周 key=" + key + (aggregate ? " 聚合" : " 自身")
                    + " #" + id + " 实时=" + live[id].ToString("0.###")
                    + " 基准=" + basev[id].ToString("0.###")
                    + " → " + pct.ToString("0.00") + "%");
            }
        }

        /// <summary>逐元素相除；分母 ≤ 0 时记 0（＝「这个区划算不出这个比值」，增速也随之记 0）。</summary>
        private static double[] Div(double[] num, double[] den)
        {
            double[] r = new double[256];
            for (int i = 0; i < 256; i++)
                r[i] = (den != null && i < den.Length && den[i] > 0.0) ? num[i] / den[i] : 0.0;
            return r;
        }

        #endregion

/// <summary>所有原版区划的居民数（按区划ID索引），直接读游戏数据，用于人口排序。</summary>
        public long[] GetDistrictPopulation()
        {
            if (_districtPop != null && Time.time - _districtPopTime < CacheLife())
                return _districtPop;
            DistrictManager dm = Singleton<DistrictManager>.instance;
            if (dm == null) return new long[256];
            long[] pop = new long[256];
            District[] dbuf = dm.m_districts.m_buffer;
            uint dsize = dm.m_districts.m_size;
            for (uint d = 1; d < dsize; d++)
            {
                if ((dbuf[d].m_flags & District.Flags.Created) == 0) continue;
                pop[d] = dbuf[d].m_populationData.m_finalCount;
            }
            _districtPop = pop;
            _districtPopTime = Time.time;
            return pop;
        }

        /// <summary>
        /// 所有已分配区划的聚合 GDP（自身 + 全部下辖，递归），按区划ID索引。
        /// 基于 GetDistrictGDP 的自身 GDP + 层级树自底向上累加。
        /// </summary>
        public double[] GetAggregateGDP()
        {
            double[] self = GetDistrictGDP();
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub == null || hub.Hierarchy == null) return new double[256];

            double[] agg = new double[256];
            var visited = new HashSet<ushort>();
            foreach (ushort root in hub.Hierarchy.GetRootNodes())
                ComputeAggregate(root, self, agg, hub.Hierarchy, visited);
            return agg;
        }

        private static double ComputeAggregate(ushort d, double[] self, double[] agg, DistrictHierarchy h, HashSet<ushort> visited)
        {
            if (!visited.Add(d)) return agg[d]; // 防环
            double total = self[d];
            foreach (ushort child in h.GetChildren(d))
                total += ComputeAggregate(child, self, agg, h, visited);
            agg[d] = total;
            return total;
        }

        /// <summary>long[] 版本（人口等整型聚合）。</summary>
        private static long ComputeAggregate(ushort d, long[] self, long[] agg, DistrictHierarchy h, HashSet<ushort> visited)
        {
            if (!visited.Add(d)) return agg[d]; // 防环
            long total = self[d];
            foreach (ushort child in h.GetChildren(d))
                total += ComputeAggregate(child, self, agg, h, visited);
            agg[d] = total;
            return total;
        }

        /// <summary>所有已分配区划的聚合人口（自身 + 下辖，递归）。</summary>
        public long[] GetAggregatePopulation()
        {
            long[] self = GetDistrictPopulation();
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub == null || hub.Hierarchy == null) return new long[256];
            long[] agg = new long[256];
            var visited = new HashSet<ushort>();
            foreach (ushort root in hub.Hierarchy.GetRootNodes())
                ComputeAggregate(root, self, agg, hub.Hierarchy, visited);
            return agg;
        }

        /// <summary>计算单个区划自身（不聚合）。</summary>
        private FinanceResult CalcSelf(ushort districtId)
        {
            FinanceResult r = new FinanceResult();
            try
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                District[] buf = dm.m_districts.m_buffer;
                if (districtId >= buf.Length) return r;

                District d = buf[districtId];
                if ((d.m_flags & District.Flags.Created) == 0) return r;

                r.LandValue = d.m_groundData.m_finalLandvalue;
                r.Population = (int)d.m_populationData.m_finalCount;
                r.Workers = GetWorkers(d);
                r.ResPop = (int)d.m_residentialData.m_finalAliveCount;
                r.ComWorkers = (int)d.m_commercialData.m_finalAliveCount;
                r.IndWorkers = (int)d.m_industrialData.m_finalAliveCount;
                r.OffWorkers = (int)d.m_officeData.m_finalAliveCount;
                r.PlayerWorkers = (int)d.m_playerData.m_finalAliveCount;

                // 密度细分默认读取（一次全城遍历缓存，面板显示由“显示调试信息”控制）
                DensityData den = GetDensity(districtId);
                r.ResLow = den.ResLow;
                r.ResHigh = den.ResHigh;
                r.ComLow = den.ComLow;
                r.ComHigh = den.ComHigh;
                // 公共服务建筑的在岗市民：游戏的区划数据里没有这一项，用建筑遍历数出来的补上
                // （2026-09-18「公共服务工人一并对齐」）
                r.Workers += den.ServiceWorkers;
                r.ResLowGDP = (long)r.ResLow * r.LandValue;
                r.ResHighGDP = (long)r.ResHigh * r.LandValue;
                r.ComLowGDP = (long)r.ComLow * r.LandValue;
                r.ComHighGDP = (long)r.ComHigh * r.LandValue;
                r.IndGDP = (long)r.IndWorkers * r.LandValue;
                r.OffGDP = (long)r.OffWorkers * r.LandValue;
                r.PlayerGDP = (long)r.PlayerWorkers * r.LandValue;

                r.BuildingCount =
                    d.m_residentialData.m_finalBuildingCount +
                    d.m_commercialData.m_finalBuildingCount +
                    d.m_industrialData.m_finalBuildingCount +
                    d.m_officeData.m_finalBuildingCount +
                    d.m_playerData.m_finalBuildingCount;

                r.Area = GetDistrictArea()[districtId]; // 面积（m²，区划网格 alpha 加权）
                r.BuiltArea = GetDistrictBuiltArea()[districtId]; // 建成区面积（m²，建筑占地×64×空隙系数，上限=区划面积）
                r.BuiltValueArea = GetDistrictBuiltWeightArea()[districtId]; // 增量用的加权面积

                // 人均可支配周收入（克朗/周）：分子来自密度分片遍历的收入统计
                r.IncomeNum = GetDistrictIncomeNumerator()[districtId];
                r.DisposableIncome = r.Population > 0 ? r.IncomeNum / r.Population : 0.0;

                r.GDP = CalcGDP(den);
                // r.Expense = CountExpenses(dm, districtId); // 支出暂时注释掉

                r.Diag = "地价=" + r.LandValue + " 平均地价=" + GetAverageLandValue().ToString("0.00") +
                    " 住低=" + r.ResLow + " 住高=" + r.ResHigh + " 商低=" + r.ComLow + " 商高=" + r.ComHigh +
                    " 工=" + r.IndWorkers + " 办=" + r.OffWorkers + " 玩=" + r.PlayerWorkers +
                    " 服=" + den.ServiceWorkers;

                // 自身也计入合计
                r.AggGDP = r.GDP;
                r.AggPopulation = r.Population;
                r.AggWorkers = r.Workers;
                r.AggBuildings = r.BuildingCount;
                r.AggArea = r.Area;
                r.AggBuiltArea = r.BuiltArea;
                r.AggIncomeNum = r.IncomeNum;   // 聚合分子（人均值在 Calculate 末尾按聚合人口除）
                r.AggResPop = r.ResPop;
                r.AggComWorkers = r.ComWorkers;
                r.AggIndWorkers = r.IndWorkers;
                r.AggOffWorkers = r.OffWorkers;
                r.AggPlayerWorkers = r.PlayerWorkers;
                r.AggResLow = r.ResLow;
                r.AggResHigh = r.ResHigh;
                r.AggComLow = r.ComLow;
                r.AggComHigh = r.ComHigh;
                r.AggResLowGDP = r.ResLowGDP;
                r.AggResHighGDP = r.ResHighGDP;
                r.AggComLowGDP = r.ComLowGDP;
                r.AggComHighGDP = r.ComHighGDP;
                r.AggIndGDP = r.IndGDP;
                r.AggOffGDP = r.OffGDP;
                r.AggPlayerGDP = r.PlayerGDP;
                // r.AggExpense = r.Expense; // 支出暂时注释掉

                r.IsValid = true;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[DFM] GDP calc failed for district " + districtId + ": " + ex.Message);
            }
            return r;
        }

        /// <summary>递归累加所有下辖子区划到 Agg* 字段（visited 防环）。</summary>
        private void AggregateChildren(ushort districtId, ref FinanceResult r, HashSet<ushort> visited)
        {
            if (!visited.Add(districtId)) return;
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub == null || hub.Hierarchy == null) return;

            foreach (ushort child in hub.Hierarchy.GetChildren(districtId))
            {
                FinanceResult c = CalcSelf(child);
                if (!c.IsValid) continue;

                r.AggGDP += c.GDP;
                r.AggPopulation += c.Population;
                r.AggWorkers += c.Workers;
                r.AggBuildings += c.BuildingCount;
                r.AggArea += c.Area;
                r.AggBuiltArea += c.BuiltArea;
                r.AggIncomeNum += c.IncomeNum;   // 分子求和（人均值在 Calculate 末尾除聚合人口）
                r.AggResPop += c.ResPop;
                r.AggComWorkers += c.ComWorkers;
                r.AggIndWorkers += c.IndWorkers;
                r.AggOffWorkers += c.OffWorkers;
                r.AggPlayerWorkers += c.PlayerWorkers;
                r.AggResLow += c.ResLow;
                r.AggResHigh += c.ResHigh;
                r.AggComLow += c.ComLow;
                r.AggComHigh += c.ComHigh;
                r.AggResLowGDP += c.ResLowGDP;
                r.AggResHighGDP += c.ResHighGDP;
                r.AggComLowGDP += c.ComLowGDP;
                r.AggComHighGDP += c.ComHighGDP;
                r.AggIndGDP += c.IndGDP;
                r.AggOffGDP += c.OffGDP;
                r.AggPlayerGDP += c.PlayerGDP;
                // r.AggExpense += c.Expense; // 支出暂时注释掉

                AggregateChildren(child, ref r, visited); // 递归孙辈
            }
        }

        /// <summary>按GDP比例分配全市总收入，计算税收与净收入。</summary>
        private void ComputeTax(ref FinanceResult r)
        {
            double totalGDP = GetTotalCityGDP();
            long totalIncome = GetTotalIncome();
            if (totalGDP > 0 && totalIncome > 0)
            {
                r.Tax = (long)(totalIncome * r.GDP / totalGDP);
                r.AggTax = (long)(totalIncome * r.AggGDP / totalGDP);
            }
            r.NetIncome = r.Tax - r.Expense;
            r.AggNetIncome = r.AggTax - r.AggExpense;
            r.Diag += string.Format(" 收入={0} 总GDP={1} 税={2} 净={3}",
                totalIncome, totalGDP, r.Tax, r.NetIncome);
        }

        /// <summary>全市 GDP（所有原版区划自身 GDP 之和，作为税收分配分母）。</summary>
        private double GetTotalCityGDP()
        {
            if (Time.time - _totalCityGDPTime < CacheLife()) return _totalCityGDP;
            DistrictManager dm = Singleton<DistrictManager>.instance;
            if (dm == null) return 0;

            double total = 0;
            District[] dbuf = dm.m_districts.m_buffer;
            uint dsize = dm.m_districts.m_size;
            for (uint d = 1; d < dsize; d++)
            {
                if ((dbuf[d].m_flags & District.Flags.Created) == 0) continue;
                total += CalcGDP(GetDensity((ushort)d));
            }
            _totalCityGDP = total;
            _totalCityGDPTime = Time.time;
            return total;
        }

        /// <summary>
        /// 全市本周总收入 = m_income（常规服务）与 m_totalIncome（私人服务：玩家工业/渔业等）之和。
        /// 两个数组均为私有字段，反射读取；分别按 ClassIndex / GetPrivateServiceIndex 索引。
        /// </summary>
        private static long GetTotalIncome()
        {
            EconomyManager em = Singleton<EconomyManager>.instance;
            if (em == null) return 0;
            if ((object)_incomeField == null)
            {
                _incomeField = typeof(EconomyManager).GetField("m_income",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                _totalIncomeField = typeof(EconomyManager).GetField("m_totalIncome",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                long s1 = SumLongArray(_incomeField.GetValue(em) as long[]);
                long s2 = SumLongArray((object)_totalIncomeField != null ? (_totalIncomeField.GetValue(em) as long[]) : null);
                Debug.Log(string.Format("[DFM] income 常规={0} 私人={1}", s1, s2));
            }
            if ((object)_incomeField == null) return 0;
            try
            {
                long total = SumLongArray(_incomeField.GetValue(em) as long[]);
                if ((object)_totalIncomeField != null)
                    total += SumLongArray(_totalIncomeField.GetValue(em) as long[]);
                return total;
            }
            catch { return 0; }
        }

        private static long SumLongArray(long[] arr)
        {
            if (arr == null) return 0;
            long total = 0;
            foreach (long v in arr) total += v;
            return total;
        }

        /// <summary>
        /// 单次遍历所有建筑，统计每个区划的工作人数（按区划ID索引的数组），用于全市 GDP 汇总。
        /// </summary>
        private static int[] CountWorkersAll(DistrictManager dm)
        {
            int[] workers = new int[256];
            try
            {
                BuildingManager bm = Singleton<BuildingManager>.instance;
                if (bm == null) return workers;
                CitizenManager cm = Singleton<CitizenManager>.instance;
                if (cm == null) return workers;

                Building[] buf = bm.m_buildings.m_buffer;
                CitizenUnit[] units = cm.m_units.m_buffer;
                Citizen[] citizens = cm.m_citizens.m_buffer;
                uint size = bm.m_buildings.m_size;
                uint citizenSize = (uint)citizens.Length;

                for (uint i = 1; i < size; i++)
                {
                    Building b = buf[i];
                    if ((b.m_flags & Building.Flags.Created) == 0) continue;

                    BuildingInfo info = b.Info;
                    if (info == null || !IsWorkplace(info.m_class)) continue;

                    byte d = dm.GetDistrict(b.m_position);
                    if (d == 0) continue;

                    uint unit = b.m_citizenUnits;
                    int guard = 0;
                    while (unit != 0 && guard++ < 4096)
                    {
                        CitizenUnit u = units[unit];
                        for (int j = 0; j < 5; j++)
                        {
                            uint cid = u.GetCitizen(j);
                            if (cid != 0 && cid < citizenSize
                                && citizens[cid].m_workBuilding == (ushort)i)
                                workers[d]++;
                        }
                        unit = u.m_nextUnit;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[DFM] CountWorkersAll failed: " + ex.Message);
            }
            return workers;
        }

        /// <summary>
        /// 地价是否用【全图平均地价】：工业 / 玩家产业 / 公共服务建筑用平均地价，
        /// 住宅 / 商业 / 办公用建筑所在地格的地价。**收入与 GDP 共用这一条规则**（只此一处）。
        /// </summary>
        private static bool UseAverageLand(ItemClass.Service svc)
        {
            return svc == ItemClass.Service.Industrial
                || svc == ItemClass.Service.PlayerIndustry
                || IsServiceWorkplace(svc);
        }

        /// <summary>
        /// 带本轮遍历 memo 的建筑地价 —— **GDP 与工资都必须走这个入口**，
        /// 别再直接调 BuildingLandValue（否则每名在岗市民都要查一次地价格网）。
        /// </summary>
        private int CachedLandValue(Building b, ushort buildingId, byte districtId, District[] dbuf)
        {
            if (_landValuePartial == null) _landValuePartial = new Dictionary<ushort, int>();
            int v;
            if (_landValuePartial.TryGetValue(buildingId, out v)) return v;
            v = BuildingLandValue(b, districtId, dbuf);
            _landValuePartial[buildingId] = v;
            return v;
        }

        /// <summary>
        /// 建筑所在地格的地价（游戏地价格网的单格值，`ImmaterialResourceManager.Resource.LandValue`）。
        /// 取不到（资源网未就绪 / 返回 0）时回退到该建筑所在区划的平均地价，避免整片算成 0。
        /// ⚠️ 直接用它会按调用次数重复查格网；统计路径请走 CachedLandValue()。
        /// </summary>
        private static int BuildingLandValue(Building b, byte districtId, District[] dbuf)
        {
            try
            {
                ImmaterialResourceManager irm = Singleton<ImmaterialResourceManager>.instance;
                if (irm != null)
                {
                    int v;
                    irm.CheckLocalResource(ImmaterialResourceManager.Resource.LandValue,
                        b.m_position, out v);
                    if (v > 0) return v;
                }
            }
            catch (System.Exception) { }
            if (districtId != 0 && dbuf != null && districtId < dbuf.Length)
                return dbuf[districtId].m_groundData.m_finalLandvalue;
            return 0;
        }

        /// <summary>建筑当前等级（1 起）。growable 拿 Building.m_level，取不到再退到 Info 上的等级。</summary>
        private static int BuildingLevel(Building b, BuildingInfo info)
        {
            int lv = (int)b.m_level;
            if (lv <= 0 && info != null) lv = (int)info.m_class.m_level;
            return lv;
        }

        // ---- GDP 权重表（2026-09-18）----
        // 依据：各区划类型的现实产出强度，按建筑等级递增。住宅到 5 级，
        // 商业/办公/普通工业到 3 级（超出取最高档）。specialized/玩家/公共服务无等级，取固定值。
        private static readonly double[] GDP_W_RES_LOW = { 0.39, 0.41, 0.44, 0.47, 0.50 };
        private static readonly double[] GDP_W_RES_HIGH = { 0.40, 0.45, 0.50, 0.55, 0.60 };
        private static readonly double[] GDP_W_COM_LOW = { 1.85, 2.20, 2.50, 2.50, 2.50 };
        private static readonly double[] GDP_W_COM_HIGH = { 1.94, 2.42, 3.00, 3.00, 3.00 };
        private static readonly double[] GDP_W_OFFICE = { 2.19, 2.84, 3.50, 3.50, 3.50 };
        private static readonly double[] GDP_W_INDUSTRY = { 2.07, 2.48, 3.00, 3.00, 3.00 };

        /// <summary>按等级取表值，等级越界（0 或超出表长）夹到有效范围。</summary>
        private static double AtLevel(double[] tbl, int level)
        {
            if (level < 1) level = 1;
            if (level > tbl.Length) level = tbl.Length;
            return tbl[level - 1];
        }

        /// <summary>
        /// 农林（林业 / 农业）在表值基础上**再乘**的系数（2026-09-18）：
        /// 林业 1.50→1.05，农业 1.00→0.70。只作用于普通工业区的
        /// IndustrialForestry / IndustrialFarming（玩家产业不细分，仍按 3.00）。
        /// </summary>
        private const double GDP_AGRI_FOREST_FACTOR = 0.7;

        /// <summary>
        /// GDP 权重：按建筑类型 + 等级取值。
        ///   住宅：低密度 0.39~0.50 / 高密度 0.40~0.60（按等级 1~5）
        ///   低密度商业 1.85~2.50 / 高密度商业 1.94~3.00（1~3）
        ///   办公 2.19~3.50 / 普通工业 2.07~3.00（1~3）
        ///   林业 1.50×0.7=1.05 / 农业 1.00×0.7=0.70 / 矿业 2.00 / 石油 4.00 / 玩家产业 3.00（无等级）
        ///   公共服务建筑 2.00（无等级，2026-09-18 由 3.00 改为 2.00）
        /// 未列明的返回 0（= 不计入 GDP）。
        /// </summary>
        private static double GdpWeightOf(BuildingInfo info, int level)
        {
            if (info == null) return 0.0;
            ItemClass.Service svc = info.m_class.m_service;
            ItemClass.SubService sub = info.m_class.m_subService;

            if (svc == ItemClass.Service.Residential)
            {
                if (sub == ItemClass.SubService.ResidentialHigh
                    || sub == ItemClass.SubService.ResidentialHighEco)
                    return AtLevel(GDP_W_RES_HIGH, level);
                return AtLevel(GDP_W_RES_LOW, level);
            }
            if (svc == ItemClass.Service.Commercial)
            {
                // CommercialEco（生态商业）只有一个枚举、不分高低密度，按低密度计
                if (sub == ItemClass.SubService.CommercialHigh) return AtLevel(GDP_W_COM_HIGH, level);
                return AtLevel(GDP_W_COM_LOW, level);
            }
            if (svc == ItemClass.Service.Office) return AtLevel(GDP_W_OFFICE, level);
            if (svc == ItemClass.Service.Industrial)
            {
                if (sub == ItemClass.SubService.IndustrialForestry) return 1.50 * GDP_AGRI_FOREST_FACTOR;
                if (sub == ItemClass.SubService.IndustrialFarming) return 1.00 * GDP_AGRI_FOREST_FACTOR;
                if (sub == ItemClass.SubService.IndustrialOre) return 2.00;
                if (sub == ItemClass.SubService.IndustrialOil) return 4.00;
                return AtLevel(GDP_W_INDUSTRY, level);   // IndustrialGeneric 及未列明的工业子服务
            }
            if (svc == ItemClass.Service.PlayerIndustry) return 3.00;
            if (IsServiceWorkplace(svc)) return 2.00;
            return 0.0;
        }

        /// <summary>
        /// GDP = 居民项 + 工人项。权重**只取权重表**（GDP_W_* 与各类固定值），
        /// 2026-09-18 起不再有全局滑杆（原「居民权重/工人权重」已整条删除）。
        /// 分子在建筑分片遍历里累加：Σ 人数 × 地价 × 权重(类型, 等级)，见 DensityData。
        /// 注：地价用建筑所在地格地价（住宅/商业/办公）或全图平均地价（工业/玩家产业/公共服务）。
        /// </summary>
        private static double CalcGDP(DensityData den)
        {
            return (den.GdpResNum + den.GdpWorkNum) * GetDisplayFactor();
        }

        private Dictionary<ushort, DensityData> _allDensity;
        private bool _densityBuilding;
        private uint _densityProgress;
        private uint _densityPerTick;
        private uint _densityTotal;
        private Dictionary<ushort, DensityData> _densityPartial;
        private static readonly Dictionary<ushort, DensityData> _emptyDensity = new Dictionary<ushort, DensityData>();

        /// <summary>
        /// 「建筑地格地价」的**本轮遍历内 memo**（键 = 建筑 ID）。
        /// GDP 累加与工资的房子地价共用同一个 memo → 每栋楼每轮只真正查一次地价格网。
        /// 惰性填充，所以不依赖遍历顺序；随每轮遍历重建（见 TickDensityBuild）。
        /// </summary>
        private Dictionary<ushort, int> _landValuePartial;

        // 建成区：每区划的建成格数（Σ 建筑 m_width×m_length，每格 64 m²），在密度分片遍历里顺带统计
        private Dictionary<ushort, long> _allBuiltCells;
        private Dictionary<ushort, long> _builtCellsPartial;

        // 「建筑价值增量」用的加权占地：按用途区分权重，且**不含**空隙系数（用的是原始面积）
        private Dictionary<ushort, double> _allBuiltWeightCells;
        private Dictionary<ushort, double> _builtWeightPartial;

        // ================= 通勤距离 / 本地就业（**居住地口径**，2026-09-27 新增）=================
        // 基数 = 就业市民，与「人均可支配收入」的分子**完全是同一批人**（见 AccumWages）：
        //   m_workBuilding 指向的工作建筑属于 IsIncomeWorkplace，且居住地在某个区划内。
        // 两个源数组都由建筑分片遍历顺带累加、每轮（UpdateInterval×3 秒）重建并引用交换发布。
        /// <summary>
        /// OD 人数矩阵：下标 `[居住区划 * 256 + 工作区划]`。
        /// 工作区划 = 0 表示工作地在未分配区域（**照样进分母**，但 0 不是任何区划 → 永远不算「本地就业」）。
        /// 派生：行和 = 该区划的就业居民数（分母）；对角元 od[h*256+h] = 在本区划就业的人数。
        /// </summary>
        private int[] _allOd;
        private int[] _odPartial;
        /// <summary>按居住区划的通勤距离之和（**米**）—— 除以该区划就业居民数 = 平均通勤距离。</summary>
        private double[] _allCommuteSum;
        private double[] _commuteSumPartial;

        // 派生缓存：都能由上面两个源数组随时重算，所以 ClearCache 可以清
        // （⚠️ 源数组 _allOd / _allCommuteSum 与 _allIncome 同列，**绝不能在 ClearCache 里清**）
        private long[] _commuteCount;      private float _commuteCountTime;
        private double[] _commuteDist;     private float _commuteDistTime;
        private double[] _localEmpRate;    private float _localEmpRateTime;
        private double[] _aggCommuteDist;  private float _aggCommuteDistTime;
        private double[] _aggLocalEmpRate; private float _aggLocalEmpRateTime;

        // ---- 通勤时间（2026-09-27 新增）：与上面两项同源同口径（居住地归集、同一批人），只是量换成「时间」----
        // 源数据不是数组，而是**每区划一个 FIFO 样本缓冲**（`CommuteSamples[] _samples`，见下方跟踪器那段）：
        // 一趟通勤结算后按居住区划压进去，留存的样本不清零（满了才淘汰最早的），均值由 getter 现算。
        private double[] _commuteTime;      private float _commuteTimeTime;
        private long[] _commuteTimeCnt;     private float _commuteTimeCntTime;
        private double[] _aggCommuteTime;   private float _aggCommuteTimeTime;
        // 「最长 10%」口径（面板第三个按钮）：每区划取**最大的 10% 样本**求平均（至少 1 趟）。
        // 与均值是**两套并列的派生缓存**，互不覆盖 —— 周库入库永远记均值（见 Hub.SampleWeek），
        // 这一套只是显示/排名口径（用户 2026-09-27 加）。开关本身放在**面板**（`_commuteTopMode`）。
        private double[] _commuteTop;       private float _commuteTopTime;
        private double[] _aggCommuteTop;    private float _aggCommuteTopTime;
        // 「统计进度」百分比（0~100，-1 = 没有就业居民/不适用）：面板给**白色 0**那一行后面注明
        // 「已统计 x%」用（用户 2026-09-27）。= 已留存趟数 ÷ 显示门槛（就业居民数÷8）。
        private double[] _commuteProg;      private float _commuteProgTime;
        private double[] _aggCommuteProg;   private float _aggCommuteProgTime;

        /// <summary>
        /// 显示口径（用户 2026-09-27 定）：**60 模拟帧 = 10 秒** ⇒ 1 帧 = 1/6 秒。
        /// 也就是「模拟速率 6 帧/秒」下的**现实等效时间** —— 通勤时长按模拟帧数算，不经过游戏日历。
        ///
        /// ⚠️ 2026-09-27 改过一次，别改回去：早先那版用**游戏日历**换算（`m_timePerFrame` = 147.65625 秒/帧，
        /// 4096 帧 = 7 个日历日，已用用户存档实测确认），一路上算出来一趟通勤是「几小时」，
        /// 还得再编一个 ÷10 的刻度去压。问题在于**游戏有两套钟且相差 112 倍**：
        ///   · 日历钟（日期）：147.65625 游戏秒/帧；
        ///   · 昼夜钟（HUD 的时:分，`DAYTIME_FRAMES = 65536`）：1.3184 游戏秒/帧。
        /// 市民出行的时间感受跟的是**昼夜钟**（作息按它走），所以通勤时长应当直接从模拟帧出，
        /// 不要再乘日历系数 —— 用户给的 60 帧 = 10 秒 就是这条。
        /// </summary>
        private const double REAL_SECONDS_PER_FRAME = 10.0 / 60.0;

        /// <summary>
        /// 「建筑价值增量」的面积权重：不同用途的地均价值差异很大，按类别加权。
        /// 低密住宅 0.5 / 高密住宅 1 / 低密商业 2 / 高密商业 4 / 办公 4 / 玩家建筑 3 / 工业 1.5 /
        /// 公共服务建筑（含公园）3；未列出的用途按 1.0 中性计。
        /// </summary>
        private static double BuiltWeight(ItemClass.Service svc, ItemClass.SubService sub)
        {
            if (svc == ItemClass.Service.Residential)
            {
                // 生态住宅（Green Cities）也是住宅，低密的归 0.5、高密的归 1
                if (sub == ItemClass.SubService.ResidentialHigh
                    || sub == ItemClass.SubService.ResidentialHighEco) return 1.0;
                return 0.5;
            }
            if (svc == ItemClass.Service.Commercial)
                return sub == ItemClass.SubService.CommercialHigh ? 4.0 : 2.0;
            if (svc == ItemClass.Service.Office) return 4.0;
            if (svc == ItemClass.Service.PlayerIndustry) return 3.0;
            if (svc == ItemClass.Service.Industrial) return 1.5;
            switch (svc)
            {
                // 公共服务建筑（与 IsExpenseBuilding 同口径，含公园）
                case ItemClass.Service.Garbage:
                case ItemClass.Service.HealthCare:
                case ItemClass.Service.PoliceDepartment:
                case ItemClass.Service.Education:
                case ItemClass.Service.FireDepartment:
                case ItemClass.Service.Disaster:
                case ItemClass.Service.Beautification:
                    return 3.0;
            }
            return 1.0;
        }

        // 人均可支配收入：每区划的分子累加（同样在密度分片遍历里顺带统计）
        private Dictionary<ushort, IncomeData> _allIncome;
        private Dictionary<ushort, IncomeData> _incomePartial;
        private double[] _districtIncomeNum;      // 分子缓存（WageNum + PropNum）
        private float _districtIncomeNumTime;

        /// <summary>基准周工资（克朗/周），下标 = Citizen.Education（0 未受教育 / 1 小学 / 2 中学 / 3 大学）。</summary>
        private static readonly double[] BASE_WAGE = { 15.0, 20.0, 27.0, 38.0 };

        private static double _avgLandValue;
        private static float _avgLandValueTime;

        /// <summary>全地图平均地价（按区划人口加权，缓存 CACHE_LIFE）。</summary>
        private static double GetAverageLandValue()
        {
            if (Time.time - _avgLandValueTime < CacheLife()) return _avgLandValue;
            DistrictManager dm = Singleton<DistrictManager>.instance;
            double weightedSum = 0;
            double popSum = 0;
            if (dm != null)
            {
                District[] buf = dm.m_districts.m_buffer;
                uint dsize = dm.m_districts.m_size;
                for (uint d = 1; d < dsize; d++)
                {
                    if ((buf[d].m_flags & District.Flags.Created) == 0) continue;
                    double land = buf[d].m_groundData.m_finalLandvalue;
                    double pop = buf[d].m_populationData.m_finalCount;
                    weightedSum += land * pop;
                    popSum += pop;
                }
            }
            _avgLandValue = popSum > 0 ? weightedSum / popSum : 0;
            _avgLandValueTime = Time.time;
            return _avgLandValue;
        }

        /// <summary>
        /// 现实化数据换算系数（GDP / 人均GDP / 地均GDP / 人均可支配）。
        /// **两个轴相乘**：流量货币系数 × 周期周数。
        ///   原版周 = 1、原版年 = 52、人民币年 = 2625、美元年 = 375（与旧 DisplayMode 完全一致）
        ///   新增组合如 人民币月 = 2625/52×4 ≈ 201.9、美元5年 = 375/52×260 = 1875
        /// ⚠️ 地价走的是**另一套**价格系数（1/420/60，与周期无关），见 LandMultForCalc。
        /// </summary>
        public static double GetDisplayFactor()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Settings != null)
                return ModSettings.FlowFactor(hub.Settings.DisplayCurrency)
                     * ModSettings.PeriodWeeks(hub.Settings.DisplayPeriod);
            return 1.0;
        }

        /// <summary>从游戏区划数据直接读取工人数（商业/工业/办公/玩家建筑的在岗人数之和）。</summary>
        private static int GetWorkers(District d)
        {
            long workers = (long)d.m_commercialData.m_finalAliveCount
                + (long)d.m_industrialData.m_finalAliveCount
                + (long)d.m_officeData.m_finalAliveCount
                + (long)d.m_playerData.m_finalAliveCount;
            return (int)workers;
        }

        private struct DensityData
        {
            public int ResLow, ResHigh, ComLow, ComHigh;
            // GDP 分子（在建筑分片遍历里累加）：Σ 人数 × 地价 × 权重(类型, 等级)。
            // 居民/工人两个滑杆不在这里乘 —— 留到 CalcGDP 里现乘，玩家拖滑杆才能立刻生效。
            public double GdpResNum;    // 住宅居民项
            public double GdpWorkNum;   // 工作场所工人项
            // 公共服务建筑的在岗市民数。游戏的区划数据（m_commercialData 等）里没有公共服务这一项，
            // 由我们自己数，「工作人数」列会额外加上它。
            public int ServiceWorkers;
        }

        /// <summary>
        /// 每区划的收入累加器（克朗/周，原版口径，不预乘显示系数）。
        /// WageNum/PropNum 是「分子」，人均 = 分子 ÷ 该区划常住人口（在取值函数里做除法）。
        /// 聚合时也是「分子求和 ÷ 人口求和」，不能对人均值求平均。
        /// </summary>
        private struct IncomeData
        {
            public double WageNum;   // Σ 在岗市民的税后工资（按其居住地归集，地价取居住地）
            public double PropNum;   // Σ 居民的税后财产收入
            public int Workers;      // 在岗市民数
            public int ResTaxPct;    // 住宅税率读数（诊断用，取最后一栋住宅建筑的值）
        }

        /// <summary>返回分片遍历构建的密度数据（由 Hub 每帧调用 TickDensityBuild 逐步填充）。</summary>
        private Dictionary<ushort, DensityData> GetAllDensity()
        {
            return _allDensity ?? _emptyDensity;
        }

        /// <summary>取某区划的密度/GDP 分子数据，没有则返回全 0（GDP 会算成 0）。</summary>
        private DensityData GetDensity(ushort districtId)
        {
            DensityData den;
            if (GetAllDensity().TryGetValue(districtId, out den)) return den;
            return new DensityData();
        }

        /// <summary>
        /// 密度分片遍历：每次调用处理一段建筑，x 秒（自动保存间隔）完成整体遍历，
        /// 避免一次性全城遍历造成卡顿。由 Hub.Update 每秒调用。
        /// </summary>
        public void TickDensityBuild()
        {
            try
            {
                DistrictFinanceHub hub = DistrictFinanceHub.Instance;
                int period = (hub != null && hub.Settings != null)
                    ? Mathf.Max(1, hub.Settings.UpdateInterval * 3) : 30; // 遍历总时间 = 更新间隔×3
                BuildingManager bm = Singleton<BuildingManager>.instance;
                if (bm == null) return;

                if (!_densityBuilding)
                {
                    // 建筑缓冲还没就绪（读档瞬间可能读到 0）→ 什么都别做，等下一次调用。
                    // 否则首轮会"完成"成一个空快照并发布出去，BuiltAreaReady 提前变 true，
                    // 采样就会把「建成区=0」的垃圾周写进周库。
                    if (bm.m_buildings.m_size < 2) return;
                    _densityTotal = bm.m_buildings.m_size;
                    _densityProgress = 1;
                    _densityPerTick = System.Math.Max(1u, _densityTotal / (uint)period);
                    _densityPartial = new Dictionary<ushort, DensityData>();
                    _builtCellsPartial = new Dictionary<ushort, long>();
                    _builtWeightPartial = new Dictionary<ushort, double>();
                    _incomePartial = new Dictionary<ushort, IncomeData>();
                    _landValuePartial = new Dictionary<ushort, int>();   // 地价 memo 随每轮重建
                    _odPartial = new int[256 * 256];                     // 通勤 OD（居住地口径）
                    _commuteSumPartial = new double[256];
                    _watchPartial = new List<uint>();                    // 通勤监视名单（第三版）
                    _watchHomePartial = new List<byte>();
                    _densityBuilding = true;
                }

                // 首次遍历（读档后还没发布过任何数据）一次跑完整城 —— 面板立刻就有建成区/增量/收入，
                // 不用等 UpdateInterval×3 秒。之后每轮重扫仍按分片，避免每秒卡顿。
                // 判据用 _allBuiltCells：它只在首轮结束时发布，且不会被 ClearCache 清掉，
                // 所以「切显示模式」这类会清缓存的操作不会触发全量重扫。
                uint end;
                if (_allBuiltCells == null)
                    end = _densityTotal;   // 首轮：全量
                else
                    end = System.Math.Min(_densityProgress + _densityPerTick, _densityTotal);

                ProcessDensityRange(_densityProgress, end);
                _densityProgress = end;

                if (_densityProgress >= _densityTotal)
                {
                    _allDensity = _densityPartial;
                    _allBuiltCells = _builtCellsPartial;
                    _allBuiltWeightCells = _builtWeightPartial;
                    _allIncome = _incomePartial;
                    _allOd = _odPartial;
                    _allCommuteSum = _commuteSumPartial;
                    PublishCommuteWatch();                                // 名单随本轮一起发布（见第三版注释）
                    _densityBuilding = false;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[DFM] TickDensityBuild failed: " + ex.Message);
                _densityBuilding = false;
            }
        }

        private void ProcessDensityRange(uint start, uint end)
        {
            DistrictManager dm = Singleton<DistrictManager>.instance;
            BuildingManager bm = Singleton<BuildingManager>.instance;
            CitizenManager cm = Singleton<CitizenManager>.instance;
            if (dm == null || bm == null || cm == null) return;

            Building[] buf = bm.m_buildings.m_buffer;
            CitizenUnit[] units = cm.m_units.m_buffer;
            Citizen[] citizens = cm.m_citizens.m_buffer;
            uint citizenSize = (uint)citizens.Length;
            District[] dbuf = dm.m_districts.m_buffer;
            EconomyManager em = Singleton<EconomyManager>.instance;

            for (uint i = start; i < end; i++)
            {
                Building b = buf[i];
                if ((b.m_flags & Building.Flags.Created) == 0) continue;
                BuildingInfo info = b.Info;
                if (info == null) continue;
                byte d = dm.GetDistrict(b.m_position);

                // 工资项必须在「d==0 提前返回」**之前**处理：口径是「全城所有在岗市民」，
                // 区划外（未分配区域）的工作建筑里也有在岗市民，他们的工资照样要按居住地归集。
                if (IsIncomeWorkplace(info.m_class.m_service))
                    AccumWages(b, (ushort)i, d, dm, units, citizens, citizenSize, buf, dbuf, em);

                if (d == 0 || d >= dbuf.Length) continue;

                // 建成区：本建筑占地格数（m_width×m_length，每格 64 m²）—— 不限用途，凡建成即计
                long bc;
                _builtCellsPartial.TryGetValue(d, out bc);
                _builtCellsPartial[d] = bc + (long)b.m_width * (long)b.m_length;

                // 加权占地：给「建筑价值增量」用，按用途区分权重（原始面积，不含空隙系数）
                double wcells;
                _builtWeightPartial.TryGetValue(d, out wcells);
                _builtWeightPartial[d] = wcells
                    + (double)b.m_width * (double)b.m_length
                      * BuiltWeight(info.m_class.m_service, info.m_class.m_subService);

                DensityData dd;
                if (!_densityPartial.TryGetValue(d, out dd)) dd = new DensityData();
                int lv = BuildingLevel(b, info);

                if (info.m_class.m_service == ItemClass.Service.Residential)
                {
                    int n = CountInBuilding(b, units, citizens, citizenSize, (ushort)i, true);
                    bool low = info.m_class.m_subService == ItemClass.SubService.ResidentialLow;
                    if (low) dd.ResLow += n; else dd.ResHigh += n;

                    // GDP 居民项：居民数 × 建筑地价 × 住宅权重(密度, 等级)
                    dd.GdpResNum += (double)n * CachedLandValue(b, (ushort)i, d, dbuf) * GdpWeightOf(info, lv);

                    // 财产收入：每个居民各计一次；地价取本区划地价，再扣住宅密度税
                    IncomeData inc;
                    if (!_incomePartial.TryGetValue(d, out inc)) inc = new IncomeData();
                    int resTax = TaxRateOf(em, info, dbuf[d].m_taxationPoliciesEffect);
                    inc.PropNum += n * dbuf[d].m_groundData.m_finalLandvalue * 0.2 * (1.0 - resTax / 100.0);
                    inc.ResTaxPct = resTax;
                    _incomePartial[d] = inc;
                }
                else if (IsIncomeWorkplace(info.m_class.m_service))
                {
                    // 工作场所（商/工/办/玩家产业/公共服务）：本楼在岗市民数
                    int n = CountInBuilding(b, units, citizens, citizenSize, (ushort)i, false);

                    // 商业沿用「非低密度即高密度」的既有分桶（人口密度统计口径，本次不动）
                    if (info.m_class.m_service == ItemClass.Service.Commercial)
                    {
                        bool low = info.m_class.m_subService == ItemClass.SubService.CommercialLow;
                        if (low) dd.ComLow += n; else dd.ComHigh += n;
                    }

                    // GDP 工人项：在岗人数 × 地价 × 权重(类型, 等级)。
                    // 地价：工业/玩家产业/公共服务用全图平均地价，商业/办公用建筑地价
                    double w = GdpWeightOf(info, lv);
                    if (w > 0.0)
                    {
                        double land = UseAverageLand(info.m_class.m_service)
                            ? GetAverageLandValue()
                            : (double)CachedLandValue(b, (ushort)i, d, dbuf);
                        dd.GdpWorkNum += (double)n * land * w;
                    }

                    // 公共服务建筑的在岗人数单独记账（游戏区划数据里没有这一项）
                    if (IsServiceWorkplace(info.m_class.m_service)) dd.ServiceWorkers += n;
                }
                _densityPartial[d] = dd;
            }
        }

        /// <summary>
        /// 计入收入统计的工作场所：商/工/办/玩家产业 + 公共服务建筑。
        /// （注意与既有的 IsWorkplace(ItemClass) 区分：那个只含商/工/办，用于「工作人数」统计，
        ///   改动它会变动既有数值，故此处另起一名。）
        /// </summary>
        private static bool IsIncomeWorkplace(ItemClass.Service svc)
        {
            return svc == ItemClass.Service.Commercial
                || svc == ItemClass.Service.Industrial
                || svc == ItemClass.Service.Office
                || svc == ItemClass.Service.PlayerIndustry
                || IsServiceWorkplace(svc);
        }

        /// <summary>
        /// 计入收入 / 工作人数 / GDP 的「服务类工作场所」。
        /// ⚠️ **2026-09-22 由白名单改为排除法**：原先只列了
        /// 公园/垃圾/医疗/警察/教育/消防/灾害/大学DLC/博物馆/大学体育，
        /// **漏掉了公交与轨道交通（火车站、地铁站、公交站、机场、港口）**，
        /// 以及电力、供水、旅游、酒店、渔业、纪念碑、服务点、赛道等一干服务建筑 ——
        /// 这些建筑里的在岗市民（`m_workBuilding == 本建筑`）同样是居民收入，没理由排除。
        /// 现在只排除「不是建筑用途」和「另有专属权重分支的商/工/办/玩家产业」。
        /// 真正没有工人的建筑计数自然是 0，所以**宁可多算不漏算**。
        /// 刻意与 IsExpenseBuilding 分开：那个用于「支出」统计，改它会变动既有数值。
        /// </summary>
        private static bool IsServiceWorkplace(ItemClass.Service svc)
        {
            switch (svc)
            {
                // ① 不是「有工人的建筑」：自然地貌 / 载具 / 市民 / 道路 / 空
                case ItemClass.Service.None:
                case ItemClass.Service.Natural:
                case ItemClass.Service.Vehicles:
                case ItemClass.Service.Citizen:
                case ItemClass.Service.Road:
                // ② 住宅：单独走居民分支，不是工作场所
                case ItemClass.Service.Residential:
                // ③ 商 / 工 / 办 / 玩家产业：由 IsIncomeWorkplace 的另一半负责，
                //    且 IncomeWeightOf(1421-1423) / GdpWeightOf **在服务类之前**给它们
                //    各自的专属权重 —— 这里若返回 true 会把它们的权重覆盖成 1.50 / 2.00。
                case ItemClass.Service.Commercial:
                case ItemClass.Service.Industrial:
                case ItemClass.Service.Office:
                case ItemClass.Service.PlayerIndustry:
                    return false;
            }
            // 其余一律按「服务类工作场所」处理：公园 / 垃圾 / 医疗 / 警察 / 教育 / 消防 / 灾害 /
            // 大学DLC / 博物馆 / 大学体育，以及 —— **2026-09-22 补上的** ——
            // 公交与轨道交通（火车站 / 地铁站 / 公交站 / 机场 / 港口 = PublicTransport）、
            // 电力、供水、旅游、酒店、渔业、纪念碑、服务点、赛道等。
            // 用排除法而不是白名单，是为了**宁可多算不漏算**：真正没有工人的建筑
            // （公园、纪念碑之类）计数自然是 0，不会贡献任何值。
            return true;
        }

        /// <summary>有等级的收入建筑满级数（商业 / 办公 / 普通工业区都是 3 级）。</summary>
        private const int INCOME_MAX_LEVEL = 3;

        /// <summary>每降一个等级的收入权重折扣。</summary>
        private const double INCOME_LEVEL_STEP = 0.85;

        /// <summary>
        /// 有等级的建筑：表值 = **满级**权重，每低一级 ×0.85。
        /// 满级 3 → 1.00×；2 级 → 0.85×；1 级 → 0.7225×。
        /// </summary>
        private static double LeveledIncomeWeight(double maxWeight, int level)
        {
            if (level < 1) level = 1;
            if (level > INCOME_MAX_LEVEL) level = INCOME_MAX_LEVEL;
            double w = maxWeight;
            for (int i = level; i < INCOME_MAX_LEVEL; i++) w *= INCOME_LEVEL_STEP;
            return w;
        }

        /// <summary>
        /// 工资权重，乘在「基准工资 × 地价系数 × 税后」之后。
        /// 表值（2026-09-18）是**最高等级**的权重，等级越低按 0.85 递减（见 LeveledIncomeWeight）。
        ///   办公 1.65；玩家产业 1.50；公共服务建筑 1.50
        ///   商业：高密度 1.20，其余（含生态商业、休闲/旅游/壁到壁）按低密度 1.05
        ///   普通工业区按子服务：石油 1.50 / 矿业 1.11 / 普通工业 0.88 / 林业 0.57 / 农业 0.55
        /// 无等级的类型（玩家产业、专业化工农业、公共服务建筑）直接用表值。
        /// 未列明的按 1.00 中性计。
        /// </summary>
        private static double IncomeWeightOf(BuildingInfo info, int level)
        {
            if (info == null) return 1.00;
            ItemClass.Service svc = info.m_class.m_service;
            ItemClass.SubService sub = info.m_class.m_subService;

            if (svc == ItemClass.Service.Office) return LeveledIncomeWeight(1.65, level);
            if (svc == ItemClass.Service.PlayerIndustry) return 1.50;
            if (IsServiceWorkplace(svc)) return 1.50;

            if (svc == ItemClass.Service.Commercial)
            {
                // CommercialEco（生态商业）游戏里只有一个枚举、不分高低密度，按低密度计
                if (sub == ItemClass.SubService.CommercialHigh)
                    return LeveledIncomeWeight(1.20, level);
                return LeveledIncomeWeight(1.05, level);
            }

            if (svc == ItemClass.Service.Industrial)
            {
                // 专业化工农业无等级（与 GDP 表的「无等级」一致），直接用表值
                if (sub == ItemClass.SubService.IndustrialOil) return 1.50;
                if (sub == ItemClass.SubService.IndustrialOre) return 1.11;
                if (sub == ItemClass.SubService.IndustrialForestry) return 0.57;
                if (sub == ItemClass.SubService.IndustrialFarming) return 0.55;
                return LeveledIncomeWeight(0.88, level);   // IndustrialGeneric 及未列明的工业子服务
            }

            return 1.00;
        }

        /// <summary>
        /// 遍历一栋工作建筑里的在岗市民（m_workBuilding == 本建筑），逐个把税后工资累加到其
        /// **居住地所在区划**的收入累加器。税率用本工作建筑的游戏税率；
        /// 地价 =（工作点地价 + 房子地价）÷ 2，工作点用全图平均地价（工业/玩家产业/公共服务建筑）
        /// 或工作建筑地格地价（商业/办公），房子用居住建筑地格地价。
        /// </summary>
        private void AccumWages(Building b, ushort buildingId, byte workDistrict, DistrictManager dm,
            CitizenUnit[] units, Citizen[] citizens, uint citizenSize,
            Building[] bbuf, District[] dbuf, EconomyManager em)
        {
            if (b.m_citizenUnits == 0) return;
            // 工作建筑可能不在任何区划内（未分配区域）→ 没有区划税收政策，按无政策取税率
            DistrictPolicies.Taxation taxation = DistrictPolicies.Taxation.None;
            if (workDistrict != 0 && workDistrict < dbuf.Length)
                taxation = dbuf[workDistrict].m_taxationPoliciesEffect;
            int taxPct = TaxRateOf(em, b.Info, taxation);
            double afterTax = 1.0 - taxPct / 100.0;

            // 工资地价 = (工作点地价 + 房子地价) ÷ 2   （2026-09-18 改）
            //   工作点地价：工业 / 玩家产业 / 公共服务建筑 →【全图平均地价】
            //               商业 / 办公 → 工作建筑所在地格地价
            //   房子地价　：工人居住建筑所在地格地价（每个工人各取各的，在循环里算）
            // 工作点那一端整栋楼共用一个值，故在循环外算一次。
            ItemClass.Service svc = b.Info.m_class.m_service;
            double workLand = UseAverageLand(svc)
                ? GetAverageLandValue()
                : (double)CachedLandValue(b, buildingId, workDistrict, dbuf);

            // 工资权重：按工作建筑的类型 + 等级区分（表值是满级权重，每低一级 ×0.85）
            double weight = IncomeWeightOf(b.Info, BuildingLevel(b, b.Info));

            uint unit = b.m_citizenUnits;
            int guard = 0;
            while (unit != 0 && guard++ < 4096)
            {
                CitizenUnit u = units[unit];
                for (int j = 0; j < 5; j++)
                {
                    uint cid = u.GetCitizen(j);
                    if (cid == 0 || cid >= citizenSize) continue;
                    Citizen c = citizens[cid];
                    if (c.m_workBuilding != buildingId) continue;   // 只算真正在此上班的（排除来访/顾客）

                    // 归集到居住地所在区划
                    ushort home = c.m_homeBuilding;
                    if (home == 0 || home >= bbuf.Length) continue;  // 无家可归 / 越界：不计
                    if ((bbuf[home].m_flags & Building.Flags.Created) == 0) continue;
                    // 居住地不在任何区划内 → 无从归集，直接不计。
                    // （口径要一致：这类居民也不计入任何区划的分母 m_populationData，
                    //   若改归到工作区划，会把非本区居民的收入摊到本区常住人口头上。）
                    byte hd = dm.GetDistrict(bbuf[home].m_position);
                    if (hd == 0 || hd >= dbuf.Length) continue;

                    int edu = (int)c.EducationLevel;
                    if (edu < 0 || edu >= BASE_WAGE.Length) edu = 0;
                    // 工资地价 = (工作点地价 + 房子地价) ÷ 2
                    double homeLand = CachedLandValue(bbuf[home], home, hd, dbuf);
                    double factor = (0.5 + (workLand + homeLand) / 2.0 / 35.0) * afterTax;
                    double wage = BASE_WAGE[edu] * factor * weight;

                    IncomeData inc;
                    if (!_incomePartial.TryGetValue(hd, out inc)) inc = new IncomeData();
                    inc.WageNum += wage;
                    inc.Workers++;
                    _incomePartial[hd] = inc;

                    // ---- 通勤距离 / 本地就业（居住地口径，2026-09-27 新增）----
                    // 与上面那笔工资**同一批人**（在岗 + 居住地在区划内），只是再记两件事：
                    //   ① 住址 ↔ 工作地的**直线**距离（取两栋建筑的中心点；世界单位就是米，一格 8 m）；
                    //   ② OD 对（居住区划 → 工作区划）：行和 = 就业居民数（分母），对角元 = 本地就业人数。
                    // 学生不是就业：**既带学生标志、又在教育建筑里**才排除（双条件 —— 只排除真正的在读学生，
                    // 教师等在教育建筑上班的人照常计入；沿用 IsServiceWorkplace「宁可多算不漏算」的原则）。
                    if (_odPartial != null
                        && !((c.m_flags & Citizen.Flags.Student) != 0
                             && svc == ItemClass.Service.Education))
                    {
                        _odPartial[hd * 256 + workDistrict]++;
                        _commuteSumPartial[hd] +=
                            Vector3.Distance(bbuf[home].m_position, b.m_position);
                        // 监视名单（第三版）：**就是这批人**（在岗 + 居住地在区划内 + 不是学生），
                        // 之后只盯他们自己的 CurrentLocation 变化来量通勤时长（见 PollCommuteWatch）
                        // —— 用户 2026-09-27 的主意：「从工作地点的 income 逆向识别」。
                        if (_watchPartial != null)
                        {
                            _watchPartial.Add(cid);
                            _watchHomePartial.Add(hd);
                        }
                    }
                }
                unit = u.m_nextUnit;
            }
        }

        // ================= 通勤时间：**监视名单轮询**（2026-09-27 第三版）=================
        // 演进史（别回退）：
        //   第一版只读「当前那一段」的路径（`PathUnit.m_length ÷ m_speed`）→ 对公交换乘者是错的：
        //     只采到走到车站那段、乘车段采不到、**候车/换乘的等待根本不在任何路径里**。
        //   第二版扫**实例缓冲 + 车辆缓冲**、按「源=家 + 终点=单位」登记 → 仍有两类硬伤：
        //     · **开车的人在车里没有 CitizenInstance**，只能靠车的 source/target 认；而 CS1 的**停车位是
        //       prop、没有 Parking 这个服务**，车还能开到车站换乘 → 终点判据稍一收紧就把自驾整批漏掉
        //       （用户 2026-09-27：「有可能是停车地点，还有车站也有很多种，都考虑了吗」）；
        //     · 每帧扫 ~2200 个实例 + 全部车辆，实测 **5~6 ms/帧**（约占 60 fps 一帧预算的三分之一）。
        //   第三版（当前）＝**轮询监视名单**（用户 2026-09-27 的主意：「从工作地点的 income 逆向识别」）：
        //     · 名单来源 = **收入口径那一遍扫描**（`AccumWages`：遍历每栋**工作建筑**的 `m_citizenUnits`
        //       —— 本来就是「全部就业居民 + 其居住区划」，与人均可支配 / OD 的分子**完全同一批人**）；
        //     · 之后只盯这些人**自己的** `Citizen.CurrentLocation`：
        //         上次看到还在**家**、这次不在家也不在单位 → **出发**（记出发帧）；
        //         在途 → 变成 `Work` = **到单位**（时长 = 到达帧 − 出发帧，压进区划 FIFO）；
        //                变回 `Home` = 半路回家（丢弃）；太久没到（`COMMUTE_TRIP_MAX_FRAMES`）= 丢弃（不记账）。
        //     · **完全不看**实例 / 车辆 / 路径 / 站台 / 停车位 —— 人走路、开车、公交地铁、在哪换乘、
        //       在哪停车都无所谓，量的是「这个人从家到单位花了多少帧」，换乘/候车/最后一段步行天然全含。
        //     · 代价 = 每 `COMMUTE_POLL_FRAMES` 帧遍历一次名单（几万次数组读）→ 比第二版低两个数量级。
        // 数值不用「发布」：一次结算就 `PushCommuteSample` 进区划 FIFO（见 `CommuteSamples`），getter 现算。

        /// <summary>
        /// 是否正在统计（用户 2026-09-27：**只有点了「开始」才统计**，默认关闭）。
        /// 「停止」会丢掉**还没跟踪完**的在途行程（不完整的记录不进统计），已统计出来的数值保留显示。
        /// </summary>
        private bool _commuteRunning;
        private uint _tripPollFrame;       // 上次轮询监视名单的帧号

        // ---- 监视名单（由收入扫描每轮重建，见 PublishCommuteWatch）----
        /// <summary>名单：在岗 + 居住地在区划内 + 不是学生的市民 ID（与 OD 分子同一批人）。</summary>
        private uint[] _watch;
        private byte[] _watchHome;         // 与 _watch 同序：各自的居住区划
        private int _watchCount;
        private List<uint> _watchPartial;      // 本轮扫描正在收集的（扫完发布）
        private List<byte> _watchHomePartial;
        /// <summary>按市民 ID 记「这一趟的出发帧」（0 = 没在跟踪）与上一次看到的位置。</summary>
        private uint[] _tripStart;
        private byte[] _lastLoc;
        /// <summary>哪些 ID 在名单里（1 在 / 0 不在；发布时用 2 当临时戳记）。</summary>
        private byte[] _watched;
        /// <summary>
        /// 每区划的样本环形缓冲（FIFO）。容量 = max(1, 就业居民数 ÷ `COMMUTE_CAP_PER_RESIDENT`(=2))，
        /// 满了之后**淘汰最早**的一趟再放新的（用户 2026-09-27：「这些样本数据要留存，直到样本达到
        /// 1/2 再刷最早的数据」）。不到上限就一直留着 —— 不按天滚动、不清零。
        /// 均值 = Sum / Count；**显示**门槛是另一条（÷8，见 CommuteSamplesEnough）。
        /// </summary>
        private class CommuteSamples
        {
            public double[] Buf;   // 环形缓冲
            public int Count;      // 已有样本数（≤ Buf.Length）
            public int Head;       // 下一个写入位置
            public double Sum;     // 缓冲内之和
        }
        private readonly CommuteSamples[] _samples = new CommuteSamples[256];

        /// <summary>
        /// 轮询监视名单的间隔（帧）。**它同时就是时长的量化误差**（出发与到达都是在这一拍才被发现的，
        /// 一趟最多多算这么多帧）：15 帧 = 0.25 秒 = 显示上 0.04 分钟，对几百帧起步的通勤可以忽略。
        /// </summary>
        private const uint COMMUTE_POLL_FRAMES = 15;
        /// <summary>
        /// 样本**显示门槛**的分母（用户 2026-09-27 最终定：**1/8**；前一版是 1/4）：
        /// 样本数 ≥ 就业居民数 ÷ 8（下限 1 趟）才显示，否则记 0（＝面板上的「无数据」：白色、排最后）。
        /// 分母用 OD 行和（该区划的就业居民数）——本来就在算，不额外统计。
        /// ⚠️ **必须小于等于留存上限的 ÷2**，否则永远攒不到能显示的量（÷8 < ÷2 ✓）。
        /// </summary>
        private const int COMMUTE_SAMPLES_PER_RESIDENT = 8;
        /// <summary>
        /// 样本**留存上限**的分母（用户 2026-09-27）：「这些样本数据要留存，直到样本达到 1/2 再刷最早的
        /// 数据」→ 容量 = max(1, 就业居民数 ÷ 2)，满了再来新样本就淘汰**最早**的一趟（FIFO）。
        /// ⚠️ 与显示门槛（÷8）不是一回事：到 ÷8 开始显示，到 ÷2 才开始淘汰老样本。
        /// </summary>
        private const int COMMUTE_CAP_PER_RESIDENT = 2;
        /// <summary>
        /// 存盘时每区划最多写多少个样本（用户 2026-09-27：「样本保存后要留着」）。抓最近这些，
        /// 免得上万人的区划把 .commute 写太大（128 区划 × 256 × ~8 字节 ≈ 260 KB 上限）。
        /// </summary>
        private const int COMMUTE_PERSIST_MAX = 256;
        /// <summary>
        /// `.commute` 文件的**口径版本**（写在第一行 `# DFM Commute v2`）。读到更低（或没有）版本 → 整份丢弃：
        /// v1 是老结算判据（「最后见到帧 − 起始帧」+ 12000 帧硬上限记账）攒出来的样本，整批卡在
        /// 33.36~33.43 分钟，没有保留价值（见 LoadCommuteSamples）。**只在换算/结算口径再变时才升**。
        /// </summary>
        private const int COMMUTE_SAMPLE_VERSION = 2;
        /// <summary>
        /// 单趟硬上限（帧）：超过还没到单位就**丢掉这一趟**（不记账），防止迷路/卡住的行程把表撑住。
        /// ⚠️ **是丢弃，不是按上限记账** —— 老版本按上限记账，结果样本全挤在 33.4 分钟（见上）。
        /// ⚠️ 2026-09-27 由 12000 → 24000 → **43200**：12000 帧 = 显示 33.33 分钟，而实测有 8% 的样本挤在
        ///    30~33.3 分钟这一段、还有一小撮被上限**整条丢掉** —— 用户看到的「最长还是 33 分钟左右」
        ///    就是这个上限在切尾巴（不是真实最长）。用户随后要求**放到 120 分钟**（显示口径：
        ///    分钟 = 帧 ÷ 360）→ 43200 帧（现实 720 秒）。再长的才当异常行程丢掉。
        /// ⚠️ 2026-09-28 用户再要求**放到 5 小时** → **108000 帧**（300 分钟 × 360 帧/分钟 = 现实 1800 秒）。
        ///    配套：档位表 `COMMUTE_TIME_TIERS` 同时拉长到 8…150 分钟（15 档），
        ///    以及下面 `PollCommuteWatch` 里那条「单趟明显不合理」的兜底（240 → 300 分钟），
        ///    **三处要一起改**，否则会出现「上限允许 300 分钟、兜底却在 240 分钟把它悄悄丢掉」。
        ///    注意上限只影响**丢弃**，不影响换算（分钟 = 帧 ÷ 360）。
        /// </summary>
        private const uint COMMUTE_TRIP_MAX_FRAMES = 108000;   // 300 分钟（5 小时）× 360 帧/分钟

        // 性能诊断（只在 ShowDebug 下打印）
        private int _dbgPollFrames;        // 日志窗口内轮询了几次（算每次耗时）
        private double _dbgTripMs;
        private float _dbgTripLogTime;
        // 窗口内结算出来的样本「帧数」分布（用来判断换算合不合理）
        private int _dbgTripMinFrames = int.MaxValue, _dbgTripMaxFrames, _dbgTripSampleN;
        private long _dbgTripSumFrames;
        // 窗口内的「出发 / 到达 / 丢弃」计数（诊断：出发与到达应当同量级）
        private int _dbgDeparted, _dbgArrived, _dbgDropHome, _dbgDropTimeout;
        // 位置分布诊断（枚举顺序 = Home,Work,Visit,Moving,Hotel）：用来确认「到单位」到底能不能被观测到
        private readonly int[] _dbgLoc = new int[8];
        private long _dbgInFlightFrames; private int _dbgInFlightN;
        /// <summary>是否正在统计（面板「开始 / 停止」按钮的状态）。默认**停止**，且不保存进设置。</summary>
        public bool CommuteTracking { get { return _commuteRunning; } }

        /// <summary>
        /// 游戏是否正在存档 —— 用户 2026-09-27：「默认是停止状态，**每次保存后都要是停止状态**」。
        /// 判据用游戏自己的存档状态标志 `SavePanel.isSaving`（**静态属性**，手动存 / 快速存 / 自动存都会置位）。
        /// 取不到（游戏版本差异/被别的 mod 动过）就当没在存档 —— 只影响这条自动停止，不影响统计本身。
        /// </summary>
        private static bool GameSaving()
        {
            try { return SavePanel.isSaving; }
            catch { return false; }
        }

        /// <summary>已留存的通勤趟数合计（面板状态文字用）。</summary>
        public long CommuteSampleTotal
        {
            get
            {
                long n = 0;
                for (int i = 0; i < 256; i++) if (_samples[i] != null) n += _samples[i].Count;
                return n;
            }
        }

        /// <summary>
        /// 开始统计（用户 2026-09-27 定稿）：**不动已留存的样本**，接着往里面攒
        /// （「没必要开始就把样本都删了」）。只清在途行程（那些本来就不完整）。
        /// </summary>
        public void StartCommuteTracking()
        {
            _commuteRunning = true;
            ClearInFlight();                    // 在途行程是上一轮的半成品，丢掉重来
            _tripPollFrame = 0;
            _commuteTime = null; _commuteTimeCnt = null; _aggCommuteTime = null;
            _commuteTop = null; _aggCommuteTop = null;
            _commuteProg = null; _aggCommuteProg = null;
            Debug.Log("[DFM] 通勤时间统计：开始（保留已留存样本 " + CommuteSampleTotal + " 趟，继续攒）");
        }

        /// <summary>
        /// 停止统计（用户 2026-09-27：「停止要停下所有正在跟踪的部分」）：
        ///   · **立刻停掉每帧的扫描与结算**（`_commuteRunning=false`，TickCommuteTrack 直接返回）；
        ///   · **丢掉全部在途行程**（还没跟踪完的，一条不留 —— 不完整的记录不进统计）；
        ///   · 结算间隔复位，下次「开始」立刻重新扫；
        ///   · 已经**留存**的样本保留（面板继续显示上次的结果），下次点「开始」才会清零重来。
        /// </summary>
        public void StopCommuteTracking()
        {
            int dropped = InFlightCount();
            ClearInFlight();                       // 所有在途行程：全停、全丢
            _commuteRunning = false;
            _tripPollFrame = 0;
            _commuteTime = null; _commuteTimeCnt = null; _aggCommuteTime = null;   // 派生缓存作废
            _commuteTop = null; _aggCommuteTop = null;
            _commuteProg = null; _aggCommuteProg = null;
            Debug.Log("[DFM] 通勤时间统计：停止（丢弃 " + dropped + " 条在途行程，保留已留存样本 "
                + CommuteSampleTotal + " 趟）");
            // 停止是个天然检查点：把留存样本落盘（用户 2026-09-27：「样本保存后要留着」）
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null) hub.MarkDirty();
        }

        /// <summary>
        /// 每帧被 Hub.Update 调用（**只在点了「开始」之后**才真的干活）：
        /// 每 `COMMUTE_POLL_FRAMES` 帧轮询一次监视名单，认出「出发 / 到单位 / 半路回家 / 超时」四种情况。
        /// 代价与名单长度成正比（几万次数组读），且**只在 ShowDebug 时**才统计耗时。
        /// </summary>
        public void TickCommuteTrack()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub == null || hub.Settings == null) return;
            uint frame = GameWeek.CurrentFrame;
            if (frame == 0) return;
            if (!_commuteRunning) return;   // 只有点了「开始」才统计（面板上那两个按钮）
            // 一存档就停（用户要求：每次保存后都要是停止状态）——不想让它在存档后继续吃性能
            if (GameSaving())
            {
                StopCommuteTracking();
                return;
            }

            // 帧号倒退 = 换存档 / 重开地图 → 全部重来（在途状态与样本都是上一局的）
            if (frame < _tripPollFrame)
            {
                ClearInFlight();
                for (int i = 0; i < 256; i++) _samples[i] = null;
                _commuteTime = null; _commuteTimeCnt = null; _aggCommuteTime = null;
                _commuteTop = null; _aggCommuteTop = null;
                _commuteProg = null; _aggCommuteProg = null;
                _tripPollFrame = 0;
                _commuteRunning = false; // 新存档要重新点「开始」（用户 2026-09-27：只有开始时才统计）
            }

            if (frame - _tripPollFrame < COMMUTE_POLL_FRAMES) return;
            _tripPollFrame = frame;

            bool debug = hub.Settings.ShowDebug;
            long t0 = debug ? System.DateTime.Now.Ticks : 0L;
            int inflight = PollCommuteWatch(frame);
            if (debug)
            {
                _dbgPollFrames++;
                _dbgTripMs += (System.DateTime.Now.Ticks - t0) / 10000.0;
                if (Time.time - _dbgTripLogTime >= 5f)
                {
                    double avg = _dbgPollFrames > 0 ? _dbgTripMs / _dbgPollFrames : 0.0;
                    double avgFrames = _dbgTripSampleN > 0 ? (double)_dbgTripSumFrames / _dbgTripSampleN : 0.0;
                    // 隐含速度：Σ(样本数 × 该区平均通勤距离) ÷ Σ(样本数 × 该区平均时长)（各区均值再按样本数加权，
                    // 只是个量级校验 —— 用来判断「帧 → 分钟」的换算合不合理：正常应在步行 5 ~ 开车 60 km/h 之间）
                    // 注意：**直接读样本的 Sum/Count**，不走 GetCommuteTime()（那条带了 ÷8 显示门槛，
                    // 区划没够门槛时是 0，隐含速度就永远算不出来 —— 校验要的是「所有样本」）。
                    double sumMin = 0.0, wDist = 0.0;
                    long nSmp = 0;
                    double[] cDist = GetCommuteDistance();
                    for (int i = 1; i < 256; i++)
                    {
                        CommuteSamples s = _samples[i];
                        if (s == null || s.Count == 0) continue;
                        sumMin += s.Sum; nSmp += s.Count;
                        wDist += cDist[i] * s.Count;   // 距离口径没门槛（有就业居民就有值）
                    }
                    double avgMin = nSmp > 0 ? sumMin / nSmp : 0.0;
                    string spd = (avgMin > 0.0 && wDist > 0.0)
                        ? ((wDist / nSmp) / (avgMin / 60.0)).ToString("0.0") + "km/h" : "—";
                    Debug.Log("[DFM] 通勤跟踪 监视名单=" + _watchCount + " 在途=" + inflight
                        + " 轮询耗时/次≈" + avg.ToString("0.000") + "ms"
                        + " | 留存样本=" + CommuteSampleTotal + " 有样本区划=" + CountSampleDistricts()
                        + " 显示门槛=就业居民数÷" + COMMUTE_SAMPLES_PER_RESIDENT
                        + " 留存上限=就业居民数÷" + COMMUTE_CAP_PER_RESIDENT
                        + " | 这次窗口结算=" + _dbgTripSampleN + "趟 均=" + avgFrames.ToString("0") + "帧"
                        + (_dbgTripSampleN > 0 ? " 范围=" + _dbgTripMinFrames + "~" + _dbgTripMaxFrames + "帧" : "")
                        + " 隐含速度≈" + spd
                        + " | 窗口内 出发=" + _dbgDeparted + " 到达=" + _dbgArrived
                        + " 回家丢弃=" + _dbgDropHome + " 超时丢弃=" + _dbgDropTimeout
                        + " | 位置分布 家=" + _dbgLoc[0] + " 单位=" + _dbgLoc[1] + " 访问=" + _dbgLoc[2]
                        + " 在路上=" + _dbgLoc[3] + " 酒店=" + _dbgLoc[4]
                        + " | 在途平均已跑=" + (_dbgInFlightN > 0 ? _dbgInFlightFrames / _dbgInFlightN : 0) + "帧");
                    _dbgPollFrames = 0; _dbgTripMs = 0.0;
                    _dbgTripSampleN = 0; _dbgTripSumFrames = 0L;
                    _dbgTripMinFrames = int.MaxValue; _dbgTripMaxFrames = 0;
                    _dbgDeparted = 0; _dbgArrived = 0; _dbgDropHome = 0; _dbgDropTimeout = 0;
                    for (int d2 = 0; d2 < 8; d2++) _dbgLoc[d2] = 0;
                    _dbgInFlightFrames = 0L; _dbgInFlightN = 0;
                    _dbgTripLogTime = Time.time;
                }
            }
        }

        /// <summary>
        /// 轮询监视名单，返回**在途**（已出发、还没到单位）的趟数。
        /// 判定只用**这个人自己**的 `Citizen.CurrentLocation`：
        ///   · 上次看到在**家**、这次不在家也不在单位 → **出发**（记出发帧）；
        ///   · 在途 → 变成 `Work` = **到单位**（记一趟样本，时长 = 到达帧 − 出发帧）；
        ///             变回 `Home` = 半路回家（丢弃 —— 不是通勤到岗）；
        ///             超过 `COMMUTE_TRIP_MAX_FRAMES` 还没到 → 丢弃（异常行程，**不记账**）。
        /// ⚠️ 「上次看到还在家」这一条不能省：否则「逛完商店直接去上班」会被当成从商店起算的一趟。
        /// </summary>
        private int PollCommuteWatch(uint frame)
        {
            if (_watch == null || _watchCount == 0) return 0;
            CitizenManager cm = Singleton<CitizenManager>.instance;
            if (cm == null) return 0;
            Citizen[] citizens = cm.m_citizens.m_buffer;
            uint size = (uint)citizens.Length;
            if (_tripStart == null || _tripStart.Length < size || _lastLoc == null || _lastLoc.Length < size)
            {
                _tripStart = new uint[size];
                _lastLoc = new byte[size];
            }
            long[] emp = null;      // 惰性：真要记账时才取（门槛/容量用）
            int inflight = 0;
            for (int k = 0; k < _watchCount; k++)
            {
                uint cid = _watch[k];
                if (cid == 0 || cid >= size) continue;
                byte loc = (byte)citizens[cid].CurrentLocation;
                if (loc < 8) _dbgLoc[loc]++;
                byte last = _lastLoc[cid];
                _lastLoc[cid] = loc;
                uint start = _tripStart[cid];
                if (loc == (byte)Citizen.Location.Work)
                {
                    if (start == 0) continue;                  // 本来就在单位：不是一趟
                    _tripStart[cid] = 0;
                    _dbgArrived++;
                    uint elapsed = frame - start;
                    double minutes = (double)elapsed * REAL_SECONDS_PER_FRAME / 60.0;
                    if (elapsed == 0 || minutes > 300.0) continue;   // 明显不合理的单趟丢掉（与 COMMUTE_TRIP_MAX_FRAMES = 300 分钟一致）
                    int hd = _watchHome[k];
                    if (hd <= 0 || hd >= 256) continue;
                    if (emp == null) emp = GetCommuteCount();
                    // 压进该区划的 FIFO（容量按该区划的就业居民数定；满了淘汰最早的一趟）
                    PushCommuteSample(hd, minutes, emp[hd]);
                    _dbgTripSampleN++; _dbgTripSumFrames += elapsed;
                    if ((int)elapsed < _dbgTripMinFrames) _dbgTripMinFrames = (int)elapsed;
                    if ((int)elapsed > _dbgTripMaxFrames) _dbgTripMaxFrames = (int)elapsed;
                }
                else if (loc == (byte)Citizen.Location.Home)
                {
                    if (start != 0) { _tripStart[cid] = 0; _dbgDropHome++; }   // 半路回家 → 不是到岗
                }
                else
                {
                    // 非家非单位 = **在路上**（走路 / 候车 / 车上 / 换乘 / 停车后步行，全在这一类）
                    if (start == 0)
                    {
                        if (last == (byte)Citizen.Location.Home)
                        {
                            _tripStart[cid] = frame;   // 刚从家出来 → 出发
                            _dbgDeparted++;
                        }
                    }
                    else if (frame - start >= COMMUTE_TRIP_MAX_FRAMES)
                    {
                        _tripStart[cid] = 0; _dbgDropTimeout++;   // 太久没到 → 丢弃（不记账）
                    }
                    else { inflight++; _dbgInFlightFrames += frame - start; _dbgInFlightN++; }
                }
            }
            return inflight;
        }

        /// <summary>清掉所有「在途」的出发记录（**不动已留存样本**）。</summary>
        private void ClearInFlight()
        {
            if (_tripStart != null) System.Array.Clear(_tripStart, 0, _tripStart.Length);
            if (_lastLoc != null) System.Array.Clear(_lastLoc, 0, _lastLoc.Length);
        }

        /// <summary>当前在途（已出发、还没到单位）的趟数。</summary>
        private int InFlightCount()
        {
            if (_watch == null || _tripStart == null) return 0;
            int n = 0;
            for (int k = 0; k < _watchCount; k++)
            {
                uint cid = _watch[k];
                if (cid < _tripStart.Length && _tripStart[cid] != 0) n++;
            }
            return n;
        }

        /// <summary>
        /// 把这一轮收入扫描收集到的**监视名单**发布出去（每轮 ≈ `UpdateInterval×3` 秒一次）。
        /// 用 `_watched` 当戳记（1 = 在名单里、2 = 本轮新进、0 = 不在）：
        ///   · 新进的 ID → **清掉状态**（ID 复用、刚入职，旧状态无意义）；
        ///   · 上一轮有、这一轮没有的 ID（搬走 / 失业 / 换单位）→ **清掉状态**；
        ///   · 两轮都在的 ID → **保留在途状态**（不打断正在跟踪的趟）。
        /// </summary>
        private void PublishCommuteWatch()
        {
            if (_watchPartial == null) return;
            int n = _watchPartial.Count;
            uint[] nw = n > 0 ? _watchPartial.ToArray() : null;
            byte[] nh = n > 0 ? _watchHomePartial.ToArray() : null;
            int size = 0;
            CitizenManager cm = Singleton<CitizenManager>.instance;
            if (cm != null) size = cm.m_citizens.m_buffer.Length;
            if (_tripStart == null || _tripStart.Length < size) { _tripStart = new uint[size]; _lastLoc = new byte[size]; }
            if (_watched == null || _watched.Length < size) _watched = new byte[size];

            for (int k = 0; k < n; k++)                       // ① 新进的：清状态 + 打戳记 2
            {
                uint cid = nw[k];
                if (cid == 0 || cid >= _watched.Length) continue;
                if (_watched[cid] == 0) { _tripStart[cid] = 0; _lastLoc[cid] = 0; }
                _watched[cid] = 2;
            }
            for (int k = 0; k < _watchCount; k++)             // ② 退出的：清状态 + 归零
            {
                uint cid = _watch[k];
                if (cid == 0 || cid >= _watched.Length) continue;
                if (_watched[cid] != 2) { _watched[cid] = 0; _tripStart[cid] = 0; _lastLoc[cid] = 0; }
            }
            for (int k = 0; k < n; k++)                       // ③ 收尾：戳记 2 → 1
            {
                uint cid = nw[k];
                if (cid != 0 && cid < _watched.Length) _watched[cid] = 1;
            }
            _watch = nw; _watchHome = nh; _watchCount = n;
        }

        // ================= 通勤距离 / 本地就业（居住地口径）— 只读接口 =================
        // 两个源数组（_allOd / _allCommuteSum）在建筑分片遍历里累加，这里只做派生。
        // 口径：**按「住在本单元的人」归集**（居住地口径，用户 2026-09-27 定）；
        // 聚合/组合统一放宽为「工作地落在范围内**任一个**成员区划内」就算本地就业。

        /// <summary>每区划的**就业居民数**（按居住地归集）= OD 行和。既是分母，也是聚合时的权重。</summary>
        public long[] GetCommuteCount()
        {
            if (_commuteCount != null && Time.time - _commuteCountTime < CacheLife())
                return _commuteCount;
            long[] r = new long[256];
            int[] od = _allOd;
            if (od != null)
                for (int h = 1; h < 256; h++)
                {
                    int b = h * 256;
                    long n = 0;
                    for (int w = 0; w < 256; w++) n += od[b + w];
                    r[h] = n;
                }
            _commuteCount = r;
            _commuteCountTime = Time.time;
            return r;
        }

        /// <summary>按居住区划的通勤距离之和（米）。只在内部给加权平均用，不对外暴露米制。</summary>
        private double[] GetCommuteSumRaw()
        {
            return _allCommuteSum != null ? _allCommuteSum : new double[256];
        }

        /// <summary>
        /// 平均通勤距离（**km**，居住地口径）= 本区划就业居民的通勤距离和 ÷ 本区划就业居民数。
        /// 没有就业居民 → 0（面板显示 0.00 km）。
        /// </summary>
        public double[] GetCommuteDistance()
        {
            if (_commuteDist != null && Time.time - _commuteDistTime < CacheLife())
                return _commuteDist;
            double[] sum = GetCommuteSumRaw();
            long[] cnt = GetCommuteCount();
            double[] r = new double[256];
            for (int i = 0; i < 256; i++)
                r[i] = cnt[i] > 0 ? sum[i] / cnt[i] / 1000.0 : 0.0;   // 米 → km
            _commuteDist = r;
            _commuteDistTime = Time.time;
            return r;
        }

        /// <summary>
        /// 本地就业率（%，居住地口径）= 工作地也在**本区划**的就业居民 ÷ 本区划就业居民。
        /// 单区划没有「跨成员」可言，就是 OD 对角元；聚合/组合见 GetAggregateLocalEmploymentRate。
        /// </summary>
        public double[] GetLocalEmploymentRate()
        {
            if (_localEmpRate != null && Time.time - _localEmpRateTime < CacheLife())
                return _localEmpRate;
            double[] r = new double[256];
            int[] od = _allOd;
            if (od != null)
            {
                long[] cnt = GetCommuteCount();
                for (int h = 1; h < 256; h++)
                    r[h] = cnt[h] > 0 ? (double)od[h * 256 + h] / cnt[h] * 100.0 : 0.0;
            }
            _localEmpRate = r;
            _localEmpRateTime = Time.time;
            return r;
        }

        /// <summary>
        /// 聚合平均通勤距离（km）= **Σ子树内所有就业居民的距离 ÷ Σ子树内就业居民数** ——
        /// 权重天然就是就业居民数（用户要的「按人口加权」），**不能对子节点的平均值再求平均**。
        /// 结构与 GetAggregateDisposableIncome 一致（未入层级的区划为 0）。
        /// </summary>
        public double[] GetAggregateCommuteDistance()
        {
            if (_aggCommuteDist != null && Time.time - _aggCommuteDistTime < CacheLife())
                return _aggCommuteDist;
            double[] dist = GetCommuteDistance();
            long[] cnt = GetCommuteCount();
            double[] wsum = new double[256];   // 距离 × 人数（可加的加权量）
            long[] selfCnt = new long[256];
            for (int i = 0; i < 256; i++) { wsum[i] = dist[i] * cnt[i]; selfCnt[i] = cnt[i]; }

            double[] aggW = new double[256];
            long[] aggC = new long[256];
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Hierarchy != null)
            {
                // 两趟各用**独立的** visited：HashSet 兼作跨根去重，复用会让第二趟直接读回 0
                var v1 = new HashSet<ushort>();
                foreach (ushort root in hub.Hierarchy.GetRootNodes())
                    ComputeAggregate(root, wsum, aggW, hub.Hierarchy, v1);
                var v2 = new HashSet<ushort>();
                foreach (ushort root in hub.Hierarchy.GetRootNodes())
                    ComputeAggregate(root, selfCnt, aggC, hub.Hierarchy, v2);
            }
            double[] r = new double[256];
            for (int i = 0; i < 256; i++) r[i] = aggC[i] > 0 ? aggW[i] / aggC[i] : 0.0;
            _aggCommuteDist = r;
            _aggCommuteDistTime = Time.time;
            return r;
        }

        /// <summary>
        /// 聚合本地就业率（%）= **居住地与工作地都落在子树内**的就业居民 ÷ 子树内就业居民。
        /// 与单区划的「工作地 == 本区划」不同：子树内跨成员上班也算本地
        /// （用户 2026-09-27 定的聚合规则，组合视图同规则）。
        /// </summary>
        public double[] GetAggregateLocalEmploymentRate()
        {
            if (_aggLocalEmpRate != null && Time.time - _aggLocalEmpRateTime < CacheLife())
                return _aggLocalEmpRate;
            double[] r = new double[256];
            int[] od = _allOd;
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (od != null && hub != null && hub.Hierarchy != null)
            {
                long[] cnt = GetCommuteCount();
                bool[] member = new bool[256];   // 复用同一张成员表（每个节点进来先清空）
                var visited = new HashSet<ushort>();
                foreach (ushort root in hub.Hierarchy.GetRootNodes())
                    AccumLocalEmp(root, od, cnt, member, r, hub.Hierarchy, visited);
            }
            _aggLocalEmpRate = r;
            _aggLocalEmpRateTime = Time.time;
            return r;
        }

        // ================== 「区域工人数」（2026-09-28，键 12 的第二个子模式）==================
        // 口径 = **原版区划面板那一格**（用户 2026-09-28：「直接读取区域面板数据」）。
        // 反汇编 DistrictWorldInfoPanel.UpdateBindings 核实：面板的 m_WorkersAmount 用
        // DISTRICT_COUNTFORMAT 填两个数 ——
        //   · 在岗人数 = 商业 / 工业 / 办公 / 玩家产业四处 `m_finalAliveCount` 之和；
        //   · 岗位容量 = 同四处的 `m_finalHomeOrWorkCount` 之和。
        // 本模组取**在岗人数**（用户 2026-09-28 选定：「实际在岗人数」）。
        // ⚠️ 与原版一致：**不含公共服务建筑的职工**（那四个桶里没有公共服务这一项），
        //    所以它与 `FinanceResult.Workers`（额外加了建筑遍历数出来的 ServiceWorkers）**不是同一个数**——
        //    要跟原版区划面板对得上就用这一个。纯读游戏数据、不做任何遍历。

        /// <summary>各区划的**区域工人数**（自身值，原版区划面板口径）。</summary>
        public double[] GetDistrictPanelWorkers()
        {
            if (_panelWorkers != null && Time.time - _panelWorkersTime < CacheLife())
                return _panelWorkers;
            double[] r = new double[256];
            try
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                if (dm != null)
                {
                    District[] buf = dm.m_districts.m_buffer;
                    uint size = dm.m_districts.m_size;
                    if (buf != null)
                    {
                        uint scan = size < (uint)buf.Length ? size : (uint)buf.Length;
                        for (uint d = 1; d < scan; d++)
                            if ((buf[d].m_flags & District.Flags.Created) != 0)
                                r[d] = GetWorkers(buf[d]);
                    }
                }
            }
            catch { }
            _panelWorkers = r;
            _panelWorkersTime = Time.time;
            return r;
        }

        /// <summary>区域工人数的**聚合**（自身 + 全部下辖，子树求和）。未入层级的区划记 0
        /// —— 与 人口 / GDP 的聚合一致（见 AggPrim 里 zeroIfUnassigned 的说明：弄反会算出假 −100%）。</summary>
        public double[] GetAggregatePanelWorkers()
        {
            if (_aggPanelWorkers != null && Time.time - _aggPanelWorkersTime < CacheLife())
                return _aggPanelWorkers;
            double[] self = GetDistrictPanelWorkers();
            double[] agg = new double[256];
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Hierarchy != null)
            {
                double[] sum = new double[256];
                var visited = new HashSet<ushort>();
                foreach (ushort root in hub.Hierarchy.GetRootNodes())
                    ComputeAggregate(root, self, sum, hub.Hierarchy, visited);
                foreach (ushort id in new List<ushort>(visited)) agg[id] = sum[id];
            }
            _aggPanelWorkers = agg;
            _aggPanelWorkersTime = Time.time;
            return agg;
        }

        /// <summary>
        /// 后序算「聚合本地就业率」：先标记本节点的子树成员，再 Σ_{h,w ∈ 子树} od[h][w] ÷ Σ_{h ∈ 子树} 行和。
        /// 规模 = 节点数 × 成员数²（上限约 128 × 128² ≈ 2 M 次数组读），每 CacheLife 只算一次，可接受。
        /// </summary>
        private static void AccumLocalEmp(ushort d, int[] od, long[] cnt, bool[] member, double[] rate,
            DistrictHierarchy h, HashSet<ushort> visited)
        {
            if (!visited.Add(d)) return;   // 防环（与 ComputeAggregate 同一套）
            for (int i = 0; i < 256; i++) member[i] = false;
            MarkSubtree(d, member, h, new HashSet<ushort>());
            double inside = 0.0;   // 子树内的 OD 对和（居住与工作都在子树内）
            long total = 0;        // 子树内的就业居民数
            for (int hi = 1; hi < 256; hi++)
            {
                if (!member[hi]) continue;
                total += cnt[hi];
                int b = hi * 256;
                for (int wi = 1; wi < 256; wi++)
                    if (member[wi]) inside += od[b + wi];
            }
            rate[d] = total > 0 ? inside / total * 100.0 : 0.0;
            foreach (ushort child in h.GetChildren(d))
                AccumLocalEmp(child, od, cnt, member, rate, h, visited);
        }

        /// <summary>把 d 及其全部下辖在 member 表上标记（防环用独立的 visited）。</summary>
        private static void MarkSubtree(ushort d, bool[] member, DistrictHierarchy h, HashSet<ushort> visited)
        {
            if (!visited.Add(d)) return;
            member[d] = true;
            foreach (ushort child in h.GetChildren(d))
                MarkSubtree(child, member, h, visited);
        }

        // ================= 通勤时间（居住地口径，仅本段用得到 PathUnit / CitizenInstance）=================
        // 数据来源（2026-09-27 用 dnfile 核实过字段）：市民/车辆**正在走的那条路径**存着
        //   `PathUnit.m_length`（米）与 `PathUnit.m_speed` → 帧数 = 长度 ÷ 速度；再按 REAL_SECONDS_PER_FRAME 换算成分钟。
        // 认「这一趟是不是通勤」用 `CitizenInstance` / `Vehicle` 的 `m_sourceBuilding` / `m_targetBuilding`：
        //   一头是住宅（家）、另一头是工作建筑 → 就是通勤，且**整趟时长**都在路径里（不用从头跟到尾）。
        // 归集与另两项一致：**按居住地所在区划**累加。

        /// <summary>
        /// 这一单元攒的样本够不够显示：**样本数 ≥ 就业居民数 ÷ 4**（下限 1 趟），不够记 0
        /// （＝面板上的「无数据」：白色、排最后）。**这是唯一的门槛**（用户 2026-09-27：
        /// 「36000 的限制不要了，就用样本量限制」）。
        /// </summary>
        /// <summary>该区划的**显示门槛**：就业居民数 ÷ 8（下限 1 趟）。判定与「统计进度」共用它。</summary>
        public static long CommuteNeed(long employedResidents)
        {
            long need = employedResidents / COMMUTE_SAMPLES_PER_RESIDENT;
            return need < 1 ? 1 : need;
        }

        /// <summary>样本数够不够显示（不够＝面板上的白色 0「无数据」）。</summary>
        public static bool CommuteSamplesEnough(long samples, long employedResidents)
        {
            return samples >= CommuteNeed(employedResidents);
        }

        /// <summary>
        /// 把留存的通勤样本序列化成文本（每区划一行 `D &lt;id&gt; &lt;count&gt; &lt;v...&gt;`，只写每区划**最近**的
        /// `COMMUTE_PERSIST_MAX` 个，把文件大小按住）。由 Hub 存盘时调用，见 `DistrictDataStore.SaveCommuteSamples`。
        /// </summary>
        public string DumpCommuteSamples()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("# DFM Commute v").Append(COMMUTE_SAMPLE_VERSION).Append('\n');
            for (int i = 1; i < 256; i++)
            {
                CommuteSamples smp = _samples[i];
                if (smp == null || smp.Count == 0 || smp.Buf == null) continue;
                int n = smp.Count;
                int start = (smp.Count == smp.Buf.Length) ? smp.Head : 0;
                int keep = n > COMMUTE_PERSIST_MAX ? COMMUTE_PERSIST_MAX : n;
                sb.Append("D ").Append(i).Append(' ').Append(keep);
                for (int k = n - keep; k < n; k++)
                    sb.Append(' ').Append(smp.Buf[(start + k) % smp.Buf.Length]
                        .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 读回通勤样本（Hub 读档时调用）。坏行/坏数一律跳过；恢复出来的样本按时间顺序进缓冲，
        /// 之后照常按容量淘汰。**不清空已发布结果**（下次 getter 现算）。
        ///
        /// ⚠️ **一次性作废旧口径样本**：老文件（没有版本头）里存的是「最后见到帧 − 起始帧」量出来的时长，
        /// 而那一版几乎整批卡在 12000 帧的硬上限上（实测全是 33.36~33.43 分钟）。结算判据改成
        /// 「到没到单位」之后这些值全无意义，且会继续被平均进去 → 读到老格式一律丢弃，重新攒。
        /// 版本头由 `DumpCommuteSamples` 写在第一行。
        /// </summary>
        public void LoadCommuteSamples(string[] lines)
        {
            if (lines == null) return;
            int fileVer = 0;
            for (int li = 0; li < lines.Length; li++)
            {
                string h = lines[li];
                if (h == null) continue;
                h = h.Trim();
                if (h.Length == 0) continue;
                if (h[0] != '#') break;                       // 头只可能在最前面
                if (h.StartsWith("# DFM Commute v"))
                {
                    int v;
                    if (int.TryParse(h.Substring(15), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out v)) fileVer = v;
                }
            }
            if (fileVer < COMMUTE_SAMPLE_VERSION)
            {
                if (lines.Length > 0)
                    Debug.Log("[DFM] 通勤样本：旧口径（v" + fileVer + " < v" + COMMUTE_SAMPLE_VERSION +
                              "，那批时长卡在硬上限上）→ 全部丢弃，重新攒");
                return;
            }
            int ok = 0;
            for (int li = 0; li < lines.Length; li++)
            {
                string t = lines[li];
                if (t == null) continue;
                t = t.Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                string[] parts = t.Split(' ');
                if (parts.Length < 3 || parts[0] != "D") continue;
                int id, count;
                if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out id)) continue;
                if (!int.TryParse(parts[2], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out count)) continue;
                if (id <= 0 || id >= 256 || count <= 0) continue;
                int avail = parts.Length - 3;
                int n = count < avail ? count : avail;
                if (n <= 0) continue;
                CommuteSamples smp = new CommuteSamples();
                smp.Buf = new double[n];
                smp.Count = n; smp.Head = 0; smp.Sum = 0.0;
                bool bad = false;
                for (int k = 0; k < n; k++)
                {
                    double v;
                    if (!double.TryParse(parts[3 + k], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out v)) { bad = true; break; }
                    smp.Buf[k] = v; smp.Sum += v;
                }
                if (bad) continue;
                _samples[id] = smp;
                ok++;
            }
            _commuteTime = null; _commuteTimeCnt = null; _aggCommuteTime = null;
            _commuteTop = null; _aggCommuteTop = null;
            _commuteProg = null; _aggCommuteProg = null;
            Debug.Log("[DFM] 通勤样本：读回 " + ok + " 个区划（" + CommuteSampleTotal + " 趟）");
        }

        /// <summary>有样本的区划数（调试日志用）。</summary>
        private int CountSampleDistricts()
        {
            int n = 0;
            for (int i = 0; i < 256; i++) if (_samples[i] != null && _samples[i].Count > 0) n++;
            return n;
        }

        /// <summary>
        /// 把一趟样本放进该区划的环形缓冲：没满就追加；满了就**淘汰最早**的一趟再放新的（FIFO）。
        /// 容量 = max(1, 就业居民数 ÷ `COMMUTE_CAP_PER_RESIDENT`)。人变多导致容量变大时会扩容
        /// （按时间顺序把老样本搬到新缓冲）。
        /// </summary>
        private void PushCommuteSample(int hd, double minutes, long employedResidents)
        {
            if (hd <= 0 || hd >= 256) return;
            CommuteSamples smp = _samples[hd];
            if (smp == null) { smp = new CommuteSamples(); _samples[hd] = smp; }
            int cap = (int)(employedResidents / COMMUTE_CAP_PER_RESIDENT);
            if (cap < 1) cap = 1;
            if (smp.Buf == null || smp.Buf.Length < cap)
            {
                double[] nb = new double[cap];
                int n = smp.Count;
                int start = (smp.Buf != null && smp.Count == smp.Buf.Length) ? smp.Head : 0;
                for (int i = 0; i < n; i++) nb[i] = smp.Buf[(start + i) % smp.Buf.Length];
                smp.Buf = nb; smp.Head = n % cap; smp.Count = n;
            }
            if (smp.Count < smp.Buf.Length)
            {
                smp.Buf[smp.Head] = minutes;
                smp.Count++;
                smp.Sum += minutes;
            }
            else
            {
                smp.Sum -= smp.Buf[smp.Head];   // 淘汰最早的那一趟
                smp.Buf[smp.Head] = minutes;
                smp.Sum += minutes;
            }
            smp.Head = (smp.Head + 1) % smp.Buf.Length;
        }

        /// <summary>
        /// 平均通勤时间（**分钟，现实等效**，居住地口径）= 本区划**留存样本**的平均（见 `CommuteSamples`）。
        /// 时间换算：帧 × 10 ÷ 60 ÷ 60（即 60 模拟帧 = 10 秒）。样本不够门槛 / 没有样本 → 0。
        /// </summary>
        public double[] GetCommuteTime()
        {
            if (_commuteTime != null && Time.time - _commuteTimeTime < CacheLife()) return _commuteTime;
            double[] r = new double[256];
            long[] emp = GetCommuteCount();   // 该区划的就业居民数（显示门槛 = 它 ÷ 4）
            for (int i = 1; i < 256; i++)
            {
                CommuteSamples smp = _samples[i];
                if (smp == null || smp.Count == 0) continue;
                if (CommuteSamplesEnough(smp.Count, emp[i])) r[i] = smp.Sum / smp.Count;
            }
            _commuteTime = r;
            _commuteTimeTime = Time.time;
            return r;
        }

        /// <summary>
        /// 「最长 10%」口径（用户 2026-09-27 加，面板第三个按钮「最长10%」）：
        /// 每区划取**留存样本里最大的 10%**（`Count ÷ 10`，至少 1 趟）求平均 ——
        /// 也就是把「这一带最堵/最远的那批通勤」抬出来看，均值会被大量短途稀释掉。
        /// 门槛与均值口径**同一条**（样本数 ≥ 就业居民数 ÷ 8，见 CommuteSamplesEnough）；不足 → 0。
        /// 实现：把该区划的环形缓冲摊平到一个临时数组，`Array.Sort` 升序后取尾部 k 个（k = 10%）。
        /// 代价 = 每区划一次 O(n log n)（n ≤ 就业居民数÷2），且只在缓存过期（CacheLife）时才算一次；
        /// 区划数 ≤ 128、n 通常几十到几千 → 可接受。
        /// </summary>
        public double[] GetCommuteTopTime()
        {
            if (_commuteTop != null && Time.time - _commuteTopTime < CacheLife()) return _commuteTop;
            double[] r = new double[256];
            long[] emp = GetCommuteCount();
            for (int i = 1; i < 256; i++)
            {
                CommuteSamples smp = _samples[i];
                if (smp == null || smp.Count == 0 || smp.Buf == null) continue;
                if (!CommuteSamplesEnough(smp.Count, emp[i])) continue;
                // 摊平成连续数组（环形缓冲的先后顺序对「取最大 k 个」没有意义，照搬 Dump 的取数方式）
                double[] tmp = new double[smp.Count];
                int start = (smp.Count == smp.Buf.Length) ? smp.Head : 0;
                for (int k = 0; k < smp.Count; k++) tmp[k] = smp.Buf[(start + k) % smp.Buf.Length];
                System.Array.Sort(tmp);   // 文件里没 using System，写全名（与 System.DateTime 同一套路）
                int take = smp.Count / 10;
                if (take < 1) take = 1;
                double s = 0.0;
                for (int k = smp.Count - take; k < smp.Count; k++) s += tmp[k];
                r[i] = s / take;
            }
            _commuteTop = r;
            _commuteTopTime = Time.time;
            return r;
        }

        /// <summary>
        /// 「统计进度」百分比（0~100；**-1 = 不适用**）：该区划**已留存趟数 ÷ 显示门槛**（就业居民数÷8）。
        /// 面板在**白色 0（＝还没到门槛）**那一行的数值后面注明它（用户 2026-09-27：「在白色状态的后面
        /// 注明已统计的百分比」），让人知道还要等多久；过了门槛就是 100（那时显示的是数值，不再注）。
        /// </summary>
        public double[] GetCommuteProgress()
        {
            if (_commuteProg != null && Time.time - _commuteProgTime < CacheLife()) return _commuteProg;
            double[] r = new double[256];
            long[] emp = GetCommuteCount();
            for (int i = 1; i < 256; i++)
            {
                if (emp[i] <= 0) { r[i] = -1.0; continue; }      // 没有就业居民 → 这一项对它不适用
                CommuteSamples smp = _samples[i];
                long have = smp != null ? smp.Count : 0;
                long need = CommuteNeed(emp[i]);
                r[i] = have >= need ? 100.0 : (double)have * 100.0 / need;
            }
            _commuteProg = r;
            _commuteProgTime = Time.time;
            return r;
        }

        /// <summary>每区划留存的通勤趟数（聚合加权用）。</summary>
        public long[] GetCommuteTimeCount()
        {
            if (_commuteTimeCnt != null && Time.time - _commuteTimeCntTime < CacheLife()) return _commuteTimeCnt;
            long[] r = new long[256];
            for (int i = 0; i < 256; i++) if (_samples[i] != null) r[i] = _samples[i].Count;
            _commuteTimeCnt = r;
            _commuteTimeCntTime = Time.time;
            return r;
        }

        /// <summary>聚合平均通勤时间（分钟）= Σ子树(时长×趟数) ÷ Σ子树趟数 —— 与通勤距离同一套加权。</summary>
        public double[] GetAggregateCommuteTime()
        {
            if (_aggCommuteTime != null && Time.time - _aggCommuteTimeTime < CacheLife()) return _aggCommuteTime;
            _aggCommuteTime = AggregateTime(GetCommuteTime());
            _aggCommuteTimeTime = Time.time;
            return _aggCommuteTime;
        }

        /// <summary>
        /// 聚合「最长 10%」通勤时间（分钟）：**Σ子树(各区划的 10% 分位值 × 该区划趟数) ÷ Σ子树趟数**。
        /// 与均值口径**同一套加权**（`AggregateTime`），只是把「各区划的值」换成 10% 分位值。
        /// 口径说明：这里**不是**「把所有成员的样本倒在一起再取前 10%」（那需要把子树里几万条样本
        /// 汇总排序，每帧/每次刷新都要做，代价不可接受）；而是「各成员先取自己的 10% 分位，再按趟数加权」。
        /// 两者在成员分布相近时几乎一样，成员之间差别大时前者会更极端一点 —— 用户 2026-09-27 要的是
        /// 「样本各区中前 10% 大的数据」，逐区取分位再加权正是这个说法。
        /// </summary>
        public double[] GetAggregateCommuteTopTime()
        {
            if (_aggCommuteTop != null && Time.time - _aggCommuteTopTime < CacheLife()) return _aggCommuteTop;
            _aggCommuteTop = AggregateTime(GetCommuteTopTime());
            _aggCommuteTopTime = Time.time;
            return _aggCommuteTop;
        }

        /// <summary>
        /// 聚合「统计进度」百分比（子树口径；-1 = 不适用）：**子树样本数 ÷ 子树门槛**（子树就业居民数÷8）。
        /// 与 `GetCommuteProgress` 同一套算法，只是两端的数都换成子树的和（同样走后序求和）。
        /// </summary>
        public double[] GetAggregateCommuteProgress()
        {
            if (_aggCommuteProg != null && Time.time - _aggCommuteProgTime < CacheLife()) return _aggCommuteProg;
            long[] selfCnt = GetCommuteTimeCount();
            long[] aggC = new long[256];
            long[] aggEmp = new long[256];
            long[] empSelf = GetCommuteCount();
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Hierarchy != null)
            {
                var v1 = new HashSet<ushort>();
                foreach (ushort root in hub.Hierarchy.GetRootNodes())
                    ComputeAggregate(root, selfCnt, aggC, hub.Hierarchy, v1);
                var v2 = new HashSet<ushort>();
                foreach (ushort root in hub.Hierarchy.GetRootNodes())
                    ComputeAggregate(root, empSelf, aggEmp, hub.Hierarchy, v2);
            }
            double[] r = new double[256];
            for (int i = 0; i < 256; i++)
            {
                if (aggEmp[i] <= 0) { r[i] = -1.0; continue; }
                long need = CommuteNeed(aggEmp[i]);
                r[i] = aggC[i] >= need ? 100.0 : (double)aggC[i] * 100.0 / need;
            }
            _aggCommuteProg = r;
            _aggCommuteProgTime = Time.time;
            return r;
        }

        /// <summary>
        /// 「各区划一个值 × 该区划趟数」沿层级后序求和再取比值（聚合与组合共用同一套加权逻辑）。
        /// 门槛同样是 and 条件：子树样本数 ≥ 子树就业居民数 ÷ 8，否则 0。
        /// </summary>
        private double[] AggregateTime(double[] valPerDistrict)
        {
            long[] cnt = GetCommuteTimeCount();
            double[] wsum = new double[256];
            long[] selfCnt = new long[256];
            for (int i = 0; i < 256; i++) { wsum[i] = valPerDistrict[i] * cnt[i]; selfCnt[i] = cnt[i]; }
            double[] aggW = new double[256];
            long[] aggC = new long[256];
            long[] aggEmp = new long[256];      // 子树内的就业居民数（门槛 = 它 ÷ 8）
            long[] empSelf = GetCommuteCount();
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Hierarchy != null)
            {
                var v1 = new HashSet<ushort>();
                foreach (ushort root in hub.Hierarchy.GetRootNodes())
                    ComputeAggregate(root, wsum, aggW, hub.Hierarchy, v1);
                var v2 = new HashSet<ushort>();
                foreach (ushort root in hub.Hierarchy.GetRootNodes())
                    ComputeAggregate(root, selfCnt, aggC, hub.Hierarchy, v2);
                var v3 = new HashSet<ushort>();
                foreach (ushort root in hub.Hierarchy.GetRootNodes())
                    ComputeAggregate(root, empSelf, aggEmp, hub.Hierarchy, v3);
            }
            double[] r = new double[256];
            for (int i = 0; i < 256; i++)
                r[i] = CommuteSamplesEnough(aggC[i], aggEmp[i]) ? aggW[i] / aggC[i] : 0.0;   // 同样 and 条件
            return r;
        }

        /// <summary>OD 人数（居住区划 home → 工作区划 work）。组合视图做「成员集内」求和用。</summary>
        public long GetOdCount(ushort home, ushort work)
        {
            int[] od = _allOd;
            if (od == null || home >= 256 || work >= 256) return 0;
            return od[home * 256 + work];
        }

        /// <summary>生态子服务映射回对应的普通子服务（游戏税率表里没有 eco 项）。</summary>
        private static ItemClass.SubService BaseSubService(ItemClass.SubService ss)
        {
            if (ss == ItemClass.SubService.ResidentialLowEco) return ItemClass.SubService.ResidentialLow;
            if (ss == ItemClass.SubService.ResidentialHighEco) return ItemClass.SubService.ResidentialHigh;
            if (ss == ItemClass.SubService.CommercialEco) return ItemClass.SubService.CommercialLow;
            return ss;
        }

        /// <summary>
        /// 取某建筑的游戏税率（百分比，1~29，默认 9）。区划税收政策（±2%）通过 taxation 标志
        /// 交给游戏自己算，不在这里硬编码。
        /// </summary>
        private static int TaxRateOf(EconomyManager em, BuildingInfo info, DistrictPolicies.Taxation taxation)
        {
            if (em == null || info == null) return 9;
            try
            {
                int t = em.GetTaxRate(info.m_class.m_service,
                    BaseSubService(info.m_class.m_subService), info.m_class.m_level, taxation);
                if (t < 0) return 0;
                if (t > 100) return 100;
                return t;
            }
            catch (System.Exception)
            {
                return 9;
            }
        }

        /// <summary>统计某建筑内居住（home=true）或工作（home=false）的市民数。</summary>
        private static int CountInBuilding(Building b, CitizenUnit[] units, Citizen[] citizens, uint citizenSize, ushort buildingId, bool home)
        {
            int n = 0;
            uint unit = b.m_citizenUnits;
            int guard = 0;
            while (unit != 0 && guard++ < 4096)
            {
                CitizenUnit u = units[unit];
                for (int j = 0; j < 5; j++)
                {
                    uint cid = u.GetCitizen(j);
                    if (cid != 0 && cid < citizenSize)
                    {
                        if (home ? citizens[cid].m_homeBuilding == buildingId
                                 : citizens[cid].m_workBuilding == buildingId)
                            n++;
                    }
                }
                unit = u.m_nextUnit;
            }
            return n;
        }

        /// <summary>
        /// 遍历所有建筑，统计位于本区划内的工作人数。
        /// 工作场所建筑（商业/工业/办公）的 CitizenUnit 链中同时挂有 Work 单元（本楼员工）
        /// 与 Visit 单元（来访顾客/游客），因此逐市民核对 m_workBuilding == 本建筑，
        /// 只统计真正在此上班的市民。逐单位遍历 m_nextUnit 链表，检查 5 个槽位。
        /// </summary>
        private static int CountWorkers(DistrictManager dm, ushort districtId)
        {
            int workers = 0;
            try
            {
                BuildingManager bm = Singleton<BuildingManager>.instance;
                if (bm == null) return 0;
                CitizenManager cm = Singleton<CitizenManager>.instance;
                if (cm == null) return 0;

                Building[] buf = bm.m_buildings.m_buffer;
                CitizenUnit[] units = cm.m_units.m_buffer;
                Citizen[] citizens = cm.m_citizens.m_buffer;
                uint size = bm.m_buildings.m_size;
                uint citizenSize = (uint)citizens.Length;
                byte target = (byte)districtId;

                for (uint i = 1; i < size; i++)
                {
                    Building b = buf[i];
                    if ((b.m_flags & Building.Flags.Created) == 0) continue;

                    BuildingInfo info = b.Info;
                    if (info == null || !IsWorkplace(info.m_class)) continue;
                    if (dm.GetDistrict(b.m_position) != target) continue;

                    uint unit = b.m_citizenUnits;
                    int guard = 0;
                    while (unit != 0 && guard++ < 4096)
                    {
                        CitizenUnit u = units[unit];
                        for (int j = 0; j < 5; j++)
                        {
                            uint cid = u.GetCitizen(j);
                            if (cid != 0 && cid < citizenSize
                                && citizens[cid].m_workBuilding == (ushort)i)
                                workers++;
                        }
                        unit = u.m_nextUnit;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[DFM] CountWorkers failed: " + ex.Message);
            }
            return workers;
        }

        /// <summary>
        /// 统计区划内公共服务建筑 + 公园建筑的维护费合计。
        /// 公园区划（Parklife / 园区 / 机场 / 步行街等 m_parks 特殊区划）内的建筑不计。
        /// </summary>
        private static long CountExpenses(DistrictManager dm, ushort districtId)
        {
            long expense = 0;
            try
            {
                BuildingManager bm = Singleton<BuildingManager>.instance;
                if (bm == null) return 0;

                Building[] buf = bm.m_buildings.m_buffer;
                uint size = bm.m_buildings.m_size;
                byte target = (byte)districtId;

                for (uint i = 1; i < size; i++)
                {
                    Building b = buf[i];
                    if ((b.m_flags & Building.Flags.Created) == 0) continue;

                    BuildingInfo info = b.Info;
                    if (info == null || !IsExpenseBuilding(info.m_class)) continue;
                    if (dm.GetDistrict(b.m_position) != target) continue;
                    if (dm.GetPark(b.m_position) != 0) continue; // 公园区划不统计

                    expense += GetBaseMaintenanceCost(info);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[DFM] CountExpenses failed: " + ex.Message);
            }
            return expense;
        }

        /// <summary>
        /// 读取建筑的基础维护费（m_maintenanceCost），未乘预算/政策系数。
        /// 与各建筑面板声明的维护费一致，避免 GetMaintenanceCost() 运行时按预算/DLC 放大。
        /// </summary>
        private static int GetBaseMaintenanceCost(BuildingInfo info)
        {
            PlayerBuildingAI ai = info.m_buildingAI as PlayerBuildingAI;
            return ai != null ? ai.m_maintenanceCost : 0;
        }

        /// <summary>提供就业岗位的分区建筑：商业/工业/办公。</summary>
        private static bool IsWorkplace(ItemClass ic)
        {
            return ic.m_service == ItemClass.Service.Commercial
                || ic.m_service == ItemClass.Service.Industrial
                || ic.m_service == ItemClass.Service.Office;
        }

        /// <summary>计入支出的建筑：公园（Beautification）或公共服务（消防/警察/医疗/教育/垃圾/灾害）。</summary>
        private static bool IsExpenseBuilding(ItemClass ic)
        {
            if (ic.m_service == ItemClass.Service.Beautification) return true; // 公园建筑
            switch (ic.m_service)
            {
                case ItemClass.Service.Garbage:
                case ItemClass.Service.HealthCare:
                case ItemClass.Service.PoliceDepartment:
                case ItemClass.Service.Education:
                case ItemClass.Service.FireDepartment:
                case ItemClass.Service.Disaster:
                    return true;
            }
            return false;
        }
    }
}
