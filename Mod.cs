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

            helper.AddCheckbox(
                "打开存档时自动显示面板 / Show panel on save load",
                _settings.ShowOnLoad,
                value => { _settings.ShowOnLoad = value; _settings.Save(); });

            helper.AddSpace(4);

            // 「自定义政府投资额」的记账周刻度（用户 2026-09-28）：勾选后按 RealTime 日历周分期。
            // 勾选状态立即写设置；已有分期由面板那条路径换锚 —— 这里也顺手换一次（选项界面也能改）
            helper.AddCheckbox(
                "自定义投资额跟随 RealTime 日历 / Custom investment follows the RealTime calendar",
                _settings.InvestFollowRealTime,
                value =>
                {
                    _settings.InvestFollowRealTime = value;
                    _settings.Save();
                    DistrictFinanceHub hub = DistrictFinanceHub.Instance;
                    if (hub != null)
                    {
                        // ⚠️ **必须先让 Hub 重新读一遍设置**（2026-09-29 核查文档 1.1，真 bug）：
                        //    选项界面这份 `_settings` 与 `hub.Settings` 是**两个对象**，而
                        //    `ReanchorInvestments` 的 `InvestWeek.ScaleName` 读的是 **hub.Settings** ——
                        //    不刷新的话那一刻它还是旧值 → `target == _investScale` → 提前 return，
                        //    换锚被**静默跳过**；接着新录入的分期按新刻度、`.inv` 文件头却还是旧刻度，
                        //    一档混两把尺 → 下次读档按错误 delta 整体平移（RealTime 档能差几百周，账被挪出窗口）。
                        hub.RefreshSettings();
                        hub.ReanchorInvestments("选项界面");
                    }
                });

            helper.AddSpace(4);

            helper.AddSpace(16);

            // 统计模式：拆成「货币 × 周期」两个独立的轴（面板右上角也有对应的两个循环按键）
            string[] curOptions = { "Vanilla kr / 原版", "RMB / 人民币", "USD / 美元" };
            UIDropDown curDrop = helper.AddDropdown("Currency / 货币",
                curOptions, _settings.DisplayCurrency,
                value => { _settings.DisplayCurrency = value; _settings.Save(); }) as UIDropDown;
            if (curDrop != null) curDrop.width += 100f; // 选项窗口控件加宽 100px

            helper.AddSpace(6);

            string[] perOptions = { "Week / 周", "Month / 月", "Quarter / 季", "Year / 年", "5 Years / 5年", "10 Years / 10年" };
            UIDropDown perDrop = helper.AddDropdown("Period / 周期",
                perOptions, _settings.DisplayPeriod,
                value => { _settings.DisplayPeriod = value; _settings.Save(); }) as UIDropDown;
            if (perDrop != null) perDrop.width += 100f;

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
