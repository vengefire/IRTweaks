﻿using BattleTech;
using BattleTech.UI;
using BattleTech.UI.TMProWrapper;
using DG.Tweening;
using Harmony;
using HBS;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.UI;
using static BattleTech.FloatieMessage;

namespace IRTweaks.Modules.UI {
    public static class CombatLog {

        private static CombatHUDInfoSidePanel infoSidePanel;
        private static CombatChatModule combatChatModule;

        // Static instance pointers from the various components 
        private static CombatGameState combat;
        private static MessageCenter messageCenter;

        private static Action<CombatChatModule> CombatChatModule_UIModule_Update;

        // Shamelessly stolen from https://github.com/janxious/BT-WeaponRealizer/blob/7422573fa69893ae7c16a9d192d85d2152f90fa2/NumberOfShotsEnabler.cs#L32
        public static bool InitModule() {
            // build a call to WeaponEffect.OnComplete() so it can be called
            // a la base.OnComplete() from the context of a BallisticEffect
            // https://blogs.msdn.microsoft.com/rmbyers/2008/08/16/invoking-a-virtual-method-non-virtually/
            // https://docs.microsoft.com/en-us/dotnet/api/system.activator?view=netframework-3.5
            // https://docs.microsoft.com/en-us/dotnet/api/system.reflection.emit.dynamicmethod.-ctor?view=netframework-3.5#System_Reflection_Emit_DynamicMethod__ctor_System_String_System_Type_System_Type___System_Type_
            // https://stackoverflow.com/a/4358250/1976
            var method = typeof(UIModule).GetMethod("Update", AccessTools.all);
            var dm = new DynamicMethod("CombatChatModule_UIModule_Update", null, new Type[] { typeof(CombatChatModule) }, typeof(CombatChatModule));
            var gen = dm.GetILGenerator();
            gen.Emit(OpCodes.Ldarg_0);
            gen.Emit(OpCodes.Call, method);
            gen.Emit(OpCodes.Ret);
            CombatChatModule_UIModule_Update = (Action<CombatChatModule>)dm.CreateDelegate(typeof(Action<CombatChatModule>));
            return true;
        }

        public static void CombatHUD_Init_Postfix(CombatHUD __instance, CombatGameState Combat) {
            Mod.Log.Trace("CHUD:I:post - entered.");

            CombatLog.infoSidePanel = LazySingletonBehavior<UIManager>.Instance.GetOrCreateUIModule<CombatHUDInfoSidePanel>("", true);
            infoSidePanel.Init();
            infoSidePanel.Visible = false;

            // Combat Chat module
            CombatLog.combatChatModule = LazySingletonBehavior<UIManager>.Instance.GetOrCreateUIModule<CombatChatModule>("", true);
            if (CombatLog.combatChatModule == null) {
                Mod.Log.Error("Error creating combat chat module");
            } else {
                CombatLog.combatChatModule.CombatInit();
                CombatLog.infoSidePanel.BumpUp();
            }

            Mod.Log.Info($"CombatChatModule pos: {CombatLog.combatChatModule.gameObject.transform.position}");
            Mod.Log.Info($"RetreatEscMenu pos: {__instance.RetreatEscMenu.gameObject.transform.position}");

            Vector3 newPos = CombatLog.combatChatModule.gameObject.transform.position;
            newPos.x = __instance.RetreatEscMenu.gameObject.transform.position.x * 1.25f;
            newPos.y = __instance.RetreatEscMenu.gameObject.transform.position.y - 120f;
            //newPos.z = __instance.WeaponPanel.gameObject.transform.position.z;

            CombatLog.combatChatModule.gameObject.transform.position = newPos;
            Mod.Log.Info($"new CombatChatModule pos: {newPos}");

            // Move the chat button into the menu
            Transform chatBtnT = CombatLog.combatChatModule.gameObject.transform.Find("Representation/chat_panel/uixPrf_chatButton");
            Mod.Log.Info($"ChatButton base  pos: {newPos}");
            if (chatBtnT != null) {
                if (__instance.Combat.BattleTechGame.Simulation == null) { 
                    newPos.x -= 620f; // skirmish, no withdraw button
                } else {
                    newPos.x -= 740f; // simgame, has withdraw button
                }
                newPos.y += 80f;
                chatBtnT.position = newPos;
                Mod.Log.Info($"ChatButton new  pos: {newPos}");
            } else {
                Mod.Log.Info("Could not find chatButton to change position!");
            }

            combat = Combat;
        }

        public static void CombatHUD_OnCombatGameDestroyed_Postfix() {
            Mod.Log.Info("Combat game destroyed, cleaning up");
            combat = null;
            messageCenter = null;
        }

        public static void CombatChatModule_Init_Postfix(CombatChatModule __instance, MessageCenter ____messageCenter,
            HBSDOTweenButton ____chatBtn, HBSDOTweenButton ____muteBtn, ActiveChatListView ____activeChatList) {
            ____chatBtn.enabled = false;
            ____chatBtn.gameObject.SetActive(false);
            ____muteBtn.enabled = false;
            ____muteBtn.gameObject.SetActive(false);

            ____messageCenter.AddSubscriber(MessageCenterMessageType.FloatieMessage, OnFloatie);
            messageCenter = ____messageCenter;
        }

        public static void CombatChatModule_OnPooled_Postfix(MessageCenter ____messageCenter) {
            ____messageCenter.RemoveSubscriber(MessageCenterMessageType.FloatieMessage, OnFloatie);
            messageCenter = null;
        }

        private static void OnFloatie(MessageCenterMessage message) {
            FloatieMessage floatieMessage = (FloatieMessage)message;

            AbstractActor target = combat.FindActorByGUID(floatieMessage.affectedObjectGuid);
            if (target == null) { return;  }

            if (floatieMessage.text == null) { return; }

            try {
                string senderColor;
                if (combat.HostilityMatrix.IsLocalPlayerEnemy(target.TeamId)) {
                    senderColor = "#" + ColorUtility.ToHtmlStringRGBA(LazySingletonBehavior<UIManager>.Instance.UIColorRefs.redHalf);
                } else if (combat.HostilityMatrix.IsLocalPlayerNeutral(target.TeamId)) {
                    senderColor = "#" + ColorUtility.ToHtmlStringRGBA(LazySingletonBehavior<UIManager>.Instance.UIColorRefs.blueHalf);
                } else {
                    senderColor = "#" + ColorUtility.ToHtmlStringRGBA(LazySingletonBehavior<UIManager>.Instance.UIColorRefs.greenHalf);
                }

                string sender = (target.IsPilotable && target.GetPilot() != null) ? $"{target.DisplayName}-{target.GetPilot().Name}" : $"{target.DisplayName}";
                string senderWithColor = $"&lt;{senderColor}&gt;{sender}&lt;/color&gt;";
                Mod.Log.Debug($"ChatMessage senderWithColor: '{senderWithColor}'");

                string logMessage = floatieMessage.text.ToString();
                switch (floatieMessage.nature) {
                    case FloatieMessage.MessageNature.ArmorDamage:
                        logMessage = $"{logMessage} armor damage";
                        break;
                    case FloatieMessage.MessageNature.StructureDamage:
                        logMessage = $"{logMessage} structure damage";
                        break;
                    default:
                        break;
                }

                messageCenter.PublishMessage(new ChatMessage(senderWithColor, logMessage, false));
            } catch (Exception e) {
                Mod.Log.Error($"Failed to send floatieMessage: {floatieMessage}");
                Mod.Log.Error(e);
            }
        }

        public static void CombatChatModule_CombatInit_Postfix(CombatChatModule __instance, MessageCenter ____messageCenter,
            HBSDOTweenButton ____chatBtn, HBSDOTweenButton ____muteBtn, HBS_InputField ____inputField, 
            GameObject ____activeChatWindow, ActiveChatListView ____activeChatList, PassiveChatListView ____passiveChatList) {

            ____chatBtn.enabled = true;
            ____chatBtn.gameObject.SetActive(true);
            ____muteBtn.enabled = false;
            ____muteBtn.gameObject.SetActive(false);
            ____inputField.enabled = false;
            ____inputField.gameObject.SetActive(false);
            ____inputField.readOnly = true;

            // Hide the send button
            Transform sendButtonT = ____activeChatWindow.gameObject.transform.Find("uixPrf_genericButton");
            if (sendButtonT != null) {
                sendButtonT.gameObject.SetActive(false);
            } else {
                Mod.Log.Info("Could not find send button to disable!");
            }

            // Set the scroll spacing to 0
            Transform scrollListT = ____activeChatWindow.gameObject.transform.Find("panel_history/uixPrfPanl_listView/ScrollRect/Viewport/List");
            if (scrollListT != null) {
                VerticalLayoutGroup scrollListVLG = scrollListT.gameObject.GetComponent<VerticalLayoutGroup>();
                scrollListVLG.spacing = 0;
            } else {
                Mod.Log.Info("Could not find scrollList to change spacing!");
            }

            // Resize the image background
            Transform imageBackgroundT = ____activeChatWindow.gameObject.transform.Find("image_background");
            if (imageBackgroundT != null) {
                RectTransform imageBackgroundRT = imageBackgroundT.gameObject.GetComponent<RectTransform>();
                Rect ibRect = imageBackgroundRT.rect;
                Mod.Log.Info($"Background image size: {ibRect.height}h x {ibRect.width}");
                Vector3 newPos = imageBackgroundRT.position;
                newPos.y += 20f;
                imageBackgroundRT.position = newPos;
                imageBackgroundRT.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, ibRect.height - 20);
                imageBackgroundRT.ForceUpdateRectTransforms();
            } else {
                Mod.Log.Info("Could not find imageBackground to change size!");
            }
        }

        public static bool CombatChatModule_OnChatMessage_Prefix(CombatChatModule __instance, MessageCenterMessage message,
            ActiveChatListView ____activeChatList, PassiveChatListView ____passiveChatList) {

            ChatMessage chatMessage = (ChatMessage)message;
            Mod.Log.Debug($"Chat message is: '{chatMessage.Message}'");
            try {
                ____activeChatList.Add(chatMessage);
                ____activeChatList.ScrollToBottom();
                ____activeChatList.Refresh();
            } catch (Exception e) {
                Mod.Log.Error($"Failed to send a message:{e.Message}");
                Mod.Log.Error($"{e.StackTrace}");
            }

            return false;
        }

        // Re-enable keyboard input (don't block out wasd)
        public static void CombatChatModule_Active_OnEnter_Postfix(CombatChatModule __instance, HBS_InputField ____inputField) {
            ____inputField.DeactivateInputField();
            BTInput.Instance.DynamicActions.Enabled = true;
        }

        [HarmonyPatch(typeof(CombatChatModule), "Update")]
        public static class CombatChatModule_Update {
            static bool Prepare() { return Mod.Config.Fixes.CombatLog; }

            static bool Prefix(CombatChatModule __instance, ActiveChatListView ____activeChatList) {
                //Mod.Log.Info(" -- CCM:Update:pre invoked");
                // Invoke base.Update()
                CombatChatModule_UIModule_Update.Invoke(__instance);

                // Remove the [T] from the chat button
                Transform chatBtnT = __instance.gameObject.transform.Find("Representation/chat_panel/uixPrf_chatButton");
                if (chatBtnT != null) {
                    LocalizableText chatBtnLT = chatBtnT.GetComponentInChildren<LocalizableText>();
                    chatBtnLT.SetText(" ");
                } else {
                    Mod.Log.Info("Could not find chat button");
                }

                return false;
            }
        }

        [HarmonyPatch(typeof(OrderSequence), "OnComplete")]
        public static class OrderSequence_OnComplete {
            static bool Prepare() { return Mod.Config.Fixes.CombatLog; }

            static void Postfix(OrderSequence __instance) {
                if (CombatLog.combatChatModule != null) {
                    CombatLog.combatChatModule.ForceRefreshImmediate();
                } 
            }
        }

        public static bool ChatListViewItem_SetData_Prefix(ChatListViewItem __instance, ChatMessage message,
            LocalizableText ____chatMessage) {

            string expandedSender = message.SenderName.Replace("&gt;", ">");
            expandedSender = expandedSender.Replace("&lt;", "<");
            string senderText = $"{expandedSender}";
            Mod.Log.Debug($"Message senderName: '{message.SenderName}'  expandedSender: '{expandedSender}'  senderText: '{senderText}'");

            string messageColor = "#" + ColorUtility.ToHtmlStringRGBA(LazySingletonBehavior<UIManager>.Instance.UIColorRefs.whiteHalf);
            string expandedMessage = message.Message.Replace("&gt;", ">");
            expandedMessage = expandedMessage.Replace("&lt;", "<");
            string messageText = $"<{messageColor}>{expandedMessage}</color>";
            Mod.Log.Debug($"Message text: '{expandedMessage}'");

            Localize.Text translatedText = new Localize.Text("<size=-3>" + senderText + " " + messageText + "</size>");
            ____chatMessage.text = translatedText.ToString();
            
            DOTweenAnimation componentInChildren = ____chatMessage.GetComponentInChildren<DOTweenAnimation>();
            if (componentInChildren != null) {
                componentInChildren.delay = 50;
                componentInChildren.CreateTween();
                componentInChildren.DOPlay();
            }

            return false;
        }

        public static void CombatHUDActorInfo_SubscribeToMessages_Postfix(CombatHUDActorInfo __instance) {
            // Unsubscribe immediately so we don't process messages
            MethodInfo onFloatieMI = AccessTools.Method(typeof(CombatHUDActorInfo), "OnFloatie", new Type[] { typeof(MessageCenterMessage) }, null);
            Delegate onFloatieDelegate = onFloatieMI.CreateDelegate(typeof(ReceiveMessageCenterMessage), __instance);
            Mod.Log.Info("Unsubscribing from CombatHUDActorInfo:OnFloatie messages.");
            __instance.Combat.MessageCenter.RemoveSubscriber(MessageCenterMessageType.FloatieMessage, (ReceiveMessageCenterMessage)onFloatieDelegate);
        }

        public static void MessageCenter_RemoveSubscriber_Prefix(MessageCenter __instance, MessageCenterMessageType GUID, ReceiveMessageCenterMessage subscriber,
            Dictionary<MessageCenterMessageType, List<MessageSubscription>> ___messageTable) {

            if (GUID == MessageCenterMessageType.FloatieMessage) {
                List<MessageSubscription> list = ___messageTable[GUID];
                Mod.Log.Info($"MCMT subscription list is size: {list.Count}");
            }
            //} else {
            //    Mod.Log.Info("MCMT not floatie, skipping.");
            //}
        }

        public static bool CombatHUDInWorldElementMgr_AddFloatieMessage_Prefix(CombatHUDInWorldElementMgr __instance, MessageCenterMessage message, CombatGameState ___combat) {

            Traverse showFloatieT = Traverse.Create(__instance).Method("ShowFloatie", new Type[] { typeof(FloatieMessage) });
            FloatieMessage floatieMessage = message as FloatieMessage;
            switch (floatieMessage.nature) {
                case MessageNature.ArmorDamage:
                case MessageNature.StructureDamage:
                case MessageNature.Buff:
                case MessageNature.Debuff:
                    showFloatieT.GetValue(new object[] { floatieMessage });
                    break;
                case MessageNature.Miss:
                case MessageNature.MeleeMiss:
                case MessageNature.Dodge:
                    //__instance.ShowFloatie(floatieMessage);
                    break;
                default:
                    //__instance.ShowStackedFloatie(floatieMessage);
                    break;
            }

            return false;
        }

    }
}
