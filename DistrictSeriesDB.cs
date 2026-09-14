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
            "BuiltArea"
        };

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

        /// <summary>把一条 FinanceResult 抽成定序数值行。</summary>
        public static double[] ToRow(DistrictFinanceCalculator.FinanceResult r)
        {
            double[] v = new double[FIELDS.Length];
            v[0] = r.GDP; v[1] = r.Population; v[2] = r.Workers; v[3] = r.LandValue; v[4] = r.BuildingCount; v[5] = r.Area;
            v[6] = r.ResPop; v[7] = r.ComWorkers; v[8] = r.IndWorkers; v[9] = r.OffWorkers; v[10] = r.PlayerWorkers;
            v[11] = r.ResLow; v[12] = r.ResHigh; v[13] = r.ComLow; v[14] = r.ComHigh;
            v[15] = r.ResLowGDP; v[16] = r.ResHighGDP; v[17] = r.ComLowGDP; v[18] = r.ComHighGDP;
            v[19] = r.IndGDP; v[20] = r.OffGDP; v[21] = r.PlayerGDP;
            v[22] = r.AggGDP; v[23] = r.AggPopulation; v[24] = r.AggWorkers; v[25] = r.AggBuildings; v[26] = r.AggArea;
            v[27] = r.AggResPop; v[28] = r.AggComWorkers; v[29] = r.AggIndWorkers; v[30] = r.AggOffWorkers; v[31] = r.AggPlayerWorkers;
            v[32] = r.AggResLow; v[33] = r.AggResHigh; v[34] = r.AggComLow; v[35] = r.AggComHigh;
            v[36] = r.AggResLowGDP; v[37] = r.AggResHighGDP; v[38] = r.AggComLowGDP; v[39] = r.AggComHighGDP;
            v[40] = r.AggIndGDP; v[41] = r.AggOffGDP; v[42] = r.AggPlayerGDP;
            v[43] = r.BuiltArea;
            return v;
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
