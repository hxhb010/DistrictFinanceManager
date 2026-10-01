using System.Collections.Generic;

namespace DistrictFinanceManager
{
    /// <summary>
    /// 周度时间序列数据库（内存模型 + 查询 API）。
    /// 按原生游戏「周」为每个原生区划记录一条 FinanceResult 全字段快照；
    /// 主键 = 真实游戏周号，**同一周重复记录时以后写的为准（覆盖）** ——
    /// 这样回档重播会覆盖对应周、再回原存档也不会出现空档/重复。
    /// 持久化见 DistrictSeriesStore（.series 文本，追加式，读取时同周取最后一段）。
    /// </summary>
    public class DistrictSeriesDB
    {
        /// <summary>持久化字段顺序 —— 改动必须同步升版本号（见 DistrictSeriesStore.SeriesVersion）。</summary>
        public static readonly string[] FIELDS =
        {
            // 自身（22）
            "GDP", "Population", "Workers", "LandValue", "BuildingCount", "Area",
            "ResPop", "ComWorkers", "IndWorkers", "OffWorkers", "PlayerWorkers",
            "ResLow", "ResHigh", "ComLow", "ComHigh",
            "ResLowGDP", "ResHighGDP", "ComLowGDP", "ComHighGDP", "IndGDP", "OffGDP", "PlayerGDP",
            // 聚合（21）
            "AggGDP", "AggPopulation", "AggWorkers", "AggBuildings", "AggArea",
            "AggResPop", "AggComWorkers", "AggIndWorkers", "AggOffWorkers", "AggPlayerWorkers",
            "AggResLow", "AggResHigh", "AggComLow", "AggComHigh",
            "AggResLowGDP", "AggResHighGDP", "AggComLowGDP", "AggComHighGDP", "AggIndGDP", "AggOffGDP", "AggPlayerGDP",
            // 追加（index 43；改动须升 SeriesVersion）
            "BuiltArea",
            // 追加（index 44）
            "DisposableIncome",
            // 追加（index 45）—— 「建筑价值增量」的基准面积（原始面积×用途权重）。
            // 老档没有这一列 → GetValue 返回 0 → 那些周自动被当作无效基准跳过（冷处理），
            // 不需要任何迁移；等新的周攒起来，增量自然就有基准了。
            "BuiltValueArea",
            // 追加（index 46~48，2026-09-27）—— 三项通勤指标（都按**居住地口径**，见计算器的通勤三兄弟）。
            // 老档没有这三列 → 读出 0；通勤时间在预热期内也是 0（＝「该周无数据」，与既有约定一致）。
            // 三个值都是**与货币/周期无关**的原始量（km / % / 分钟），入库不需要任何换算。
            "CommuteDistance",   // 平均通勤距离（km，住址↔工作地直线）
            "LocalEmployment",   // 本地就业率（%）
            "CommuteTime",       // 平均通勤时间（分钟，整趟跟踪）
            // 追加（index 49，2026-09-28）—— 「区域工人数」（键 12 第二个子模式，**原版区划面板口径**：
            // 商业/工业/办公/玩家产业四处 m_finalAliveCount 之和，不含公共服务职工）。
            // 只给「区域工人数」的增速当基准用；老档没有这一列 → 读出 0 → 那几周自动当无效基准跳过（冷处理）。
            "PanelWorkers"
        };

        // ================= 存储口径（最重要的不变量）=================
        //
        // 库里**所有列一律是原始值**，显示系数一概不进库 —— 与人口 / 地价 / 人均可支配那几列的老约定统一。
        // 唯一的例外来源是 GDP / AggGDP：实时 getter（`CalcGDP`）把显示系数（货币 × 周期）乘进去了，
        // 所以 `ToRow` 在**入库时除回来**（库里是原始克朗/周），谁读谁照原样用。
        //
        // 好处：**统计模式（货币 / 周期）怎么改都不动历史数据**。系数只在显示层现乘
        // （面板的 `IncomeToDisplay` / `LandToDisplay` / GDP 图例档位等）；跨时间比较（增速）
        // 则两端现乘**同一个**系数，比值里自然约掉 —— 所以增速与货币、周期都无关，只有窗口 N 随周期变。
        //
        // ⚠️ 不要再改成「库跟着统计模式走」（v5 就是这么干的：一改模式就整库 ×新÷旧 并重写文件）。
        //    用户 2026-09-27 明确否掉：显示设置不该写进历史数据，而且每次改模式都要重写存档侧文件。
        // ==========================================================

        /// <summary>已有的周号（唯一，升序）。</summary>
        public readonly List<uint> Weeks = new List<uint>();
        /// <summary>周号 → 该周日期（原版日历 ticks，仅供显示）。</summary>
        public readonly Dictionary<uint, long> WeekDateTicks = new Dictionary<uint, long>();
        /// <summary>区划 ID → 名称（随采样更新）。</summary>
        public readonly Dictionary<ushort, string> Names = new Dictionary<ushort, string>();

        private readonly Dictionary<uint, Dictionary<ushort, double[]>> _weekRows =
            new Dictionary<uint, Dictionary<ushort, double[]>>();

        /// <summary>自上次落盘后被写入/覆盖的周（Flush 时追加这些）。</summary>
        public readonly List<uint> PendingWeeks = new List<uint>();
        /// <summary>已落盘的区划名（避免重复写 N 行）。</summary>
        public readonly Dictionary<ushort, string> WrittenNames = new Dictionary<ushort, string>();

        public int WeekCount { get { return Weeks.Count; } }
        public bool HasAny { get { return Weeks.Count > 0; } }

        /// <summary>已有记录的最大周号（无记录返回 0）。</summary>
        public uint LastWeek() { return Weeks.Count > 0 ? Weeks[Weeks.Count - 1] : 0u; }

        public bool HasWeek(uint week) { return _weekRows.ContainsKey(week); }

        /// <summary>
        /// 把一个区划从周库里彻底抹掉（所有周的该行 + 名字缓存），返回抹掉的行数。
        /// 用于 **ID 复用**：原版区划被删后 ID 会被回收，玩家新画的区划可能拿到同一个 ID，
        /// 不该继承旧主人的历史（否则它的增量/增速会拿完全无关的另一块地当基准）。
        /// 注意：只改内存。追加式文件里的旧行还在，需要配合 DistrictSeriesStore.Rewrite 才能落盘生效。
        /// </summary>
        public int DropDistrict(ushort id)
        {
            int n = 0;
            foreach (KeyValuePair<uint, Dictionary<ushort, double[]>> kv in _weekRows)
                if (kv.Value.Remove(id)) n++;
            Names.Remove(id);
            WrittenNames.Remove(id); // 让下次 Flush 重写它的 N 行
            return n;
        }

        /// <summary>
        /// 丢弃所有 &gt; week 的记录，返回丢弃的周数。读档时调用：
        /// 玩家没点保存就退出的话，那些周的记录仍留在（追加式的）文件里，但重新读档后
        /// 游戏周已回到存档点之前 —— 这些"未来周"属于另一条时间线，不该参与任何统计。
        /// 注意：不写回文件（文件仍保留它们），靠每次读档重新丢弃来保证不生效。
        /// </summary>
        public int DropWeeksAfter(uint week)
        {
            int n = 0;
            for (int i = Weeks.Count - 1; i >= 0; i--)   // Weeks 升序，从尾部丢
            {
                uint w = Weeks[i];
                if (w <= week) break;
                _weekRows.Remove(w);
                WeekDateTicks.Remove(w);
                while (PendingWeeks.Remove(w)) { }       // 别把丢弃的周再写一遍
                Weeks.RemoveAt(i);
                n++;
            }
            return n;
        }

        /// <summary>
        /// 把一条 FinanceResult 抽成定序数值行。**一律存原始值**：GDP / AggGDP 在实时端带显示系数
        /// （`CalcGDP` 里乘了 `GetDisplayFactor()` = 货币 × 周期），这里除回去；其余列本来就是原始值。
        /// 见类顶部的「存储口径」。
        /// </summary>
        public static double[] ToRow(DistrictFinanceCalculator.FinanceResult r)
        {
            return ToRow(r, 0.0, 0.0, 0.0, 0.0);
        }

        /// <summary>
        /// 带通勤三指标的版本（周采样走这个）：三项都按**居住地**归到区划，所以由调用方按区划取值传进来
        /// （见 Hub.SampleWeek）。它们与货币/周期无关，入库不做任何换算。
        /// </summary>
        public static double[] ToRow(DistrictFinanceCalculator.FinanceResult r,
            double commuteDistanceKm, double localEmploymentPct, double commuteTimeMin)
        {
            return ToRow(r, commuteDistanceKm, localEmploymentPct, commuteTimeMin, 0.0);
        }

        /// <summary>完整版：额外带上「区域工人数」（周库 v8 的第 50 列，只给这一项的增速当基准）。</summary>
        public static double[] ToRow(DistrictFinanceCalculator.FinanceResult r,
            double commuteDistanceKm, double localEmploymentPct, double commuteTimeMin, double panelWorkers)
        {
            // 1 ÷ 显示系数（GDP 列专用）。拿不到设置（=1）或异常值时不换算，宁可少做动作。
            double f = DistrictFinanceCalculator.GetDisplayFactor();
            double g = f > 0.0 ? 1.0 / f : 1.0;
            double[] v = new double[FIELDS.Length];
            v[0] = r.GDP * g; v[1] = r.Population; v[2] = r.Workers; v[3] = r.LandValue; v[4] = r.BuildingCount; v[5] = r.Area;
            v[6] = r.ResPop; v[7] = r.ComWorkers; v[8] = r.IndWorkers; v[9] = r.OffWorkers; v[10] = r.PlayerWorkers;
            v[11] = r.ResLow; v[12] = r.ResHigh; v[13] = r.ComLow; v[14] = r.ComHigh;
            v[15] = r.ResLowGDP; v[16] = r.ResHighGDP; v[17] = r.ComLowGDP; v[18] = r.ComHighGDP;
            v[19] = r.IndGDP; v[20] = r.OffGDP; v[21] = r.PlayerGDP;
            v[22] = r.AggGDP * g; v[23] = r.AggPopulation;   // AggGDP 同样是 GDP 口径 → 一起除回
            v[24] = r.AggWorkers; v[25] = r.AggBuildings; v[26] = r.AggArea;
            v[27] = r.AggResPop; v[28] = r.AggComWorkers; v[29] = r.AggIndWorkers; v[30] = r.AggOffWorkers; v[31] = r.AggPlayerWorkers;
            v[32] = r.AggResLow; v[33] = r.AggResHigh; v[34] = r.AggComLow; v[35] = r.AggComHigh;
            v[36] = r.AggResLowGDP; v[37] = r.AggResHighGDP; v[38] = r.AggComLowGDP; v[39] = r.AggComHighGDP;
            v[40] = r.AggIndGDP; v[41] = r.AggOffGDP; v[42] = r.AggPlayerGDP;
            v[43] = r.BuiltArea;          // 建成区面积（面板显示口径：含空隙系数、上限=区划面积）
            v[44] = r.DisposableIncome;   // 人均可支配周收入（克朗/周，原版口径）
            v[45] = r.BuiltValueArea;     // 增量基准面积（原始面积×用途权重）
            v[46] = commuteDistanceKm;    // 平均通勤距离（km，居住地口径）
            v[47] = localEmploymentPct;   // 本地就业率（%）
            v[48] = commuteTimeMin;       // 平均通勤时间（分钟，整趟跟踪；预热期内是 0）
            v[49] = panelWorkers;         // 区域工人数（v8；原版区划面板口径的人数，只用于增速基准）
            return v;
        }

        /// <summary>补空档时最多补多少周（一周 46 列 × 区划数，别补出天量）。</summary>
        private const long MAX_GAP_FILL_WEEKS = 520;

        /// <summary>
        /// 补齐「模组被卸下、过一阵又装回来」造成的空档（用户 2026-09-27）：在**卸下前最后一有记录的周**
        /// 与**装回来后的第一周**之间，按区划**逐列线性插值**，把中间那些周补进周库。
        ///
        /// 为什么需要：空档周不存在的话，增速/增量这类「取 N 周前那周当基准」的统计会**跨过空档**去拿更早的周，
        /// 于是「近 N 周」实际上测的是「近 N + 空档」周，数值会莫名其妙地大或小。补上插值周之后窗口含义就对了。
        ///
        /// 规则：
        ///   · 只对**两端都 &gt; 0** 的列插值；任一端是 0（＝那一端没数据，例如被清洗过的老 GDP）就留 0 ——
        ///     不无中生有（从 0 拉一条斜坡会造出假增长）；
        ///   · 只对**两端都出现**的区划补（新画/已删的区划各按各的，不硬凑）；
        ///   · 周行的日期按两端线性插值，这样周库里那一列时间也是连续的；
        ///   · 补出来的周照常进 `PendingWeeks`，会被 `Flush` 落盘。
        /// 返回补出来的周数。
        /// </summary>
        public int FillGapWeeks(uint firstNewWeek, Dictionary<ushort, double[]> newRows)
        {
            if (newRows == null || newRows.Count == 0) return 0;
            uint lastWeek = LastWeek();
            if (lastWeek == 0 || firstNewWeek <= lastWeek + 1) return 0;   // 没有空档
            Dictionary<ushort, double[]> beforeRows = RowsOf(lastWeek);
            if (beforeRows == null || beforeRows.Count == 0) return 0;
            long gap = (long)firstNewWeek - (long)lastWeek - 1;
            if (gap > MAX_GAP_FILL_WEEKS) gap = MAX_GAP_FILL_WEEKS;

            long t0 = 0L, t1 = 0L;
            WeekDateTicks.TryGetValue(lastWeek, out t0);
            WeekDateTicks.TryGetValue(firstNewWeek, out t1);

            int filled = 0;
            for (long k = 1; k <= gap; k++)
            {
                uint wk = (uint)(lastWeek + k);
                double f = (double)k / (double)(gap + 1);          // 插值系数（0~1 之间）
                Dictionary<ushort, double[]> rows = new Dictionary<ushort, double[]>();
                foreach (KeyValuePair<ushort, double[]> kv in beforeRows)
                {
                    double[] after;
                    if (!newRows.TryGetValue(kv.Key, out after)) continue;   // 这一端没有这个区划 → 不补
                    double[] b = kv.Value;
                    if (b == null || after == null) continue;
                    int n = b.Length < after.Length ? b.Length : after.Length;
                    double[] r = new double[n];
                    for (int i = 0; i < n; i++)
                        r[i] = (b[i] > 0.0 && after[i] > 0.0) ? b[i] + (after[i] - b[i]) * f : 0.0;
                    rows[kv.Key] = r;
                }
                long ticks = (t0 > 0 && t1 > 0) ? t0 + (long)((t1 - t0) * f) : t0;
                UpsertWeek(wk, ticks, rows);
                filled++;
            }
            return filled;
        }

        /// <summary>插入或覆盖某一周（同周以后写的为准）。</summary>
        public void UpsertWeek(uint week, long dateTicks, Dictionary<ushort, double[]> rows)
        {
            if (!_weekRows.ContainsKey(week))
            {
                int pos = Weeks.BinarySearch(week);
                if (pos < 0) pos = ~pos;
                Weeks.Insert(pos, week);
            }
            _weekRows[week] = rows;
            WeekDateTicks[week] = dateTicks;
            if (!PendingWeeks.Contains(week)) PendingWeeks.Add(week);
        }

        /// <summary>落盘后清空待写清单。</summary>
        public void ClearPending() { PendingWeeks.Clear(); }

        /// <summary>第 index 周（按 Weeks 升序）的行表；供持久化遍历。</summary>
        public Dictionary<ushort, double[]> RowsAt(int index)
        {
            if (index < 0 || index >= Weeks.Count) return null;
            Dictionary<ushort, double[]> r;
            return _weekRows.TryGetValue(Weeks[index], out r) ? r : null;
        }

        /// <summary>指定周号的行表。</summary>
        public Dictionary<ushort, double[]> RowsOf(uint week)
        {
            Dictionary<ushort, double[]> r;
            return _weekRows.TryGetValue(week, out r) ? r : null;
        }

        public bool TryGetRow(ushort id, uint week, out double[] row)
        {
            row = null;
            Dictionary<ushort, double[]> rows;
            if (!_weekRows.TryGetValue(week, out rows)) return false;
            return rows.TryGetValue(id, out row);
        }

        public double GetValue(ushort id, uint week, int field)
        {
            double[] row;
            if (!TryGetRow(id, week, out row)) return 0.0;
            if (row == null || field < 0 || field >= row.Length) return 0.0;
            return row[field];
        }

        /// <summary>某区划有记录的周号（升序）。</summary>
        public List<uint> SeriesWeeks(ushort id)
        {
            List<uint> res = new List<uint>();
            for (int i = 0; i < Weeks.Count; i++)
            {
                Dictionary<ushort, double[]> rows;
                if (_weekRows.TryGetValue(Weeks[i], out rows) && rows.ContainsKey(id))
                    res.Add(Weeks[i]);
            }
            return res;
        }

        /// <summary>某区划某字段的周序列（与 SeriesWeeks 一一对应）。</summary>
        public List<double> SeriesValues(ushort id, int field)
        {
            List<uint> ws = SeriesWeeks(id);
            List<double> res = new List<double>();
            for (int i = 0; i < ws.Count; i++) res.Add(GetValue(id, ws[i], field));
            return res;
        }

        /// <summary>相邻周差值：value(week) − value(该区划上一有记录的周)。无上一周返回 0。</summary>
        public double Delta(ushort id, uint week, int field)
        {
            List<uint> ws = SeriesWeeks(id);
            for (int i = 0; i < ws.Count; i++)
            {
                if (ws[i] == week)
                {
                    if (i == 0) return 0.0;
                    return GetValue(id, week, field) - GetValue(id, ws[i - 1], field);
                }
            }
            return 0.0;
        }
    }
}
