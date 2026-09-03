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
            public long Area;             // 本区划面积（64 m² 格数）
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
            public long AggArea;          // 聚合面积（含下辖所有子区划，64 m² 格数）
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
            _totalCityGDP = 0;
            _totalCityGDPTime = 0f;
            _avgLandValue = 0;
            _avgLandValueTime = 0f;
            _allDensity = null;
            _allDensityTime = 0f;
        }

        private static ushort _logDistrict;
        private static string _logDiag;

        private double _totalCityGDP;
        private float _totalCityGDPTime;
        private double[] _districtGDP;
        private float _districtGDPTime;
        private long[] _districtPop;
        private float _districtPopTime;
        private long[] _districtArea;
        private float _districtAreaTime;
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
                District dd = dbuf[d];
                long land = dd.m_groundData.m_finalLandvalue;
                long pop = dd.m_populationData.m_finalCount;
                gdp[d] = CalcGDP(dd, (int)land, (int)pop, ComW(dd), IndW(dd), OffW(dd), PlayerW(dd));
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
        /// 聚合人口加权地价：区划自身 + 全部下辖，按各成员人口加权平均地价。
        /// （单区划 = 自身地价；有下辖 = 加权平均）
        /// </summary>
        public double[] GetAggregateLandValue()
        {
            long[] selfLong = GetDistrictLandValue();
            double[] self = new double[256];
            for (int i = 0; i < 256; i++) self[i] = selfLong[i];
            long[] area = GetDistrictArea();
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            double[] agg = new double[256];
            if (hub == null || hub.Hierarchy == null) return self;

            foreach (ushort root in hub.Hierarchy.GetRootNodes())
                ComputeWeightedLand(root, self, area, agg, hub.Hierarchy, new HashSet<ushort>());
            DistrictManager dm = Singleton<DistrictManager>.instance;
            if (dm != null)
            {
                District[] dbuf = dm.m_districts.m_buffer;
                uint dsize = dm.m_districts.m_size;
                for (uint d = 1; d < dsize; d++)
                    if ((dbuf[d].m_flags & District.Flags.Created) != 0 && agg[d] == 0)
                        agg[d] = self[d];
            }
            return agg;
        }

        /// <summary>区划精确面积：遍历区划网格逐格统计格数（缓存 CACHE_LIFE）。</summary>
        public long[] GetDistrictArea()
        {
            if (_districtArea != null && Time.time - _districtAreaTime < CacheLife())
                return _districtArea;
            long[] cnt = new long[256];
            try
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                if (dm == null) return cnt;
                DistrictManager.Cell[] grid = dm.m_districtGrid;
                if (grid == null) return cnt;
                for (int i = 0; i < grid.Length; i++)
                {
                    DistrictManager.Cell c = grid[i];
                    cnt[c.m_district1]++;
                    cnt[c.m_district2]++;
                    cnt[c.m_district3]++;
                    cnt[c.m_district4]++;
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

        private static double ComputeWeightedLand(ushort d, double[] self, long[] weight, double[] agg, DistrictHierarchy h, HashSet<ushort> visited)
        {
            if (!visited.Add(d)) return agg[d];
            double landSum = self[d] * System.Math.Max(1, weight[d]);
            double wSum = System.Math.Max(1, weight[d]);
            CollectWeighted(d, self, weight, ref landSum, ref wSum, h, new HashSet<ushort>());
            double r = wSum > 0 ? landSum / wSum : self[d];
            agg[d] = r;
            return r;
        }

        private static void CollectWeighted(ushort d, double[] self, long[] weight, ref double landSum, ref double wSum, DistrictHierarchy h, HashSet<ushort> visited)
        {
            if (!visited.Add(d)) return;
            foreach (ushort child in h.GetChildren(d))
            {
                landSum += self[child] * System.Math.Max(1, weight[child]);
                wSum += System.Math.Max(1, weight[child]);
                CollectWeighted(child, self, weight, ref landSum, ref wSum, h, visited);
            }
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
                DensityData den;
                if (!GetAllDensity().TryGetValue(districtId, out den)) den = new DensityData();
                r.ResLow = den.ResLow;
                r.ResHigh = den.ResHigh;
                r.ComLow = den.ComLow;
                r.ComHigh = den.ComHigh;
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

                r.Area = GetDistrictArea()[districtId]; // 面积（64 m² 格数，逐格统计缓存）

                r.GDP = CalcGDP(d, r.LandValue, r.Population,
                    r.ComWorkers, r.IndWorkers, r.OffWorkers, r.PlayerWorkers);
                // r.Expense = CountExpenses(dm, districtId); // 支出暂时注释掉

                r.Diag = "地价=" + r.LandValue + " 平均地价=" + GetAverageLandValue().ToString("0.00") +
                    " 住低=" + r.ResLow + " 住高=" + r.ResHigh + " 商低=" + r.ComLow + " 商高=" + r.ComHigh +
                    " 工=" + r.IndWorkers + " 办=" + r.OffWorkers + " 玩=" + r.PlayerWorkers;

                // 自身也计入合计
                r.AggGDP = r.GDP;
                r.AggPopulation = r.Population;
                r.AggWorkers = r.Workers;
                r.AggBuildings = r.BuildingCount;
                r.AggArea = r.Area;
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
                District dd = dbuf[d];
                long land = dd.m_groundData.m_finalLandvalue;
                long pop = dd.m_populationData.m_finalCount;
                total += CalcGDP(dd, (int)land, (int)pop, ComW(dd), IndW(dd), OffW(dd), PlayerW(dd));
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
        /// 加权 GDP = 地价 ×（居民×a + 工人×b），其中 b = ratio×a 且 a + b = 2。
        /// a+b=2 保持与旧模型（居民+工人）相同的量级；ratio 为「工人产出/居民」比值
        /// （默认 2.5，可在选项里 0.5~5 调整）。
        /// </summary>
        private static double CalcGDP(District d, int landValue, int population,
            int comWorkers, int indWorkers, int offWorkers, int playerWorkers)
        {
            double resW = GetResWeight();
            double worW = GetWorkWeight();

            double gdp = population * landValue * resW; // 居民×区域地价×居民权重

            // 工业：农业/林业区划用区域地价×居民权重，否则用全地图平均地价×工人权重
            bool agri = (d.m_specializationPolicies & DistrictPolicies.Specialization.Farming) != 0
                     || (d.m_specializationPolicies & DistrictPolicies.Specialization.Forest) != 0;
            double avgLand = GetAverageLandValue();
            if (agri)
                gdp += indWorkers * avgLand * (worW / 3.0);   // 农林：平均地价×(工人权重÷3)
            else
                gdp += indWorkers * avgLand * worW;   // 其他工业：平均地价×工人权重

            // 商业+办公工人 × 区域地价 × 工人权重
            gdp += (comWorkers + offWorkers) * landValue * worW;

            // 玩家（工业等）按一般工业处理：全地图平均地价 × 工人权重
            gdp += playerWorkers * GetAverageLandValue() * worW;

            return gdp * GetDisplayFactor();
        }

        private Dictionary<ushort, DensityData> _allDensity;
        private float _allDensityTime;
        private bool _densityBuilding;
        private uint _densityProgress;
        private uint _densityPerTick;
        private uint _densityTotal;
        private Dictionary<ushort, DensityData> _densityPartial;
        private static readonly Dictionary<ushort, DensityData> _emptyDensity = new Dictionary<ushort, DensityData>();

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

        private static int ComW(District d) { return (int)d.m_commercialData.m_finalAliveCount; }
        private static int IndW(District d) { return (int)d.m_industrialData.m_finalAliveCount; }
        private static int OffW(District d) { return (int)d.m_officeData.m_finalAliveCount; }
        private static int PlayerW(District d) { return (int)d.m_playerData.m_finalAliveCount; }

        private static double GetResWeight()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null) return hub.GetEffectiveResWeight();
            return 0.5;
        }

        private static double GetWorkWeight()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null) return hub.GetEffectiveWorkWeight();
            return 3.0;
        }

        /// <summary>现实化数据换算系数（GDP/人均）：0 原版按周×1，1 原版按年×52，2 人民币×3500，3 美元×500。（地价另按 ×420/×60 换算）</summary>
        public static double GetDisplayFactor()
        {
            DistrictFinanceHub hub = DistrictFinanceHub.Instance;
            if (hub != null && hub.Settings != null)
            {
                switch (hub.Settings.DisplayMode)
                {
                    case 1: return 52.0;
                    case 2: return 3500.0;
                    case 3: return 500.0;
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
        }

        /// <summary>返回分片遍历构建的密度数据（由 Hub 每帧调用 TickDensityBuild 逐步填充）。</summary>
        private Dictionary<ushort, DensityData> GetAllDensity()
        {
            return _allDensity ?? _emptyDensity;
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
                    _densityTotal = bm.m_buildings.m_size;
                    _densityProgress = 1;
                    _densityPerTick = System.Math.Max(1u, _densityTotal / (uint)period);
                    _densityPartial = new Dictionary<ushort, DensityData>();
                    _densityBuilding = true;
                }

                uint end = System.Math.Min(_densityProgress + _densityPerTick, _densityTotal);
                ProcessDensityRange(_densityProgress, end);
                _densityProgress = end;

                if (_densityProgress >= _densityTotal)
                {
                    _allDensity = _densityPartial;
                    _allDensityTime = Time.time;
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

            for (uint i = start; i < end; i++)
            {
                Building b = buf[i];
                if ((b.m_flags & Building.Flags.Created) == 0) continue;
                BuildingInfo info = b.Info;
                if (info == null) continue;
                byte d = dm.GetDistrict(b.m_position);
                if (d == 0) continue;

                DensityData dd;
                if (!_densityPartial.TryGetValue(d, out dd)) dd = new DensityData();

                if (info.m_class.m_service == ItemClass.Service.Residential)
                {
                    int n = CountInBuilding(b, units, citizens, citizenSize, (ushort)i, true);
                    bool low = info.m_class.m_subService == ItemClass.SubService.ResidentialLow;
                    if (low) dd.ResLow += n; else dd.ResHigh += n;
                }
                else if (info.m_class.m_service == ItemClass.Service.Commercial)
                {
                    int n = CountInBuilding(b, units, citizens, citizenSize, (ushort)i, false);
                    bool low = info.m_class.m_subService == ItemClass.SubService.CommercialLow;
                    if (low) dd.ComLow += n; else dd.ComHigh += n;
                }
                _densityPartial[d] = dd;
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
