using ColossalFramework;
using UnityEngine;

namespace DistrictFinanceManager
{
    /// <summary>
    /// 核心枢纽 — 管理原版区划层级关系 + 财务计算。
    /// 不创建自定义区域，不修改游戏逻辑。
    /// </summary>
    public class DistrictFinanceHub : MonoBehaviour
    {
        private static DistrictFinanceHub _instance;
        public static DistrictFinanceHub Instance { get { return _instance; } }

        public static void Dispose()
        {
            if (_instance != null)
            {
                if (_instance.Hierarchy != null)
                    DistrictDataStore.Save(_instance.Hierarchy, _instance.SaveName);
                _instance.Hierarchy = null;
                Destroy(_instance.gameObject);
                _instance = null;
            }
        }

        public DistrictHierarchy Hierarchy;
        public DistrictFinanceCalculator Calculator;
        public ModSettings Settings { get; private set; }
        public string SaveName { get; private set; }
        public System.Collections.Generic.List<GroupData> Groups = new System.Collections.Generic.List<GroupData>();

        public ushort SelectedID;
        public int EditingLevel = 1;

        /// <summary>当前存档的有效居民/工人权重（per-save，未保存时回退全局设置）。</summary>
        public float CurrentResWeight = 0.5f;
        public float CurrentWorkWeight = 3f;

        /// <summary>原版区划工具当前选中的区划 ID（byte，0 表示未选中）。</summary>
        public byte SelectedVanillaDistrict { get; set; }

        private float _saveTimer;
        private bool _dirty;
        private float _densityTick;

        private void Awake()
        {
            _instance = this;
            Settings = ModSettings.Load();
            Calculator = new DistrictFinanceCalculator();
            SaveName = MakeSaveName();
            Hierarchy = DistrictDataStore.Load(SaveName);
            Groups = DistrictDataStore.LoadGroups(SaveName);
            CleanupInvalidHierarchy();
            Debug.Log("[DFM] Save key='" + SaveName + "' levels=" +
                (Hierarchy != null ? Hierarchy.LevelOf.Count : -1) +
                " metaId='" + (Singleton<SimulationManager>.instance != null &&
                    Singleton<SimulationManager>.instance.m_metaData != null
                    ? Singleton<SimulationManager>.instance.m_metaData.m_gameInstanceIdentifier : "") + "'");
            float savedRes, savedWor;
            if (DistrictDataStore.TryLoadWeights(SaveName, out savedRes, out savedWor))
            {
                CurrentResWeight = savedRes;
                CurrentWorkWeight = savedWor;
            }
            else
            {
                CurrentResWeight = Settings.ResidentWeight;
                CurrentWorkWeight = Settings.WorkerWeight;
            }
        }

        /// <summary>
        /// 清理引用已不存在区划的层级条目（旧存档 / 换地图 / 区划被删时数据会残留）。
        /// 无效条目会导致点击时读取区划数据出错，这里加载后自动移除。
        /// </summary>
        private void CleanupInvalidHierarchy()
        {
            try
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                if (dm == null || Hierarchy == null) return;
                District[] buf = dm.m_districts.m_buffer;
                var invalid = new System.Collections.Generic.List<ushort>();
                foreach (var kv in Hierarchy.LevelOf)
                {
                    ushort id = kv.Key;
                    if (id >= buf.Length || (buf[id].m_flags & District.Flags.Created) == 0)
                        invalid.Add(id);
                }
                if (invalid.Count > 0)
                {
                    foreach (ushort id in invalid) Hierarchy.Remove(id);
                    MarkDirty();
                    Debug.Log("[DFM] Removed " + invalid.Count + " invalid hierarchy entries");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[DFM] CleanupInvalidHierarchy failed: " + ex.Message);
            }
        }

        /// <summary>当前存档的居民权重（保存过用存档值，否则用全局设置）。</summary>
        public float GetEffectiveResWeight()
        {
            return CurrentResWeight;
        }

        /// <summary>当前存档的工人权重（保存过用存档值，否则用全局设置）。</summary>
        public float GetEffectiveWorkWeight()
        {
            return CurrentWorkWeight;
        }

        /// <summary>把居民/工人权重保存到当前存档，并清缓存让下一帧重算。</summary>
        public void SaveCurrentWeights(float resWeight, float worWeight)
        {
            CurrentResWeight = resWeight;
            CurrentWorkWeight = worWeight;
            DistrictDataStore.SaveWeights(resWeight, worWeight, SaveName);
            if (Calculator != null) Calculator.ClearCache();
            Debug.Log("[DFM] Weights saved for " + SaveName + ": res=" + resWeight + " wor=" + worWeight);
        }

        /// <summary>层级数据按存档区分：用存档唯一标识作为文件名 key，互不覆盖。</summary>
        private static string MakeSaveName()
        {
            try
            {
                SimulationManager sm = Singleton<SimulationManager>.instance;
                if (sm != null && sm.m_metaData != null)
                {
                    string id = sm.m_metaData.m_gameInstanceIdentifier;
                    if (!string.IsNullOrEmpty(id))
                        return "district_hierarchy_" + id;
                    if (!string.IsNullOrEmpty(sm.m_metaData.m_CityName))
                        return "district_hierarchy_" + sm.m_metaData.m_CityName;
                }
            }
            catch { }
            return "district_hierarchy_unsaved"; // 未保存新游戏用独立名，避免读到旧全局数据
        }

        private void Update()
        {
            // 每秒遍历一部分建筑，自动保存间隔秒完成整体密度遍历（避免卡顿）
            _densityTick -= Time.deltaTime;
            if (_densityTick <= 0f)
            {
                _densityTick = 1f;
                if (Calculator != null) Calculator.TickDensityBuild();
            }

            if (_dirty)
            {
                _saveTimer -= Time.deltaTime;
                if (_saveTimer <= 0f)
                {
                    DistrictDataStore.Save(Hierarchy, SaveName);
                    DistrictDataStore.SaveGroups(Groups, SaveName);
                    _dirty = false;
                }
            }
        }

        private void OnDestroy()
        {
            if (_dirty && Hierarchy != null)
            {
                DistrictDataStore.Save(Hierarchy, SaveName);
                DistrictDataStore.SaveGroups(Groups, SaveName);
            }
            if (_instance == this) _instance = null;
        }

        public void RefreshSettings() { Settings = ModSettings.Load(); }

        public void MarkDirty()
        {
            _dirty = true;
            _saveTimer = Settings.UpdateInterval;
        }

        /// <summary>获取原版区划名</summary>
        public string GetVanillaDistrictName(ushort id)
        {
            if (id == 0) return "(none)";
            try
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                return dm.GetDistrictName(id);
            }
            catch { return "District #" + id; }
        }

        /// <summary>获取原版区划列表</summary>
        public ushort[] GetVanillaDistricts()
        {
            try
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                District[] buf = dm.m_districts.m_buffer;
                uint size = dm.m_districts.m_size;
                var list = new System.Collections.Generic.List<ushort>();
                for (uint i = 1; i < size; i++)
                {
                    if ((buf[i].m_flags & District.Flags.Created) != 0)
                        list.Add((ushort)i);
                }
                return list.ToArray();
            }
            catch { return new ushort[0]; }
        }
    }
}
