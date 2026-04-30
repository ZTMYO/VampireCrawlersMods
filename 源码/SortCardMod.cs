using BepInEx;
using BepInEx.Unity.IL2CPP;
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

            Log.LogInfo("Sort Card Mod loaded.");
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

            _nextSortAscending = scrollY > 0f;
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
                    bool isWildA = IsWildCard(a);
                    bool isWildB = IsWildCard(b);
                    if (isWildA && !isWildB) return -1 * direction;
                    if (!isWildA && isWildB) return 1 * direction;

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
                    TryFixCardFocusDirection(handView);
                    return;
                }
                LogWarningOnce("hard_rebuild_fallback", "Hard rebuild failed, using native refresh fallback.");

                handView.SyncView();
                try { handView.RefreshCardsUI(); } catch { }
                try { handView.RefreshCardsUI(player); } catch { }
                try { player.HandPile.DEBUG_UpdateCardsInHand(); } catch { }
                TryFixCardFocusDirection(handView);

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
                // Let game runtime compute final mana cost (includes gem modifiers and runtime effects).
                return card.GetCardCostTypeManaCost();
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
                    int baseCost;
                    if (baseConfig != null) baseCost = baseConfig.GetManaCost();
                    else
                    {
                        var cfg = card.CardConfig;
                        if (cfg == null) return GetActualCost(card);
                        baseCost = cfg.GetManaCost();
                    }

                    if (TryGetGemManaModifierSum(card, out var gemManaModifier))
                    {
                        return baseCost + gemManaModifier;
                    }

                    return baseCost;
                }
            }
            catch { }

            return GetActualCost(card);
        }

        private static bool TryGetGemManaModifierSum(Nosebleed.Pancake.Models.CardModel card, out int totalGemManaModifier)
        {
            totalGemManaModifier = 0;
            if (card == null) return false;

            try
            {
                var gemsModel = card.CardGemsModel;
                if (gemsModel == null) gemsModel = card.CardGems;
                if (gemsModel == null) return false;

                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                var gemsType = gemsModel.GetType();
                var gemManaModifiersField = FindFieldInHierarchy(gemsType, "_gemManaModifiers", flags);
                var dictObj = gemManaModifiersField != null ? gemManaModifiersField.GetValue(gemsModel) : null;
                if (dictObj == null) return false;

                // Prefer iterating dictionary values (GemManaModifierInfo) directly.
                object valuesObj = null;
                var valuesProp = dictObj.GetType().GetProperty("Values", flags);
                if (valuesProp != null)
                {
                    valuesObj = valuesProp.GetValue(dictObj);
                }

                var source = valuesObj ?? dictObj;
                foreach (var rawObj in EnumerateUnknownList(source))
                {
                    if (rawObj == null) continue;

                    object infoObj = rawObj;
                    var valueProp = rawObj.GetType().GetProperty("Value", flags);
                    if (valueProp != null)
                    {
                        var maybeInfo = valueProp.GetValue(rawObj);
                        if (maybeInfo != null) infoObj = maybeInfo;
                    }

                    var infoType = infoObj.GetType();
                    int amount = 0;
                    int count = 1;

                    var amountField = FindFieldInHierarchy(infoType, "Amount", flags);
                    if (amountField != null)
                    {
                        var v = amountField.GetValue(infoObj);
                        if (v is int ai) amount = ai;
                    }
                    else
                    {
                        var amountProp = infoType.GetProperty("Amount", flags);
                        if (amountProp != null)
                        {
                            var v = amountProp.GetValue(infoObj);
                            if (v is int ai) amount = ai;
                        }
                    }

                    var countField = FindFieldInHierarchy(infoType, "Count", flags);
                    if (countField != null)
                    {
                        var v = countField.GetValue(infoObj);
                        if (v is int ci) count = ci;
                    }
                    else
                    {
                        var countProp = infoType.GetProperty("Count", flags);
                        if (countProp != null)
                        {
                            var v = countProp.GetValue(infoObj);
                            if (v is int ci) count = ci;
                        }
                    }

                    totalGemManaModifier += amount * count;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsWildCard(Nosebleed.Pancake.Models.CardModel card)
        {
            try
            {
                if (card != null && card.CardCostType != null)
                {
                    var costType = card.CardCostType;
                    
                    try
                    {
                        var tryCastMethod = typeof(Il2CppObjectBase).GetMethod("TryCast", BindingFlags.Public | BindingFlags.Instance);
                        if (tryCastMethod != null)
                        {
                            var genericMethod = tryCastMethod.MakeGenericMethod(typeof(Nosebleed.Pancake.GameLogic.WildCostType));
                            var casted = genericMethod.Invoke(costType, null);
                            if (casted != null) return true;
                        }
                    }
                    catch { }

                    if (costType is Nosebleed.Pancake.GameLogic.WildCostType)
                    {
                        return true;
                    }
                    
                    var typeName = costType.GetType().Name;
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

        private void TryFixCardFocusDirection(Nosebleed.Pancake.View.HandPileView handView)
        {
            if (handView == null) return;

            try
            {
                var cardGroup = handView.CardGroup;
                if (cardGroup == null) return;

                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                var activeSlotsField = FindFieldInHierarchy(cardGroup.GetType(), "_activeCardSlots", flags);
                var activeSlotsObj = activeSlotsField != null ? activeSlotsField.GetValue(cardGroup) : null;
                if (activeSlotsObj == null) return;

                var slots = new List<object>();
                foreach (var slotObj in EnumerateUnknownList(activeSlotsObj))
                {
                    if (slotObj != null) slots.Add(slotObj);
                }
                if (slots.Count <= 1) return;

                float firstX = 0f;
                float lastX = 0f;
                bool firstFound = false;
                bool lastFound = false;

                for (int i = 0; i < slots.Count; i++)
                {
                    if (!TryGetCardViewXFromSlot(slots[i], flags, out var x)) continue;
                    firstX = x;
                    firstFound = true;
                    break;
                }

                for (int i = slots.Count - 1; i >= 0; i--)
                {
                    if (!TryGetCardViewXFromSlot(slots[i], flags, out var x)) continue;
                    lastX = x;
                    lastFound = true;
                    break;
                }

                if (!firstFound || !lastFound) return;

                // firstX > lastX means slot list is right-to-left; reverse to align with visual left-to-right.
                if (firstX <= lastX) return;

                if (activeSlotsObj is System.Collections.IList ilist)
                {
                    int i = 0;
                    int j = ilist.Count - 1;
                    while (i < j)
                    {
                        var tmp = ilist[i];
                        ilist[i] = ilist[j];
                        ilist[j] = tmp;
                        i++;
                        j--;
                    }
                }
                else
                {
                    var listType = activeSlotsObj.GetType();
                    var countProp = listType.GetProperty("Count", flags);
                    var itemProp = listType.GetProperty("Item", flags);
                    if (countProp == null || itemProp == null || !itemProp.CanWrite) return;

                    int count;
                    try { count = (int)countProp.GetValue(activeSlotsObj); } catch { return; }
                    int i = 0;
                    int j = count - 1;
                    while (i < j)
                    {
                        object a;
                        object b;
                        try { a = itemProp.GetValue(activeSlotsObj, new object[] { i }); } catch { break; }
                        try { b = itemProp.GetValue(activeSlotsObj, new object[] { j }); } catch { break; }
                        try { itemProp.SetValue(activeSlotsObj, b, new object[] { i }); } catch { break; }
                        try { itemProp.SetValue(activeSlotsObj, a, new object[] { j }); } catch { break; }
                        i++;
                        j--;
                    }
                }

                var trySetIndexMethod = FindMethodInHierarchy(cardGroup.GetType(), "TrySetLayoutGroupIndex", flags, new Type[] { typeof(int) });
                try { if (trySetIndexMethod != null) trySetIndexMethod.Invoke(cardGroup, new object[] { -1 }); } catch { }
            }
            catch { }
        }

        private static bool TryGetCardViewXFromSlot(object slotObj, BindingFlags flags, out float x)
        {
            x = 0f;
            if (slotObj == null) return false;

            try
            {
                var interactable = TryGetSlottedInteractableFromSlot(slotObj, flags);
                if (interactable == null) return false;

                var view = TryGetCardViewFromInteractable(interactable, flags);
                if (view == null) return false;

                x = view.transform.position.x;
                return true;
            }
            catch
            {
                return false;
            }
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
            System.Collections.IList modelOrderCards)
        {
            if (handView == null || modelOrderCards == null || modelOrderCards.Count == 0) return false;

            try
            {
                var cardGroup = handView.CardGroup;
                if (cardGroup == null) return false;

                // 先移除全部，再按模型顺序重建，确保 CardSlotHolder 内部列表与层级一致
                for (int i = 0; i < modelOrderCards.Count; i++)
                {
                    var card = modelOrderCards[i] as Nosebleed.Pancake.Models.CardModel;
                    if (card == null) continue;
                    try { cardGroup.TryRemoveCard(card); } catch { }
                }

                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                var addBottomMethod = FindSingleParameterMethodInHierarchy(cardGroup.GetType(), "AddCardToBottom", flags);
                var addTopMethod = FindSingleParameterMethodInHierarchy(cardGroup.GetType(), "AddCardToTop", flags);

                if (addBottomMethod != null)
                {
                    // Preferred: append left-to-right in model order to keep navigation direction natural.
                    for (int i = 0; i < modelOrderCards.Count; i++)
                    {
                        var card = modelOrderCards[i] as Nosebleed.Pancake.Models.CardModel;
                        if (card == null) continue;
                        try { addBottomMethod.Invoke(cardGroup, new object[] { card }); } catch { }
                    }
                }
                else if (addTopMethod != null)
                {
                    // Fallback when only AddCardToTop exists: insert in reverse to preserve visual order.
                    for (int i = modelOrderCards.Count - 1; i >= 0; i--)
                    {
                        var card = modelOrderCards[i] as Nosebleed.Pancake.Models.CardModel;
                        if (card == null) continue;
                        try { addTopMethod.Invoke(cardGroup, new object[] { card }); } catch { }
                    }
                }
                else
                {
                    // Last fallback to previous direct call path.
                    for (int i = modelOrderCards.Count - 1; i >= 0; i--)
                    {
                        var card = modelOrderCards[i] as Nosebleed.Pancake.Models.CardModel;
                        if (card == null) continue;
                        try { cardGroup.AddCardToTop(card); } catch { }
                    }
                }

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
            System.Collections.IList fallbackList,
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
