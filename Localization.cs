namespace DistrictFinanceManager
{
    /// <summary>
    /// 简单位置化：根据 Lang 在中英文之间选择。Lang 由 ModSettings.Language 驱动，面板周期性刷新。
    /// </summary>
    public static class Loc
    {
        /// <summary>"zh" = 中文（默认），"en" = English</summary>
        public static string Lang = "zh";

        public static bool IsEn { get { return Lang == "en"; } }

        /// <summary>根据当前语言返回中/英文。</summary>
        public static string T(string zh, string en)
        {
            return IsEn ? en : zh;
        }
    }
}
