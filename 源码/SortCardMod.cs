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
        private static bool _inputSystemChecked = false;
        private static bool _useNewInputSystem = false;
        private static readonly HashSet<string> _warningOnceKeys = new HashSet<string>();
        private const float ScrollTriggerCooldownSeconds = 0.25f;
        private float _lastScrollTriggerTime = -999f;
        private bool _nextSortAscending = true;

        private void Update()
        {
            if (IsScrollTriggered())
            {
                SortHandCards(_nextSortAscending);
            }
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

            // Unity 中通常 y>0 为上滑, y<0 为下滑
            // 需求: 下滑升序，上滑降序
            _nextSortAscending = scrollY < 0f;
            return true;
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
                sortedCards.Sort((a, b) =>
                {
                    int pA = GetSortPriority(a);
                    int pB = GetSortPriority(b);
                    if (pA != pB) return pA.CompareTo(pB);

                    int costA = GetSortCost(a);
                    int costB = GetSortCost(b);
                    return costA.CompareTo(costB);
                });

                if (!ascending)
                {
                    sortedCards.Reverse();
                }

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
                try
                {
                    var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                    FieldInfo cardViewsField = null;
                    MethodInfo tryInsertMethod = null;

                    for (var t = handView.GetType(); t != null; t = t.BaseType)
                    {
                        if (cardViewsField == null)
                        {
                            cardViewsField = t.GetField("_cardViews", flags);
                        }

                        if (tryInsertMethod == null)
                        {
                            foreach (var m in t.GetMethods(flags))
                            {
                                if (m.Name != "TryInsertCard") continue;
                                var ps = m.GetParameters();
                                if (ps.Length == 2 && ps[1].ParameterType == typeof(int) && ps[0].ParameterType.Name.Contains("CardView"))
                                {
                                    tryInsertMethod = m;
                                    break;
                                }
                            }
                        }

                        if (cardViewsField != null && tryInsertMethod != null) break;
                    }

                    var currentViews = new List<Nosebleed.Pancake.View.CardView>();
                    if (cardViewsField != null)
                    {
                        var listObj = cardViewsField.GetValue(handView);
                        if (listObj is System.Collections.IEnumerable enumerable)
                        {
                            foreach (var obj in enumerable)
                            {
                                var view = obj as Nosebleed.Pancake.View.CardView;
                                if (view != null) currentViews.Add(view);
                            }
                        }
                    }

                    // 反射字段失败时，退化为从 PileRoot 的子物体上收集 CardView
                    if (currentViews.Count == 0)
                    {
                        var pileRootProp = handView.GetType().GetProperty("PileRoot", flags);
                        var pileRoot = pileRootProp != null ? pileRootProp.GetValue(handView) as Transform : null;
                        if (pileRoot != null)
                        {
                            for (int i = 0; i < pileRoot.childCount; i++)
                            {
                                var child = pileRoot.GetChild(i);
                                if (child == null) continue;
                                var view = child.GetComponent<Nosebleed.Pancake.View.CardView>();
                                if (view != null) currentViews.Add(view);
                            }
                        }
                    }

                    // 按模型排序结果映射到对应的 CardView 顺序
                    var sortedViews = new List<Nosebleed.Pancake.View.CardView>();
                    foreach (var sortedCard in sortedCards)
                    {
                        int matchIndex = -1;
                        for (int i = 0; i < currentViews.Count; i++)
                        {
                            if (currentViews[i] == null) continue;
                            if (AreSameIl2CppObject(currentViews[i].CardModel, sortedCard))
                            {
                                matchIndex = i;
                                break;
                            }
                        }

                        if (matchIndex >= 0)
                        {
                            sortedViews.Add(currentViews[matchIndex]);
                            currentViews.RemoveAt(matchIndex);
                        }
                    }

                    // 把剩余未匹配的 view 追加，避免丢卡
                    for (int i = 0; i < currentViews.Count; i++)
                    {
                        if (currentViews[i] != null) sortedViews.Add(currentViews[i]);
                    }

                    if (sortedViews.Count == 0)
                    {
                        LogWarningOnce("no_cardviews_collected", "No CardViews collected for UI reordering.");
                    }
                    else if (tryInsertMethod != null)
                    {
                        for (int i = 0; i < sortedViews.Count; i++)
                        {
                            tryInsertMethod.Invoke(handView, new object[] { sortedViews[i], i });
                        }
                    }
                    else
                    {
                        // 最终兜底：直接修改层级顺序，强制视觉位置变化
                        for (int i = 0; i < sortedViews.Count; i++)
                        {
                            if (sortedViews[i] != null) sortedViews[i].transform.SetSiblingIndex(i);
                        }
                        LogWarningOnce("tryinsert_missing", "TryInsertCard not found, used transform.SetSiblingIndex fallback.");
                    }
                }
                catch (Exception ex)
                {
                    LogWarningOnce("ui_reorder_fallback_failed", "UI reorder fallback failed: " + ex.Message);
                }

                try
                {
                    var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                    var cardGroup = handView.CardGroup;
                    if (cardGroup != null)
                    {
                        var activeSlotsField = FindFieldInHierarchy(cardGroup.GetType(), "_activeCardSlots", flags);
                        var layoutGroupField = FindFieldInHierarchy(cardGroup.GetType(), "_layoutGroup", flags);
                        var trySetIndexMethod = FindMethodInHierarchy(cardGroup.GetType(), "TrySetLayoutGroupIndex", flags, new Type[] { typeof(int) });
                        var activeSlotsObj = activeSlotsField != null ? activeSlotsField.GetValue(cardGroup) : null;

                        if (activeSlotsObj == null)
                        {
                            LogWarningOnce("active_slots_null", "Step 6.6: _activeCardSlots field is null. cardGroupType=" + cardGroup.GetType().FullName);
                        }

                        var slotInfos = new List<Tuple<object, Nosebleed.Pancake.Models.CardModel>>();
                        if (activeSlotsObj != null)
                        {
                            foreach (var slotObj in EnumerateUnknownList(activeSlotsObj))
                            {
                                if (slotObj == null) continue;
                                var slotCardModel = TryGetCardModelFromSlot(slotObj, flags);
                                slotInfos.Add(new Tuple<object, Nosebleed.Pancake.Models.CardModel>(slotObj, slotCardModel));
                            }
                        }

                        if (slotInfos.Count > 0)
                        {
                            var sortedSlots = new List<object>();
                            foreach (var sortedCard in sortedCards)
                            {
                                int idx = -1;
                                for (int i = 0; i < slotInfos.Count; i++)
                                {
                                    if (AreSameIl2CppObject(slotInfos[i].Item2, sortedCard))
                                    {
                                        idx = i;
                                        break;
                                    }
                                }

                                if (idx >= 0)
                                {
                                    sortedSlots.Add(slotInfos[idx].Item1);
                                    slotInfos.RemoveAt(idx);
                                }
                            }

                            for (int i = 0; i < slotInfos.Count; i++)
                            {
                                sortedSlots.Add(slotInfos[i].Item1);
                            }

                            for (int i = 0; i < sortedSlots.Count; i++)
                            {
                                var slotObj = sortedSlots[i];
                                if (slotObj is Component comp)
                                {
                                    comp.transform.SetSiblingIndex(i);
                                }
                            }

                            if (activeSlotsObj != null)
                            {
                                var listType = activeSlotsObj.GetType();
                                var clearMethod = listType.GetMethod("Clear", flags, null, Type.EmptyTypes, null);
                                MethodInfo addMethod = null;
                                foreach (var m in listType.GetMethods(flags))
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
                                    clearMethod.Invoke(activeSlotsObj, null);
                                    for (int i = 0; i < sortedSlots.Count; i++)
                                    {
                                        addMethod.Invoke(activeSlotsObj, new object[] { sortedSlots[i] });
                                    }
                                }
                            }

                            if (trySetIndexMethod != null) trySetIndexMethod.Invoke(cardGroup, new object[] { 0 });

                            var layoutGroupObj = layoutGroupField != null ? layoutGroupField.GetValue(cardGroup) : null;
                            if (layoutGroupObj != null)
                            {
                                var forceMethod = layoutGroupObj.GetType().GetMethod("ForceLayoutRefresh", flags, null, Type.EmptyTypes, null);
                                if (forceMethod != null) forceMethod.Invoke(layoutGroupObj, null);
                            }

                        }
                        else
                        {
                            LogWarningOnce("no_slots_found", "Step 6.6: No slots found; rebuilding CardSlotHolder via TryRemoveCard/AddCardToTop...");

                            // 最后兜底：重建 CardSlotHolder 的可视槽位，不改模型，只重建 UI
                            try
                            {
                                for (int i = 0; i < finalModelCards.Count; i++)
                                {
                                    try { cardGroup.TryRemoveCard(finalModelCards[i]); } catch { }
                                }

                                // 按模型顺序重建，保证 UI 重新绑定最新卡序
                                for (int i = 0; i < finalModelCards.Count; i++)
                                {
                                    try { cardGroup.AddCardToTop(finalModelCards[i]); } catch { }
                                }

                                var layoutGroupObj = layoutGroupField != null ? layoutGroupField.GetValue(cardGroup) : null;
                                if (layoutGroupObj != null)
                                {
                                    var forceMethod = FindMethodInHierarchy(layoutGroupObj.GetType(), "ForceLayoutRefresh", flags, Type.EmptyTypes);
                                    if (forceMethod != null) forceMethod.Invoke(layoutGroupObj, null);
                                }

                                if (trySetIndexMethod != null) trySetIndexMethod.Invoke(cardGroup, new object[] { 0 });
                            }
                            catch (Exception ex2)
                            {
                                LogWarningOnce("step66_rebuild_failed", "Step 6.6 rebuild failed: " + ex2.Message);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogWarningOnce("step66_failed", "Step 6.6 failed: " + ex.Message);
                }

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
    }
}
