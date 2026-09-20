using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

using HarmonyLib;

using UnityEngine;

namespace FOff
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(
        ConfigurationManagerGuid,
        BepInDependency.DependencyFlags.SoftDependency
    )]
    public sealed class FOffPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.Jello.Foff";
        public const string PluginName = "F Off";
        public const string PluginVersion = "0.3.0";

        public const string ConfigurationManagerGuid =
            "com.bepis.bepinex.configurationmanager";

        internal static ConfigEntry<KeyboardShortcut> FilterKey = null!;
        internal static ConfigEntry<KeyboardShortcut> PreviousTabKey = null!;
        internal static ConfigEntry<KeyboardShortcut> NextTabKey = null!;

        internal static ManualLogSource Log = null!;

        private Harmony? _harmony;

         /* TextMeshPro is kept reflective*/
        private static bool _uiReflectionInitialized;
        private static bool _uiReflectionAvailable;
        private static bool _uiFailureLogged;

        private static Type? _textMeshProType;
        private static FieldInfo? _mouseKeyboardHintField;
        private static PropertyInfo? _tmpTextProperty;

        private void Awake()
        {
            Log = Logger;

            PreviousTabKey = Config.Bind(
                "Build Menu",
                "Previous Tab Key",
                new KeyboardShortcut(KeyCode.Q),
                new ConfigDescription(
                    "Key used to move to the previous tab in Valheim's build menu.",
                    null,
                    new ConfigurationManagerAttributes
                    {
                        Order = 3
                    }
                )
            );

            NextTabKey = Config.Bind(
                "Build Menu",
                "Next Tab Key",
                new KeyboardShortcut(KeyCode.E),
                new ConfigDescription(
                    "Key used to move to the next tab in Valheim's build menu.",
                    null,
                    new ConfigurationManagerAttributes
                    {
                        Order = 2
                    }
                )
            );

            FilterKey = Config.Bind(
                "Build Menu",
                "Filter Key",
                new KeyboardShortcut(KeyCode.F),
                new ConfigDescription(
                    "Key used to focus the Filter field in Valheim's build menu.",
                    null,
                    new ConfigurationManagerAttributes
                    {
                        Order = 1
                    }
                )
            );

            FilterKey.SettingChanged += OnFilterKeyChanged;
            PreviousTabKey.SettingChanged += OnTabKeyChanged;
            NextTabKey.SettingChanged += OnTabKeyChanged;

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(FOffPlugin).Assembly);

            Logger.LogInfo(
                $"{PluginName} {PluginVersion} loaded. " +
                $"Filter: {FilterKey.Value}, " +
                $"Previous Tab: {PreviousTabKey.Value}, " +
                $"Next Tab: {NextTabKey.Value}"
            );
        }

        private void OnDestroy()
        {
            if (FilterKey != null)
            {
                FilterKey.SettingChanged -= OnFilterKeyChanged;
            }

            if (PreviousTabKey != null)
            {
                PreviousTabKey.SettingChanged -= OnTabKeyChanged;
            }

            if (NextTabKey != null)
            {
                NextTabKey.SettingChanged -= OnTabKeyChanged;
            }

            _harmony?.UnpatchSelf();
        }

        private static void OnFilterKeyChanged(
            object sender,
            EventArgs args
        )
        {
            Log?.LogInfo(
                $"Filter key changed to: {FilterKey.Value}"
            );

            RefreshVisibleFilterHints();
        }

        private static void OnTabKeyChanged(
            object sender,
            EventArgs args
        )
        {
            Log?.LogInfo(
                $"Build tab keys changed. " +
                $"Previous: {PreviousTabKey.Value}, " +
                $"Next: {NextTabKey.Value}"
            );

            RefreshVisibleTabHints();
        }

        /*Vanilla's Filter check uses GetKey rather than GetKeyDown,*/
         
        internal static bool IsFilterKeyPressed()
        {
            if (FilterKey == null)
            {
                return false;
            }

            KeyboardShortcut shortcut = FilterKey.Value;

            if (shortcut.MainKey == KeyCode.None)
            {
                return false;
            }

            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (!ZInput.GetKey(modifier, true))
                {
                    return false;
                }
            }

            return ZInput.GetKey(
                shortcut.MainKey,
                true
            );
        }

        /*
          Build-tab navigation should fire once per press, matching
          vanilla TabHandler's GetButtonDown behavior.
         */
        internal static bool IsShortcutDown(
            KeyboardShortcut shortcut
        )
        {
            return shortcut.IsDown();
        }

        /*
         BuildUi's tab navigation.
         We do NOT modify Valheim's global TABs
         */
        internal static void UpdateBuildMenuTabKeys(
            BuildUi buildUi
        )
        {
            if (buildUi == null ||
                buildUi.m_tabHandler == null ||
                PreviousTabKey == null ||
                NextTabKey == null)
            {
                return;
            }

            TabHandler tabHandler =
                buildUi.m_tabHandler;

            // Diagnostics.
            bool previousPressed = IsShortcutDown(PreviousTabKey.Value);
            bool nextPressed = IsShortcutDown(NextTabKey.Value);

           int direction = 0;

            if (previousPressed)
            {
                direction = -1;
            }
            else if (nextPressed)
            {
                direction = 1;
            }

            if (direction == 0 ||
                tabHandler.m_tabs.Count == 0)
            {
                return;
            }

            int current =
                tabHandler.GetActiveTab();

            int next =
                current + direction;

            /*Match TabHandler's normal cycling behavior.*/
            if (tabHandler.m_cycling)
            {
                if (next < 0)
                {
                    next =
                        tabHandler.m_tabs.Count - 1;
                }
                else if (next >=
                         tabHandler.m_tabs.Count)
                {
                    next = 0;
                }
            }
            else
            {
                next = Math.Max(
                    0,
                    Math.Min(
                        tabHandler.m_tabs.Count - 1,
                        next
                    )
                );
            }

            /*Skip entries without a button, matching vanilla TabHandler.Update().*/
            int start = next;

            while (!tabHandler.m_tabs[next].m_button)
            {
                next += direction;

                if (tabHandler.m_cycling)
                {
                    if (next < 0)
                    {
                        next =
                            tabHandler.m_tabs.Count - 1;
                    }
                    else if (next >=
                             tabHandler.m_tabs.Count)
                    {
                        next = 0;
                    }
                }
                else
                {
                    return;
                }

                if (next == start)
                {
                    return;
                }
            }

            /* Invoke the actual tab button so Valheim runs itsormal BuildUi listener and SelectPieceList */
                       
             
            tabHandler.SetActiveTabViaButtonIfAvailable(
                next
            );
        }

        /* Cache the reflective pieces needed to updateTextMeshPro */

        private static bool EnsureUiReflection()
        {
            if (_uiReflectionInitialized)
            {
                return _uiReflectionAvailable;
            }

            _uiReflectionInitialized = true;

            try
            {
                _textMeshProType =
                    AccessTools.TypeByName(
                        "TMPro.TextMeshProUGUI"
                    );

                if (_textMeshProType == null)
                {
                    LogUiWarningOnce(
                        "Could not resolve TextMeshPro. " +
                        "Keybinds will still work."
                    );

                    return false;
                }

                _mouseKeyboardHintField =
                    AccessTools.Field(
                        typeof(UIInputHint),
                        "m_mouseKeyboardHint"
                    );

                _tmpTextProperty =
                    AccessTools.Property(
                        _textMeshProType,
                        "text"
                    );

                if (_mouseKeyboardHintField == null ||
                    _tmpTextProperty == null ||
                    !_tmpTextProperty.CanWrite)
                {
                    LogUiWarningOnce(
                        "Valheim's key hint UI no longer matches " +
                        "the expected layout. Keybinds will still work."
                    );

                    return false;
                }

                _uiReflectionAvailable = true;
                return true;
            }
            catch (Exception ex)
            {
                LogUiWarningOnce(
                    $"Could not initialize key hint support: " +
                    $"{ex.Message}"
                );

                return false;
            }
        }

        /* Update only the build-menu SearchBar Filter hint.
         */
        internal static void UpdateFilterHint(
            UIInputHint inputHint
        )
        {
            if (!EnsureUiReflection())
            {
                return;
            }

            try
            {
                GameObject hintObject =
                    inputHint.gameObject;

                if (hintObject.name != "SearchBarHint")
                {
                    return;
                }

                Transform parent =
                    hintObject.transform.parent;

                if (parent == null ||
                    parent.name != "SearchBar")
                {
                    return;
                }

                GameObject? keyboardHint =
                    _mouseKeyboardHintField!
                        .GetValue(inputHint)
                    as GameObject;

                if (keyboardHint == null)
                {
                    return;
                }

                Transform textTransform =
                    keyboardHint.transform.Find("Text");

                if (textTransform == null)
                {
                    return;
                }

                Component? textComponent =
                    textTransform.GetComponent(
                        _textMeshProType!
                    );

                if (textComponent == null)
                {
                    return;
                }

                _tmpTextProperty!.SetValue(
                    textComponent,
                    GetFilterHintText(),
                    null
                );
            }
            catch (Exception ex)
            {
                LogUiWarningOnce(
                    $"Could not update Filter key hint: " +
                    $"{ex.Message}"
                );
            }
        }

        /* Refresh the Filter hint immediately when the setting config  */


        private static void RefreshVisibleFilterHints()
        {
            if (!EnsureUiReflection())
            {
                return;
            }

            try
            {
                UIInputHint[] hints =
                    UnityEngine.Object
                        .FindObjectsByType<UIInputHint>(
                            FindObjectsSortMode.None
                        );

                foreach (UIInputHint hint in hints)
                {
                    UpdateFilterHint(hint);
                }
            }
            catch (Exception ex)
            {
                LogUiWarningOnce(
                    $"Could not refresh Filter key hint: " +
                    $"{ex.Message}"
                );
            }
        }


        internal static void UpdateTabHints(
            BuildUi buildUi
        )
        {
            if (buildUi == null ||
                PreviousTabKey == null ||
                NextTabKey == null)
            {
                return;
            }

            if (!EnsureUiReflection())
            {
                return;
            }

            try
            {
                Transform root =
                    buildUi.transform;

                Transform? leftHint =
                    root.Find(
                        "bar/SelectionWindow/TabContainer/" +
                        "InputHelp/MK hints/Left"
                    );

                Transform? rightHint =
                    root.Find(
                        "bar/SelectionWindow/TabContainer/" +
                        "InputHelp/MK hints/Right"
                    );

                if (leftHint == null ||
                    rightHint == null)
                {
                    return;
                }

                SetTabHintText(
                    leftHint,
                    GetShortcutHintText(
                        PreviousTabKey.Value
                    )
                );

                SetTabHintText(
                    rightHint,
                    GetShortcutHintText(
                        NextTabKey.Value
                    )
                );
            }
            catch (Exception ex)
            {
                Log?.LogWarning(
                    $"Could not update build tab key hints: " +
                    $"{ex.Message}"
                );
            }
        }


        private static void RefreshVisibleTabHints()
        {
            if (!EnsureUiReflection())
            {
                return;
            }

            try
            {
                BuildUi[] buildUis =
                    UnityEngine.Object
                        .FindObjectsByType<BuildUi>(
                            FindObjectsSortMode.None
                        );

                foreach (BuildUi buildUi in buildUis)
                {
                    UpdateTabHints(buildUi);
                }
            }
            catch (Exception ex)
            {
                Log?.LogWarning(
                    $"Could not refresh build tab key hints: " +
                    $"{ex.Message}"
                );
            }
        }

        private static void SetTabHintText(
            Transform hint,
            string text
        )
        {
            Component? textComponent =
                hint.GetComponentInChildren(
                    _textMeshProType!,
                    true
                );

            if (textComponent == null)
            {
                return;
            }

            _tmpTextProperty!.SetValue(
                textComponent,
                text,
                null
            );
        }


        private static string GetShortcutHintText(
            KeyboardShortcut shortcut
        )
        {
            KeyCode key =
                shortcut.MainKey;

            if (key == KeyCode.None)
            {
                return string.Empty;
            }

            string name =
                key.ToString();


            if (name.StartsWith(
                    "Alpha",
                    StringComparison.Ordinal
                ) &&
                name.Length == 6)
            {
                return name.Substring(5);
            }

            return name;
        }

        private static string GetFilterHintText()
        {
            return GetShortcutHintText(
                FilterKey.Value
            );
        }

        private static void LogUiWarningOnce(
            string message
        )
        {
            if (_uiFailureLogged)
            {
                return;
            }

            _uiFailureLogged = true;
            Log?.LogWarning(message);
        }
    }


    [HarmonyPatch(
        typeof(BuildUi),
        nameof(BuildUi.Awake)
    )]
    internal static class BuildUiAwakePatch
    {
        private static void Postfix(
            BuildUi __instance
        )
        {
            if (__instance.m_tabHandler != null)
            {
                __instance.m_tabHandler.m_keybaordInput =
                    false;

                FOffPlugin.Log?.LogInfo(
                    "Disabled vanilla BuildUi Q/E tab handling."
                );
            }

            FOffPlugin.UpdateTabHints(
                __instance
            );
        }
    }
    [HarmonyPatch(
        typeof(TabHandler),
        nameof(TabHandler.Update)
    )]
    internal static class TabHandlerUpdatePatch
    {
        private static void Prefix(TabHandler __instance)
        {
            if (__instance.GetComponentInParent<BuildUi>() == null)
            {
                return;
            }

            __instance.m_keybaordInput = false;
        }
    }
    

    [HarmonyPatch(
        typeof(BuildUi),
        nameof(BuildUi.NavigationUpdate)
    )]
    internal static class BuildUiNavigationUpdatePatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions
        )
        {
            List<CodeInstruction> codes =
                instructions.ToList();

            MethodInfo? zInputGetKey =
                AccessTools.Method(
                    typeof(ZInput),
                    nameof(ZInput.GetKey),
                    new[]
                    {
                        typeof(KeyCode),
                        typeof(bool)
                    }
                );

            MethodInfo? replacementMethod =
                AccessTools.Method(
                    typeof(FOffPlugin),
                    nameof(FOffPlugin.IsFilterKeyPressed)
                );

            if (zInputGetKey == null ||
                replacementMethod == null)
            {
                FOffPlugin.Log?.LogError(
                    "Could not resolve Filter patch methods. " +
                    "BuildUi was left untouched."
                );

                return codes;
            }

            List<int> matches =
                new List<int>();

            for (int i = 0;
                 i <= codes.Count - 3;
                 i++)
            {
                if (LoadsFilterKey(codes[i]) &&
                    LoadsTrue(codes[i + 1]) &&
                    CallsMethod(
                        codes[i + 2],
                        zInputGetKey
                    ))
                {
                    matches.Add(i);
                }
            }

            if (matches.Count != 1)
            {
                FOffPlugin.Log?.LogWarning(
                    $"Expected one hardcoded BuildUi Filter-key " +
                    $"pattern, but found {matches.Count}. " +
                    $"Vanilla behavior was preserved."
                );

                return codes;
            }

            int index =
                matches[0];

            CodeInstruction replacement =
                new CodeInstruction(
                    OpCodes.Call,
                    replacementMethod
                );


            for (int i = index;
                 i < index + 3;
                 i++)
            {
                replacement.labels.AddRange(
                    codes[i].labels
                );

                replacement.blocks.AddRange(
                    codes[i].blocks
                );
            }

            codes.RemoveRange(
                index,
                3
            );

            codes.Insert(
                index,
                replacement
            );

            FOffPlugin.Log?.LogInfo(
                "Replaced the hardcoded F check in " +
                "BuildUi.NavigationUpdate()."
            );

            return codes;
        }


        private static void Postfix(
            BuildUi __instance
        )
        {
            FOffPlugin.UpdateBuildMenuTabKeys(
                __instance
            );

            FOffPlugin.UpdateTabHints(
                __instance
            );
        }

        private static bool LoadsFilterKey(
            CodeInstruction instruction
        )
        {
            return
                instruction.opcode ==
                    OpCodes.Ldc_I4_S &&
                instruction.operand is sbyte value &&
                value == (int)KeyCode.F;
        }

        private static bool LoadsTrue(
            CodeInstruction instruction
        )
        {
            return instruction.opcode ==
                   OpCodes.Ldc_I4_1;
        }

        private static bool CallsMethod(
            CodeInstruction instruction,
            MethodInfo method
        )
        {
            return
                (instruction.opcode ==
                     OpCodes.Call ||
                 instruction.opcode ==
                     OpCodes.Callvirt) &&
                instruction.operand
                    is MethodInfo called &&
                called == method;
        }
    }
    internal sealed class ConfigurationManagerAttributes
    {
        public int? Order;
    }
    

    [HarmonyPatch(
        typeof(UIInputHint),
        nameof(UIInputHint.UpdateInputHints)
    )]
    internal static class SearchBarInputHintPatch
    {
        private static void Postfix(
            UIInputHint __instance
        )
        {
            FOffPlugin.UpdateFilterHint(
                __instance
            );
        }
    }
}
