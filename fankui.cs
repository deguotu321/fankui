using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Interfaces;
using Exiled.Events.EventArgs.Player;
using PlayerStatsSystem;
using UnityEngine;
using MEC;
using static Broadcast;

namespace AttackFeedbackPlugin
{
    public class AttackFeedbackPlugin : Plugin<Config>
    {
        public override string Author => "YourName";
        public override string Name => "Enhanced Attack Feedback";
        public override Version Version => new Version(1, 3, 0);
        public override string Prefix => "AttackFeedback";

        private static readonly Dictionary<Player, DateTime> LastFeedbackTime = new Dictionary<Player, DateTime>();
        private static readonly Dictionary<Player, float> LastKillTime = new Dictionary<Player, float>();

        // RGB分离效果颜色组
        private static readonly Color[] RgbColors = new Color[]
        {
            Color.red,
            Color.green,
            Color.blue,
            new Color(1, 0.5f, 0), // 橙色
            new Color(1, 0, 1),    // 紫色
            new Color(0, 1, 1)     // 青色
        };

        public override void OnEnabled()
        {
            Exiled.Events.Handlers.Player.Hurt += OnPlayerHurt;
            Exiled.Events.Handlers.Player.Dying += OnPlayerDying;
            base.OnEnabled();
        }

        public override void OnDisabled()
        {
            Exiled.Events.Handlers.Player.Hurt -= OnPlayerHurt;
            Exiled.Events.Handlers.Player.Dying -= OnPlayerDying;
            LastKillTime.Clear();
            base.OnDisabled();
        }

        private void OnPlayerDying(DyingEventArgs ev)
        {
            if (ev.Attacker == null || ev.Player == null) return;

            ApplyFeedback(ev.Attacker, FeedbackType.Strong, true);
            LastKillTime[ev.Attacker] = Time.time;

            if (Config.EnableBloodEffect)
                Timing.RunCoroutine(SpawnBloodDecals(ev.Player.Position, Config.DeathBloodAmount, true));
        }

        private void OnPlayerHurt(HurtEventArgs ev)
        {
            if (ev.Amount <= 0) return;

            Player attacker = IsScp207Damage(ev.Player, ev.DamageHandler) ? ev.Player : ev.Attacker;
            if (attacker == null) return;

            if (LastFeedbackTime.TryGetValue(attacker, out DateTime lastTime) &&
                (DateTime.UtcNow - lastTime).TotalSeconds < Config.MinFeedbackInterval)
                return;

            LastFeedbackTime[attacker] = DateTime.UtcNow;

            var feedbackType = GetFeedbackType(ev.DamageHandler);
            ApplyFeedback(attacker, feedbackType, false);

            if (Config.EnableBloodEffect)
                Timing.RunCoroutine(SpawnBloodDecals(ev.Player.Position, Config.HurtBloodAmount, false));
        }

        private IEnumerator<float> SpawnBloodDecals(Vector3 position, int count, bool isDeath)
        {
            if (position == Vector3.zero) yield break;

            float spreadRadius = isDeath ? Config.DeathBloodSpread : Config.HurtBloodSpread;
            float sprayHeight = isDeath ? Config.DeathBloodHeight : Config.HurtBloodHeight;

            for (int i = 0; i < count; i++)
            {
                bool success = false;
                try
                {
                    float angle = UnityEngine.Random.Range(0, 360);
                    Vector3 radialDir = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                    float distance = UnityEngine.Random.Range(0.1f, spreadRadius);
                    float height = UnityEngine.Random.Range(0.1f, sprayHeight);

                    Vector3 offset = new Vector3(
                        radialDir.x * distance,
                        height,
                        radialDir.z * distance
                    );

                    Vector3 bloodPos = position + offset;

                    RaycastHit hit;
                    if (Physics.Raycast(bloodPos + Vector3.up * 2, Vector3.down, out hit, 5f))
                    {
                        bloodPos = hit.point + Vector3.up * 0.05f;
                        Vector3 surfaceNormal = hit.normal;
                        Vector3 decalDirection = (surfaceNormal + Vector3.down * 0.3f).normalized;
                        Map.PlaceBlood(bloodPos, decalDirection);
                        success = true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Blood spawn error: {ex}");
                }

                if (!success || i % 10 == 0)
                    yield return Timing.WaitForOneFrame;
                else
                    yield return Timing.WaitForSeconds(0.02f);
            }
        }

        private bool IsScp207Damage(Player player, DamageHandlerBase handler)
        {
            bool hasScp207 = player.Items.Any(item => item.Type == ItemType.SCP207);
            if (!hasScp207) return false;

            string handlerName = handler.GetType().Name;
            bool isScp207Damage = handlerName.Contains("Scp207") ||
                                handlerName.Contains("Poison") ||
                                handlerName.Contains("Ahp");

            bool isSelfDamage = handler is AttackerDamageHandler attackerHandler &&
                              attackerHandler.Attacker.PlayerId == player.Id;

            return isScp207Damage || isSelfDamage;
        }

        private FeedbackType GetFeedbackType(DamageHandlerBase handler)
        {
            if (handler is WarheadDamageHandler)
                return FeedbackType.Weak;

            if (handler is Scp049DamageHandler)
                return FeedbackType.Normal;

            string handlerName = handler.GetType().Name;
            if (handlerName.Contains("Corrod") || handlerName.Contains("Pocket"))
                return FeedbackType.Normal;

            return FeedbackType.Normal;
        }

        private void ApplyFeedback(Player player, FeedbackType type, bool isKill)
        {
            try
            {
                if (isKill && Config.EnableScreenShake)
                {
                    if (!LastKillTime.TryGetValue(player, out float lastTime) || Time.time - lastTime >= 2.0f)
                    {
                        Timing.RunCoroutine(KillEffectSequence(player));
                    }
                }

                RunFeedbackActions(player, type, isKill);

                if (!isKill && type != FeedbackType.Strong)
                {
                    Timing.CallDelayed(Config.FeedbackRepeatDelay, () =>
                        RunFeedbackActions(player, type, false));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error applying feedback: {ex}");
            }
        }

        private IEnumerator<float> KillEffectSequence(Player player)
        {
            if (!player.IsConnected || !player.IsAlive) yield break;

            // RGB特效阶段
            float rgbTimer = 0f;
            int colorIndex = 0;

            while (rgbTimer < Config.RgbEffectDuration)
            {
                if (!player.IsConnected || !player.IsAlive) yield break;

                // 三层RGB分离效果
                for (int i = 0; i < 3; i++)
                {
                    float offset = (i + 1) * 0.03f;
                    Color color = RgbColors[(colorIndex + i) % RgbColors.Length];
                    string hexColor = ColorUtility.ToHtmlStringRGBA(color);
                  
                }

                // 屏幕震动
                if (Config.EnableScreenShake)
                {
                    player.EnableEffect(EffectType.Disabled, 0.3f);
                    player.EnableEffect(EffectType.Concussed, 0.3f);
                }

                colorIndex = (colorIndex + 1) % RgbColors.Length;
                rgbTimer += Config.RgbEffectInterval;
                yield return Timing.WaitForSeconds(Config.RgbEffectInterval);
            }

            // 冲击波效果
            if (Config.EnableShockwave)
            {
                CreateShockwaveEffect(player.Position);
            }
        }

        private void CreateShockwaveEffect(Vector3 center)
        {
            // 用屏幕效果替代实际手雷创建
            foreach (Player player in Player.List)
            {
                if (Vector3.Distance(player.Position, center) < 10f)
                {
                    player.EnableEffect(EffectType.SinkHole, 0.3f);
                }
            }
        }

        private void RunFeedbackActions(Player player, FeedbackType type, bool isKill)
        {
            try
            {
                if (Config.EnableScreenShake)
                {
                    float duration = isKill ? Config.ScreenShakeDuration * 2 : Config.ScreenShakeDuration;
                    player.EnableEffect(EffectType.Concussed, duration);
                }

                if (Config.EnableSound)
                {
                    if (isKill)
                    {
                        player.PlayGunSound(ItemType.GunRevolver, 120, 220);
                    }
                    else
                    {
                        byte volume = (byte)(type == FeedbackType.Strong ? 200 : 150);
                        byte pitch = (byte)(type == FeedbackType.Strong ? 120 : 100);
                        player.PlayGunSound(type == FeedbackType.Strong ?
                            ItemType.GunE11SR : ItemType.GunCOM15, pitch, volume);
                    }
                }

                player.ShowHitMarker(isKill ? Config.HitMarkerDuration * 1.5f : Config.HitMarkerDuration);
            }
            catch (Exception ex)
            {
                Log.Error($"Feedback actions error: {ex}");
            }
        }
    }

    public enum FeedbackType
    {
        Weak,
        Normal,
        Strong
    }

    public class Config : IConfig
    {
        [Description("是否启用插件")]
        public bool IsEnabled { get; set; } = true;

        [Description("是否启用调试模式")]
        public bool Debug { get; set; } = false;

        [Description("启用/禁用音效反馈")]
        public bool EnableSound { get; set; } = true;

        [Description("启用/禁用屏幕震动")]
        public bool EnableScreenShake { get; set; } = true;

        [Description("启用/禁用血液效果")]
        public bool EnableBloodEffect { get; set; } = true;

        [Description("启用/禁用粒子效果")]
        public bool EnableParticleEffect { get; set; } = false; // 禁用粒子效果

        [Description("启用/禁用冲击波效果")]
        public bool EnableShockwave { get; set; } = true;

        [Description("最小反馈间隔(秒)")]
        public float MinFeedbackInterval { get; set; } = 0.1f;

        [Description("受伤时血液贴图数量")]
        public int HurtBloodAmount { get; set; } = 25;

        [Description("死亡时血液贴图数量")]
        public int DeathBloodAmount { get; set; } = 50;

        [Description("RGB效果持续时间(秒)")]
        public float RgbEffectDuration { get; set; } = 1.5f;

        [Description("RGB效果切换间隔(秒)")]
        public float RgbEffectInterval { get; set; } = 0.15f;

        [Description("摄像机缩放强度 (降低的FOV值)")]
        public float CameraZoomIntensity { get; set; } = 20f;

        [Description("反馈重复延迟(秒)")]
        public float FeedbackRepeatDelay { get; set; } = 0.1f;

        [Description("命中标记显示时长(秒)")]
        public float HitMarkerDuration { get; set; } = 1.0f;

        [Description("屏幕震动持续时间(秒)")]
        public float ScreenShakeDuration { get; set; } = 0.9f;

        [Description("受伤血液扩散范围 (米)")]
        public float HurtBloodSpread { get; set; } = 1.5f;

        [Description("死亡血液扩散范围 (米)")]
        public float DeathBloodSpread { get; set; } = 3.0f;

        [Description("受伤血液喷射高度 (米)")]
        public float HurtBloodHeight { get; set; } = 1.0f;

        [Description("死亡血液喷射高度 (米)")]
        public float DeathBloodHeight { get; set; } = 2.0f;
    }
}