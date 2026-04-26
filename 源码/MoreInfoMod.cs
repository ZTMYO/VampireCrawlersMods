using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VampireCreeperMod
{
    [BepInPlugin("com.vampire.creeper.moreinfo", "More Info Mod", "1.0.0")]
    public class MoreInfoMod : BasePlugin
    {
        public static MoreInfoMod Instance { get; private set; }
        internal static bool EncounterHasPlayedCard { get; set; }

        public override void Load()
        {
            Instance = this;
            ClassInjector.RegisterTypeInIl2Cpp<MoreInfoRunner>();

            var harmony = new Harmony("com.vampire.creeper.moreinfo");
            harmony.PatchAll();

            var go = new GameObject("MoreInfoObject");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<MoreInfoRunner>();

            Log.LogInfo("More Info Mod loaded.");
        }
    }

    public class MoreInfoRunner : MonoBehaviour
    {
        private string _crackHudText;
        private bool _showCrackHud;
        private bool _crackHudIsLastPlay;
        private GUIStyle _crackHudStyle;

        private string _comboHudText;
        private bool _showComboHud;
        private GUIStyle _comboHudStyle;
        private bool _wasInEncounter;

        private float _nextCrackHudDebugLogTime = 0f;
        private float _nextComboHudDebugLogTime = 0f;
        private const bool EnableHudDebugLogs = false;
        private float _nextCrackRefreshTime = 0f;
        private float _nextComboRefreshTime = 0f;
        private const float CrackRefreshIntervalSeconds = 0.25f;
        private const float ComboRefreshIntervalSeconds = 0.15f;
        private float _nextSelectedCardFallbackScanTime = 0f;
        private float _nextPlayerLookupTime = 0f;
        private Nosebleed.Pancake.Models.PlayerModel _cachedPlayer;

        private void Update()
        {
            float now;
            try { now = Time.unscaledTime; } catch { now = Time.realtimeSinceStartup; }

            if (now >= _nextCrackRefreshTime)
            {
                _nextCrackRefreshTime = now + CrackRefreshIntervalSeconds;
                UpdateCrackHudInfo();
            }

            if (now >= _nextComboRefreshTime)
            {
                _nextComboRefreshTime = now + ComboRefreshIntervalSeconds;
                UpdateComboHudInfo();
            }
        }

        private void OnGUI()
        {
            if (_crackHudStyle == null)
            {
                _crackHudStyle = new GUIStyle(GUI.skin.label);
                _crackHudStyle.fontSize = 22;
                _crackHudStyle.fontStyle = FontStyle.Bold;
            }

            if (_comboHudStyle == null)
            {
                _comboHudStyle = new GUIStyle(GUI.skin.label);
                _comboHudStyle.fontSize = 22;
                _comboHudStyle.fontStyle = FontStyle.Bold;
                _comboHudStyle.normal.textColor = new Color(254f / 255f, 254f / 255f, 254f / 255f, 1f);
            }

            float crackY = Screen.height - 64f;
            float comboY = crackY - 32f;

            if (_showComboHud && !string.IsNullOrEmpty(_comboHudText))
            {
                var comboRect = new Rect(16f, comboY, 900f, 40f);
                GUI.Label(comboRect, new GUIContent(_comboHudText), _comboHudStyle);
            }

            if (_showCrackHud && !string.IsNullOrEmpty(_crackHudText))
            {
                _crackHudStyle.normal.textColor = _crackHudIsLastPlay
                    ? new Color(1f, 0.25f, 0.25f, 1f)
                    : new Color(254f / 255f, 254f / 255f, 254f / 255f, 1f);

                var crackRect = new Rect(16f, crackY, 900f, 40f);
                GUI.Label(crackRect, new GUIContent(_crackHudText), _crackHudStyle);
            }
        }

        private void UpdateCrackHudInfo()
        {
            _showCrackHud = false;
            _crackHudText = null;
            _crackHudIsLastPlay = false;

            try
            {
                if (!TryGetPlayerModelCached(out var player) || player == null)
                {
                    TryLogCrackHudDebug("HUD: PlayerModel not ready.");
                    return;
                }

                bool inEncounter = false;
                try { inEncounter = player.IsInEncounter; } catch { }
                if (!inEncounter)
                {
                    return;
                }

                var handView = player?.HandPile?.View;
                var cardGroup = handView?.CardGroup;
                if (cardGroup == null)
                {
                    TryLogCrackHudDebug("HUD: CardGroup not ready.");
                    return;
                }

                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                var selectedView = TryGetSelectedCardView(cardGroup, handView, flags);

                if (selectedView == null)
                {
                    TryLogCrackHudDebug("HUD: selected card not found.");
                    return;
                }

                var breakable = selectedView.GetComponent<Nosebleed.Pancake.GameLogic.BreakableCard>();
                if (breakable == null) breakable = selectedView.GetComponentInChildren<Nosebleed.Pancake.GameLogic.BreakableCard>(true);
                if (breakable == null)
                {
                    TryLogCrackHudDebug("HUD: selected card has no BreakableCard.");
                    return;
                }

                int played = breakable.TimesPlayedThisTurn;
                int totalPlayable = 4;
                bool hasUncrackableGem = false;
                if (TryHasUncrackableGem(selectedView, out hasUncrackableGem) && hasUncrackableGem)
                {
                    totalPlayable = 6;
                }

                if (played < 3)
                {
                    return;
                }

                int remainingPlayable = Math.Max(0, totalPlayable - played);
                int displayTotalPlayable = Math.Max(1, totalPlayable - 3);
                _crackHudText = "可打出次数：" + remainingPlayable + "/" + displayTotalPlayable;
                _crackHudIsLastPlay = remainingPlayable == 0;
                _showCrackHud = true;
            }
            catch (Exception ex)
            {
                TryLogCrackHudDebug("HUD error: " + ex.Message);
            }
        }

        private void UpdateComboHudInfo()
        {
            _showComboHud = false;
            _comboHudText = null;

            try
            {
                if (!TryGetPlayerModelCached(out var player) || player == null)
                {
                    return;
                }

                bool inEncounter = false;
                try { inEncounter = player.IsInEncounter; } catch { }

                if (!inEncounter)
                {
                    _wasInEncounter = false;
                    MoreInfoMod.EncounterHasPlayedCard = false;
                    return;
                }

                if (!_wasInEncounter)
                {
                    _wasInEncounter = true;
                }

                if (!HasComboContext(player)) return;
                if (!MoreInfoMod.EncounterHasPlayedCard) return;

                if (TryGetComboTriggerPoint(player, out var comboPoint))
                {
                    _comboHudText = "可连击法力:" + comboPoint;
                    _showComboHud = true;
                }
            }
            catch (Exception ex)
            {
                TryLogComboHudDebug("ComboHUD error: " + ex.Message);
            }
        }

        private static bool HasComboContext(Nosebleed.Pancake.Models.PlayerModel player)
        {
            if (player == null) return false;

            try
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                var hasPlayedField = FindFieldInHierarchy(player.GetType(), "_hasPlayedCardThisTurn", flags);
                if (hasPlayedField != null)
                {
                    var v = hasPlayedField.GetValue(player);
                    if (v is bool b) return b;
                }
            }
            catch { }

            try
            {
                if (TryGetMemberValue(player, "PreviousCard", out var previousCardObj) &&
                    previousCardObj is Nosebleed.Pancake.Models.CardModel previousCard &&
                    previousCard != null)
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool TryGetComboTriggerPoint(Nosebleed.Pancake.Models.PlayerModel player, out int comboPoints)
        {
            comboPoints = 0;
            if (player == null) return false;

            try
            {
                bool reverseCombo = false;
                if (TryGetMemberValue(player, "ReverseCombo", out var reverseObj) && reverseObj is bool rb)
                {
                    reverseCombo = rb;
                }

                int step = reverseCombo ? -1 : 1;

                if (TryGetMemberValue(player, "PreviousComboCardCost", out var previousComboCostObj) &&
                    TryReadIntFromUnknown(previousComboCostObj, out var previousComboCost))
                {
                    comboPoints = Math.Max(0, previousComboCost + step);
                    return true;
                }

                if (TryGetMemberValue(player, "PreviousCardCost", out var previousCardCostObj) &&
                    TryReadIntFromUnknown(previousCardCostObj, out var previousCost))
                {
                    comboPoints = Math.Max(0, previousCost + step);
                    return true;
                }
            }
            catch { }

            try
            {
                bool reverseCombo = false;
                if (TryGetMemberValue(player, "ReverseCombo", out var reverseObj) && reverseObj is bool rb)
                {
                    reverseCombo = rb;
                }

                int step = reverseCombo ? -1 : 1;
                if (TryGetMemberValue(player, "PreviousCard", out var previousCardObj) &&
                    previousCardObj is Nosebleed.Pancake.Models.CardModel previousCard &&
                    previousCard != null)
                {
                    if (TryGetCardComboCost(previousCard, out var prevComboCost))
                    {
                        comboPoints = Math.Max(0, prevComboCost + step);
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool TryGetCardComboCost(Nosebleed.Pancake.Models.CardModel cardModel, out int comboCost)
        {
            comboCost = 0;
            if (cardModel == null) return false;
            try
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                var t = cardModel.GetType();
                var m = t.GetMethod("GetCardComboCost", flags, null, new[] { typeof(bool) }, null);
                if (m != null)
                {
                    var ret = m.Invoke(cardModel, new object[] { false });
                    if (ret is int i)
                    {
                        comboCost = i;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool TryGetMemberValue(object obj, string memberName, out object value)
        {
            value = null;
            if (obj == null || string.IsNullOrEmpty(memberName)) return false;

            try
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                var t = obj.GetType();

                var p = t.GetProperty(memberName, flags);
                if (p != null && p.CanRead)
                {
                    value = p.GetValue(obj);
                    return true;
                }

                var f = FindFieldInHierarchy(t, memberName, flags);
                if (f != null)
                {
                    value = f.GetValue(obj);
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool TryReadIntFromUnknown(object obj, out int value)
        {
            value = 0;
            if (obj == null) return false;

            if (obj is int i)
            {
                value = i;
                return true;
            }

            try
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                var t = obj.GetType();
                string[] names =
                {
                    "Value", "CurrentValue", "RuntimeValue", "IntValue",
                    "_value", "value"
                };

                for (int idx = 0; idx < names.Length; idx++)
                {
                    var n = names[idx];
                    var p = t.GetProperty(n, flags);
                    if (p != null && p.CanRead)
                    {
                        var v = p.GetValue(obj);
                        if (v is int pi)
                        {
                            value = pi;
                            return true;
                        }
                    }

                    var f = FindFieldInHierarchy(t, n, flags);
                    if (f != null)
                    {
                        var v = f.GetValue(obj);
                        if (v is int fi)
                        {
                            value = fi;
                            return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        private void TryLogCrackHudDebug(string message)
        {
            if (!EnableHudDebugLogs) return;
            if (string.IsNullOrEmpty(message)) return;
            float now;
            try { now = Time.unscaledTime; } catch { now = Time.realtimeSinceStartup; }
            if (now < _nextCrackHudDebugLogTime) return;
            _nextCrackHudDebugLogTime = now + 2f;
            try { MoreInfoMod.Instance?.Log?.LogInfo(message); } catch { }
        }

        private void TryLogComboHudDebug(string message)
        {
            if (!EnableHudDebugLogs) return;
            if (string.IsNullOrEmpty(message)) return;
            float now;
            try { now = Time.unscaledTime; } catch { now = Time.realtimeSinceStartup; }
            if (now < _nextComboHudDebugLogTime) return;
            _nextComboHudDebugLogTime = now + 2f;
            try { MoreInfoMod.Instance?.Log?.LogInfo(message); } catch { }
        }

        private bool TryGetPlayerModelCached(out Nosebleed.Pancake.Models.PlayerModel player)
        {
            player = _cachedPlayer;
            if (player != null)
            {
                try
                {
                    // Touch a light property to ensure cached reference is still alive.
                    var _ = player.IsInEncounter;
                    return true;
                }
                catch
                {
                    _cachedPlayer = null;
                    player = null;
                }
            }

            float now;
            try { now = Time.unscaledTime; } catch { now = Time.realtimeSinceStartup; }
            if (now < _nextPlayerLookupTime)
            {
                return false;
            }

            _nextPlayerLookupTime = now + 1f;
            if (TryGetPlayerModel(out player) && player != null)
            {
                _cachedPlayer = player;
                return true;
            }

            return false;
        }

        private Nosebleed.Pancake.View.CardView TryGetSelectedCardView(object cardGroup, Nosebleed.Pancake.View.HandPileView handView, BindingFlags flags)
        {
            if (cardGroup == null) return null;

            try
            {
                var currentInteracted = Nosebleed.Pancake.GameLogic.SelectedCardManager.CurrentInteractedCard;
                if (currentInteracted != null && currentInteracted.CardView != null)
                {
                    return currentInteracted.CardView;
                }
            }
            catch { }

            try
            {
                var currentInteractedField = FindFieldInHierarchy(cardGroup.GetType(), "_currentlyInteractedCard", flags);
                var currentInteracted = currentInteractedField != null ? currentInteractedField.GetValue(cardGroup) : null;
                var selectedFromCurrent = TryGetCardViewFromInteractable(currentInteracted, flags);
                if (selectedFromCurrent != null) return selectedFromCurrent;
            }
            catch { }

            // Full scan is expensive; run it as low-frequency fallback.
            float now;
            try { now = Time.unscaledTime; } catch { now = Time.realtimeSinceStartup; }
            if (now < _nextSelectedCardFallbackScanTime)
            {
                return null;
            }
            _nextSelectedCardFallbackScanTime = now + 1f;

            try
            {
                var activeSlotsField = FindFieldInHierarchy(cardGroup.GetType(), "_activeCardSlots", flags);
                var activeSlotsObj = activeSlotsField != null ? activeSlotsField.GetValue(cardGroup) : null;
                if (activeSlotsObj != null)
                {
                    foreach (var slotObj in EnumerateUnknownList(activeSlotsObj))
                    {
                        var interactable = TryGetSlottedInteractableFromSlot(slotObj, flags);
                        if (interactable == null) continue;
                        if (!TryIsInteractableSelected(interactable, flags)) continue;

                        var selectedFromSlots = TryGetCardViewFromInteractable(interactable, flags);
                        if (selectedFromSlots != null) return selectedFromSlots;
                    }
                }
            }
            catch { }

            return TryFindSelectedCardViewFromPileRoot(handView);
        }

        private static bool TryGetPlayerModel(out Nosebleed.Pancake.Models.PlayerModel player)
        {
            player = null;
            try
            {
                player = UnityEngine.Object.FindFirstObjectByType<Nosebleed.Pancake.Models.PlayerModel>();
                if (player != null) return true;
            }
            catch { }

            if (TryFindPlayerModelViaObjectApi(out player))
            {
                return true;
            }

            try
            {
                var m = typeof(Resources).GetMethod("FindObjectsOfTypeAll", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Type) }, null);
                if (m != null)
                {
                    var arr = m.Invoke(null, new object[] { typeof(Nosebleed.Pancake.Models.PlayerModel) }) as Array;
                    if (TryPickFirstPlayerModel(arr, out player)) return true;
                }
            }
            catch { }

            return false;
        }

        private static bool TryFindPlayerModelViaObjectApi(out Nosebleed.Pancake.Models.PlayerModel player)
        {
            player = null;
            try
            {
                var methods = typeof(UnityEngine.Object).GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < methods.Length; i++)
                {
                    var m = methods[i];
                    if (m == null || m.Name != "FindObjectsOfType") continue;

                    var ps = m.GetParameters();
                    object[] args = null;
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Type))
                    {
                        args = new object[] { typeof(Nosebleed.Pancake.Models.PlayerModel) };
                    }
                    else if (ps.Length == 2 && ps[0].ParameterType == typeof(Type) && ps[1].ParameterType == typeof(bool))
                    {
                        args = new object[] { typeof(Nosebleed.Pancake.Models.PlayerModel), true };
                    }
                    else
                    {
                        continue;
                    }

                    var arr = m.Invoke(null, args) as Array;
                    if (TryPickFirstPlayerModel(arr, out player)) return true;
                }
            }
            catch { }

            return false;
        }

        private static bool TryPickFirstPlayerModel(Array arr, out Nosebleed.Pancake.Models.PlayerModel player)
        {
            player = null;
            if (arr == null || arr.Length == 0) return false;

            for (int i = 0; i < arr.Length; i++)
            {
                var p = arr.GetValue(i) as Nosebleed.Pancake.Models.PlayerModel;
                if (p != null)
                {
                    player = p;
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<object> EnumerateUnknownList(object listObj)
        {
            if (listObj == null) yield break;

            if (listObj is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item != null) yield return item;
                }
                yield break;
            }

            var t = listObj.GetType();
            var countProp = t.GetProperty("Count", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var itemProp = t.GetProperty("Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (countProp != null && itemProp != null)
            {
                int count;
                try { count = (int)countProp.GetValue(listObj); } catch { yield break; }
                for (int i = 0; i < count; i++)
                {
                    object item = null;
                    try { item = itemProp.GetValue(listObj, new object[] { i }); } catch { }
                    if (item != null) yield return item;
                }
            }
        }

        private static bool TryIsInteractableSelected(object interactable, BindingFlags flags)
        {
            if (interactable == null) return false;
            try
            {
                var t = interactable.GetType();
                var isSelectedProp = t.GetProperty("IsSelected", flags);
                if (isSelectedProp != null)
                {
                    var v = isSelectedProp.GetValue(interactable);
                    if (v is bool b) return b;
                }

                var isSelectedField = t.GetField("_isSelected", flags);
                if (isSelectedField != null)
                {
                    var v = isSelectedField.GetValue(interactable);
                    if (v is bool b) return b;
                }
            }
            catch { }
            return false;
        }

        private static Nosebleed.Pancake.View.CardView TryGetCardViewFromInteractable(object interactable, BindingFlags flags)
        {
            if (interactable == null) return null;
            try
            {
                var t = interactable.GetType();
                var cardViewProp = t.GetProperty("CardView", flags);
                if (cardViewProp != null)
                {
                    var v = cardViewProp.GetValue(interactable) as Nosebleed.Pancake.View.CardView;
                    if (v != null) return v;
                }

                var cardViewField = t.GetField("_cardView", flags);
                if (cardViewField != null)
                {
                    return cardViewField.GetValue(interactable) as Nosebleed.Pancake.View.CardView;
                }
            }
            catch { }
            return null;
        }

        private static Nosebleed.Pancake.View.CardView TryFindSelectedCardViewFromPileRoot(Nosebleed.Pancake.View.HandPileView handView)
        {
            if (handView == null) return null;
            try
            {
                var pileRoot = handView.PileRoot;
                if (pileRoot == null) return null;

                for (int i = 0; i < pileRoot.childCount; i++)
                {
                    var child = pileRoot.GetChild(i);
                    if (child == null) continue;

                    var interactable = child.GetComponentInChildren<Nosebleed.Pancake.GameLogic.InteractableCard>(true);
                    if (interactable == null) continue;
                    if (!interactable.IsSelected) continue;

                    if (interactable.CardView != null) return interactable.CardView;
                    var view = child.GetComponentInChildren<Nosebleed.Pancake.View.CardView>(true);
                    if (view != null) return view;
                }
            }
            catch { }
            return null;
        }

        private static bool TryHasUncrackableGem(Nosebleed.Pancake.View.CardView cardView, out bool hasUncrackableGem)
        {
            hasUncrackableGem = false;
            if (cardView == null) return false;

            try
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                object cardModelObj = null;

                try { cardModelObj = cardView.CardModel; } catch { }
                if (cardModelObj == null)
                {
                    var viewType = cardView.GetType();
                    var cardModelProp = viewType.GetProperty("CardModel", flags);
                    if (cardModelProp != null) cardModelObj = cardModelProp.GetValue(cardView);
                    if (cardModelObj == null)
                    {
                        var cardModelField = FindFieldInHierarchy(viewType, "_cardModel", flags);
                        if (cardModelField != null) cardModelObj = cardModelField.GetValue(cardView);
                    }
                }
                if (cardModelObj == null) return false;

                object gemsObj = null;
                var modelType = cardModelObj.GetType();
                var gemsModelProp = modelType.GetProperty("CardGemsModel", flags);
                if (gemsModelProp != null) gemsObj = gemsModelProp.GetValue(cardModelObj);
                if (gemsObj == null)
                {
                    var gemsProp = modelType.GetProperty("CardGems", flags);
                    if (gemsProp != null) gemsObj = gemsProp.GetValue(cardModelObj);
                }
                if (gemsObj == null)
                {
                    var gemsField = FindFieldInHierarchy(modelType, "_cardGemsModel", flags);
                    if (gemsField != null) gemsObj = gemsField.GetValue(cardModelObj);
                }
                if (gemsObj == null) return false;

                var gemsType = gemsObj.GetType();
                var hasGemProp = gemsType.GetProperty("HasUncrackableGem", flags);
                if (hasGemProp != null)
                {
                    var v = hasGemProp.GetValue(gemsObj);
                    if (v is bool b1)
                    {
                        hasUncrackableGem = b1;
                        return true;
                    }
                }

                var hasGemField = FindFieldInHierarchy(gemsType, "_hasUncrackableGem", flags);
                if (hasGemField != null)
                {
                    var v = hasGemField.GetValue(gemsObj);
                    if (v is bool b2)
                    {
                        hasUncrackableGem = b2;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static object TryGetSlottedInteractableFromSlot(object slotObj, BindingFlags flags)
        {
            if (slotObj == null) return null;
            try
            {
                var slotType = slotObj.GetType();
                var slottedInteractableProp = slotType.GetProperty("SlottedInteractableCard", flags);
                if (slottedInteractableProp != null)
                {
                    var interactable = slottedInteractableProp.GetValue(slotObj);
                    if (interactable != null) return interactable;
                }

                var slottedInteractableField = slotType.GetField("_slottedInteractableCard", flags);
                return slottedInteractableField != null ? slottedInteractableField.GetValue(slotObj) : null;
            }
            catch
            {
                return null;
            }
        }

        private static FieldInfo FindFieldInHierarchy(Type type, string fieldName, BindingFlags flags)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(fieldName, flags);
                if (f != null) return f;
            }
            return null;
        }
    }

    [HarmonyPatch(typeof(Nosebleed.Pancake.Models.PlayerModel), nameof(Nosebleed.Pancake.Models.PlayerModel.OnEncounterStarted))]
    public static class MoreInfoEncounterStartedPatch
    {
        public static void Postfix()
        {
            MoreInfoMod.EncounterHasPlayedCard = false;
        }
    }

    [HarmonyPatch(typeof(Nosebleed.Pancake.Models.PlayerModel), "OnAfterPlayCard")]
    public static class MoreInfoAfterPlayCardPatch
    {
        public static void Postfix()
        {
            MoreInfoMod.EncounterHasPlayedCard = true;
        }
    }

    [HarmonyPatch(typeof(Nosebleed.Pancake.Models.EnemyEncounterModel), nameof(Nosebleed.Pancake.Models.EnemyEncounterModel.OnEncounterEnded))]
    public static class MoreInfoEncounterEndedPatch
    {
        public static void Postfix()
        {
            MoreInfoMod.EncounterHasPlayedCard = false;
        }
    }
}
