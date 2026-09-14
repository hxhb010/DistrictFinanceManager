using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace DistrictFinanceManager
{
    /// <summary>
    /// 周度时间序列持久化（.series）。沿用 DistrictDataStore 的目录/文件名/文本约定，
    /// 但改用「追加式」写入：只写未落盘的新周，不重写旧数据 → 支持无上限增长。
    ///
    /// 格式（TAG 行，空格分隔，数值一律 InvariantCulture）：
    ///   # DFM Series v1 fields=43
    ///   # F GDP Population ...
    ///   N &lt;districtId&gt; &lt;name&gt;
    ///   W &lt;week&gt; &lt;dateTicks&gt;
    ///   D &lt;districtId&gt; &lt;v1&gt; &lt;v2&gt; ... &lt;vN&gt;
    /// </summary>
    public static class DistrictSeriesStore
    {
        public const string SeriesVersion = "v3"; // v3: 追加 DisposableIncome 字段（44→45 列）；v2=BuiltArea（43→44）

        private static string GetDir()
        {
            string p = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            p = Path.Combine(p, "Colossal Order");
            p = Path.Combine(p, "Cities_Skylines");
            p = Path.Combine(p, "Addons");
            p = Path.Combine(p, "Mods");
            p = Path.Combine(p, "DistrictFinanceManager");
            p = Path.Combine(p, "saves");
            return p;
        }

        private static string GetSeriesPath(string saveName)
        {
            string safe = saveName;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(GetDir(), safe + ".series");
        }

        private static string OneLine(string s)
        {
            if (s == null) return "";
            return s.Replace('\r', ' ').Replace('\n', ' ');
        }

        /// <summary>
        /// **整表重写**（不是追加）。用于 ID 复用后清除某区划的陈旧历史：
        /// Flush 是追加式的，旧行物理上还在文件里，只清内存的话下次读档又会读回来。
        /// 数据量是百 KB 级，一次性重写代价可以忽略。
        /// </summary>
        public static void Rewrite(DistrictSeriesDB db, string saveName)
        {
            try
            {
                if (db == null) return;

                StringBuilder sb = new StringBuilder();
                sb.Append("# DFM Series ").Append(SeriesVersion)
                  .Append(" fields=").Append(DistrictSeriesDB.FIELDS.Length).Append('\n');
                sb.Append("# F");
                for (int i = 0; i < DistrictSeriesDB.FIELDS.Length; i++)
                    sb.Append(' ').Append(DistrictSeriesDB.FIELDS[i]);
                sb.Append('\n');

                foreach (KeyValuePair<ushort, string> kv in db.Names)
                {
                    string nm = OneLine(kv.Value);
                    if (nm.Length == 0) continue;
                    sb.Append("N ").Append(kv.Key).Append(' ').Append(nm).Append('\n');
                }

                List<uint> weeks = new List<uint>(db.Weeks);
                weeks.Sort();
                for (int p = 0; p < weeks.Count; p++)
                {
                    uint wk = weeks[p];
                    long ticks;
                    if (!db.WeekDateTicks.TryGetValue(wk, out ticks)) ticks = 0L;
                    sb.Append("W ").Append(wk.ToString(CultureInfo.InvariantCulture))
                      .Append(' ').Append(ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');

                    Dictionary<ushort, double[]> rows = db.RowsOf(wk);
                    if (rows == null) continue;
                    foreach (KeyValuePair<ushort, double[]> kv in rows)
                    {
                        double[] row = kv.Value;
                        if (row == null) continue;
                        sb.Append("D ").Append(kv.Key.ToString(CultureInfo.InvariantCulture));
                        for (int i = 0; i < row.Length; i++) { sb.Append(' '); sb.Append(FormatNum(row[i])); }
                        sb.Append('\n');
                    }
                }

                string dir = GetDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(GetSeriesPath(saveName), sb.ToString(), new UTF8Encoding(false));

                db.ClearPending();
                foreach (KeyValuePair<ushort, string> kv in db.Names)
                {
                    string nm = OneLine(kv.Value);
                    if (nm.Length > 0) db.WrittenNames[kv.Key] = nm;
                }
                Debug.Log("[DFM] Series rewritten: " + db.WeekCount + " weeks -> " + GetSeriesPath(saveName));
            }
            catch (Exception ex) { Debug.LogError("[DFM] Series rewrite failed: " + ex.Message); }
        }

        /// <summary>把该存档的 .series 读进 db（文件不存在则保持为空）。</summary>
        public static void Load(DistrictSeriesDB db, string saveName)
        {
            try
            {
                if (db == null) return;
                string path = GetSeriesPath(saveName);
                if (!File.Exists(path))
                {
                    Debug.Log("[DFM] No series file — starting empty");
                    return;
                }

                Dictionary<ushort, double[]> curRows = null;
                int fieldCount = -1;
                int weeks = 0, rows = 0;

                foreach (string raw in File.ReadAllLines(path))
                {
                    string t = raw.Trim();
                    if (t.Length == 0 || t[0] == '#') continue;

                    int sp = t.IndexOf(' ');
                    string tag = sp < 0 ? t : t.Substring(0, sp);
                    string rest = sp < 0 ? "" : t.Substring(sp + 1).Trim();

                    if (tag == "N")
                    {
                        int sp2 = rest.IndexOf(' ');
                        if (sp2 < 0) continue;
                        ushort id;
                        if (!ushort.TryParse(rest.Substring(0, sp2), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) continue;
                        string name = rest.Substring(sp2 + 1);
                        db.Names[id] = name;
                        db.WrittenNames[id] = name;
                    }
                    else if (tag == "W")
                    {
                        string[] p = rest.Split(' ');
                        if (p.Length < 1) continue;
                        uint wk;
                        if (!uint.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out wk)) continue;
                        long ticks = 0;
                        if (p.Length >= 2) long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks);
                        curRows = new Dictionary<ushort, double[]>();
                        db.UpsertWeek(wk, ticks, curRows); // 同周以文件里更靠后的为准
                        weeks++;
                    }
                    else if (tag == "D")
                    {
                        if (curRows == null) continue;
                        int sp2 = rest.IndexOf(' ');
                        if (sp2 < 0) continue;
                        ushort id;
                        if (!ushort.TryParse(rest.Substring(0, sp2), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) continue;
                        string[] vals = rest.Substring(sp2 + 1).Split(' ');
                        double[] row = new double[vals.Length];
                        bool ok = true;
                        for (int i = 0; i < vals.Length; i++)
                        {
                            double d;
                            if (!double.TryParse(vals[i], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { ok = false; break; }
                            row[i] = d;
                        }
                        if (ok && row.Length > 0)
                        {
                            curRows[id] = row;
                            if (fieldCount < 0) fieldCount = row.Length;
                            rows++;
                        }
                    }
                }

                db.ClearPending(); // 已读入的周不再需要写
                Debug.Log("[DFM] Series loaded: " + db.Weeks.Count + " unique weeks (" + weeks +
                          " blocks), " + rows + " rows, fields=" + fieldCount);
            }
            catch (Exception ex) { Debug.LogError("[DFM] Series load failed: " + ex.Message); }
        }

        /// <summary>把未落盘的新周（及新增/改名的 N 行）追加写入 .series。</summary>
        public static void Flush(DistrictSeriesDB db, string saveName)
        {
            try
            {
                if (db == null) return;

                string path = GetSeriesPath(saveName);
                bool isNew = !File.Exists(path);
                if (isNew && db.Weeks.Count == 0) return; // 无数据不建文件

                StringBuilder sb = new StringBuilder();
                if (isNew)
                {
                    sb.Append("# DFM Series ").Append(SeriesVersion)
                      .Append(" fields=").Append(DistrictSeriesDB.FIELDS.Length).Append('\n');
                    sb.Append("# F");
                    for (int i = 0; i < DistrictSeriesDB.FIELDS.Length; i++) sb.Append(' ').Append(DistrictSeriesDB.FIELDS[i]);
                    sb.Append('\n');
                }

                // 新增/改名的区划名
                foreach (KeyValuePair<ushort, string> kv in db.Names)
                {
                    string nm = OneLine(kv.Value);
                    if (nm.Length == 0) continue;
                    string prev;
                    if (!db.WrittenNames.TryGetValue(kv.Key, out prev) || prev != nm)
                    {
                        sb.Append("N ").Append(kv.Key).Append(' ').Append(nm).Append('\n');
                    }
                }

                // 待写周（新增或覆盖；按周号升序写出）
                List<uint> pending = new List<uint>(db.PendingWeeks);
                pending.Sort();
                for (int p = 0; p < pending.Count; p++)
                {
                    uint wk = pending[p];
                    long ticks;
                    if (!db.WeekDateTicks.TryGetValue(wk, out ticks)) ticks = 0L;
                    sb.Append("W ").Append(wk.ToString(CultureInfo.InvariantCulture))
                      .Append(' ').Append(ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');

                    Dictionary<ushort, double[]> rows = db.RowsOf(wk);
                    if (rows != null)
                    {
                        foreach (KeyValuePair<ushort, double[]> kv in rows)
                        {
                            double[] row = kv.Value;
                            if (row == null) continue;
                            sb.Append("D ").Append(kv.Key.ToString(CultureInfo.InvariantCulture));
                            for (int i = 0; i < row.Length; i++)
                            {
                                sb.Append(' ');
                                sb.Append(FormatNum(row[i]));
                            }
                            sb.Append('\n');
                        }
                    }
                }

                if (sb.Length == 0) return;

                int added = db.PendingWeeks.Count;

                string dir = GetDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using (StreamWriter w = new StreamWriter(path, true, new UTF8Encoding(false)))
                {
                    w.Write(sb.ToString());
                }

                // 落盘成功后再更新状态
                db.ClearPending();
                foreach (KeyValuePair<ushort, string> kv in db.Names)
                {
                    string nm = OneLine(kv.Value);
                    if (nm.Length > 0) db.WrittenNames[kv.Key] = nm;
                }
                Debug.Log("[DFM] Series flushed: +" + added + " weeks, total " + db.Weeks.Count + " -> " + path);
            }
            catch (Exception ex) { Debug.LogError("[DFM] Series flush failed: " + ex.Message); }
        }

        /// <summary>double → InvariantCulture 文本（保留 3 位小数；统计量够用，且比 R 格式小约 40%）。</summary>
        private static string FormatNum(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
