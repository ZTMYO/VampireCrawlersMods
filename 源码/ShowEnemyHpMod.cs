using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Nosebleed.Pancake.Models;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace ShowEnemyHpMod
{
    [BepInPlugin("com.zc.showenemyhpmod", "Enemy HP Display Mod", "1.0.0")]
    public class ShowEnemyHpMod : BasePlugin
    {
        public static ShowEnemyHpMod Instance;

        public PlayerModel CachedPlayer;
        public EnemyEncounterModel CapturedEncounter;
        public readonly List<EnemyModel> ActiveEnemies = new List<EnemyModel>();
        
        // --- 响应式延迟逻辑变量 ---
        public double LastRealHp = 0;        // 上一帧的真实血量
        public float DisplayedPercent = 0f;  // 进度条当前渲染的平滑百分比
        public float LastChangeTime = 0f;    // 上一次真实数值变化的时间
        public float UpdateDelay = 0.6f;     // 触发进度条更新前的停顿时间（秒）
        public float LerpSpeed = 5f;         // 进度条追赶速度
        public double LastKnownMaxHp = 0;    // 辅助变量：记录最后的总血量，防止归零时 UI 闪烁

        public GUIStyle HpStyle;
        public GUIStyle ShadowStyle;
        public GUIStyle BgStyle;
        public GUIStyle RedBarStyle;
        public GUIStyle BorderStyle;
        public Texture2D BgTexture;
        public Texture2D RedTexture;
        public Texture2D BorderTexture;

        public void GetTotalHp(out double currentHp, out double maxHp)
        {
            currentHp = 0;
            maxHp = 0;

            // 移除已销毁的引用，但保留已死亡的敌人以维持 MaxHp 的稳定性，防止进度条跳变
            ActiveEnemies.RemoveAll(e => e == null);

            if (ActiveEnemies.Count > 0)
            {
                foreach (var enemy in ActiveEnemies)
                {
                    // 汇总所有记录到的敌人的最大血量
                    maxHp += (double)enemy.MaxHealth;
                    // 只有存活的敌人才计入当前血量
                    if (!enemy.IsDead)
                    {
                        currentHp += enemy.Health;
                    }
                }
            }

            // 如果 ActiveEnemies 还没抓到东西，尝试使用战斗模型的属性作为保底
            if (maxHp <= 0.001 && CapturedEncounter != null)
            {
                try
                {
                    // 使用 try-catch 包裹，防止某些属性在特定版本中缺失导致崩溃
                    currentHp = CapturedEncounter.CurrentHealth;
                    maxHp = CapturedEncounter.MaxHealth;
                }
                catch { }
            }

            if (maxHp > 0)
            {
                LastKnownMaxHp = maxHp;
            }
            else
            {
                maxHp = LastKnownMaxHp;
            }
        }

        public override void Load()
        {
            Instance = this;
            Log.LogInfo("Enemy HP Display Mod loaded.");

            // 0. 注册自定义 MonoBehaviour 类型
            ClassInjector.RegisterTypeInIl2Cpp<HpUiRunner>();

            // 1. 初始化文字样式
            HpStyle = new GUIStyle();
            HpStyle.fontSize = 18;
            HpStyle.normal.textColor = Color.white;
            HpStyle.fontStyle = FontStyle.Bold;
            HpStyle.alignment = TextAnchor.MiddleCenter;

            ShadowStyle = new GUIStyle(HpStyle);
            ShadowStyle.normal.textColor = Color.black;

            // 2. 初始化血条样式
            BgStyle = new GUIStyle();
            RedBarStyle = new GUIStyle();
            BorderStyle = new GUIStyle();

            // 3. 注入 Harmony 补丁
            var harmony = new Harmony("com.zc.showenemyhpmod");
            harmony.PatchAll();

            // 4. 创建 UI 显示器
            GameObject bootstrapper = new GameObject("EnemyHpDisplay");
            bootstrapper.AddComponent<HpUiRunner>();
            Object.DontDestroyOnLoad(bootstrapper);

            Log.LogInfo("Enemy HP UI ready.");
        }

        // 辅助方法：创建纯色纹理
        public Texture2D CreateTexture(int width, int height, Color col)
        {
            Texture2D result = new Texture2D(width, height);
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    result.SetPixel(x, y, col);
                }
            }
            result.Apply();
            return result;
        }

        // 补丁类：拦截玩家，确保任何时候都能抓到引用
        [HarmonyPatch(typeof(PlayerModel), "Update")]
        public static class PlayerUpdatePatch
        {
            public static void Postfix(PlayerModel __instance)
            {
                if (Instance.CachedPlayer == null)
                    Instance.CachedPlayer = __instance;
            }
        }

        // 补丁类：拦截敌人，确保已经在场上的敌人也能被抓到
        [HarmonyPatch(typeof(EnemyModel), "Start")]
        public static class EnemyStartPatch
        {
            public static void Postfix(EnemyModel __instance)
            {
                if (!Instance.ActiveEnemies.Contains(__instance))
                    Instance.ActiveEnemies.Add(__instance);
            }
        }

        [HarmonyPatch(typeof(PlayerModel), nameof(PlayerModel.OnEncounterStarted))]
        public static class PlayerEncounterPatch
        {
            public static void Postfix(PlayerModel __instance)
            {
                Instance.CachedPlayer = __instance;
                Instance.ActiveEnemies.Clear(); 
                Instance.CapturedEncounter = null; // 重置
                Instance.LastKnownMaxHp = 0; 
                Instance.LastRealHp = 0;
                Instance.DisplayedPercent = 0;
                Instance.LastChangeTime = 0;
            }
        }

        // 补丁类：捕捉战斗模型
        [HarmonyPatch(typeof(EnemyEncounterModel), nameof(EnemyEncounterModel.EnableEncounter))]
        public static class EncounterEnablePatch
        {
            public static void Postfix(EnemyEncounterModel __instance)
            {
                Instance.CapturedEncounter = __instance;
                Instance.Log.LogInfo("Encounter captured: " + __instance.name);
            }
        }

        [HarmonyPatch(typeof(EnemyEncounterModel), nameof(EnemyEncounterModel.OnEncounterEnded))]
        public static class EncounterEndPatch
        {
            public static void Postfix()
            {
                Instance.CapturedEncounter = null;
            }
        }

        // 补丁类：拦截敌人加入战斗
        [HarmonyPatch(typeof(EnemyModel), nameof(EnemyModel.OnEnemyAddedToGroup))]
        public static class EnemyGroupPatch
        {
            public static void Postfix(EnemyModel __instance)
            {
                if (!Instance.ActiveEnemies.Contains(__instance))
                    Instance.ActiveEnemies.Add(__instance);
            }
        }
    }

    public class HpUiRunner : MonoBehaviour
    {
        private void Update()
        {
            var mod = ShowEnemyHpMod.Instance;
            if (mod == null) return;

            // 1. 获取真实实时数值
            double realHp;
            double realMaxHp;
            mod.GetTotalHp(out realHp, out realMaxHp);
            float realPercent = realMaxHp > 0 ? (float)(realHp / realMaxHp) : 0f;

            // 2. 检测数值变化以触发延迟逻辑
            if (Mathf.Abs((float)(realHp - mod.LastRealHp)) > 0.01f)
            {
                if (realHp < mod.LastRealHp)
                {
                    if (mod.LastChangeTime <= 0.001f)
                    {
                        mod.LastChangeTime = Time.time;
                    }
                }
                else
                {
                    // 增加血量（回血或新敌人）时立即同步，不应用延迟
                    mod.LastChangeTime = 0;
                    mod.DisplayedPercent = realPercent;
                }
                mod.LastRealHp = realHp;
            }

            // 3. 进度条逻辑：首段伤害 0.6s 后开始平滑追赶
            if (mod.LastChangeTime > 0)
            {
                if (Time.time - mod.LastChangeTime > mod.UpdateDelay)
                {
                    mod.DisplayedPercent = Mathf.Lerp(mod.DisplayedPercent, realPercent, Time.deltaTime * mod.LerpSpeed);
                    
                    // 如果追平了，重置计时器，准备迎接下一波伤害的延迟
                    if (Mathf.Abs(mod.DisplayedPercent - realPercent) < 0.001f)
                    {
                        mod.DisplayedPercent = realPercent;
                        mod.LastChangeTime = 0;
                    }
                }
            }
            else
            {
                // 稳定状态下保持同步
                mod.DisplayedPercent = realPercent;
            }
        }

        private void OnGUI()
        {
            var mod = ShowEnemyHpMod.Instance;
            if (mod == null) return;

            // 懒加载纹理和样式
            if (mod.BgTexture == null) 
            {
                // #010100 转换为 Color 对象 (近黑色)
                mod.BgTexture = mod.CreateTexture(1, 1, new Color(0.004f, 0.004f, 0f, 1f));
                mod.BgStyle.normal.background = mod.BgTexture;
            }
            if (mod.RedTexture == null) 
            {
                mod.RedTexture = mod.CreateTexture(1, 1, new Color(0.8f, 0, 0, 1f));
                mod.RedBarStyle.normal.background = mod.RedTexture;
            }
            if (mod.BorderTexture == null)
            {
                // #bdbe60 转换为 Color 对象
                mod.BorderTexture = mod.CreateTexture(1, 1, new Color(0.741f, 0.745f, 0.376f, 1f));
                mod.BorderStyle.normal.background = mod.BorderTexture;
            }

            if (mod.CachedPlayer == null || !mod.CachedPlayer.IsInEncounter)
                return;

            // 获取实时数值用于文字显示
            double actualTotalHp;
            double totalMaxHp;
            mod.GetTotalHp(out actualTotalHp, out totalMaxHp);

            // 同步：使显示的数值与平滑后的血条进度一致
            double displayedHp = totalMaxHp * mod.DisplayedPercent;
            // 修正：如果实际血量已经归零，则显示数值也应强制归零，防止微小残余
            if (actualTotalHp <= 0.001) displayedHp = 0;

            float currentTextPercent = mod.DisplayedPercent;

            // 只有当血条彻底归零且没有活着的敌人时，才停止渲染
            if (actualTotalHp <= 0.001f && mod.DisplayedPercent <= 0.001f)
                return;

            // 动态计算响应式尺寸 (基于 1080p 的比例)
            float barWidth = Screen.width * 0.59f; // 59% 宽度
            float barHeight = Screen.height * 0.025f; // 约 2.5% 高度 (1080p 下约 27px)
            float marginTop = Screen.height * 0.005f; // 约 0.5% 边距 (1080p 下约 5px)
            
            float x = (Screen.width - barWidth) / 2;
            float y = marginTop;

            // 动态调整字体大小 (1080p 下约 18px)
            int dynamicFontSize = (int)(Screen.height * 0.016f);
            mod.HpStyle.fontSize = dynamicFontSize;
            mod.ShadowStyle.fontSize = dynamicFontSize;

            GUI.Box(new Rect(x - 2, y - 2, barWidth + 4, barHeight + 4), new GUIContent(""), mod.BorderStyle);

            GUI.Box(new Rect(x, y, barWidth, barHeight), new GUIContent(""), mod.BgStyle);

            if (mod.DisplayedPercent > 0)
            {
                GUI.Box(new Rect(x + 2, y + 2, (barWidth - 4) * mod.DisplayedPercent, barHeight - 4), new GUIContent(""), mod.RedBarStyle);
            }

            string text = $"敌人总血量: {displayedHp:0} / {totalMaxHp:0} ({currentTextPercent * 100:0}%)";
            
            GUI.Label(new Rect(x + 1, y + 1, barWidth, barHeight), new GUIContent(text), mod.ShadowStyle);
            GUI.Label(new Rect(x, y, barWidth, barHeight), new GUIContent(text), mod.HpStyle);
        }
    }
}
