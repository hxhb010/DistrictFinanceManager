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
            _districtArea = null;
            _districtAreaTime = 0f;
            _builtDelta = null;
            _builtDeltaTime = 0f;
            _districtBuiltArea = null;
            _districtBuiltAreaTime = 0f;
            _districtBuiltWeightArea = null;
            _districtBuiltWeightAreaTime = 0f;
            // ⚠️ 这两个是「清零 + 记时间戳」型缓存，时间戳必须用 -∞ 而不是 0f：
            // 判据是 `Time.time - 时间戳 < CacheLife()`，开局 10 秒内 Time.time < 10，
            // 写成 0f 会让刚清零的值被当成有效缓存返回 0（工业/玩家/服务的 GDP、平均地价一起读 0）。
            _totalCityGDP = 0;
            _totalCityGDPTime = float.NegativeInfinity;
            _avgLandValue = 0;
            _avgLandValueTime = float.NegativeInfinity;
            _districtIncomeNum = null;
            _districtIncomeNumTime = 0f;
            // ⚠️ 以下都是**建筑派生统计**，与设置/权重无关，绝不能在 ClearCache 里清：
            //   _allDensity（密度分桶 + GDP 分子） / _allBuiltCells（建成区） /
            //   _allBuiltWeightCells（加权建成区） / _allIncome（收入分子）
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
        private static double LandMultForCalc()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Settings != null)
            {
                if (hub.Settings.DisplayMode == 2) return 420.0;
                if (hub.Settings.DisplayMode == 3) return 60.0;
            }
            return 1.0;
        }

        /// <summary>周期周数：年化(1/2/3) = 52 周；周化(0) = 1 周。</summary>
        private static int PeriodWeeksForCalc()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Settings != null && hub.Settings.DisplayMode != 0) return 52;
            return 1;
        }

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

        /// <summary>现实化数据换算系数（GDP/人均）：0 原版按周×1，1 原版按年×52，2 人民币×2625，3 美元×375。（地价另按 ×420/×60 换算）</summary>
        public static double GetDisplayFactor()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Settings != null)
            {
                switch (hub.Settings.DisplayMode)
                {
                    case 1: return 52.0;
                    case 2: return 2625.0; // 人民币年化
                    case 3: return 375.0;  // 美元年化
                }
            }
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
        /// 计入收入统计的公共服务建筑：公园(Beautification)、垃圾、医疗、警察、教育、消防、灾害，
        /// 以及大学 DLC(PlayerEducation) 与博物馆/大学体育(Museums/VarsitySports)。
        /// 刻意与 IsExpenseBuilding 分开：那个用于「支出」统计，改它会变动既有数值。
        /// </summary>
        private static bool IsServiceWorkplace(ItemClass.Service svc)
        {
            if (svc == ItemClass.Service.Beautification) return true;   // 公园建筑
            switch (svc)
            {
                case ItemClass.Service.Garbage:
                case ItemClass.Service.HealthCare:
                case ItemClass.Service.PoliceDepartment:
                case ItemClass.Service.Education:
                case ItemClass.Service.FireDepartment:
                case ItemClass.Service.Disaster:
                case ItemClass.Service.PlayerEducation:
                case ItemClass.Service.Museums:
                case ItemClass.Service.VarsitySports:
                    return true;
            }
            return false;
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
                }
                unit = u.m_nextUnit;
            }
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
