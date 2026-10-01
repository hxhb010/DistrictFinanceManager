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
        public const string SeriesVersion = "v8"; // v8（2026-09-28）: 追加一列 PanelWorkers（49→50 列）——
                                                  //     「区域工人数」（键 12 第二个子模式）的增速基准。
                                                  //     老档该列读出 0（= 该周无数据）→ 自动被当作无效基准跳过，
                                                  //     不需要迁移；等新周攒起来，这一项的增速自然就有基准了。
                                                  // --- 以下为历史 ---
                                                  // v7: 追加三列通勤指标（CommuteDistance / LocalEmployment /
                                                  //     CommuteTime，46→49 列）。老档读出 0（= 该周无数据），
                                                  //     不需要迁移；通勤时间在预热期内也是 0。
                                                  // --- 以下为历史 ---
                                                  // v6: 库里 GDP 列也改存**原始值**（写入时除回显示系数）→
                                                  //     统计模式（货币/周期）不再影响库内容，改模式不再重写周库；
                                                  //     老 v5 的 GDP 带系数，读档时按文件头的 factor= 除回一次
                                                  //     （见 ConvertGdpToRaw）。--- 以下为历史 ---
                                                  // v5: 文件头加 factor=，并做一次「历史 GDP 清洗」（见 GdpWipeVersion）；
                                                  // v4: 追加 BuiltValueArea（45→46 列）；v3=DisposableIncome（44→45）；v2=BuiltArea（43→44）

        /// <summary>
        /// **一次性 GDP 清洗**的分界版本（2026-09-27）：读到文件版本 **&lt;** 这个数时，
        /// 把历史周里的 GDP / AggGDP 列**清零**再重写周库（顺带把文件头升到 SeriesVersion → 之后不再触发）。
        ///
        /// 原因：这一版之前 `CalcGDP` 把 `GetDisplayFactor()`（货币 × 周期）乘进 GDP 才入周库，
        /// 而**每个周是哪次用哪个模式记的没存** —— 实测同一区划的「GDP ÷ Σ(GDP子项)」
        /// 在 0.58 与 1500 两个平台间跳（差 2625 倍 = 人民币/年）。增速拿它当基准就会把「模式差」
        /// 当变化量：原版/月 整齐 +300%（×4−1）、人民币/月 +20092%（50.48×4−1），用户截图实测。
        /// 老数据没法换算（记录时的模式没存），只能清掉：GDP 清 0 = 按既有约定「该周无数据」
        /// → 基准选择会跳过它 → 增速从「第一周干净数据」重新长起来（刚开始会偏小，随后自然恢复）。
        /// 清零后文件头升到 SeriesVersion → 之后不再触发。
        ///
        /// ⚠️⚠️ **这是写死的一次性迁移，绝不要跟着将来的版本号往上改** ——
        /// 一动它就会把用户**以后**攒的正常数据也清掉。用户明确要求"仅这一次更新，不要之后更新也删历史数据"。
        /// </summary>
        private const int GdpWipeVersion = 5;

        /// <summary>从文件头 "# DFM Series v4 fields=46" 里取出主版本号；取不到返回 0（= 未知 → 不清洗）。</summary>
        private static int ParseSeriesVersion(string header)
        {
            int i = header.IndexOf('v');
            if (i < 0) return 0;
            int j = i + 1;
            while (j < header.Length && header[j] >= '0' && header[j] <= '9') j++;
            if (j <= i + 1) return 0;
            int v;
            return int.TryParse(header.Substring(i + 1, j - i - 1), out v) ? v : 0;
        }

        /// <summary>
        /// 把历史周里的 GDP 列清零（一次性迁移用，见 GdpWipeVersion）。
        /// **只碰 field[0]（GDP）与 field[22]（AggGDP）** —— 人口 / 地价 / 面积 / 建成区 / 人均可支配
        /// 等列本来就是原始值，一根手指都别动。返回清零的行数。
        /// </summary>
        private static int WipeGdpColumns(DistrictSeriesDB db)
        {
            int n = 0;
            for (int i = 0; i < db.Weeks.Count; i++)
            {
                Dictionary<ushort, double[]> rows = db.RowsAt(i);
                if (rows == null) continue;
                foreach (KeyValuePair<ushort, double[]> kv in rows)
                {
                    double[] row = kv.Value;
                    if (row == null) continue;
                    if (row.Length > 0 && row[0] != 0.0) { row[0] = 0.0; n++; }
                    if (row.Length > 22) row[22] = 0.0;
                }
            }
            return n;
        }

        /// <summary>
        /// v5 → v6 的一次性换算：v5 的 GDP / AggGDP 存的是「原始值 × 当时的显示系数」，
        /// 而那个系数就写在文件头 `factor=` 里 → **除得回来**（不像 v4 及以前没记，只能清零）。
        /// 只碰 field[0]（GDP）与 field[22]（AggGDP），其余列本来就是原始值。
        /// 系数不可用（缺 factor= / ≤0，理论上不会有，兜底）→ 退化成清零：
        /// 宁可少算（那段历史当无数据跳过），也不能拿一个猜的比例去改用户的数据。
        /// 返回处理的行数。
        /// </summary>
        private static int ConvertGdpToRaw(DistrictSeriesDB db, double fileFactor)
        {
            if (fileFactor <= 0.0) return WipeGdpColumns(db);
            double k = 1.0 / fileFactor;
            int n = 0;
            for (int i = 0; i < db.Weeks.Count; i++)
            {
                Dictionary<ushort, double[]> rows = db.RowsAt(i);
                if (rows == null) continue;
                foreach (KeyValuePair<ushort, double[]> kv in rows)
                {
                    double[] row = kv.Value;
                    if (row == null) continue;
                    if (row.Length > 0) { row[0] *= k; n++; }
                    if (row.Length > 22) row[22] *= k;
                }
            }
            return n;
        }

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
                  .Append(" fields=").Append(DistrictSeriesDB.FIELDS.Length)
                  .Append('\n');   // v6 起不再写 factor=：库里的 GDP 就是原始值，没有系数要记
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
                int fileVer = 0;
                double fileFactor = 0.0;   // 只有当老档是 v5（库里的 GDP 带显示系数）时才用得上

                foreach (string raw in File.ReadAllLines(path))
                {
                    string t = raw.Trim();
                    if (t.Length == 0) continue;
                    if (t[0] == '#')
                    {
                        // 文件头 "# DFM Series v7 fields=49"（老档 v5 还带 factor=4）——
                        //   v < GdpWipeVersion → 老 GDP 带"记录当时的"系数且没记下来 → 一次性清零；
                        //   v == GdpWipeVersion(5) → 系数记在 factor= 里 → 除回原始值（见 ConvertGdpToRaw）
                        if (fileVer == 0 && t.StartsWith("# DFM Series "))
                        {
                            fileVer = ParseSeriesVersion(t);
                            int fi = t.IndexOf("factor=");
                            if (fi >= 0)
                            {
                                double fv;
                                if (double.TryParse(t.Substring(fi + 7), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out fv) && fv > 0.0)
                                    fileFactor = fv;
                            }
                        }
                        continue;
                    }

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
                          " blocks), " + rows + " rows, fields=" + fieldCount + " ver=v" + fileVer);

                // ---- 一次性迁移：把老档的 GDP 列弄成「原始值」（v6 的口径）----
                // 都是只做一次：整表重写后文件头就是 SeriesVersion，下次读档两个分支都不再进
                // （清理前必须落盘，否则下次读档又读回污染值）。见 GdpWipeVersion 的长注释。
                if (fileVer > 0 && fileVer < GdpWipeVersion)
                {
                    // v4 及以前：GDP 带的是**记录当时**那个模式的系数，而"当时是哪个模式"没存 → 只能清
                    int wiped = WipeGdpColumns(db);
                    Rewrite(db, saveName);
                    Debug.Log("[DFM] 一次性 GDP 清洗：老数据带统计模式系数（v" + fileVer + " → " +
                              SeriesVersion + "），已把 " + wiped + " 行的 GDP/AggGDP 归零并重写周库");
                }
                else if (fileVer == GdpWipeVersion)
                {
                    // v5：GDP 带系数，但**系数记在文件头**里 → 除得回来，不用清零（保住用户的历史）
                    int fixedRows = ConvertGdpToRaw(db, fileFactor);
                    Rewrite(db, saveName);
                    if (fileFactor > 0.0)
                        Debug.Log("[DFM] 周库 v5 → v6：GDP 除回原始值（÷" + fileFactor.ToString("0.######",
                                  CultureInfo.InvariantCulture) + "，" + fixedRows +
                                  " 行）—— 此后改统计模式不再改写历史数据");
                    else
                        Debug.LogWarning("[DFM] 周库 v5 → v6：文件头没有可用的 factor=，退回清零 GDP（" +
                                         fixedRows + " 行）");
                }
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
                      .Append(" fields=").Append(DistrictSeriesDB.FIELDS.Length)
                      .Append('\n');   // v6 起不再写 factor=（见 Rewrite）
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
