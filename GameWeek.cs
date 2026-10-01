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
        /// 游戏当前是否**夜晚**（用户 2026-09-28 的「只统计白天数据」用）。
        /// 取 `SimulationManager.m_isNightTime`（原版 SimulationManager.Update 每帧刷新，
        /// 由 `m_dayTimeFrame` / `DAYTIME_FRAMES` 决定；RealTime 改写了昼夜钟，实测它引用了
        /// `m_isNightTime` / `m_dayTimeFrame` / `m_currentDayTimeHour` → 装了 RealTime 后这个标志
        /// 仍然是"玩家看到的天黑了"）。取不到一律当白天（宁可多写数据，也别把白天判成夜里不写）。
        /// </summary>
        public static bool IsNight
        {
            get
            {
                try
                {
                    SimulationManager sm = Sm;
                    return sm != null && sm.m_isNightTime;
                }
                catch { return false; }
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

        /// <summary>
        /// **RealTime 日历周**（用户 2026-09-28）：按游戏日历（`SimulationManager.m_currentGameTime` ——
        /// RealTime 改写 `m_timePerFrame` 后，这个字段就是玩家在游戏里看到的日期）从**开局日期**起算的
        /// 第几个七天。
        ///
        /// 与 <see cref="CurrentWeek"/> 的关系：
        ///   · 原版：4096 帧 = 7 游戏日，所以两者几乎相等（差一个开局偏移）；
        ///   · 装了 RealTime：它的「一天」按真实时间拉长（`Real Time.xml` 里的 DayTimeSpeed/NightTimeSpeed），
        ///     日历推进得比帧计数慢得多 —— 帧周几秒一个，日历周可能要玩几小时，两者能差出几百。
        /// 反汇编依据：`SimulationManager.Update` 里
        ///   m_currentGameTime = new DateTime(帧号 × m_timePerFrame.Ticks + m_referenceTimer × … + m_timeOffsetTicks)
        /// 而 RealTime.dll 引用了 m_currentGameTime 与 m_timePerFrame（不碰 m_startingDateTime），即日历由它驱动。
        ///
        /// 读不到（无 SimulationManager / 未开局 / 日历或开局日期为 0）时**退回帧周**，保证永远返回一个可用的数。
        /// </summary>
        public static uint CalendarWeek
        {
            get
            {
                try
                {
                    SimulationManager sm = Sm;
                    if (sm == null || sm.m_metaData == null) return CurrentWeek;
                    DateTime now = sm.m_currentGameTime;
                    if (now.Ticks <= 0L) return CurrentWeek;
                    DateTime start = sm.m_metaData.m_startingDateTime;
                    if (start.Ticks <= 0L) return CurrentWeek;
                    double days = (now.Date - start.Date).TotalDays;
                    if (days < 0.0) days = 0.0;          // 开局当天钟点比开局日期早 → 不算负数周
                    return (uint)(days / 7.0);
                }
                catch { return CurrentWeek; }
            }
        }
    }

    /// <summary>
    /// 「自定义政府投资额」的**记账周刻度**（用户 2026-09-28：「设置里加入自定义投资额是否跟随 realtime 日历，
    /// 勾选后把自定义投资额记入步长改为按 realtime 日历的周」）。
    ///
    /// 默认 = <see cref="GameWeek.CurrentWeek"/>（原版帧周，对 RealTime 免疫，与周库同一把尺子）；
    /// 勾选后 = <see cref="GameWeek.CalendarWeek"/>（RealTime 日历周，玩家在游戏里看到的日期）。
    ///
    /// ⚠️ 分期数（周/月/季/年/5年）、记账周号、统计窗口下界、"待计入"判定这**四处必须同源** ——
    /// 任何一处还用 GameWeek.CurrentWeek，两种刻度下数字就会对不上（差几百周，全被算成"很久以前"或"未来"）。
    /// 刻度本身按存档记在 .inv 文件头，刻度变了要换锚（见 Hub.ReanchorInvestments）。
    /// </summary>
    internal static class InvestWeek
    {
        /// <summary>是否跟随 RealTime 日历（读设置，默认 false）。</summary>
        public static bool FollowRealTime
        {
            get
            {
                DistrictFinanceHub h = DistrictFinanceHub.Instance;
                return h != null && h.Settings != null && h.Settings.InvestFollowRealTime;
            }
        }

        /// <summary>当前投资周（按设置取帧周或日历周）。录入/窗口/待计入一律用它。</summary>
        public static uint Now
        {
            get { return FollowRealTime ? GameWeek.CalendarWeek : GameWeek.CurrentWeek; }
        }

        /// <summary>原版帧周（换锚时取"旧刻度"的当前值用）。</summary>
        public static uint Frame { get { return GameWeek.CurrentWeek; } }

        /// <summary>RealTime 日历周（换锚时用）。</summary>
        public static uint Calendar { get { return GameWeek.CalendarWeek; } }

        /// <summary>当前刻度名（写进 .inv 文件头）。</summary>
        public static string ScaleName
        {
            get { return FollowRealTime ? DistrictDataStore.InvestScaleCal : DistrictDataStore.InvestScaleFrame; }
        }
    }
}
