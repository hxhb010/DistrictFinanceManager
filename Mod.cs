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
            if (_settings == null) _settings = ModSettings.Load();

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

            helper.AddSpace(16);

            // 居民/工人权重（GDP 贡献）
            float initialRes = _settings.ResidentWeight;
            float initialWor = _settings.WorkerWeight;
            DistrictFinanceHub h0 = DistrictFinanceHub.Instance;
            if (h0 != null)
            {
                initialRes = h0.GetEffectiveResWeight();
                initialWor = h0.GetEffectiveWorkWeight();
            }

            UISlider resSlider = helper.AddSlider(
                "Resident weight / 居民权重",
                0f, 1f, 0.01f, initialRes,
                value => { _settings.ResidentWeight = value; _settings.Save(); }) as UISlider;

            helper.AddSpace(12);

            UITextComponent resField = helper.AddButton(
                "居民贡献权重 / Resident contribution: " + initialRes.ToString("0.00"),
                () => { }) as UITextComponent;

            helper.AddSpace(8);

            UISlider worSlider = helper.AddSlider(
                "Worker weight / 工人权重",
                0f, 5f, 0.01f, initialWor,
                value => { _settings.WorkerWeight = value; _settings.Save(); }) as UISlider;

            UITextComponent worField = helper.AddButton(
                "工人贡献权重 / Worker contribution: " + initialWor.ToString("0.00"),
                () => { }) as UITextComponent;

            helper.AddSpace(10);

            if (resSlider != null && resField != null)
                resSlider.eventValueChanged += (comp, val) => { resField.text = "居民贡献权重 / Resident contribution: " + val.ToString("0.00"); };
            if (worSlider != null && worField != null)
                worSlider.eventValueChanged += (comp, val) => { worField.text = "工人贡献权重 / Worker contribution: " + val.ToString("0.00"); };

            // 保存当前两个权重到当前存档
            helper.AddButton("Click to apply / 点击应用",
                () =>
                {
                    DistrictFinanceHub h = DistrictFinanceHub.Instance;
                    if (h != null)
                        h.SaveCurrentWeights(_settings.ResidentWeight, _settings.WorkerWeight);
                    else
                        Debug.Log("[DFM] 未进入存档，无法保存权重");
                });

            helper.AddButton("Reset to default (Res 0.5, Wor 3) / 恢复默认（居民0.5 工人3）",
                () =>
                {
                    _settings.ResidentWeight = 0.5f;
                    _settings.WorkerWeight = 3f;
                    _settings.Save();
                    if (resSlider != null) resSlider.value = 0.5f;
                    if (worSlider != null) worSlider.value = 3f;
                    DistrictFinanceHub h = DistrictFinanceHub.Instance;
                    if (h != null) h.SaveCurrentWeights(0.5f, 3f);
                });

            helper.AddSpace(10);

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
                () => { _settings.Language = "zh"; _settings.Save(); });
            helper.AddButton("English",
                () => { _settings.Language = "en"; _settings.Save(); });

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
            helper.AddTextfield("Version 2.0",
                "Use the vanilla district tool to paint/select districts.\n" +
                "Press F9 to toggle the standalone panel:\n" +
                "view finance and assign hierarchy levels there.\n" +
                "No game logic is modified — read-only finance data.",
                s => { }, s => { });
        }

    }
}
