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
        public bool ShowOnLoad = true; // 打开存档时自动显示面板（关闭则需按快捷键打开）
        public string Language = "zh"; // "zh" = 中文, "en" = English
        // ---- 统计模式：拆成「货币 × 周期」两个独立的轴（2026-09-25）----
        // 旧字段 DisplayMode(0周/1年/2人民币年/3美元年) 已废弃，读档时自动迁移（见 ParseLine）。
        public int DisplayCurrency = 0; // 0=原版 kr  1=人民币 ¥  2=美元 $
        public int DisplayPeriod = 0;   // 0=周 1=月(4周) 2=季(13周) 3=年(52周) 4=5年(260周)
        public bool IncludeDirect = false; // 排名是否包含直辖区划（默认关闭）
        public bool ShowDebug = false; // 区域信息下方显示调试文本
        public bool AutoLanguage = true; // 打开存档时按系统语言自动切面板语言（非简/繁中→英文）
        public float PanelScale = 1.2f; // 面板缩放（滚轮），保存记忆

        #endregion

        #region 统计模式：货币 × 周期（两个轴，唯一的换算真源）

        /// <summary>最长周期（5 年）的周数。自定义投资额的分期裁剪、周库基准回退的下界都用它。</summary>
        public const int MaxPeriodWeeks = 260;

        /// <summary>周期周数：周 / 月 / 季 / 年 / 5年 = 1 / 4 / 13 / 52 / 260。
        /// 建筑价值增量的「基准周回退几周」也用它（用户 2026-09-25 指定）。</summary>
        public static int PeriodWeeks(int period)
        {
            switch (period)
            {
                case 1: return 4;    // 月
                case 2: return 13;   // 季
                case 3: return 52;   // 年
                case 4: return 260;  // 5 年
                default: return 1;   // 周
            }
        }

        /// <summary>
        /// 货币系数 —— **流量**（GDP / 人均GDP / 地均GDP / 人均可支配）。
        /// 基准是「周」；**总系数 = 本系数 × PeriodWeeks(周期)**。
        /// 数值刻意校准到旧 DisplayMode 的四个锚点，保证老档的值一分不变：
        ///   原版年 = 52、人民币年 = 2625、美元年 = 375
        /// 于是有人民币周 = 2625/52、美元周 = 375/52（小数但自洽）。
        /// ⚠️ 与下面的 **价格** 系数（地价 1/420/60）**不是同一套**，千万别互换。
        /// </summary>
        public static double FlowFactor(int currency)
        {
            if (currency == 1) return 2625.0 / 52.0;   // 人民币
            if (currency == 2) return 375.0 / 52.0;    // 美元
            return 1.0;                                // 原版 kr
        }

        /// <summary>货币系数 —— **价格**（地价、增量里的地价）。价格是存量，**与周期无关**。</summary>
        public static double PriceFactor(int currency)
        {
            if (currency == 1) return 420.0;   // 人民币/㎡
            if (currency == 2) return 60.0;    // 美元/㎡
            return 1.0;                        // 原版 kr/㎡
        }

        public static string CurrencySymbolOf(int currency)
        {
            if (currency == 1) return "¥";
            if (currency == 2) return "$";
            return "kr";
        }

        /// <summary>货币名（用于按钮/图例）。</summary>
        public static string CurrencyName(int currency)
        {
            if (currency == 1) return Loc.T("人民币", "RMB");
            if (currency == 2) return Loc.T("美元", "USD");
            return Loc.T("原版", "Vanilla");
        }

        /// <summary>周期名（用于按钮/图例/单位后缀）。</summary>
        public static string PeriodName(int period)
        {
            switch (period)
            {
                case 1: return Loc.T("月", "mo");
                case 2: return Loc.T("季", "qtr");
                case 3: return Loc.T("年", "yr");
                case 4: return Loc.T("5年", "5yr");
                default: return Loc.T("周", "wk");
            }
        }

        /// <summary>是否非周周期（图例里决定写「/周」还是「/年」之类）。</summary>
        public static bool IsMultiWeek(int period) { return period >= 1 && period <= 4; }

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
                        case "ShowOnLoad": s.ShowOnLoad = ParseBool(v, true); break;
                        case "Language": s.Language = v; break;
                        case "DisplayCurrency": s.DisplayCurrency = ParseInt(v, 0); break;
                        case "DisplayPeriod": s.DisplayPeriod = ParseInt(v, 0); break;
                        case "DisplayMode": // 旧字段迁移：0 原版周 / 1 原版年 / 2 人民币年 / 3 美元年
                            {
                                int dm = ParseInt(v, 0);
                                if (dm == 1) { s.DisplayCurrency = 0; s.DisplayPeriod = 3; }
                                else if (dm == 2) { s.DisplayCurrency = 1; s.DisplayPeriod = 3; }
                                else if (dm == 3) { s.DisplayCurrency = 2; s.DisplayPeriod = 3; }
                                else { s.DisplayCurrency = 0; s.DisplayPeriod = 0; }
                            }
                            break;
                        case "IncludeDirect": s.IncludeDirect = ParseBool(v, false); break;
                        case "ShowDebug": s.ShowDebug = ParseBool(v, false); break;
                        case "AutoLanguage": s.AutoLanguage = ParseBool(v, true); break;
                        case "PanelScale": s.PanelScale = ParseFloat(v, 1.2f); break;
                    }
                }
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
                    w.WriteLine("ShowOnLoad=" + ShowOnLoad);
                    w.WriteLine("Language=" + Language);
                    w.WriteLine("DisplayCurrency=" + DisplayCurrency);
                    w.WriteLine("DisplayPeriod=" + DisplayPeriod);
                    w.WriteLine("IncludeDirect=" + IncludeDirect);
                    w.WriteLine("ShowDebug=" + ShowDebug);
                    w.WriteLine("AutoLanguage=" + AutoLanguage);
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
