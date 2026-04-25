using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using System.Collections.Generic;
using System.Reflection;
using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace VampireCreeperMod
{
    [BepInPlugin("com.vampire.creeper.sortcard", "Sort Card Mod", "1.0.0")]
    public class SortCardMod : BasePlugin
    {
        public static SortCardMod Instance { get; private set; }

        public override void Load()
        {
            Instance = this;
            ClassInjector.RegisterTypeInIl2Cpp<CardSorter>();

            var go = new UnityEngine.GameObject("CardSorterObject");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
            go.AddComponent<CardSorter>();

            Log.LogInfo("SortCardMod loaded successfully!");
        }
    }

    public class CardSorter : UnityEngine.MonoBehaviour
    {
        private static PropertyInfo _mouseScrollProp;
        private static MethodInfo _scrollReadValueMethod;
        private static object _mouseCurrent;
        private static PropertyInfo _gamepadLeftTriggerProp;
        private static PropertyInfo _gamepadRightTriggerProp;
        private static MethodInfo _triggerReadValueMethod;
        private static object _gamepadCurrent;
        private static Type _gamepadType;
        private static PropertyInfo _gamepadCurrentProp;
        private static bool _inputSystemChecked = false;
        private static bool _useNewInputSystem = false;
        private static readonly HashSet<string> _warningOnceKeys = new HashSet<string>();
        private const float ScrollTriggerCooldownSeconds = 0.25f;
        private const float GamepadTriggerThreshold = 0.5f;
        private float _lastScrollTriggerTime = -999f;
        private bool _wasLeftTriggerPressed = false;
        private bool _wasRightTriggerPressed = false;
        private bool _nextSortAscending = true;
        private bool _pendingSecondSort = false;
        private int _pendingSecondSortFrames = 0;
        private bool _pendingSecondSortAscending = true;

        private void Update()
        {
            if (_pendingSecondSort)
            {
                if (_pendingSecondSortFrames > 0)
                {
                    _pendingSecondSortFrames--;
                }
                else
                {
                    _pendingSecondSort = false;
                    SortHandCards(_pendingSecondSortAscending);
                }
            }

            if (IsScrollTriggered())
            {
                SortHandCards(_nextSortAscending);
                ScheduleSecondSortPass(_nextSortAscending);
            }
        }

        private void ScheduleSecondSortPass(bool ascending)
        {
            _pendingSecondSort = true;
            _pendingSecondSortFrames = 1;
            _pendingSecondSortAscending = ascending;
        }

        private bool IsScrollTriggered()
        {
            if (!_inputSystemChecked)
            {
                try
                {
                    var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    Type mouseType = null;
                    foreach (var ass in assemblies)
                    {
                        if (ass.FullName.Contains("InputSystem"))
                        {
                            mouseType = ass.GetType("UnityEngine.InputSystem.Mouse");
                            if (mouseType != null) break;
                        }
                    }

                    if (mouseType != null)
                    {
                        var currentProp = mouseType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                        _mouseCurrent = currentProp?.GetValue(null);
                        if (_mouseCurrent != null)
                        {
                            _mouseScrollProp = _mouseCurrent.GetType().GetProperty("scroll", BindingFlags.Public | BindingFlags.Instance);
                            var scrollControl = _mouseScrollProp?.GetValue(_mouseCurrent);
                            if (scrollControl != null)
                            {
                                _scrollReadValueMethod = FindMethodInHierarchy(scrollControl.GetType(), "ReadValue", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
                                _useNewInputSystem = _scrollReadValueMethod != null;
                            }
                        }
                    }

                    Type gamepadType = null;
                    foreach (var ass in assemblies)
                    {
                        if (!ass.FullName.Contains("InputSystem")) continue;
                        gamepadType = ass.GetType("UnityEngine.InputSystem.Gamepad");
                        if (gamepadType != null) break;
                    }

                    if (gamepadType != null)
                    {
                        var gamepadCurrentProp = gamepadType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                        _gamepadCurrent = gamepadCurrentProp?.GetValue(null);
                        if (_gamepadCurrent != null)
                        {
                            _gamepadLeftTriggerProp = _gamepadCurrent.GetType().GetProperty("leftTrigger", BindingFlags.Public | BindingFlags.Instance);
                            _gamepadRightTriggerProp = _gamepadCurrent.GetType().GetProperty("rightTrigger", BindingFlags.Public | BindingFlags.Instance);

                            var leftTriggerControl = _gamepadLeftTriggerProp?.GetValue(_gamepadCurrent);
                            if (leftTriggerControl != null)
                            {
                                _triggerReadValueMethod = FindMethodInHierarchy(leftTriggerControl.GetType(), "ReadValue", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
                            }
                        }
                    }

                    SortCardMod.Instance.Log.LogInfo("Input system check: NewSystem=" + _useNewInputSystem);
                }
                catch (Exception e)
                {
                    SortCardMod.Instance.Log.LogError("Input check failed: " + e.Message);
                }
                _inputSystemChecked = true;
            }

            if (_useNewInputSystem && _mouseCurrent != null)
            {
                try
                {
                    var scrollControl = _mouseScrollProp != null ? _mouseScrollProp.GetValue(_mouseCurrent) : null;
                    if (scrollControl != null && _scrollReadValueMethod != null)
                    {
                        var scrollValue = _scrollReadValueMethod.Invoke(scrollControl, null);
                        if (TryReadVector2Y(scrollValue, out var y) && Math.Abs(y) > 0.01f)
                        {
                            return TryTriggerSortByScroll(y);
                        }
                    }
                }
                catch { }
            }

            if (TryTriggerSortByGamepad())
            {
                return true;
            }

            try
            {
                return TryTriggerSortByScroll(UnityEngine.Input.mouseScrollDelta.y);
            }
            catch
            {
                return false;
            }
        }

        private bool TryTriggerSortByScroll(float scrollY)
        {
            if (Math.Abs(scrollY) <= 0.01f) return false;
            if (!CanTriggerSortByScroll()) return false;

            _nextSortAscending = scrollY < 0f;
            return true;
        }

        private bool TryTriggerSortByGamepad()
        {
            bool leftPressed = false;
            bool rightPressed = false;

            try
            {
                if (TryEnsureGamepadInputReady())
                {
                    var leftControl = _gamepadLeftTriggerProp != null ? _gamepadLeftTriggerProp.GetValue(_gamepadCurrent) : null;
                    var rightControl = _gamepadRightTriggerProp != null ? _gamepadRightTriggerProp.GetValue(_gamepadCurrent) : null;

                    if (TryReadFloatValue(leftControl, _triggerReadValueMethod, out var leftValue))
                    {
                        leftPressed = leftValue >= GamepadTriggerThreshold;
                    }
                    if (TryReadFloatValue(rightControl, _triggerReadValueMethod, out var rightValue))
                    {
                        rightPressed = rightValue >= GamepadTriggerThreshold;
                    }
                }
            }
            catch { }

            bool leftDown = leftPressed && !_wasLeftTriggerPressed;
            bool rightDown = rightPressed && !_wasRightTriggerPressed;
            _wasLeftTriggerPressed = leftPressed;
            _wasRightTriggerPressed = rightPressed;

            if (!leftDown && !rightDown) return false;
            if (!CanTriggerSortByScroll()) return false;

            if (rightDown)
            {
                // RT 升序
                _nextSortAscending = true;
                return true;
            }

            // LT 降序
            _nextSortAscending = false;
            return true;
        }

        private bool TryEnsureGamepadInputReady()
        {
            try
            {
                if (_gamepadType == null)
                {
                    var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    foreach (var ass in assemblies)
                    {
                        if (!ass.FullName.Contains("InputSystem")) continue;
                        _gamepadType = ass.GetType("UnityEngine.InputSystem.Gamepad");
                        if (_gamepadType != null) break;
                    }
                }

                if (_gamepadType == null) return false;

                if (_gamepadCurrentProp == null)
                {
                    _gamepadCurrentProp = _gamepadType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                }

                _gamepadCurrent = _gamepadCurrentProp != null ? _gamepadCurrentProp.GetValue(null) : null;
                if (_gamepadCurrent == null) return false;

                if (_gamepadLeftTriggerProp == null || _gamepadRightTriggerProp == null)
                {
                    var padType = _gamepadCurrent.GetType();
                    _gamepadLeftTriggerProp = padType.GetProperty("leftTrigger", BindingFlags.Public | BindingFlags.Instance);
                    _gamepadRightTriggerProp = padType.GetProperty("rightTrigger", BindingFlags.Public | BindingFlags.Instance);
                }

                if (_triggerReadValueMethod == null)
                {
                    var leftControl = _gamepadLeftTriggerProp != null ? _gamepadLeftTriggerProp.GetValue(_gamepadCurrent) : null;
                    var rightControl = _gamepadRightTriggerProp != null ? _gamepadRightTriggerProp.GetValue(_gamepadCurrent) : null;
                    var sampleControl = leftControl ?? rightControl;
                    if (sampleControl != null)
                    {
                        _triggerReadValueMethod = FindMethodInHierarchy(sampleControl.GetType(), "ReadValue", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
                    }
                }

                return _triggerReadValueMethod != null;
            }
            catch
            {
                return false;
            }
        }

        private bool CanTriggerSortByScroll()
        {
            if (ShouldBlockSortInput()) return false;

            float now;
            try { now = Time.unscaledTime; }
            catch { now = Time.realtimeSinceStartup; }

            if (now - _lastScrollTriggerTime < ScrollTriggerCooldownSeconds)
            {
                return false;
            }

            _lastScrollTriggerTime = now;
            return true;
        }

        private bool ShouldBlockSortInput()
        {
            try
            {
                // 按下/按住 ESC 的这一帧不触发排序，避免和暂停操作冲突
                if (UnityEngine.Input.GetKey(KeyCode.Escape) || UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                {
                    return true;
                }
            }
            catch { }

            try
            {
                // 暂停/设置面板打开时通常 timeScale 为 0，直接屏蔽滚轮排序
                if (Time.timeScale <= 0.0001f)
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        private void SortHandCards(bool ascending)
        {            try
            {
                var player = UnityEngine.Object.FindFirstObjectByType<Nosebleed.Pancake.Models.PlayerModel>();
                if (player == null)
                {
                    LogWarningOnce("player_not_found", "PlayerModel instance not found!");
                    return;
                }

                if (player.HandPile == null || player.HandPile.CardPile == null)
                {
                    LogWarningOnce("handpile_null", "HandPile or CardPile is null!");
                    return;
                }

                var cardPile = player.HandPile.CardPile;
                var handView = player.HandPile.View;
                int count = cardPile.Count;
                
                var handCards = new List<Nosebleed.Pancake.Models.CardModel>();
                for (int i = 0; i < count; i++)
                {
                    Nosebleed.Pancake.Models.CardModel card;
                    if (cardPile.TryPeekIndex(i, out card) && card != null)
                    {
                        handCards.Add(card);
                    }
                }

                if (handCards.Count <= 1)
                {
                    return;
                }

                // 3. 排序 (对 CardModel 进行排序)
                var sortedCards = new List<Nosebleed.Pancake.Models.CardModel>(handCards);
                var originalIndexByPtr = new Dictionary<IntPtr, int>();
                for (int i = 0; i < handCards.Count; i++)
                {
                    var ptr = GetIl2CppPtr(handCards[i]);
                    if (ptr != IntPtr.Zero && !originalIndexByPtr.ContainsKey(ptr))
                    {
                        originalIndexByPtr[ptr] = i;
                    }
                }

                int direction = ascending ? 1 : -1;
                sortedCards.Sort((a, b) =>
                {
                    int pA = GetSortPriority(a);
                    int pB = GetSortPriority(b);
                    int cmp = pA.CompareTo(pB);
                    if (cmp != 0) return cmp * direction;

                    int costA = GetSortCost(a);
                    int costB = GetSortCost(b);
                    cmp = costA.CompareTo(costB);
                    if (cmp != 0) return cmp * direction;

                    // 完全相同优先级时保持原相对顺序，避免升降序切换时边界抖动
                    int indexA = GetOriginalIndex(originalIndexByPtr, handCards, a);
                    int indexB = GetOriginalIndex(originalIndexByPtr, handCards, b);
                    return indexA.CompareTo(indexB);
                });

                for (int targetIndex = 0; targetIndex < sortedCards.Count; targetIndex++)
                {
                    var targetCard = sortedCards[targetIndex];
                    if (handCards[targetIndex] == targetCard) continue;

                    int currentIndex = handCards.IndexOf(targetCard);
                    if (currentIndex < 0 || currentIndex == targetIndex) continue;

                    var cardAtTarget = handCards[targetIndex];
                    bool swapped = cardPile.TrySwapCards(cardAtTarget, targetCard);
                    if (swapped)
                    {
                        handCards[targetIndex] = targetCard;
                        handCards[currentIndex] = cardAtTarget;
                    }
                    else
                    {
                        LogWarningOnce("swap_failed", "TrySwapCards failed at least once.");
                    }
                }

                // 6.2 验证模型层顺序；若未生效，则强制改写 CardPileModel._cards
                var modelAfterSwap = new List<Nosebleed.Pancake.Models.CardModel>();
                for (int i = 0; i < count; i++)
                {
                    Nosebleed.Pancake.Models.CardModel card;
                    if (cardPile.TryPeekIndex(i, out card) && card != null)
                    {
                        modelAfterSwap.Add(card);
                    }
                }

                bool sameAsTarget = modelAfterSwap.Count == sortedCards.Count;
                if (sameAsTarget)
                {
                    for (int i = 0; i < sortedCards.Count; i++)
                    {
                        if (modelAfterSwap[i] != sortedCards[i])
                        {
                            sameAsTarget = false;
                            break;
                        }
                    }
                }

                if (!sameAsTarget)
                {
                    LogWarningOnce("force_cards_rewrite", "TrySwapCards did not reach target order, forcing _cards rewrite.");
                    try
                    {
                        var flags2 = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                        FieldInfo cardsField = null;
                        for (var t = cardPile.GetType(); t != null; t = t.BaseType)
                        {
                            cardsField = t.GetField("_cards", flags2);
                            if (cardsField != null) break;
                        }

                        if (cardsField != null)
                        {
                            var listObj = cardsField.GetValue(cardPile);
                            if (listObj != null)
                            {
                                var listType = listObj.GetType();
                                var clearMethod = listType.GetMethod("Clear", flags2, null, Type.EmptyTypes, null);
                                MethodInfo addMethod = null;
                                foreach (var m in listType.GetMethods(flags2))
                                {
                                    if (m.Name != "Add") continue;
                                    var ps = m.GetParameters();
                                    if (ps.Length == 1)
                                    {
                                        addMethod = m;
                                        break;
                                    }
                                }

                                if (clearMethod != null && addMethod != null)
                                {
                                    clearMethod.Invoke(listObj, null);
                                    for (int i = 0; i < sortedCards.Count; i++)
                                    {
                                        addMethod.Invoke(listObj, new object[] { sortedCards[i] });
                                    }
                                }
                                else
                                {
                                    LogWarningOnce("cards_list_clear_add_missing", "Could not find Clear/Add on _cards list type.");
                                }
                            }
                        }
                        else
                        {
                            LogWarningOnce("cards_field_missing", "Could not find _cards field on CardPileModel.");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarningOnce("force_cards_rewrite_failed", "Force _cards rewrite failed: " + ex.Message);
                    }
                }

                // 输出模型层最终顺序，确认重排是否真实生效
                var finalModelCards = new List<Nosebleed.Pancake.Models.CardModel>();
                for (int i = 0; i < count; i++)
                {
                    Nosebleed.Pancake.Models.CardModel card;
                    if (cardPile.TryPeekIndex(i, out card) && card != null)
                    {
                        finalModelCards.Add(card);
                    }
                }

                // 优先使用“硬重建”保证 CardSlot/UI 层级一致
                if (TryHardRebuildHandViewByModelOrder(handView, finalModelCards))
                {
                    handView.SyncView();
                    try { handView.RefreshCardsUI(); } catch { }
                    try { handView.RefreshCardsUI(player); } catch { }
                    try { player.HandPile.DEBUG_UpdateCardsInHand(); } catch { }
                    SortCardMod.Instance.Log.LogInfo("Sort completed via hard rebuild + refresh.");
                    return;
                }
                LogWarningOnce("hard_rebuild_fallback", "Hard rebuild failed, using native refresh fallback.");

                handView.SyncView();
                try { handView.RefreshCardsUI(); } catch { }
                try { handView.RefreshCardsUI(player); } catch { }
                try { player.HandPile.DEBUG_UpdateCardsInHand(); } catch { }

                SortCardMod.Instance.Log.LogInfo("Sort completed via Swapping + Refresh!");
            }
            catch (Exception ex)
            {
                SortCardMod.Instance.Log.LogError("Sort error: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        private int GetActualCost(Nosebleed.Pancake.Models.CardModel card)
        {
            try
            {
                int totalModifier = card.ManaCostModifier + card.TempManaCostModifier + card.ReducedCostModifier;
                return card.GetCardCostTypeManaCost(totalModifier, false);
            }
            catch
            {
                return card.GetCardCostTypeManaCost();
            }
        }

        private int GetSortPriority(Nosebleed.Pancake.Models.CardModel card)
        {
            try
            {
                if (IsWildCard(card)) return 3;
            }
            catch { }

            int cost = GetSortCost(card);
            if (cost < 0) return 1;

            return 2;
        }

        private int GetSortCost(Nosebleed.Pancake.Models.CardModel card)
        {
            try
            {
                if (!IsWildCard(card) && card.IsCardFreeToPlay())
                {
                    var baseConfig = card.BaseCardConfig;
                    if (baseConfig != null) return baseConfig.GetManaCost();

                    var cfg = card.CardConfig;
                    if (cfg != null) return cfg.GetManaCost();
                }
            }
            catch { }

            return GetActualCost(card);
        }

        private bool IsWildCard(Nosebleed.Pancake.Models.CardModel card)
        {
            try
            {
                if (card != null && card.CardCostType != null)
                {
                    var typeName = card.CardCostType.GetType().Name;
                    if (!string.IsNullOrEmpty(typeName) && typeName.Contains("WildCostType"))
                    {
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private void TryNormalizeCardVisualLayering(Nosebleed.Pancake.View.HandPileView handView)
        {
            if (handView == null) return;

            try
            {
                var views = new List<Nosebleed.Pancake.View.CardView>();
                Transform pileRoot = null;
                try { pileRoot = handView.PileRoot; } catch { }

                if (pileRoot != null)
                {
                    for (int i = 0; i < pileRoot.childCount; i++)
                    {
                        var child = pileRoot.GetChild(i);
                        if (child == null) continue;

                        var v = child.GetComponent<Nosebleed.Pancake.View.CardView>();
                        if (v != null) views.Add(v);
                    }
                }

                if (views.Count <= 1) return;

                // 固定层级规则：仍按 X 排序，但按“右到左”回写 sibling，避免手柄左右导航反向
                views.Sort((a, b) =>
                {
                    float ax = a.transform.position.x;
                    float bx = b.transform.position.x;
                    return ax.CompareTo(bx);
                });

                for (int i = 0; i < views.Count; i++)
                {
                    views[i].transform.SetSiblingIndex(views.Count - 1 - i);
                }
            }
            catch { }
        }

        private bool TryRunNativeInteractionLayerRefresh(Nosebleed.Pancake.View.HandPileView handView)
        {
            if (handView == null) return false;

            try
            {
                var cardGroup = handView.CardGroup;
                if (cardGroup == null) return false;

                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                var activeSlotsField = FindFieldInHierarchy(cardGroup.GetType(), "_activeCardSlots", flags);
                var layoutGroupField = FindFieldInHierarchy(cardGroup.GetType(), "_layoutGroup", flags);
                var trySetIndexMethod = FindMethodInHierarchy(cardGroup.GetType(), "TrySetLayoutGroupIndex", flags, new Type[] { typeof(int) });
                var startedMethod = FindSingleParameterMethodInHierarchy(cardGroup.GetType(), "OnCardInteractionStarted", flags);
                var endedMethod = FindSingleParameterMethodInHierarchy(cardGroup.GetType(), "OnCardInteractionEnded", flags);
                var activeSlotsObj = activeSlotsField != null ? activeSlotsField.GetValue(cardGroup) : null;
                if (activeSlotsObj == null) return false;

                int touchedCount = 0;
                foreach (var slotObj in EnumerateUnknownList(activeSlotsObj))
                {
                    var interactable = TryGetSlottedInteractableFromSlot(slotObj, flags);
                    if (interactable == null) continue;

                    try { startedMethod?.Invoke(cardGroup, new object[] { interactable }); } catch { }
                    try
                    {
                        var forceHover = FindMethodInHierarchy(interactable.GetType(), "ForceHover", flags, Type.EmptyTypes);
                        if (forceHover != null) forceHover.Invoke(interactable, null);
                    }
                    catch { }
                    try { endedMethod?.Invoke(cardGroup, new object[] { interactable }); } catch { }
                    touchedCount++;
                }

                if (touchedCount == 0) return false;

                try { if (trySetIndexMethod != null) trySetIndexMethod.Invoke(cardGroup, new object[] { -1 }); } catch { }

                var layoutGroupObj = layoutGroupField != null ? layoutGroupField.GetValue(cardGroup) : null;
                if (layoutGroupObj != null)
                {
                    var forceMethod = FindMethodInHierarchy(layoutGroupObj.GetType(), "ForceLayoutRefresh", flags, Type.EmptyTypes);
                    try { forceMethod?.Invoke(layoutGroupObj, null); } catch { }
                }

                try { handView.SyncView(); } catch { }
                try { handView.RefreshCardsUI(); } catch { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryHardRebuildHandViewByModelOrder(
            Nosebleed.Pancake.View.HandPileView handView,
            List<Nosebleed.Pancake.Models.CardModel> modelOrderCards)
        {
            if (handView == null || modelOrderCards == null || modelOrderCards.Count == 0) return false;

            try
            {
                var cardGroup = handView.CardGroup;
                if (cardGroup == null) return false;

                // 先移除全部，再按模型顺序重建，确保 CardSlotHolder 内部列表与层级一致
                for (int i = 0; i < modelOrderCards.Count; i++)
                {
                    try { cardGroup.TryRemoveCard(modelOrderCards[i]); } catch { }
                }

                // AddCardToTop 需要倒序插入，最终内部顺序才与模型顺序一致
                for (int i = modelOrderCards.Count - 1; i >= 0; i--)
                {
                    try { cardGroup.AddCardToTop(modelOrderCards[i]); } catch { }
                }

                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                var layoutGroupField = FindFieldInHierarchy(cardGroup.GetType(), "_layoutGroup", flags);
                var layoutGroupObj = layoutGroupField != null ? layoutGroupField.GetValue(cardGroup) : null;
                if (layoutGroupObj != null)
                {
                    var forceMethod = FindMethodInHierarchy(layoutGroupObj.GetType(), "ForceLayoutRefresh", flags, Type.EmptyTypes);
                    if (forceMethod != null) forceMethod.Invoke(layoutGroupObj, null);
                }

                return true;
            }
            catch (Exception ex)
            {
                LogWarningOnce("hard_rebuild_failed", "Hard rebuild hand view failed: " + ex.Message);
                return false;
            }
        }

        private static IntPtr GetIl2CppPtr(object obj)
        {
            if (obj == null) return IntPtr.Zero;

            if (obj is Il2CppObjectBase il2CppObj)
            {
                return il2CppObj.Pointer;
            }

            try
            {
                var ptrProp = obj.GetType().GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
                if (ptrProp != null)
                {
                    var v = ptrProp.GetValue(obj);
                    if (v is IntPtr p) return p;
                }
            }
            catch { }

            return IntPtr.Zero;
        }

        private static bool AreSameIl2CppObject(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;

            var pa = GetIl2CppPtr(a);
            var pb = GetIl2CppPtr(b);
            if (pa != IntPtr.Zero && pb != IntPtr.Zero) return pa == pb;

            return a.Equals(b);
        }

        private static int GetOriginalIndex(
            Dictionary<IntPtr, int> originalIndexByPtr,
            List<Nosebleed.Pancake.Models.CardModel> fallbackList,
            Nosebleed.Pancake.Models.CardModel card)
        {
            if (card == null) return int.MaxValue;

            var ptr = GetIl2CppPtr(card);
            if (ptr != IntPtr.Zero && originalIndexByPtr != null && originalIndexByPtr.TryGetValue(ptr, out var idx))
            {
                return idx;
            }

            return fallbackList != null ? fallbackList.IndexOf(card) : int.MaxValue;
        }

        private static void LogWarningOnce(string key, string message)
        {
            if (_warningOnceKeys.Add(key))
            {
                SortCardMod.Instance.Log.LogWarning(message);
            }
        }

        private static IEnumerable<object> EnumerateUnknownList(object listObj)
        {
            if (listObj == null) yield break;

            if (listObj is System.Collections.IEnumerable enumerable)
            {
                foreach (var o in enumerable) yield return o;
                yield break;
            }

            var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            var listType = listObj.GetType();
            var countProp = listType.GetProperty("Count", flags);
            var itemProp = listType.GetProperty("Item", flags);
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

        private static Nosebleed.Pancake.Models.CardModel TryGetCardModelFromSlot(object slotObj, BindingFlags flags)
        {
            if (slotObj == null) return null;
            try
            {
                var slotType = slotObj.GetType();
                var slottedInteractableProp = slotType.GetProperty("SlottedInteractableCard", flags);
                var interactable = slottedInteractableProp != null ? slottedInteractableProp.GetValue(slotObj) : null;
                if (interactable == null) return null;

                var cardViewProp = interactable.GetType().GetProperty("CardView", flags);
                var cardViewObj = cardViewProp != null ? cardViewProp.GetValue(interactable) : null;
                if (cardViewObj == null) return null;

                var cardModelProp = cardViewObj.GetType().GetProperty("CardModel", flags);
                return cardModelProp != null ? cardModelProp.GetValue(cardViewObj) as Nosebleed.Pancake.Models.CardModel : null;
            }
            catch
            {
                return null;
            }
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

        private static MethodInfo FindMethodInHierarchy(Type type, string methodName, BindingFlags flags, Type[] parameterTypes)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var m = t.GetMethod(methodName, flags, null, parameterTypes, null);
                if (m != null) return m;
            }
            return null;
        }

        private static MethodInfo FindSingleParameterMethodInHierarchy(Type type, string methodName, BindingFlags flags)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var methods = t.GetMethods(flags);
                for (int i = 0; i < methods.Length; i++)
                {
                    var m = methods[i];
                    if (m.Name != methodName) continue;
                    var ps = m.GetParameters();
                    if (ps != null && ps.Length == 1) return m;
                }
            }
            return null;
        }

        private static bool TryReadVector2Y(object value, out float y)
        {
            y = 0f;
            if (value == null) return false;

            if (value is Vector2 v)
            {
                y = v.y;
                return true;
            }

            try
            {
                var t = value.GetType();
                var yProp = t.GetProperty("y", BindingFlags.Public | BindingFlags.Instance);
                if (yProp != null)
                {
                    var py = yProp.GetValue(value);
                    if (py is float fy)
                    {
                        y = fy;
                        return true;
                    }
                }

                var yField = t.GetField("y", BindingFlags.Public | BindingFlags.Instance);
                if (yField != null)
                {
                    var fyObj = yField.GetValue(value);
                    if (fyObj is float fy2)
                    {
                        y = fy2;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool TryReadFloatValue(object control, MethodInfo readValueMethod, out float value)
        {
            value = 0f;
            if (control == null || readValueMethod == null) return false;

            try
            {
                var raw = readValueMethod.Invoke(control, null);
                if (raw is float f)
                {
                    value = f;
                    return true;
                }

                if (raw != null)
                {
                    value = Convert.ToSingle(raw);
                    return true;
                }
            }
            catch { }

            return false;
        }
    }
}
