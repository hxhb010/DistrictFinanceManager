using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DistrictFinanceManager
{
    /// <summary>
    /// 层级关系持久化。简单文本格式，易于调试。
    /// </summary>
    public static class DistrictDataStore
    {
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

        private static string GetPath(string saveName)
        {
            string safe = saveName;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(GetDir(), safe + ".hier");
        }

        public static void Save(DistrictHierarchy h, string saveName)
        {
            try
            {
                string dir = GetDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (StreamWriter w = new StreamWriter(GetPath(saveName), false, System.Text.Encoding.UTF8))
                {
                    w.WriteLine("# DFM Hierarchy — parent-child relationships for vanilla districts");
                    w.WriteLine("# parent: child=parent  |  level: id=level");
                    w.WriteLine();
                    foreach (var kv in h.ParentOf)
                        w.WriteLine("P " + kv.Key + "=" + kv.Value);
                    foreach (var kv in h.LevelOf)
                        w.WriteLine("L " + kv.Key + "=" + kv.Value);
                }
                Debug.Log("[DFM] Hierarchy saved: " + h.LevelOf.Count + " districts in tree");
            }
            catch (Exception ex) { Debug.LogError("[DFM] Save failed: " + ex.Message); }
        }

        /// <summary>按存档保存组合（Groups）。</summary>
        public static void SaveGroups(List<GroupData> groups, string saveName)
        {
            try
            {
                string dir = GetDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = GetGroupsPath(saveName);
                using (StreamWriter w = new StreamWriter(path, false, System.Text.Encoding.UTF8))
                {
                    foreach (GroupData g in groups)
                    {
                        w.WriteLine("G " + g.Name);
                        foreach (ushort m in g.Members)
                            w.WriteLine("M " + m);
                        w.WriteLine();
                    }
                }
                Debug.Log("[DFM] Groups saved: " + groups.Count + " -> " + path);
            }
            catch (Exception ex) { Debug.LogError("[DFM] Save groups failed: " + ex.Message); }
        }

        /// <summary>读取该存档的组合；无则返回空列表。</summary>
        public static List<GroupData> LoadGroups(string saveName)
        {
            var list = new List<GroupData>();
            try
            {
                string path = GetGroupsPath(saveName);
                if (!File.Exists(path)) return list;
                GroupData cur = null;
                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    if (t.Length == 0) { cur = null; continue; }
                    int sp = t.IndexOf(' ');
                    string type = sp < 0 ? t : t.Substring(0, sp);
                    string val = sp < 0 ? "" : t.Substring(sp + 1).Trim();
                    if (type == "G" && val.Length > 0)
                    {
                        cur = new GroupData { Name = val };
                        list.Add(cur);
                    }
                    else if (type == "M" && cur != null)
                    {
                        ushort id;
                        if (ushort.TryParse(val, out id)) cur.Members.Add(id);
                    }
                }
                Debug.Log("[DFM] Groups loaded: " + list.Count);
            }
            catch (Exception ex) { Debug.LogWarning("[DFM] Load groups failed: " + ex.Message); }
            return list;
        }

        private static string GetGroupsPath(string saveName)
        {
            string safe = saveName;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(GetDir(), safe + ".grp");
        }

        /// <summary>
        /// 按存档保存「自定义政府投资额」的**分期表**：每行 `D &lt;区划ID&gt; &lt;游戏周&gt; &lt;该期金额&gt;`。
        /// 金额是**单期**的原始值（原版 kr/周），不是累计额 —— 一笔录入会被拆成 N 行（N = 录入时的周期周数）。
        /// 2026-09-26 之前是 `D &lt;ID&gt; &lt;累计额&gt;`（无周号），读取时按「本周录入」迁移，见 LoadInvestments。
        /// </summary>
        public static void SaveInvestments(Dictionary<ushort, List<InvestInstallment>> investments, string saveName)
        {
            try
            {
                string dir = GetDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = GetInvestPath(saveName);
                int rows = 0;
                using (StreamWriter w = new StreamWriter(path, false, System.Text.Encoding.UTF8))
                {
                    foreach (KeyValuePair<ushort, List<InvestInstallment>> kv in investments)
                    {
                        List<InvestInstallment> list = kv.Value;
                        if (list == null) continue;
                        for (int i = 0; i < list.Count; i++)
                        {
                            w.WriteLine("D " + kv.Key + " " + list[i].Week + " " + list[i].Amount.ToString(
                                "0.###", System.Globalization.CultureInfo.InvariantCulture));
                            rows++;
                        }
                    }
                }
                Debug.Log("[DFM] Investments saved: " + investments.Count + " districts / " + rows + " installments -> " + path);
            }
            catch (Exception ex) { Debug.LogError("[DFM] Save investments failed: " + ex.Message); }
        }

        /// <summary>
        /// 读取该存档的「自定义政府投资额」分期表；无文件则返回空字典。坏行跳过，不抛。
        /// 兼容旧格式 `D &lt;ID&gt; &lt;累计额&gt;`（无周号的那批）：当成「在当前周一次性录入」的一条分期迁进来
        /// —— 旧数据没有任何周号可用，锚在当周是最不坏的解读（此后按正常分期逐周退出窗口）。
        /// </summary>
        public static Dictionary<ushort, List<InvestInstallment>> LoadInvestments(string saveName)
        {
            var map = new Dictionary<ushort, List<InvestInstallment>>();
            try
            {
                string path = GetInvestPath(saveName);
                if (!File.Exists(path)) return map;
                uint legacyWeek = GameWeek.CurrentWeek;
                int legacy = 0;
                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t[0] != 'D') continue;
                    string[] parts = t.Split(new char[] { ' ', '\t' },
                        StringSplitOptions.RemoveEmptyEntries);
                    ushort id;
                    if (parts.Length < 3) continue;
                    if (!ushort.TryParse(parts[1], out id) || id == 0) continue;

                    double v;
                    uint week;
                    if (parts.Length >= 4)
                    {
                        // 新格式：D <id> <week> <amount>
                        if (!uint.TryParse(parts[2], out week)) continue;
                        if (!double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out v)) continue;
                    }
                    else
                    {
                        // 旧格式：D <id> <amount>
                        if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out v)) continue;
                        week = legacyWeek;
                        legacy++;
                    }

                    List<InvestInstallment> list;
                    if (!map.TryGetValue(id, out list)) { list = new List<InvestInstallment>(); map[id] = list; }
                    list.Add(new InvestInstallment(week, v));
                }
                Debug.Log("[DFM] Investments loaded: " + map.Count + " districts"
                    + (legacy > 0 ? "（其中 " + legacy + " 行为旧格式，已按本周一次性录入迁移）" : ""));
            }
            catch (Exception ex) { Debug.LogWarning("[DFM] Load investments failed: " + ex.Message); }
            return map;
        }

        private static string GetInvestPath(string saveName)
        {
            string safe = saveName;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(GetDir(), safe + ".inv");
        }

        /// <summary>墓碑文件：记录「曾经存在、后来被删掉」的原版区划 ID。</summary>
        private static string GetDeadPath(string saveName)
        {
            string safe = saveName;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(GetDir(), safe + ".dead");
        }

        /// <summary>
        /// 读墓碑。用途：原版区划被删后 ID 会被回收，玩家新画的区划可能拿到同一个 ID。
        /// 若某个墓碑 ID 又变成已创建，说明 ID 被复用了 —— 新主人不该继承旧主人的层级/组合/周库历史。
        /// </summary>
        public static System.Collections.Generic.HashSet<ushort> LoadDeadIds(string saveName)
        {
            var set = new System.Collections.Generic.HashSet<ushort>();
            try
            {
                string path = GetDeadPath(saveName);
                if (!File.Exists(path)) return set;
                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    ushort id;
                    if (ushort.TryParse(t, out id) && id != 0) set.Add(id);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[DFM] LoadDeadIds failed: " + ex.Message);
            }
            return set;
        }

        /// <summary>写墓碑（全量覆盖）。集合为空时删除文件。</summary>
        public static void SaveDeadIds(System.Collections.Generic.HashSet<ushort> ids, string saveName)
        {
            try
            {
                string path = GetDeadPath(saveName);
                if (ids == null || ids.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                string dir = GetDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var list = new System.Collections.Generic.List<ushort>(ids);
                list.Sort();
                using (StreamWriter w = new StreamWriter(path, false, System.Text.Encoding.UTF8))
                {
                    w.WriteLine("# DFM dead districts (deleted vanilla district IDs)");
                    for (int i = 0; i < list.Count; i++)
                        w.WriteLine(list[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[DFM] SaveDeadIds failed: " + ex.Message);
            }
        }

        public static DistrictHierarchy Load(string saveName)
        {
            DistrictHierarchy h = new DistrictHierarchy();
            try
            {
                string path = GetPath(saveName);
                if (!File.Exists(path))
                {
                    Debug.Log("[DFM] No hierarchy file — starting empty");
                    return h;
                }

                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    if (string.IsNullOrEmpty(t) || t.StartsWith("#")) continue;

                    int sp = t.IndexOf(' ');
                    if (sp < 0) continue;
                    string type = t.Substring(0, sp);
                    int eq = t.IndexOf('=', sp);
                    if (eq < 0) continue;

                    string keyStr = t.Substring(sp + 1, eq - sp - 1).Trim();
                    string valStr = t.Substring(eq + 1).Trim();

                    ushort kid, vid;
                    if (!ushort.TryParse(keyStr, out kid) ||
                        !ushort.TryParse(valStr, out vid)) continue;

                    if (type == "P")
                    {
                        h.ParentOf[kid] = vid; // vid 可为 0（顶级），保留父级信息
                        if (vid != 0)
                        {
                            if (!h.ChildrenOf.ContainsKey(vid))
                                h.ChildrenOf[vid] = new List<ushort>();
                            if (!h.ChildrenOf[vid].Contains(kid))
                                h.ChildrenOf[vid].Add(kid);
                        }
                    }
                    else if (type == "L")
                    {
                        h.LevelOf[kid] = (int)vid;
                    }
                }
                // 诊断：把根节点（无父级 = 独立节点）列出来，用来判断「独立区县」到底有没有读进来
                var sb = new System.Text.StringBuilder();
                int rootCount = 0;
                foreach (KeyValuePair<ushort, int> kv in h.LevelOf)
                {
                    ushort rid = kv.Key;
                    if (!h.ParentOf.ContainsKey(rid) || h.ParentOf[rid] == 0)
                    {
                        rootCount++;
                        if (rootCount <= 24) sb.Append(rid).Append("(L").Append(kv.Value).Append(") ");
                    }
                }
                Debug.Log("[DFM] Hierarchy loaded: " + h.LevelOf.Count + " districts, key='" + saveName +
                    "', roots=" + rootCount + " -> " + sb.ToString());
            }
            catch (Exception ex) { Debug.LogError("[DFM] Load failed: " + ex.Message); }
            return h;
        }
    }
}
