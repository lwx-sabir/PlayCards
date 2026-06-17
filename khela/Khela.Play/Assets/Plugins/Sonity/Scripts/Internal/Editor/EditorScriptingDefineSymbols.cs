// Created by Victor Engström
// Copyright 2025 Sonigon AB
// http://www.sonity.org/

#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace Sonity.Internal {

    public static class EditorScriptingDefineSymbols {

        private static string editorToolSelectionHistoryDefineSymbol = "SONITY_ENABLE_EDITOR_TOOL_SELECTION_HISTORY";
        private static string editorToolReferenceFinderDefineSymbol = "SONITY_ENABLE_EDITOR_TOOL_REFERENCE_FINDER";
        private static string editorToolSelectSameTypeDefineSymbol = "SONITY_ENABLE_EDITOR_TOOL_SELECT_SAME_TYPE";
        private static string audioListenerVolumeIncreaseDefineSymbol = "SONITY_ENABLE_VOLUME_INCREASE";
        private static string addressableAudioMixerDefineSymbol = "SONITY_ENABLE_ADDRESSABLE_AUDIOMIXER";
        private static string integrationSteamAudioDefineSymbol = "SONITY_ENABLE_INTEGRATION_STEAM_AUDIO";
        private static string integrationPlayMakerDefineSymbol = "SONITY_ENABLE_INTEGRATION_PLAYMAKER";
        private static string integrationEntitySoundDefineSymbol = "SONITY_ENABLE_INTEGRATION_ENTITY_SOUND";
        private static string legacyFunctionsMusicAnd2DDefineSymbol = "SONITY_ENABLE_LEGACY_FUNCTIONS_MUSIC_AND_2D";

        // Integration Steam Audio
        public static bool IntegrationSteamAudioExists()
        {
            return DefineSymbolExists(integrationSteamAudioDefineSymbol);
        }

        public static void IntegrationSteamAudioAddRemove(bool shouldExist) {
            DefineSymbolAddRemove(integrationSteamAudioDefineSymbol, shouldExist);
        }

        // Integration Steam Audio
        public static bool IntegrationPlayMakerExists() {
            return DefineSymbolExists(integrationPlayMakerDefineSymbol);
        }

        public static void IntegrationPlayMakerAddRemove(bool shouldExist) {
            DefineSymbolAddRemove(integrationPlayMakerDefineSymbol, shouldExist);
        }
        
        // Integration Entity
        public static bool IntegrationEntityExists() {
            return DefineSymbolExists(integrationEntitySoundDefineSymbol);
        }

        public static void IntegrationEntityShouldExist(bool shouldExist) {
            DefineSymbolAddRemove(integrationEntitySoundDefineSymbol, shouldExist);
        }

        // Editor Tool Selection History
        public static bool EditorToolSelectionHistoryExists()
        {
            return DefineSymbolExists(editorToolSelectionHistoryDefineSymbol);
        }

        public static void EditorToolSelectionHistoryAddRemove(bool shouldExist) {
            DefineSymbolAddRemove(editorToolSelectionHistoryDefineSymbol, shouldExist);
        }

        // Editor Tool Reference Finder
        public static bool EditorToolReferenceFinderExists() {
            return DefineSymbolExists(editorToolReferenceFinderDefineSymbol);
        }

        public static void EditorToolReferenceFinderAddRemove(bool shouldExist) {
            DefineSymbolAddRemove(editorToolReferenceFinderDefineSymbol, shouldExist);
        }

        // Editor Tool Select Same Type
        public static bool EditorToolSelectSameTypeExists() {
            return DefineSymbolExists(editorToolSelectSameTypeDefineSymbol);
        }

        public static void EditorToolSelectSameTypeAddRemove(bool shouldExist) {
            DefineSymbolAddRemove(editorToolSelectSameTypeDefineSymbol, shouldExist);
        }

        // Volume Increase
        public static bool AudioListenerVolumeIncreaseExists() {
            return DefineSymbolExists(audioListenerVolumeIncreaseDefineSymbol);
        }

        public static void AudioListenerVolumeIncreaseAddRemove(bool shouldExist) {
            DefineSymbolAddRemove(audioListenerVolumeIncreaseDefineSymbol, shouldExist);
        }

        // Addressable AudioMixer
        public static bool AddressableAudioMixerExists() {
            return DefineSymbolExists(addressableAudioMixerDefineSymbol);
        }

        public static void AddressableAudioMixerAddRemove(bool shouldExist) {
            DefineSymbolAddRemove(addressableAudioMixerDefineSymbol, shouldExist);
        }

        public static void LegacyFunctionsMusicAnd2DAddRemove(bool shouldExist) {
            DefineSymbolAddRemove(legacyFunctionsMusicAnd2DDefineSymbol, shouldExist);
        }


        // Checks if the Define Symbol Exists
        private static bool DefineSymbolExists(string defineSymbol) {
#if UNITY_2021_2_OR_NEWER
            string definesString = PlayerSettings.GetScriptingDefineSymbols(UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup));
#else
            string definesString = PlayerSettings.GetScriptingDefineSymbolsForGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
#endif
            List<string> allDefines = definesString.Split(';').ToList();
            return allDefines.Contains(defineSymbol);
        }


        // Adds or removes the given define symbols to PlayerSettings define symbols
        private static void DefineSymbolAddRemove(string defineSymbol, bool shouldExist) {
#if UNITY_2021_2_OR_NEWER
            string definesString = PlayerSettings.GetScriptingDefineSymbols(UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup));
#else
            string definesString = PlayerSettings.GetScriptingDefineSymbolsForGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
#endif
            List<string> allDefines = definesString.Split(';').ToList();
            if (shouldExist) {
                // Adds a new define if it doesnt already exist
                if (!allDefines.Contains(defineSymbol)) {
                    allDefines.Add(defineSymbol);
                    Debug.Log($"Sonity.{nameof(NameOf.SoundManager)}: Added scripting define symbol \"" + defineSymbol + "\"");
                }
            } else {
                // Remove the define if it exists
                for (int i = allDefines.Count - 1; i >= 0; i--) {
                    if (allDefines[i] == defineSymbol) {
                        allDefines.RemoveAt(i);
                        Debug.Log($"Sonity.{nameof(NameOf.SoundManager)}: Removed scripting define symbol \"" + defineSymbol + "\"");
                    }
                }
            }

            // Merges and adds the defines
#if UNITY_2021_2_OR_NEWER
            PlayerSettings.SetScriptingDefineSymbols(UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup), string.Join(";", allDefines.ToArray()));
#else
            PlayerSettings.SetScriptingDefineSymbolsForGroup(EditorUserBuildSettings.selectedBuildTargetGroup, string.Join(";", allDefines.ToArray()));
#endif
        }
    }
}
#endif