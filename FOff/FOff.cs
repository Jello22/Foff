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
    [BepInDependency(ConfigurationManagerGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class FOffPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.Jello.Foff";
        public const string PluginName = "F Off";
        public const string PluginVersion = "0.2.0";

        public const string ConfigurationManagerGuid =
            "com.bepis.bepinex.configurationmanager";

        internal static ConfigEntry<KeyboardShortcut> FilterKey = null!;
        internal static ManualLogSource Log = null!;

        private Harmony? _harmony;

        private static ZInputGetKeyDelegate? _zInputGetKey;

        private static bool _uiReflectionInitialized;
        private static bool _uiReflectionAvailable;
        private static bool _uiFailureLogged;

        private static Type? _uiInputHintType;
        private static Type? _textMeshProType;
        private static FieldInfo? _mouseKeyboardHintField;
        private static PropertyInfo? _tmpTextProperty;

        private delegate bool ZInputGetKeyDelegate(
            KeyCode key,
            bool blockOtherInput
        );

        private void Awake()
        {
            Log = Logger;

            FilterKey = Config.Bind(
                "Build Menu",
                "Filter Key",
                new KeyboardShortcut(KeyCode.F),
                "Key used to focus the Filter field in Valheim's build menu."
            );

            FilterKey.SettingChanged += OnFilterKeyChanged;

            if (!TryBindZInputGetKey())
            {
                Logger.LogError(
                    "Could not resolve ZInput.GetKey(KeyCode, bool). F Off will not patch the Filter key."
                );

                return;
            }

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(FOffPlugin).Assembly);

            Logger.LogInfo(
                $"{PluginName} {PluginVersion} loaded. Filter key: {FilterKey.Value}"
            );
        }

        private void OnDestroy()
        {
            if (FilterKey != null)
            {
                FilterKey.SettingChanged -= OnFilterKeyChanged;
            }

            _harmony?.UnpatchSelf();
        }

        private static void OnFilterKeyChanged(
            object sender,
            EventArgs args
        )
        {
            Log?.LogInfo($"Filter key changed to: {FilterKey.Value}");
            RefreshVisibleFilterHints();
        }

        private static bool TryBindZInputGetKey()
        {
            try
            {
                Type? zInputType = AccessTools.TypeByName("ZInput");

                MethodInfo? method = zInputType == null
                    ? null
                    : AccessTools.Method(
                        zInputType,
                        "GetKey",
                        new[]
                        {
                            typeof(KeyCode),
                            typeof(bool)
                        }
                    );

                if (method == null)
                {
                    return false;
                }

                _zInputGetKey =
                    (ZInputGetKeyDelegate)Delegate.CreateDelegate(
                        typeof(ZInputGetKeyDelegate),
                        method
                    );

                return true;
            }
            catch (Exception ex)
            {
                Log?.LogError(
                    $"Failed to bind ZInput.GetKey(KeyCode, bool): {ex}"
                );

                return false;
            }
        }

        internal static bool IsFilterKeyPressed()
        {
            if (_zInputGetKey == null || FilterKey == null)
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
                if (!_zInputGetKey(modifier, true))
                {
                    return false;
                }
            }

            return _zInputGetKey(shortcut.MainKey, true);
        }

        // Cache the Valheim UI fields used by the Filter hint.
        private static bool EnsureUiReflection()
        {
            if (_uiReflectionInitialized)
            {
                return _uiReflectionAvailable;
            }

            _uiReflectionInitialized = true;

            try
            {
                _uiInputHintType =
                    AccessTools.TypeByName("UIInputHint");

                _textMeshProType =
                    AccessTools.TypeByName("TMPro.TextMeshProUGUI");

                if (_uiInputHintType == null ||
                    _textMeshProType == null)
                {
                    LogUiWarningOnce(
                        "Could not resolve Valheim's Filter hint UI. The keybind will still work."
                    );

                    return false;
                }

                _mouseKeyboardHintField =
                    AccessTools.Field(
                        _uiInputHintType,
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
                        "Valheim's Filter hint UI no longer matches the expected layout. The keybind will still work."
                    );

                    return false;
                }

                _uiReflectionAvailable = true;
                return true;
            }
            catch (Exception ex)
            {
                LogUiWarningOnce(
                    $"Could not initialize Filter hint support: {ex.Message}"
                );

                return false;
            }
        }

        // Update only the build-menu SearchBar keyboard hint.
        internal static void UpdateFilterHint(object instance)
        {
            if (!EnsureUiReflection())
            {
                return;
            }

            try
            {
                if (!(instance is Component hintComponent))
                {
                    return;
                }

                GameObject hintObject = hintComponent.gameObject;

                if (hintObject.name != "SearchBarHint")
                {
                    return;
                }

                Transform parent = hintObject.transform.parent;

                if (parent == null ||
                    parent.name != "SearchBar")
                {
                    return;
                }

                GameObject? keyboardHint =
                    _mouseKeyboardHintField!.GetValue(instance)
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
                    textTransform.GetComponent(_textMeshProType!);

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
                    $"Could not update Filter key hint: {ex.Message}"
                );
            }
        }

        // Refresh the visible hint immediately when the setting changes.
        private static void RefreshVisibleFilterHints()
        {
            if (!EnsureUiReflection())
            {
                return;
            }

            try
            {
                UnityEngine.Object[] hints =
                    UnityEngine.Object.FindObjectsByType(
                        _uiInputHintType!,
                        FindObjectsSortMode.None
                    );

                foreach (UnityEngine.Object hint in hints)
                {
                    UpdateFilterHint(hint);
                }
            }
            catch (Exception ex)
            {
                LogUiWarningOnce(
                    $"Could not refresh Filter key hint: {ex.Message}"
                );
            }
        }

        private static string GetFilterHintText()
        {
            KeyCode key = FilterKey.Value.MainKey;

            if (key == KeyCode.None)
            {
                return string.Empty;
            }

            string name = key.ToString();

            // Unity names number-row keys Alpha0 through Alpha9.
            if (name.StartsWith("Alpha", StringComparison.Ordinal) &&
                name.Length == 6)
            {
                return name.Substring(5);
            }

            return name;
        }

        private static void LogUiWarningOnce(string message)
        {
            if (_uiFailureLogged)
            {
                return;
            }

            _uiFailureLogged = true;
            Log?.LogWarning(message);
        }
    }

    [HarmonyPatch]
    internal static class BuildUiNavigationUpdatePatch
    {
        private static MethodBase? TargetMethod()
        {
            Type? buildUiType =
                AccessTools.TypeByName("BuildUi");

            MethodInfo? target =
                buildUiType == null
                    ? null
                    : AccessTools.Method(
                        buildUiType,
                        "NavigationUpdate"
                    );

            if (target == null)
            {
                FOffPlugin.Log?.LogError(
                    "Could not find BuildUi.NavigationUpdate(). No Filter-key patch will be applied."
                );
            }

            return target;
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions
        )
        {
            List<CodeInstruction> codes =
                instructions.ToList();

            Type? zInputType =
                AccessTools.TypeByName("ZInput");

            MethodInfo? zInputGetKey =
                zInputType == null
                    ? null
                    : AccessTools.Method(
                        zInputType,
                        "GetKey",
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
                    "Could not resolve patch methods. BuildUi was left untouched."
                );

                return codes;
            }

            List<int> matches = new List<int>();

            for (int i = 0; i <= codes.Count - 3; i++)
            {
                if (LoadsFilterKey(codes[i]) &&
                    LoadsTrue(codes[i + 1]) &&
                    CallsMethod(codes[i + 2], zInputGetKey))
                {
                    matches.Add(i);
                }
            }

            if (matches.Count != 1)
            {
                FOffPlugin.Log?.LogWarning(
                    $"Expected one hardcoded BuildUi Filter-key pattern, but found {matches.Count}. Vanilla behavior was preserved."
                );

                return codes;
            }

            int index = matches[0];

            CodeInstruction replacement =
                new CodeInstruction(
                    OpCodes.Call,
                    replacementMethod
                );

            // Preserve Harmony metadata from the replaced instructions.
            for (int i = index; i < index + 3; i++)
            {
                replacement.labels.AddRange(codes[i].labels);
                replacement.blocks.AddRange(codes[i].blocks);
            }

            codes.RemoveRange(index, 3);
            codes.Insert(index, replacement);

            FOffPlugin.Log?.LogInfo(
                "Replaced the hardcoded F check in BuildUi.NavigationUpdate()."
            );

            return codes;
        }

        private static bool LoadsFilterKey(
            CodeInstruction instruction
        )
        {
            return
                instruction.opcode == OpCodes.Ldc_I4_S &&
                instruction.operand is sbyte value &&
                value == (int)KeyCode.F;
        }

        private static bool LoadsTrue(
            CodeInstruction instruction
        )
        {
            return instruction.opcode == OpCodes.Ldc_I4_1;
        }

        private static bool CallsMethod(
            CodeInstruction instruction,
            MethodInfo method
        )
        {
            return
                (instruction.opcode == OpCodes.Call ||
                 instruction.opcode == OpCodes.Callvirt) &&
                instruction.operand is MethodInfo called &&
                called == method;
        }
    }

    [HarmonyPatch]
    internal static class SearchBarInputHintPatch
    {
        private static MethodBase? TargetMethod()
        {
            Type? hintType =
                AccessTools.TypeByName("UIInputHint");

            MethodInfo? target =
                hintType == null
                    ? null
                    : AccessTools.Method(
                        hintType,
                        "UpdateInputHints"
                    );

            if (target == null)
            {
                FOffPlugin.Log?.LogWarning(
                    "Could not find UIInputHint.UpdateInputHints(). The Filter key will work, but its on-screen hint will remain vanilla."
                );
            }

            return target;
        }

        private static void Postfix(object __instance)
        {
            FOffPlugin.UpdateFilterHint(__instance);
        }
    }
}