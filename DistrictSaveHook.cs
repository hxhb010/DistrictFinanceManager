using ICities;
using UnityEngine;

namespace DistrictFinanceManager
{
    /// <summary>
    /// **只在游戏存档的那一刻**通知本模组把内存缓存落盘（用户 2026-09-28：
    /// 「用户回档不保存产生的数据就不要覆盖数据库，可以每次都作为缓存，保存时覆盖」）。
    ///
    /// 为什么要有这个类：
    ///   之前本模组自己定时（UpdateInterval 秒）把 .hier / .grp / .inv / .commute / .series 写盘。
    ///   于是「玩一段 → 不存档直接回档/读旧档」时，那条**被丢弃的时间线**的数据已经覆盖了数据库，
    ///   回到存档点后统计里混着未来的记录（区划周库、投资分期、墓碑都会脏）。
    ///   现在改成：内存里的东西一律只是**缓存**，只有游戏真的存档了才写文件；
    ///   回档（读档）时缓存直接丢掉，数据库原样不动 —— 它永远等于「上一次存档时的样子」。
    ///
    /// 怎么知道游戏存档了：CS1 的存档流程会调用所有 `ISerializableDataExtension` 的 `OnSaveData()`
    /// （反汇编 `SerializableDataWrapper.OnSaveData`：遍历 `m_SerializableDataExtensions` 逐个调用；
    /// 实例由 `PluginManager.GetImplementations<ISerializableDataExtension>()` 从各插件程序集里扫出来，
    /// 和本模组已经在用的 `LoadingExtension` 是同一套机制 → 放在本程序集里就会被找到，不需要注册）。
    ///
    /// ⚠️ `OnSaveData()` 可能在**别的线程**上被调用（存档是后台线程做的），所以这里**只置一个标志位**，
    /// 真正的文件写在主线程的 Update 里做（见 `DistrictFinanceHub.FlushCache`）——
    /// 不在这里碰任何 Unity / 游戏对象，避免线程问题。
    /// </summary>
    public class DistrictSaveHook : SerializableDataExtensionBase
    {
        public override void OnSaveData()
        {
            DistrictFinanceHub.NotifyGameSaved();
        }
    }
}
