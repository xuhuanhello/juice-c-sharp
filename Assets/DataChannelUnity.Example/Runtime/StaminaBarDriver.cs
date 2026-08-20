using UnityEngine;
using UnityEngine.UI;
using FishNet.Demo.Prediction.CharacterControllers;

namespace DataChannelUnity.Example
{
    /// <summary>
    /// 驱动 demo 的体力条。
    ///
    /// **为什么要我们写：上游那份是空的。** FishNet 4.7.2 的
    /// `Demos/Prediction/CharacterController/Scripts/StaminaCanvas.cs` 全部内容是
    /// `//Intentionally left blank.`（30 字节），但场景里 `StaminaCanvas` 物体上仍挂着
    /// 指向它的组件引用 —— `.meta` 的 GUID 留着、类没了，于是 Unity 报
    /// 「The referenced script (Unknown) on this Behaviour is missing!」，体力条也永远
    /// 停在满格。官方 main 分支那份也是 30 字节，两边逐字节相同，所以这不是包缓存坏了。
    ///
    /// 不叫 `StaminaCanvas` 是刻意的：同名会让人以为我们在覆盖上游那份，而实际是**补**
    /// 一个上游没有实现的东西。
    /// </summary>
    [RequireComponent(typeof(Canvas))]
    public sealed class StaminaBarDriver : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("体力条那张 Filled 型 Image。留空则取第一个子物体上的 Image。")]
        private Image _bar;

        private CharacterControllerPrediction _owner;

        private void Awake()
        {
            if (_bar == null && transform.childCount > 0)
                _bar = transform.GetChild(0).GetComponent<Image>();
        }

        // 本地角色 spawn 时上游会 raise 这个静态事件（`CharacterControllerPrediction
        // .cs:109`），所以不用自己轮询找 owner。
        private void OnEnable() => CharacterControllerPrediction.OnOwner += HandleOwner;

        private void OnDisable() => CharacterControllerPrediction.OnOwner -= HandleOwner;

        private void HandleOwner(CharacterControllerPrediction owner) => _owner = owner;

        private void Update()
        {
            if (_bar == null) return;

            // 没有 owner 时把条藏起来，而不是留在满格 —— 满格会被误读成「有体力」。
            if (_owner == null)
            {
                if (_bar.enabled) _bar.enabled = false;
                return;
            }

            if (!_bar.enabled) _bar.enabled = true;
            _bar.fillAmount = Mathf.Clamp01(
                _owner.Stamina / CharacterControllerPrediction.Maximum_Stamina);
        }
    }
}
