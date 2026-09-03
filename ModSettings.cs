using System;
using System.IO;
using UnityEngine;

namespace DistrictFinanceManager
{
    /// <summary>
    /// MOD 持久化设置。使用简单文本格式存储。
    /// </summary>
    public class ModSettings
    {
        #region Fields

        public int UpdateInterval = 6; // 更新间隔（计算间隔）1~30，密度遍历单轮=值×5
        public bool ShowFinanceBreakdown = true;
        public string PanelKey = "F9";
        public string Language = "zh"; // "zh" = 中文, "en" = English
        public float ResidentWeight = 0.5f; // 居民权重 0~1
        public float WorkerWeight = 3f;     // 工人权重 0~5
        public int DisplayMode = 0; // 0 原版按周(GDP×1) 1 原版按年(GDP×52) 2 人民币年化(GDP×3500/地价×420) 3 美元年化(GDP×500/地价×60)
        public bool IncludeDirect = true; // 排名是否包含直辖区划
        public bool ShowDebug = false; // 区域信息下方显示调试文本
        public float PanelScale = 1.2f; // 面板缩放（滚轮），保存记忆

        #endregion

        #region Static load/save

        private static string _path;

        private static string FilePath
        {
            get
            {
                if (_path == null)
                {
                    _path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _path = Path.Combine(_path, "Colossal Order");
                    _path = Path.Combine(_path, "Cities_Skylines");
                    _path = Path.Combine(_path, "Addons");
                    _path = Path.Combine(_path, "Mods");
                    _path = Path.Combine(_path, "DistrictFinanceManager");
                    _path = Path.Combine(_path, "settings.cfg");
                }
                return _path;
            }
        }

        public static ModSettings Load()
        {
            ModSettings s = new ModSettings();
            try
            {
                if (!File.Exists(FilePath)) return s;

                string[] lines = File.ReadAllLines(FilePath);
                foreach (string line in lines)
                {
                    string t = line.Trim();
                    if (string.IsNullOrEmpty(t) || t.StartsWith("#")) continue;
                    int eq = t.IndexOf('=');
                    if (eq < 0) continue;
                    string k = t.Substring(0, eq).Trim();
                    string v = t.Substring(eq + 1).Trim();

                    switch (k)
                    {
                        case "UpdateInterval": s.UpdateInterval = ParseInt(v, 6); break;
                        case "ShowFinanceBreakdown": s.ShowFinanceBreakdown = ParseBool(v, true); break;
                        case "PanelKey": s.PanelKey = v; break;
                        case "Language": s.Language = v; break;
                        case "ResidentWeight": s.ResidentWeight = ParseFloat(v, 0.5f); break;
                        case "WorkerWeight": s.WorkerWeight = ParseFloat(v, 3f); break;
                        case "DisplayMode": s.DisplayMode = ParseInt(v, 0); break;
                        case "IncludeDirect": s.IncludeDirect = ParseBool(v, true); break;
                        case "ShowDebug": s.ShowDebug = ParseBool(v, false); break;
                        case "PanelScale": s.PanelScale = ParseFloat(v, 1.2f); break;
                    }
                }
                Debug.Log("[DFM] Settings loaded");
            }
            catch (Exception ex)
            {
                Debug.LogError("[DFM] Load settings failed: " + ex.Message);
            }
            return s;
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (StreamWriter w = new StreamWriter(FilePath, false, System.Text.Encoding.UTF8))
                {
                    w.WriteLine("# DistrictFinanceManager settings");
                    w.WriteLine();
                    w.WriteLine("UpdateInterval=" + UpdateInterval);
                    w.WriteLine("ShowFinanceBreakdown=" + ShowFinanceBreakdown);
                    w.WriteLine("PanelKey=" + PanelKey);
                    w.WriteLine("Language=" + Language);
                    w.WriteLine("ResidentWeight=" + ResidentWeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteLine("WorkerWeight=" + WorkerWeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteLine("DisplayMode=" + DisplayMode);
                    w.WriteLine("IncludeDirect=" + IncludeDirect);
                    w.WriteLine("ShowDebug=" + ShowDebug);
                    w.WriteLine("PanelScale=" + PanelScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                Debug.Log("[DFM] Settings saved.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[DFM] Save settings failed: " + ex.Message);
            }
        }

        #endregion

        #region Parsers

        public KeyCode GetPanelKeyCode()
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), PanelKey, true); }
            catch { return KeyCode.F9; }
        }

        private static float ParseFloat(string s, float def)
        {
            float v;
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static int ParseInt(string s, int def)
        {
            int v;
            return int.TryParse(s, out v) ? v : def;
        }

        private static bool ParseBool(string s, bool def)
        {
            string l = s.ToLowerInvariant();
            if (l == "true" || l == "1" || l == "yes") return true;
            if (l == "false" || l == "0" || l == "no") return false;
            return def;
        }

        #endregion
    }
}
