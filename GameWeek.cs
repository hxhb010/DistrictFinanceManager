using System;
using ColossalFramework;
using UnityEngine;

namespace DistrictFinanceManager
{
    /// <summary>
    /// 原版（RealTime 等真实时间模组转换前）游戏周计数器。
    ///
    /// 原理（反汇编 Assembly-CSharp.dll 得到，非猜测）：
    ///   · SimulationManager.m_currentFrameIndex : public uint，每模拟步 +1（原版 SimulationStep() 写）
    ///   · SimulationManager.SIMULATION_WEEK_FRAMES = 4096（.cctor: ldc.i4 4096 → stsfld）
    ///   · 原版 m_timePerFrame = TimeSpan(1476562500) ticks = 147.65625 秒/帧
    ///     4096 × 147.65625s = 604800s = 7×86400s → 4096 帧恰好 = 7 个原版游戏日 = 1 周
    ///
    /// 为什么不用游戏日历 m_currentGameTime：
    ///   m_currentGameTime = new DateTime(m_currentFrameIndex * m_timePerFrame.Ticks + m_timeOffsetTicks)
    ///   RealTime 模组改写了 m_timePerFrame → 整条日历公式（即日历）被重写。
    ///   而 RealTime 从不引用 SIMULATION_WEEK_FRAMES / SIMULATION_DAY_FRAMES，也不写 m_currentFrameIndex，
    ///   所以「帧计数」完全免疫。
    /// </summary>
    public static class GameWeek
    {
        /// <summary>原版每帧的游戏时间（TimeSpan ticks）。取自原版 SimulationManager.Awake() 的 IL。</summary>
        public const long VanillaTicksPerFrame = 1476562500L;

        private const uint VanillaFramesPerWeek = 4096;
        private static uint _framesPerWeek;
        private static bool _checked;

        /// <summary>多少帧 = 一个原版游戏周。默认 4096；常量被其它模组篡改时告警并采用其值。</summary>
        public static uint FramesPerWeek
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    uint v = VanillaFramesPerWeek;
                    try
                    {
                        uint g = SimulationManager.SIMULATION_WEEK_FRAMES;
                        if (g != 0) v = g;
                    }
                    catch { }
                    if (v != VanillaFramesPerWeek)
                        Debug.LogWarning("[DFM] SIMULATION_WEEK_FRAMES=" + v + "（原版 4096），疑似被其它模组修改");
                    _framesPerWeek = v;
                }
                return _framesPerWeek;
            }
        }

        private static SimulationManager Sm { get { return Singleton<SimulationManager>.instance; } }

        /// <summary>当前模拟帧号。游戏暂停时不推进。RealTime 不写此字段。</summary>
        public static uint CurrentFrame
        {
            get { SimulationManager sm = Sm; return sm != null ? sm.m_currentFrameIndex : 0u; }
        }

        /// <summary>当前原版游戏周序号（自开局起，0 = 第 1 周）。</summary>
        public static uint CurrentWeek { get { return CurrentFrame / FramesPerWeek; } }

        /// <summary>当前原版游戏周序号（从 1 开始，便于显示）。</summary>
        public static uint CurrentWeekNumber { get { return CurrentWeek + 1u; } }

        /// <summary>原版日历日期（不受 RealTime 改写 m_timePerFrame 影响），仅供显示。</summary>
        public static DateTime VanillaDate
        {
            get
            {
                SimulationManager sm = Sm;
                if (sm == null || sm.m_metaData == null) return default(DateTime);
                return sm.m_metaData.m_startingDateTime
                     + TimeSpan.FromTicks(VanillaTicksPerFrame * (long)CurrentFrame);
            }
        }

        /// <summary>
        /// 周长检测：返回 true 表示"刚跨过一个原版游戏周"。
        /// 首次调用只记录基线、不触发；读档后把 lastWeek 还原即可续接。
        /// </summary>
        public static bool Tick(ref uint lastWeek)
        {
            uint w = CurrentWeek;
            if (lastWeek == uint.MaxValue) { lastWeek = w; return false; }
            if (w == lastWeek) return false;
            lastWeek = w;
            return true;
        }
    }
}
