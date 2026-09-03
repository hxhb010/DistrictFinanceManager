using System.Collections.Generic;
using UnityEngine;

namespace DistrictFinanceManager
{
    /// <summary>
    /// 原版区划的层级封装。
    /// 在原版 DistrictManager 基础上增加 parent-child 关系。
    /// 原版 District ID 直接复用，不创建新区划。
    /// </summary>

    public static class DistLevel
    {
        public const int REGION = 1;       // 市
        public const int DISTRICT = 2;     // 区县
        public const int NEIGHBOR = 3;     // 乡镇
        public const int VILLAGE = 4;      // 村社区（更小一级）

        public static string Name(int level)
        {
            switch (level)
            {
                case REGION: return Loc.T("市", "City");
                case DISTRICT: return Loc.T("区县", "District");
                case NEIGHBOR: return Loc.T("乡镇", "Town");
                case VILLAGE: return Loc.T("村社区", "Village");
                default: return "?";
            }
        }
    }

    /// <summary>
    /// 组合（Group）：任意区划的自定义组合，可命名。统计用各成员自身值，不影响层级。
    /// </summary>
    public class GroupData
    {
        public string Name = "";
        public HashSet<ushort> Members = new HashSet<ushort>();
    }

    /// <summary>
    /// 层级关系存储：每个原版 district ID → 父级 ID + 层级 + 子级列表
    /// </summary>
    public class DistrictHierarchy
    {
        public Dictionary<ushort, ushort> ParentOf = new Dictionary<ushort, ushort>();   // child → parent
        public Dictionary<ushort, List<ushort>> ChildrenOf = new Dictionary<ushort, List<ushort>>(); // parent → children
        public Dictionary<ushort, int> LevelOf = new Dictionary<ushort, int>();            // district → level

        /// <summary>设置层级和父级（自动从所有旧父级移除 + 去重，保证数据一致）。</summary>
        public void SetParent(ushort childId, ushort parentId, int childLevel)
        {
            // 从所有 ChildrenOf 列表里彻底移除 childId（清理残留/重复）
            foreach (var kv in ChildrenOf)
                kv.Value.RemoveAll(x => x == childId);

            if (parentId != 0)
            {
                if (!ChildrenOf.ContainsKey(parentId))
                    ChildrenOf[parentId] = new List<ushort>();
                if (!ChildrenOf[parentId].Contains(childId))
                    ChildrenOf[parentId].Add(childId);
            }

            ParentOf[childId] = parentId;
            LevelOf[childId] = childLevel;
        }

        /// <summary>获取指定层级的已分配区划列表</summary>
        public List<ushort> GetDistrictsByLevel(int level)
        {
            List<ushort> result = new List<ushort>();
            foreach (var kv in LevelOf)
                if (kv.Value == level) result.Add(kv.Key);
            return result;
        }

        /// <summary>获取根区划（没有父级的市）</summary>
        public List<ushort> GetRootRegions()
        {
            List<ushort> roots = new List<ushort>();
            foreach (var kv in LevelOf)
            {
                if (kv.Value == DistLevel.REGION)
                {
                    // 根大区 = 没有父级或父级为0
                    if (!ParentOf.ContainsKey(kv.Key) || ParentOf[kv.Key] == 0)
                        roots.Add(kv.Key);
                }
            }
            return roots;
        }

        /// <summary>获取所有无父级的已分配区划（任意层级），用于树显示，避免孤立节点丢失。</summary>
        public List<ushort> GetRootNodes()
        {
            List<ushort> roots = new List<ushort>();
            foreach (var kv in LevelOf)
            {
                if (!ParentOf.ContainsKey(kv.Key) || ParentOf[kv.Key] == 0)
                    roots.Add(kv.Key);
            }
            return roots;
        }

        /// <summary>获取子区划列表</summary>
        public List<ushort> GetChildren(ushort parentId)
        {
            if (ChildrenOf.ContainsKey(parentId))
                return ChildrenOf[parentId];
            return new List<ushort>();
        }

        /// <summary>判断 id 是否为 ancestor 的（严格）子孙。</summary>
        public bool IsDescendantOf(ushort id, ushort ancestor)
        {
            while (ParentOf.ContainsKey(id) && ParentOf[id] != 0)
            {
                id = ParentOf[id];
                if (id == ancestor) return true;
            }
            return false;
        }

        /// <summary>移除区划的层级分配</summary>
        public void Remove(ushort id)
        {
            // 从父级移除
            if (ParentOf.ContainsKey(id))
            {
                ushort pid = ParentOf[id];
                if (ChildrenOf.ContainsKey(pid))
                    ChildrenOf[pid].Remove(id);
                ParentOf.Remove(id);
            }

            // 递归移除所有子节点
            if (ChildrenOf.ContainsKey(id))
            {
                foreach (ushort child in new List<ushort>(ChildrenOf[id]))
                    Remove(child);
                ChildrenOf.Remove(id);
            }

            LevelOf.Remove(id);
        }
    }
}
