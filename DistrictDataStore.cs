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

        /// <summary>按存档保存「自定义政府投资额」，每行 `D &lt;区划ID&gt; &lt;原始值&gt;`。</summary>
        public static void SaveInvestments(Dictionary<ushort, double> investments, string saveName)
        {
            try
            {
                string dir = GetDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = GetInvestPath(saveName);
                using (StreamWriter w = new StreamWriter(path, false, System.Text.Encoding.UTF8))
                {
                    foreach (KeyValuePair<ushort, double> kv in investments)
                        w.WriteLine("D " + kv.Key + " " + kv.Value.ToString("0.###",
                            System.Globalization.CultureInfo.InvariantCulture));
                }
                Debug.Log("[DFM] Investments saved: " + investments.Count + " -> " + path);
            }
            catch (Exception ex) { Debug.LogError("[DFM] Save investments failed: " + ex.Message); }
        }

        /// <summary>读取该存档的「自定义政府投资额」；无文件则返回空字典。坏行跳过，不抛。</summary>
        public static Dictionary<ushort, double> LoadInvestments(string saveName)
        {
            var map = new Dictionary<ushort, double>();
            try
            {
                string path = GetInvestPath(saveName);
                if (!File.Exists(path)) return map;
                foreach (string line in File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t[0] != 'D') continue;
                    string[] parts = t.Split(new char[] { ' ', '\t' },
                        StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) continue;
                    ushort id;
                    double v;
                    if (!ushort.TryParse(parts[1], out id) || id == 0) continue;
                    if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out v)) continue;
                    map[id] = v;
                }
                Debug.Log("[DFM] Investments loaded: " + map.Count);
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
                Debug.Log("[DFM] Hierarchy loaded: " + h.LevelOf.Count + " districts");
            }
            catch (Exception ex) { Debug.LogError("[DFM] Load failed: " + ex.Message); }
            return h;
        }
    }
}
