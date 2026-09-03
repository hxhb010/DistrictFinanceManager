using HarmonyLib;
using ICities;
using UnityEngine;

namespace DistrictFinanceManager
{
    public class LoadingExtension : LoadingExtensionBase
    {
        private static bool _patched;

        public override void OnLevelLoaded(LoadMode mode)
        {
            if (!_patched)
            {
                _patched = true;
                new Harmony("DistrictFinanceManager").PatchAll();
            }

            if (mode != LoadMode.NewGame && mode != LoadMode.LoadGame) return;

            DistrictFinanceHub.Dispose();

            var go = new GameObject("DistrictFinanceManager");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<DistrictFinanceHub>();
            go.AddComponent<DistrictFinancePanel>();

            Debug.Log("[DFM] Initialized — F9 to toggle panel");
        }

        public override void OnLevelUnloading()
        {
            DistrictFinanceHub.Dispose();
            Debug.Log("[DFM] Unloaded");
        }
    }

    /// <summary>
    /// 阻止鼠标位于面板上时滚轮缩放游戏镜头。
    /// 镜头缩放走 CameraController.HandleScrollWheelEvent（读 Input.GetAxis，与 IMGUI 事件无关），需 Harmony 补丁。
    /// </summary>
    [HarmonyPatch(typeof(CameraController), "HandleScrollWheelEvent")]
    public static class CameraZoomPatch
    {
        private static bool Prefix()
        {
            return !DistrictFinancePanel.IsMouseOverPanel();
        }
    }
}
