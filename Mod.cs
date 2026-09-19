using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace DistrictFinanceManager
{
    public class Mod : IUserMod
    {
        private ModSettings _settings;

        public string Name { get { return "District Hierarchy & Economy"; } }

        public string Description
        {
            get
            {
                return "在原版区划基础上增加四级行政区划（市/区县/乡镇/村社区）与经济统计。" +
                       "用原版区划工具绘制/选中区划，独立面板显示 GDP、人均GDP、人口等财务数据，" +
                       "支持多视图排名排序、父节点筛选与 16 色分级。F9 开关面板。" +
                       "\n\n" +
                       "Adds a 4-level administrative hierarchy (City/District/Town/Village) and economy " +
                       "stats on top of vanilla districts. The standalone panel shows GDP, GDP per capita, " +
                       "population, with ranking views, parent-node filtering and 16-color tiers. Press F9 to toggle.";
            }
        }

        public void OnEnabled() { _settings = ModSettings.Load(); }

        public void OnDisabled()
        {
            DistrictFinanceHub.Dispose();
            _settings = null;
        }

        public void OnSettingsUI(UIHelperBase helper)
        {
            // 每次都重新读一遍：面板顶部的「语言」按钮会直接写设置文件，
            // 这里若不重载，「当前语言」就一直停在打开选项前的那份快照上。
            _settings = ModSettings.Load();

            helper.AddGroup("⚙️ Settings / 设置");
            helper.AddSpace(2);

            // 更新间隔（计算间隔；密度遍历单轮 = 值×5）
            UISlider autoSlider = helper.AddSlider(
                "Update interval / 更新间隔",
                1f, 30f, 1f, _settings.UpdateInterval,
                value => { _settings.UpdateInterval = (int)value; _settings.Save(); }) as UISlider;

            helper.AddSpace(16);

            UITextComponent autoField = helper.AddButton(
                "更新间隔 / Update interval: " + _settings.UpdateInterval + "s · 点击应用",
                () => { _settings.Save(); }) as UITextComponent;

            helper.AddSpace(10);

            if (autoSlider != null && autoField != null)
            {
                autoSlider.eventValueChanged += (comp, val) =>
                {
                    autoField.text = "更新间隔 / Update interval: " + ((int)val).ToString() + "s · 点击应用";
                };
            }

            helper.AddCheckbox(
                "排序是否包含直辖区划 / Include direct-admin in ranking",
                _settings.IncludeDirect,
                value => { _settings.IncludeDirect = value; _settings.Save(); });

            helper.AddSpace(4);

            helper.AddCheckbox(
                "显示调试信息 / Show debug info",
                _settings.ShowDebug,
                value => { _settings.ShowDebug = value; _settings.Save(); });

            helper.AddSpace(4);

            helper.AddSpace(16);

            // 现实化数据（显示单位/换算）
            string[] displayOptions = { "Vanilla weekly / 原版周化", "Vanilla yearly / 原版年化", "RMB yearly / 人民币年化", "USD yearly / 美元年化" };
            UIDropDown displayDrop = helper.AddDropdown("Realistic data / 现实化数据",
                displayOptions, _settings.DisplayMode,
                value => { _settings.DisplayMode = value; _settings.Save(); }) as UIDropDown;
            if (displayDrop != null) displayDrop.width += 100f; // 选项窗口控件加宽 100px

            helper.AddSpace(6);

            // Language buttons
            string langCur = _settings.Language;
            string curName = langCur == "en" ? "English" : "中文";
            helper.AddGroup(" Language / 语言 (current: " + curName + ")");
            helper.AddButton("中文",
                () => { _settings.Language = "zh"; _settings.AutoLanguage = false; _settings.Save(); Loc.Lang = "zh"; });
            helper.AddButton("English",
                () => { _settings.Language = "en"; _settings.AutoLanguage = false; _settings.Save(); Loc.Lang = "en"; });

            helper.AddSpace(6);

            // Panel hotkey buttons
            {
                string cur = _settings.PanelKey;
                string[] keys = { "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "Tab", "BackQuote" };
                helper.AddGroup(" Panel hotkey / 面板快捷键 (current: " + cur + ")");
                foreach (string k in keys)
                {
                    string cap = k;
                    helper.AddButton(k + (cur == k ? " ★" : ""),
                        () => { _settings.PanelKey = cap; _settings.Save(); });
                }
            }

            helper.AddSpace(8);

            helper.AddGroup("ℹ️ About");
            helper.AddTextfield("Version 3.0",
                "Use the vanilla district tool to paint/select districts.\n" +
                "Press F9 to toggle the standalone panel:\n" +
                "view finance and assign hierarchy levels there.\n" +
                "No game logic is modified — read-only finance data.",
                s => { }, s => { });
        }

    }
}
